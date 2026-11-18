using System.Text.Json;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Core.Services;

/// <summary>
/// Manages an Obsidian-style Basin folder (.scholarflow/ config, courses, inbox.md, and per-course tasks.md).
/// </summary>
public sealed class BasinService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetMetaDir(string basinRoot) => Path.Combine(basinRoot, ".scholarflow");
    public static string GetBasinConfigPath(string basinRoot) => Path.Combine(GetMetaDir(basinRoot), "basin.json");
    public static string GetCoursesPath(string basinRoot) => Path.Combine(GetMetaDir(basinRoot), "courses.json");
    public static string GetInboxPath(string basinRoot) => Path.Combine(GetMetaDir(basinRoot), "inbox.md");
    public static string GetPollStatePath(string basinRoot) => Path.Combine(GetMetaDir(basinRoot), "poll_state.json");
    public static string GetCourseTasksPath(string basinRoot, string courseFolder) => Path.Combine(basinRoot, courseFolder, "tasks.md");

    public async Task<BasinConfig> InitializeOrOpenAsync(string basinRoot, string? name = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(basinRoot);
        Directory.CreateDirectory(GetMetaDir(basinRoot));

        var configPath = GetBasinConfigPath(basinRoot);
        BasinConfig config;
        if (File.Exists(configPath))
        {
            config = await LoadJsonAsync<BasinConfig>(configPath, ct) ?? new BasinConfig();
        }
        else
        {
            config = new BasinConfig { Name = name ?? Path.GetFileName(Path.GetFullPath(basinRoot)) };
            await SaveJsonAsync(configPath, config, ct);
        }

        var inboxPath = GetInboxPath(basinRoot);
        if (!File.Exists(inboxPath))
            await MarkdownTaskCodec.SaveDocumentAsync(inboxPath, [], "Inbox", ct);

        await DeduplicateAndMergeCoursesAsync(basinRoot, ct);
        return config;
    }

    public async Task<List<BasinCourseEntry>> LoadCoursesAsync(string basinRoot, CancellationToken ct = default)
        => await LoadJsonAsync<List<BasinCourseEntry>>(GetCoursesPath(basinRoot), ct) ?? [];

    public Task SaveCoursesAsync(string basinRoot, IEnumerable<BasinCourseEntry> courses, CancellationToken ct = default)
        => SaveJsonAsync(GetCoursesPath(basinRoot), courses.ToList(), ct);

    public async Task<PollState> LoadPollStateAsync(string basinRoot, CancellationToken ct = default)
        => await LoadJsonAsync<PollState>(GetPollStatePath(basinRoot), ct) ?? new PollState();

    public Task SavePollStateAsync(string basinRoot, PollState state, CancellationToken ct = default)
        => SaveJsonAsync(GetPollStatePath(basinRoot), state, ct);

    /// <summary>
    /// Computes a normalized comparison key for matching legacy course folders (e.g. Platform_Technology_(Operating_System))
    /// against eLMS titles (e.g. Platform Technology (Operating Systems), Network Technology 1, Information Assurance and Security (Data Privacy)).
    /// </summary>
    public static string NormalizeCourseMatchKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Replace('_', ' ').ToLowerInvariant();
        // Strip parenthesized qualifiers like "(data privacy)" when comparing base names, or normalize words
        var tokens = System.Text.RegularExpressions.Regex.Matches(s, @"[a-z]+")
            .Select(m => m.Value)
            .Where(w => w is not ("and" or "in" or "of" or "the" or "for"))
            .Select(w => w.Length > 4 && w.EndsWith('s') ? w[..^1] : w)
            .ToList();

        return string.Join(' ', tokens);
    }

    public static bool IsSameCourseFuzzy(string? nameA, string? nameB)
    {
        var keyA = NormalizeCourseMatchKey(nameA);
        var keyB = NormalizeCourseMatchKey(nameB);
        if (string.IsNullOrEmpty(keyA) || string.IsNullOrEmpty(keyB))
            return false;

        if (keyA == keyB)
            return true;

        // Prefix match when one course includes an extra parenthesized subtitle like "(Data Privacy)"
        var shorter = keyA.Length <= keyB.Length ? keyA : keyB;
        var longer = keyA.Length <= keyB.Length ? keyB : keyA;
        return SplitWordCount(shorter) >= 3 && longer.StartsWith(shorter + " ", StringComparison.Ordinal);
    }

    private static int SplitWordCount(string s)
        => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Merges duplicate course entries in courses.json (where a legacy folder without CourseId coexists with a newly
    /// created eLMS folder), copying any newer module files and LMS tasks into the canonical folder and removing the duplicate directory.
    /// </summary>
    public async Task<List<BasinCourseEntry>> DeduplicateAndMergeCoursesAsync(string basinRoot, CancellationToken ct = default)
    {
        var courses = await LoadCoursesAsync(basinRoot, ct);
        if (courses.Count <= 1)
            return courses;

        var deduplicated = new List<BasinCourseEntry>();
        bool changed = false;

        foreach (var entry in courses)
        {
            var existingMatch = deduplicated.FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(c.CourseId) && !string.IsNullOrWhiteSpace(entry.CourseId) && c.CourseId == entry.CourseId) ||
                c.FolderName.Equals(entry.FolderName, StringComparison.OrdinalIgnoreCase) ||
                ((string.IsNullOrWhiteSpace(c.CourseId) || string.IsNullOrWhiteSpace(entry.CourseId)) &&
                 (IsSameCourseFuzzy(c.FolderName, entry.FolderName) || IsSameCourseFuzzy(c.Title, entry.Title))));

            if (existingMatch is null)
            {
                deduplicated.Add(entry);
                continue;
            }

            changed = true;
            // Keep existingMatch's original FolderName & Alias, merge eLMS metadata from entry
            var dupFolder = entry.FolderName;
            existingMatch.CourseId ??= entry.CourseId;
            existingMatch.CourseCode ??= entry.CourseCode;
            existingMatch.SectionCode ??= entry.SectionCode;
            existingMatch.TermSuffix ??= entry.TermSuffix;
            existingMatch.CourseUrl ??= entry.CourseUrl;

            if (!existingMatch.FolderName.Equals(dupFolder, StringComparison.OrdinalIgnoreCase))
            {
                await MergeCourseDirectoriesAsync(
                    basinRoot,
                    canonicalEntry: existingMatch,
                    duplicateFolderName: dupFolder,
                    ct);
            }
        }

        if (changed)
        {
            await SaveCoursesAsync(basinRoot, deduplicated, ct);
        }

        return deduplicated;
    }

    private static async Task MergeCourseDirectoriesAsync(
        string basinRoot,
        BasinCourseEntry canonicalEntry,
        string duplicateFolderName,
        CancellationToken ct)
    {
        var canonicalDir = Path.Combine(basinRoot, canonicalEntry.FolderName);
        var dupDir = Path.Combine(basinRoot, duplicateFolderName);
        if (!Directory.Exists(dupDir))
            return;

        Directory.CreateDirectory(canonicalDir);

        // 1. Merge tasks.md if duplicate has tasks
        var dupTasksPath = Path.Combine(dupDir, "tasks.md");
        var canonTasksPath = Path.Combine(canonicalDir, "tasks.md");
        if (File.Exists(dupTasksPath))
        {
            var canonTasks = await MarkdownTaskCodec.LoadFileAsync(canonTasksPath, canonicalEntry.FolderName, ct);
            var dupTasks = await MarkdownTaskCodec.LoadFileAsync(dupTasksPath, canonicalEntry.FolderName, ct);
            foreach (var t in dupTasks)
            {
                t.CourseFolder = canonicalEntry.FolderName;
                t.CourseAlias = canonicalEntry.Alias;
                t.SourceMarkdownPath = canonTasksPath;
            }
            var merged = MarkdownTaskCodec.MergeLmsTasks(canonTasks, dupTasks);
            await MarkdownTaskCodec.SaveDocumentAsync(canonTasksPath, merged, canonicalEntry.Title, ct);
        }

        // 2. Copy all subdirectories/files (new handouts, term.json, ongoing/, completed/) from dupDir into canonicalDir
        foreach (var file in Directory.GetFiles(dupDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("tasks.md", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("_Master.pdf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rel = Path.GetRelativePath(dupDir, file);
            var dest = Path.Combine(canonicalDir, rel);
            var destParent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destParent))
                Directory.CreateDirectory(destParent);

            if (!File.Exists(dest) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(dest))
            {
                File.Copy(file, dest, overwrite: true);
            }
        }

        // 3. Remove duplicate directory
        try
        {
            Directory.Delete(dupDir, recursive: true);
        }
        catch
        {
            // Ignore if locked
        }
    }

    /// <summary>
    /// Registers or updates discovered eLMS courses in courses.json, generating unique short aliases (#sqa, #os).
    /// </summary>
    public async Task<List<BasinCourseEntry>> UpsertCoursesAsync(
        string basinRoot,
        IEnumerable<ElmsCourse> discoveredCourses,
        CancellationToken ct = default)
    {
        var existing = await DeduplicateAndMergeCoursesAsync(basinRoot, ct);
        var usedAliases = existing.Select(c => c.Alias).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        foreach (var elms in discoveredCourses)
        {
            var match = existing.FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(c.CourseId) && c.CourseId == elms.CourseId) ||
                c.FolderName.Equals(elms.SanitizedFolderName, StringComparison.OrdinalIgnoreCase) ||
                IsSameCourseFuzzy(c.FolderName, elms.SanitizedFolderName) ||
                IsSameCourseFuzzy(c.Title, elms.Title));

            if (match is not null)
            {
                match.CourseId = elms.CourseId;
                match.CourseCode = elms.CourseCode ?? match.CourseCode;
                match.SectionCode = elms.TermOrSection ?? match.SectionCode;
                match.TermSuffix = elms.TermSuffix ?? match.TermSuffix;
                match.CourseUrl = elms.CourseUrl;
                if (string.IsNullOrWhiteSpace(match.Alias))
                {
                    match.Alias = CourseAliasGenerator.Generate(elms.Title, usedAliases);
                    usedAliases.Add(match.Alias);
                }
            }
            else
            {
                var alias = CourseAliasGenerator.Generate(elms.Title, usedAliases);
                usedAliases.Add(alias);
                match = new BasinCourseEntry
                {
                    CourseId = elms.CourseId,
                    Title = elms.Title,
                    FolderName = elms.SanitizedFolderName,
                    Alias = alias,
                    CourseCode = elms.CourseCode,
                    SectionCode = elms.TermOrSection,
                    TermSuffix = elms.TermSuffix,
                    CourseUrl = elms.CourseUrl
                };
                existing.Add(match);
            }

            var courseDir = Path.Combine(basinRoot, match.FolderName);
            Directory.CreateDirectory(Path.Combine(courseDir, "ongoing"));
            Directory.CreateDirectory(Path.Combine(courseDir, "completed"));
        }

        await SaveCoursesAsync(basinRoot, existing, ct);
        return existing;
    }

    public async Task<IReadOnlyList<TaskItem>> LoadAllTasksAsync(string basinRoot, CancellationToken ct = default)
    {
        var all = new List<TaskItem>();
        var inbox = await MarkdownTaskCodec.LoadFileAsync(GetInboxPath(basinRoot), defaultCourseFolder: null, ct);
        all.AddRange(inbox);

        var courses = await LoadCoursesAsync(basinRoot, ct);
        foreach (var course in courses.Where(c => !c.IsArchived))
        {
            var tasksPath = GetCourseTasksPath(basinRoot, course.FolderName);
            var courseTasks = await MarkdownTaskCodec.LoadFileAsync(tasksPath, course.FolderName, ct);
            foreach (var t in courseTasks)
            {
                t.CourseFolder ??= course.FolderName;
                t.CourseAlias ??= course.Alias;
            }
            all.AddRange(courseTasks);
        }

        return all;
    }

    public async Task<TaskItem> AddQuickTaskAsync(
        string basinRoot,
        string quickAddInput,
        DateOnly? todayOverride = null,
        CancellationToken ct = default)
    {
        var courses = await LoadCoursesAsync(basinRoot, ct);
        var parsed = QuickAddParser.Parse(quickAddInput, courses, todayOverride);
        var task = parsed.Task;

        if (string.IsNullOrWhiteSpace(task.Title))
            throw new ArgumentException("Task title cannot be empty.", nameof(quickAddInput));

        var targetPath = string.IsNullOrWhiteSpace(task.CourseFolder)
            ? GetInboxPath(basinRoot)
            : GetCourseTasksPath(basinRoot, task.CourseFolder);

        var header = string.IsNullOrWhiteSpace(task.CourseFolder)
            ? "Inbox"
            : courses.FirstOrDefault(c => c.FolderName.Equals(task.CourseFolder, StringComparison.OrdinalIgnoreCase))?.Title ?? task.CourseFolder;

        task.SourceMarkdownPath = targetPath;

        var existing = (await MarkdownTaskCodec.LoadFileAsync(targetPath, task.CourseFolder, ct)).ToList();
        existing.Add(task);
        await MarkdownTaskCodec.SaveDocumentAsync(targetPath, existing, header, ct);

        return task;
    }

    internal static async Task<T?> LoadJsonAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return default;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    internal static async Task SaveJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = $"{path}.tmp.{Guid.NewGuid():N}";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
        }

        File.Move(temp, path, overwrite: true);
    }
}
