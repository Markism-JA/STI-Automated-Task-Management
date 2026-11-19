using Microsoft.Extensions.Logging;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Core.Sync;

public sealed record BasinSyncReport(
    int CoursesSynced,
    int FilesDownloaded,
    int FilesUpdated,
    int FilesRelocated,
    int TasksUpserted,
    int MasterPdfsCompiled,
    IReadOnlyList<AppNotification> Notifications,
    IReadOnlyList<string> Errors);

/// <summary>
/// Orchestrates ScholarFlow.Elms scraping/downloading into a Basin, upserts LMS assignments into tasks.md,
/// diffs PollState to emit AppNotifications, and triggers incremental MasterPdfService compilation.
/// </summary>
public sealed class ElmsBasinSyncService
{
    private readonly IElmsClient _elmsClient;
    private readonly BasinService _basinService;
    private readonly ISyncResolver _syncResolver;
    private readonly MasterPdfService _pdfService;
    private readonly ILogger<ElmsBasinSyncService>? _logger;

    public ElmsBasinSyncService(
        IElmsClient elmsClient,
        BasinService? basinService = null,
        ISyncResolver? syncResolver = null,
        MasterPdfService? pdfService = null,
        ILogger<ElmsBasinSyncService>? logger = null)
    {
        _elmsClient = elmsClient ?? throw new ArgumentNullException(nameof(elmsClient));
        _basinService = basinService ?? new BasinService();
        _syncResolver = syncResolver ?? new SyncResolver();
        _pdfService = pdfService ?? new MasterPdfService();
        _logger = logger;
    }

