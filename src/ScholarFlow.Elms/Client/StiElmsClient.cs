using System.Net;
using Microsoft.Extensions.Logging;
using ScholarFlow.Elms.Abstractions;
using ScholarFlow.Elms.Auth;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Parsing;

namespace ScholarFlow.Elms.Client;

/// <summary>
/// Concurrent HttpClient + AngleSharp implementation of <see cref="IElmsClient"/> backed by <see cref="IElmsAuthProvider"/>.
/// </summary>
public sealed class StiElmsClient : IElmsClient
{
    private readonly IElmsAuthProvider _authProvider;
    private readonly ILogger<StiElmsClient>? _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private AuthOptions _authOptions = new();
    private ElmsSession? _session;
    private HttpClient? _httpClient;
    private HttpClientHandler? _httpHandler;

    public StiElmsClient(IElmsAuthProvider? authProvider = null, ILogger<StiElmsClient>? logger = null)
    {
        _authProvider = authProvider ?? new PlaywrightAuthProvider();
        _logger = logger;
    }

    public HttpClient HttpClient => _httpClient ?? throw new InvalidOperationException("Client is not connected. Call EnsureConnectedAsync first.");
    public string BaseUrl => _authOptions.BaseUrl.TrimEnd('/');

    public async Task EnsureConnectedAsync(AuthOptions? options = null, CancellationToken ct = default)
    {
        if (_session is not null && _httpClient is not null && options?.ForceInteractive != true)
            return;

        await _authLock.WaitAsync(ct);
        try
        {
            if (options is not null)
                _authOptions = options;

            if (_session is not null && _httpClient is not null && options?.ForceInteractive != true)
                return;

            _session = await _authProvider.EnsureAuthenticatedAsync(_authOptions, ct);
            RebuildHttpClient(_session);
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<IReadOnlyList<ElmsCourse>> GetEnrolledCoursesAsync(
        CourseFilter? filter = null,
        bool includeCompleted = false,
        bool includeAdministrative = false,
        CancellationToken ct = default)
    {
        var html = await FetchAuthenticatedHtmlAsync("/user_dashboard", ct);
        var courses = DashboardParser.ParseEnrolledCourses(html, BaseUrl, includeCompleted, includeAdministrative);
        return filter is null ? courses : filter.Apply(courses);
    }

    public async Task<ElmsCourseSnapshot> GetCourseSnapshotAsync(
        ElmsCourse course,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        progress?.Report(new SyncProgress("Discover", $"Scanning course: {course.Title}", CourseTitle: course.Title));

        var courseHtml = await FetchAuthenticatedHtmlAsync(course.CourseUrl, ct);
        var parsedCourse = CoursePageParser.Parse(courseHtml, course, BaseUrl);

        if (parsedCourse.Modules.Count == 0 && parsedCourse.GeneralResources.Count == 0)
        {
            warnings.Add($"No modules or resources discovered on {course.CourseUrl}; course may be empty or use non-standard layout.");
        }

        // Build target list of handout/lesson pages to scrape (skipping SCORM Online Presentations and embedded assignments)
        var lessonTargets = new List<(string Url, string Title, string? TermHeader)>();
        foreach (var mod in parsedCourse.Modules)
        {
            if (mod.ModuleSections.Count > 0)
            {
                foreach (var sec in mod.ModuleSections.Where(s => s.Kind is ElmsSectionKind.Handout or ElmsSectionKind.Other))
                {
                    lessonTargets.Add((sec.Url, sec.Title, mod.TermHeader));
                }
            }
            else if (!mod.IsScormOrExternal && !mod.LessonUrl.Contains("scorm_player.html", StringComparison.OrdinalIgnoreCase))
            {
                lessonTargets.Add((mod.LessonUrl, mod.Title, mod.TermHeader));
            }
        }

        using var throttle = new SemaphoreSlim(4, 4);
        var lessonTasks = lessonTargets.Select(async target =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                return await GetLessonAsync(target.Url, course.CourseId, target.TermHeader, target.Title, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (warnings)
                    warnings.Add($"Failed to fetch lesson '{target.Title}' ({target.Url}): {ex.Message}");
                return null;
            }
            finally
            {
                throttle.Release();
            }
        });

        var fetchedLessons = await Task.WhenAll(lessonTasks);
        var lessons = fetchedLessons.Where(l => l is not null).Select(l => l!).ToList();

        progress?.Report(new SyncProgress("Assignments", $"Scanning assignments for {course.Title}", CourseTitle: course.Title));
        var assignments = await FetchCourseAssignmentsInternalAsync(course.CourseId, parsedCourse, throttle, warnings, ct);

        return new ElmsCourseSnapshot(
            Course: course,
            GeneralResources: parsedCourse.GeneralResources,
            Modules: parsedCourse.Modules,
            Lessons: lessons,
            Assignments: assignments,
            Warnings: warnings,
            CapturedAt: DateTimeOffset.UtcNow);
    }

    public async Task<ElmsLesson> GetLessonAsync(
        string lessonUrl,
        string courseId,
        string? termHeader = null,
        string? fallbackTitle = null,
        CancellationToken ct = default)
    {
        var html = await FetchAuthenticatedHtmlAsync(lessonUrl, ct);
        return LessonParser.Parse(html, lessonUrl, courseId, termHeader, BaseUrl, fallbackTitle);
    }

    public async Task<IReadOnlyList<ElmsAssignment>> GetAssignmentsAsync(string courseId, CancellationToken ct = default)
    {
        var courseUrl = $"{BaseUrl}/student_class/show/{courseId}";
        var courseHtml = await FetchAuthenticatedHtmlAsync(courseUrl, ct);
        var parsed = CoursePageParser.Parse(courseHtml, new ElmsCourse(courseId, courseId, courseUrl, courseId), BaseUrl);

        using var throttle = new SemaphoreSlim(4, 4);
        var warnings = new List<string>();
        return await FetchCourseAssignmentsInternalAsync(courseId, parsed, throttle, warnings, ct);
    }

    private async Task<IReadOnlyList<ElmsAssignment>> FetchCourseAssignmentsInternalAsync(
        string courseId,
        ParsedCoursePage parsedCourse,
        SemaphoreSlim throttle,
        List<string> warnings,
        CancellationToken ct)
    {
        var summariesByUrl = new Dictionary<string, ElmsAssignmentSummary>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var listHtml = await FetchAuthenticatedHtmlAsync(parsedCourse.AssignmentsListUrl, ct);
            foreach (var summary in AssignmentsListParser.Parse(listHtml, courseId, BaseUrl))
            {
                summariesByUrl[ElmsUrlCanonicalizer.GetCanonicalUrl(summary.Url)] = summary;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (warnings)
                warnings.Add($"Could not fetch assignments list ({parsedCourse.AssignmentsListUrl}): {ex.Message}");
        }

        var targetUrls = summariesByUrl.Values.Select(s => s.Url)
            .Concat(parsedCourse.AssignmentUrls)
            .DistinctBy(ElmsUrlCanonicalizer.GetCanonicalUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tasks = targetUrls.Select(async url =>
        {
            summariesByUrl.TryGetValue(ElmsUrlCanonicalizer.GetCanonicalUrl(url), out var summary);

            await throttle.WaitAsync(ct);
            try
            {
                var html = await FetchAuthenticatedHtmlAsync(url, ct);
                var assignment = AssignmentParser.Parse(html, url, courseId, BaseUrl, summary);
                if (assignment is null)
                    return SummaryToFallbackAssignment(summary);

                var subUrl = assignment.SubmissionInfo.SubmissionDetailsUrl;
                if (!string.IsNullOrWhiteSpace(subUrl))
                {
                    try
                    {
                        var subHtml = await FetchAuthenticatedHtmlAsync(subUrl, ct);
                        var submittedFiles = AssignmentParser.ParseSubmissionFiles(subHtml, courseId, assignment.Title, BaseUrl);
                        if (submittedFiles.Count > 0)
                        {
                            assignment = assignment with
                            {
                                Submission = assignment.SubmissionInfo with { SubmittedFiles = submittedFiles }
                            };
                        }
                    }
                    catch (Exception subEx) when (subEx is not OperationCanceledException)
                    {
                        lock (warnings)
                            warnings.Add($"Could not fetch submission details ({subUrl}): {subEx.Message}");
                    }
                }

                return assignment;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (warnings)
                    warnings.Add($"Failed to fetch assignment ({url}): {ex.Message}");
                return SummaryToFallbackAssignment(summary);
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(a => a is not null).Select(a => a!).ToList();
    }

    private static ElmsAssignment? SummaryToFallbackAssignment(ElmsAssignmentSummary? summary)
    {
        if (summary is null)
            return null;

        return new ElmsAssignment(
            LmsId: summary.LmsId,
            CourseId: summary.CourseId,
            Title: summary.Title,
            Url: summary.Url,
            DueDate: summary.DueDate,
            Status: summary.Status,
            Score: summary.ScoreText,
            InstructionsHtml: null,
            Attachments: [],
            Kind: summary.Kind,
            ParentModuleTitle: summary.ParentModuleTitle,
            TermHeader: summary.TermHeader,
            WeightPercent: summary.WeightPercent);
    }

    public async Task<string> FetchAuthenticatedHtmlAsync(string urlOrPath, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct: ct);
        var targetUrl = HtmlParsingHelpers.ToAbsoluteUrl(urlOrPath, BaseUrl);

        using var response = await HttpClient.GetAsync(targetUrl, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await RefreshAndRebuildAsync(ct);
            using var retry = await HttpClient.GetAsync(targetUrl, ct);
            retry.EnsureSuccessStatusCode();
            return await retry.Content.ReadAsStringAsync(ct);
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);

        if (HtmlParsingHelpers.IsLoginRedirectHtml(html, response.RequestMessage?.RequestUri))
        {
            _logger?.LogWarning("Detected login redirect while fetching {Url}; refreshing session.", targetUrl);
            await RefreshAndRebuildAsync(ct);
            using var retry = await HttpClient.GetAsync(targetUrl, ct);
            retry.EnsureSuccessStatusCode();
            return await retry.Content.ReadAsStringAsync(ct);
        }

        return html;
    }

    private async Task RefreshAndRebuildAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            _session = await _authProvider.RefreshSessionAsync(_authOptions, ct);
            RebuildHttpClient(_session);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private void RebuildHttpClient(ElmsSession session)
    {
        _httpClient?.Dispose();
        _httpHandler?.Dispose();

        _httpHandler = PlaywrightAuthProvider.CreateCookieHandler(session);
        _httpClient = new HttpClient(_httpHandler) { Timeout = TimeSpan.FromSeconds(60) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(session.UserAgent);
    }

    public ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        _httpHandler?.Dispose();
        _authLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
