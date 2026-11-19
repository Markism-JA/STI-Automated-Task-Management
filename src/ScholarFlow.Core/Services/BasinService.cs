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
        var courses = await LoadCoursesAsync(basinRoot, ct);
        return await Database.ScholarFlowDatabase.SyncAndLoadAllTasksAsync(basinRoot, courses, ct);
    }

    public static Dictionary<string, List<string>> LoadCourseTermMap(string basinRoot, string courseFolder)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(basinRoot, courseFolder, "term.json");
        if (!File.Exists(path))
            return result;

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (parsed is not null)
            {
                foreach (var (k, v) in parsed)
                    result[k] = v ?? [];
            }
        }
        catch
        {
            // Ignore malformed term.json
        }

        return result;
    }

    public static string? ResolveAcademicTerm(
        Dictionary<string, List<string>>? courseTermMap,
        string? explicitTerm,
        string? title,
        string? parentModuleOrSection = null)
    {
        var norm = TaskItem.NormalizeTerm(explicitTerm)
                   ?? TaskItem.NormalizeTerm(title)
                   ?? TaskItem.NormalizeTerm(parentModuleOrSection);
        if (norm is not null)
            return norm;

        // Extract leading 2-digit module prefix (e.g. "04 Quiz 1" -> "04", "05 Task Performance 1" -> "05")
        var prefix = ExtractTwoDigitModulePrefix(title) ?? ExtractTwoDigitModulePrefix(parentModuleOrSection);
        if (prefix is null)
            return null;

        if (courseTermMap is not null)
        {
            foreach (var (termKey, prefixes) in courseTermMap)
            {
                if (prefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                    return TaskItem.NormalizeTerm(termKey) ?? termKey;
            }
        }

        if (int.TryParse(prefix, out int num))
        {
            return num switch
            {
                <= 2 => "Prelims",
                <= 4 => "Midterms",
                <= 6 => "Prefinals",
                _ => "Finals"
            };
        }

        return null;
    }

    private static string? ExtractTwoDigitModulePrefix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var m = System.Text.RegularExpressions.Regex.Match(text, @"\b(0[1-9]|1[0-9])\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static List<string> InferAcademicTags(string? title, string? term, string? lmsKind)
    {
        var tags = new List<string>();

        var normTerm = TaskItem.NormalizeTerm(term) ?? TaskItem.NormalizeTerm(title);
        if (normTerm is not null)
            tags.Add(normTerm.ToLowerInvariant());

        var t = (title ?? string.Empty).ToLowerInvariant();
        if (t.Contains("quiz") || string.Equals(lmsKind, "Quiz", StringComparison.OrdinalIgnoreCase))
            tags.Add("quiz");
        else if (t.Contains("task performance") || t.Contains("performance task") || t.Contains("tp"))
            tags.Add("tp");
        else if (t.Contains("laboratory") || t.Contains("lab "))
            tags.Add("lab");
        else if (t.Contains("seatwork") || t.Contains("sw "))
            tags.Add("seatwork");
        else if (t.Contains("exam"))
            tags.Add("exam");
        else if (t.Contains("activity"))
            tags.Add("activity");

        if (!string.IsNullOrWhiteSpace(lmsKind))
        {
            var kindLower = lmsKind.ToLowerInvariant();
            if (kindLower is not ("unknown" or "quiz") && !tags.Contains(kindLower))
                tags.Add(kindLower);
        }

        return tags;
    }

    public static bool EnrichTaskMetadataAndFiles(
        string basinRoot,
        BasinCourseEntry course,
        TaskItem task,
        Dictionary<string, List<string>>? termMap = null)
    {
        bool changed = false;
        termMap ??= LoadCourseTermMap(basinRoot, course.FolderName);

        var resolvedTerm = ResolveAcademicTerm(termMap, task.Term, task.Title, task.Section);
        if (resolvedTerm is not null && !string.Equals(task.Term, resolvedTerm, StringComparison.OrdinalIgnoreCase))
        {
            task.Term = resolvedTerm;
            changed = true;
        }

        foreach (var tag in InferAcademicTags(task.Title, task.Term, task.LmsKind))
        {
            // Remove noisy legacy "@freeform" / "@dropbox" tag if a cleaner activity tag exists, or keep and add
            if (!task.Labels.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                task.Labels.Add(tag);
                changed = true;
            }
        }

        var courseDir = Path.Combine(basinRoot, course.FolderName);
        if (!Directory.Exists(courseDir))
            return changed;

        var taskStem = ScholarFlow.Elms.Parsing.FilenameSanitizer.SanitizeFileName(task.Title, "").TrimEnd('.');
        if (string.IsNullOrWhiteSpace(taskStem))
            return changed;

        // Discover matching instruction worksheet PDFs in module subdirectories or ongoing/
        foreach (var dir in Directory.GetDirectories(courseDir))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.StartsWith('.'))
                continue;

            bool isCompletedDir = dirName.Equals("completed", StringComparison.OrdinalIgnoreCase);
            bool isOngoingDir = dirName.Equals("ongoing", StringComparison.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileStem = Path.GetFileNameWithoutExtension(file).TrimEnd('_');
                bool isExactWorksheetMatch = fileStem.Equals(taskStem, StringComparison.OrdinalIgnoreCase) ||
                                             dirName.Equals(taskStem, StringComparison.OrdinalIgnoreCase);

                var relPath = Path.GetRelativePath(courseDir, file).Replace('\\', '/');
                if (isExactWorksheetMatch && ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (!task.InstructionFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
                    {
                        task.InstructionFiles.Add(relPath);
                        changed = true;
                    }
                    if (task.SubmissionFiles.RemoveAll(s => s.Equals(relPath, StringComparison.OrdinalIgnoreCase)) > 0)
                    {
                        changed = true;
                    }
                }
                else if (isCompletedDir && (isExactWorksheetMatch || MatchesDeliverableSignature(task.Title, fileStem)))
                {
                    if (!task.InstructionFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase) &&
                        !task.SubmissionFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
                    {
                        task.SubmissionFiles.Add(relPath);
                        changed = true;
                    }
                }
            }
        }

        // Ensure SubmissionFiles never duplicates an InstructionFile
        if (task.SubmissionFiles.RemoveAll(s => task.InstructionFiles.Contains(s, StringComparer.OrdinalIgnoreCase)) > 0)
            changed = true;

        task.EnsureChildTasksSynced();
        return changed;
    }

    private static bool MatchesDeliverableSignature(string taskTitle, string candidateFileStem)
    {
        var taskMod = ExtractTwoDigitModulePrefix(taskTitle);
        var fileMod = ExtractTwoDigitModulePrefix(candidateFileStem);
        if (taskMod is null || fileMod is null || !taskMod.Equals(fileMod, StringComparison.OrdinalIgnoreCase))
            return false;

        static string ExtractKind(string s)
        {
            var lower = s.ToLowerInvariant();
            if (lower.Contains("task performance") || lower.Contains("performance task") ||
                System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?:^|[_\-\s]|\d)tp(?:\d|[_\-\s]|$)"))
                return "tp";
            if (lower.Contains("laboratory") ||
                System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?:^|[_\-\s]|\d)lab(?:\d|[_\-\s]|$)"))
                return "lab";
            if (lower.Contains("activity") ||
                System.Text.RegularExpressions.Regex.IsMatch(lower, @"(?:^|[_\-\s]|\d)act(?:\d|[_\-\s]|$)"))
                return "act";
            if (lower.Contains("quiz"))
                return "quiz";
            return "";
        }

        var taskKind = ExtractKind(taskTitle);
        var fileKind = ExtractKind(candidateFileStem);
        if (string.IsNullOrEmpty(taskKind) || !taskKind.Equals(fileKind, StringComparison.Ordinal))
            return false;

        // Compare trailing activity number (e.g. "1" in "05 Task Performance 1" vs "05 TP 1")
        var taskNums = System.Text.RegularExpressions.Regex.Matches(taskTitle, @"\d+");
        var fileNums = System.Text.RegularExpressions.Regex.Matches(candidateFileStem, @"\d+");
        if (taskNums.Count >= 2 && fileNums.Count >= 2)
        {
            int tNum = int.Parse(taskNums[^1].Value);
            // Find any non-module number in fileNums that matches tNum
            for (int i = 1; i < fileNums.Count; i++)
            {
                if (int.TryParse(fileNums[i].Value, out int fNum) && fNum == tNum)
                    return true;
            }
            return false;
        }

        return true;
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
