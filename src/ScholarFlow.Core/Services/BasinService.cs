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
    /// Registers or updates discovered eLMS courses in courses.json, generating unique short aliases (#sqa, #os).
    /// </summary>
    public async Task<List<BasinCourseEntry>> UpsertCoursesAsync(
        string basinRoot,
        IEnumerable<ElmsCourse> discoveredCourses,
        CancellationToken ct = default)
    {
        var existing = await LoadCoursesAsync(basinRoot, ct);
        var usedAliases = existing.Select(c => c.Alias).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        foreach (var elms in discoveredCourses)
        {
            var match = existing.FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(c.CourseId) && c.CourseId == elms.CourseId) ||
                c.FolderName.Equals(elms.SanitizedFolderName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                match.CourseId = elms.CourseId;
                match.Title = elms.Title;
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
                existing.Add(new BasinCourseEntry
                {
                    CourseId = elms.CourseId,
                    Title = elms.Title,
                    FolderName = elms.SanitizedFolderName,
                    Alias = alias,
                    CourseCode = elms.CourseCode,
                    SectionCode = elms.TermOrSection,
                    TermSuffix = elms.TermSuffix,
                    CourseUrl = elms.CourseUrl
                });
            }

            var courseDir = Path.Combine(basinRoot, elms.SanitizedFolderName);
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