    public async Task<BasinSyncReport> SyncAllAsync(
        string basinRoot,
        bool forceRedownload = false,
        bool compileMasterPdfs = true,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default,
        bool allowInteractiveAuth = true)
    {
        var notifications = new List<AppNotification>();
        var errors = new List<string>();

        var config = await _basinService.InitializeOrOpenAsync(basinRoot, ct: ct);
        await _elmsClient.EnsureConnectedAsync(
            new AuthOptions { BaseUrl = config.ElmsBaseUrl, AllowInteractiveBrowser = allowInteractiveAuth },
            ct);

        var ignoreFilter = await CourseFilterStore.LoadAsync(CourseFilterStore.GetDefaultFilterPath(basinRoot), ct);
        var discoveredCourses = await _elmsClient.GetEnrolledCoursesAsync(ignoreFilter, ct: ct);
        var basinCourses = await _basinService.UpsertCoursesAsync(basinRoot, discoveredCourses, ct);

        var statePath = LocalSyncStateStore.GetDefaultStatePath(basinRoot);
        var syncState = await LocalSyncStateStore.LoadAsync(statePath, ct);
        var pollState = await _basinService.LoadPollStateAsync(basinRoot, ct);

        int downloaded = 0, updated = 0, relocated = 0, tasksUpserted = 0, pdfsCompiled = 0;

        foreach (var rawElmsCourse in discoveredCourses)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var courseEntry = basinCourses.FirstOrDefault(c =>
                    (!string.IsNullOrWhiteSpace(c.CourseId) && c.CourseId == rawElmsCourse.CourseId) ||
                    c.FolderName.Equals(rawElmsCourse.SanitizedFolderName, StringComparison.OrdinalIgnoreCase) ||
                    BasinService.IsSameCourseFuzzy(c.FolderName, rawElmsCourse.SanitizedFolderName))
                    ?? basinCourses.First(c => c.Title.Equals(rawElmsCourse.Title, StringComparison.OrdinalIgnoreCase));

                // Map to the canonical local course folder name from courses.json so we never create duplicate directories
                var elmsCourse = rawElmsCourse with { SanitizedFolderName = courseEntry.FolderName };

                var snapshot = await _elmsClient.GetCourseSnapshotAsync(elmsCourse, progress, ct);
                var plan = _syncResolver.Resolve(snapshot, syncState, basinRoot, forceRedownload);

                await CourseArtifactWriter.WriteCourseArtifactsAsync(snapshot, basinRoot, ct);

                var executor = new SyncExecutor(((ScholarFlow.Elms.Client.StiElmsClient)_elmsClient).HttpClient);
                var execResult = await executor.ExecuteAsync(
                    plan,
                    new SyncOptions { OutputRootDirectory = basinRoot, ForceRedownload = forceRedownload },
                    syncState,
                    progress,
                    ct);

                downloaded += execResult.Downloaded;
                updated += execResult.Updated;
                relocated += execResult.Relocated;
                errors.AddRange(execResult.Errors);

                if (execResult.Downloaded > 0 || execResult.Updated > 0)
                {
                    notifications.Add(new AppNotification(
                        NotificationKind.MaterialDownloaded,
                        $"{courseEntry.Title}: {execResult.Downloaded + execResult.Updated} file(s) synced",
                        $"Downloaded {execResult.Downloaded} new, updated {execResult.Updated}.",
                        courseEntry.FolderName,
                        elmsCourse.CourseUrl));
                }

                var upserted = await UpsertCourseAssignmentsAsync(basinRoot, courseEntry, snapshot.Assignments, pollState, notifications, ct, snapshot.Lessons);
                tasksUpserted += upserted;

                if (compileMasterPdfs && config.AutoCompileMasterPdf)
                {
                    var pdfResult = await _pdfService.CompileIfStaleAsync(basinRoot, courseEntry.FolderName, courseEntry.Title, force: false, ct);
                    if (pdfResult.WasRegenerated)
                    {
                        pdfsCompiled++;
                        notifications.Add(new AppNotification(
                            NotificationKind.MasterPdfCompiled,
                            $"Compiled {courseEntry.Title} Master PDF",
                            $"Regenerated {Path.GetFileName(pdfResult.OutputPdfPath)} ({pdfResult.PageCount} pages).",
                            courseEntry.FolderName));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Error syncing course {Course}", rawElmsCourse.Title);
                errors.Add($"{rawElmsCourse.Title}: {ex.Message}");
            }
        }

        syncState.LastFullSyncAt = DateTimeOffset.UtcNow;
        await LocalSyncStateStore.SaveAsync(statePath, syncState, ct);

        pollState.LastFullSyncAt = DateTimeOffset.UtcNow;
        await _basinService.SavePollStateAsync(basinRoot, pollState, ct);

        return new BasinSyncReport(
            discoveredCourses.Count,
            downloaded,
            updated,
            relocated,
            tasksUpserted,
            pdfsCompiled,
            notifications,
            errors);
    }

    /// <summary>
    /// Lightweight assignment-only poll across registered courses (1 HTML fetch per course)
    /// to detect new deadlines, due-date changes, and newly posted grades.
    /// </summary>
    public async Task<IReadOnlyList<AppNotification>> FastPollAssignmentsAsync(
        string basinRoot,
        CancellationToken ct = default,
        bool allowInteractiveAuth = true)
    {
        var notifications = new List<AppNotification>();
        var config = await _basinService.InitializeOrOpenAsync(basinRoot, ct: ct);
        await _elmsClient.EnsureConnectedAsync(
            new AuthOptions { BaseUrl = config.ElmsBaseUrl, AllowInteractiveBrowser = allowInteractiveAuth },
            ct);

        var courses = (await _basinService.LoadCoursesAsync(basinRoot, ct))
            .Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.CourseId))
            .ToList();

        var pollState = await _basinService.LoadPollStateAsync(basinRoot, ct);

        foreach (var course in courses)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var assignments = await _elmsClient.GetAssignmentsAsync(course.CourseId!, ct);
                await UpsertCourseAssignmentsAsync(basinRoot, course, assignments, pollState, notifications, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Fast poll failed for course {Course}", course.Title);
            }
        }

        pollState.LastFastPollAt = DateTimeOffset.UtcNow;
        await _basinService.SavePollStateAsync(basinRoot, pollState, ct);
        return notifications;
    }

    public static async Task<int> UpsertCourseAssignmentsAsync(
        string basinRoot,
        BasinCourseEntry course,
        IReadOnlyList<ElmsAssignment> assignments,
        PollState pollState,
        List<AppNotification> notifications,
        CancellationToken ct,
        IReadOnlyList<ElmsLesson>? lessons = null)
    {
        var tasksPath = BasinService.GetCourseTasksPath(basinRoot, course.FolderName);
        var existingTasks = await MarkdownTaskCodec.LoadFileAsync(tasksPath, course.FolderName, ct);
        var courseTermMap = BasinService.LoadCourseTermMap(basinRoot, course.FolderName);
        var courseDir = Path.Combine(basinRoot, course.FolderName);
        var syllabusPdfPath = Pdf.SyllabusTermParser.FindSyllabusPdfPath(courseDir);

        var incomingTasks = new List<TaskItem>();
        foreach (var a in assignments)
        {
            var isDone = a.Status is AssignmentStatus.Submitted or AssignmentStatus.Graded;
            var noteSubfolder = isDone ? "completed" : "ongoing";
            var noteFileName = FilenameSanitizer.SanitizeFileName(a.Title, ".md");
            var assignmentStem = FilenameSanitizer.SanitizeFileName(a.Title, "").TrimEnd('.');

            var instructionFiles = a.Attachments
                .Where(r => r.Kind == ElmsResourceKind.AssignmentAttachment)
                .Select(r => r.RelativeTargetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (lessons is not null && !string.IsNullOrWhiteSpace(assignmentStem))
            {
                foreach (var lesson in lessons)
                {
                    bool lessonMatches = lesson.Title.Equals(a.Title, StringComparison.OrdinalIgnoreCase) ||
                                         lesson.SanitizedFolderName.Equals(assignmentStem, StringComparison.OrdinalIgnoreCase);

                    foreach (var res in lesson.Resources)
                    {
                        var resStem = Path.GetFileNameWithoutExtension(res.RelativeTargetPath).TrimEnd('_');
                        if (lessonMatches || resStem.Equals(assignmentStem, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!instructionFiles.Contains(res.RelativeTargetPath, StringComparer.OrdinalIgnoreCase))
                                instructionFiles.Add(res.RelativeTargetPath);
                        }
                    }
                }
            }

            var submissionFiles = a.SubmissionInfo.SubmittedFiles
                .Where(r => r.Kind == ElmsResourceKind.SubmittedArtifact)
                .Select(r => r.RelativeTargetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var linkedFiles = new List<string>();

            var resolvedTerm = BasinService.ResolveAcademicTerm(
                courseTermMap,
                a.TermHeader,
                a.Title,
                a.ParentModuleTitle);
            if (resolvedTerm is null && syllabusPdfPath is not null)
            {
                resolvedTerm = Pdf.SyllabusTermParser.InferTaskTermFromSyllabus(
                    syllabusPdfPath,
                    courseDir,
                    a.Title,
                    a.ParentModuleTitle);
            }

            var inferredTags = BasinService.InferAcademicTags(a.Title, resolvedTerm, a.Kind.ToString());

            var priority = ComputeInitialPriority(a);
            var task = new TaskItem
            {
                Id = $"lms-{a.LmsId}",
                Title = a.Title,
                IsCompleted = isDone,
                DueDate = a.DueDate.HasValue ? DateOnly.FromDateTime(a.DueDate.Value.LocalDateTime) : null,
                DueTime = a.DueDate.HasValue ? TimeOnly.FromDateTime(a.DueDate.Value.LocalDateTime) : null,
                CompletedDate = isDone ? DateOnly.FromDateTime(DateTime.Today) : null,
                Priority = priority,
                CourseFolder = course.FolderName,
                CourseAlias = course.Alias,
                Term = resolvedTerm,
                Section = a.ParentModuleTitle,
                LmsKind = a.Kind != ElmsAssignmentKind.Unknown ? a.Kind.ToString() : null,
                WeightPercent = a.WeightPercent,
                Labels = inferredTags,
                InstructionFiles = instructionFiles,
                SubmissionFiles = submissionFiles,
                LinkedFiles = linkedFiles,
                NotePath = $"{noteSubfolder}/{noteFileName}",
                LmsId = a.LmsId,
                LmsStatus = a.Status.ToString(),
                LmsScore = a.Score,
                LmsUrl = a.Url,
                SourceMarkdownPath = tasksPath
            };
            incomingTasks.Add(task);

            DiffPollSnapshot(course, a, pollState, notifications);
        }

        var merged = MarkdownTaskCodec.MergeLmsTasks(existingTasks, incomingTasks);
        foreach (var t in merged)
        {
            BasinService.EnrichTaskMetadataAndFiles(basinRoot, course, t, courseTermMap);
        }
        await MarkdownTaskCodec.SaveDocumentAsync(tasksPath, merged, course.Title, ct);
        return incomingTasks.Count;
    }

    private static void DiffPollSnapshot(
        BasinCourseEntry course,
        ElmsAssignment assignment,
        PollState pollState,
        List<AppNotification> notifications)
    {
        if (!pollState.AssignmentsByLmsId.TryGetValue(assignment.LmsId, out var prev))
        {
            if (assignment.Status == AssignmentStatus.Pending)
            {
                notifications.Add(new AppNotification(
                    NotificationKind.NewAssignment,
                    $"New Assignment: {assignment.Title}",
                    $"[{course.Title}] Due {assignment.DueDate?.ToString("MMM d, h:mm tt") ?? "TBD"}",
                    course.FolderName,
                    assignment.Url));
            }
        }
        else
        {
            if (prev.DueDate != assignment.DueDate && assignment.DueDate.HasValue)
            {
                notifications.Add(new AppNotification(
                    NotificationKind.DueDateChanged,
                    $"Due Date Updated: {assignment.Title}",
                    $"[{course.Title}] New due date: {assignment.DueDate.Value:MMM d, h:mm tt}",
                    course.FolderName,
                    assignment.Url));
            }

            if (assignment.Status == AssignmentStatus.Graded &&
                !string.Equals(prev.Status, AssignmentStatus.Graded.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                notifications.Add(new AppNotification(
                    NotificationKind.GradePosted,
                    $"Grade Posted: {assignment.Title}",
                    $"[{course.Title}] Score: {assignment.Score ?? "Graded"}",
                    course.FolderName,
                    assignment.Url));
            }
        }

        if (assignment.Status == AssignmentStatus.Pending && assignment.DueDate.HasValue)
        {
            var remaining = assignment.DueDate.Value - DateTimeOffset.Now;
            if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(24))
            {
                notifications.Add(new AppNotification(
                    NotificationKind.DueSoon,
                    $"Due Soon: {assignment.Title}",
                    $"[{course.Title}] Due in {(int)remaining.TotalHours}h ({assignment.DueDate.Value:MMM d, h:mm tt})",
                    course.FolderName,
                    assignment.Url));
            }
        }

        pollState.AssignmentsByLmsId[assignment.LmsId] = new PollAssignmentSnapshot
        {
            LmsId = assignment.LmsId,
            CourseId = assignment.CourseId,
            Title = assignment.Title,
            Status = assignment.Status.ToString(),
            Score = assignment.Score,
            DueDate = assignment.DueDate,
            Url = assignment.Url
        };
    }

    private static TaskPriority ComputeInitialPriority(ElmsAssignment a)
    {
        if (a.Status == AssignmentStatus.Missing)
            return TaskPriority.P1;

        if (a.DueDate.HasValue)
        {
            var days = (a.DueDate.Value.Date - DateTime.Today).TotalDays;
            if (days <= 2) return TaskPriority.P1;
            if (days <= 7) return TaskPriority.P2;
            if (days <= 14) return TaskPriority.P3;
        }

        return TaskPriority.P4;
    }
}
