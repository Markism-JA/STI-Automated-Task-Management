using Microsoft.Extensions.Logging;
using ScholarFlow.Core.Sync;
using ScholarFlow.Elms.Auth;
using ScholarFlow.Elms.Client;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Sync;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
if (command == "ignore")
    return await RunIgnoreAsync(args[1..]);

if (command == "open")
{
    var urlOrPath = args.Length > 1 ? args[1] : "/user_dashboard";
    ElmsBrowserLauncher.LaunchAppWindow(urlOrPath);
    Console.WriteLine($"Launched standalone eLMS app window for: {urlOrPath}");
    return 0;
}

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        })
        .SetMinimumLevel(LogLevel.Information)
);

var forceInteractive = args.Contains("--interactive", StringComparer.OrdinalIgnoreCase);

var authProvider = new PlaywrightAuthProvider(loggerFactory.CreateLogger<PlaywrightAuthProvider>());
await using var client = new StiElmsClient(
    authProvider,
    loggerFactory.CreateLogger<StiElmsClient>()
);

try
{
    await client.EnsureConnectedAsync(new AuthOptions { ForceInteractive = forceInteractive });

    switch (command)
    {
        case "auth":
            Console.WriteLine("[OK] Authenticated session established and cached.");
            return 0;

        case "courses":
            return await RunCoursesAsync(client, args[1..]);

        case "inspect":
            var target = args.Length > 1 ? args[1] : "/user_dashboard";
            var html = await client.FetchAuthenticatedHtmlAsync(target);
            Console.WriteLine($"Fetched {html.Length} chars from {target}");
            var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
            Console.WriteLine($"Title: {doc.Title}");
            foreach (var link in doc.QuerySelectorAll("a[href]").Take(40))
            {
                var href = link.GetAttribute("href");
                var text = string.Join(
                    ' ',
                    link.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                );
                if (!string.IsNullOrWhiteSpace(text))
                    Console.WriteLine($"  <a> {text} => {href}");
            }
            return 0;

        case "sync":
            return await RunSyncAsync(client, args[1..]);

        default:
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintHelp();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    return 1;
}

static async Task<int> RunCoursesAsync(StiElmsClient client, string[] cmdArgs)
{
    var outDir = GetOptionValue(cmdArgs, "--out") ?? Path.GetFullPath("./STI_Sync");
    var showAll = cmdArgs.Contains("--all", StringComparer.OrdinalIgnoreCase);
    var includeAdmin =
        showAll || cmdArgs.Contains("--include-admin", StringComparer.OrdinalIgnoreCase);
    var includeCompleted =
        showAll || cmdArgs.Contains("--include-completed", StringComparer.OrdinalIgnoreCase);

    var filter = await CourseFilterStore.LoadAsync(CourseFilterStore.GetDefaultFilterPath(outDir));
    var rawCourses = await client.GetEnrolledCoursesAsync(
        filter: null,
        includeCompleted: includeCompleted,
        includeAdministrative: includeAdmin
    );

    var visible = showAll ? rawCourses : filter.Apply(rawCourses);

    Console.WriteLine($"Showing {visible.Count} course(s):");
    foreach (var c in visible)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.CourseCode))
            tags.Add(c.CourseCode);
        if (!string.IsNullOrWhiteSpace(c.TermOrSection))
            tags.Add(c.TermOrSection);
        if (c.IsAdministrativeShell)
            tags.Add("ADMIN");
        if (c.IsCompleted)
            tags.Add("COMPLETED");
        if (filter.IsIgnored(c))
            tags.Add("IGNORED");

        var tagStr = tags.Count > 0 ? $" ({string.Join(" · ", tags)})" : "";
        Console.WriteLine($"  [{c.CourseId}] {c.Title}{tagStr} -> {c.SanitizedFolderName}/");
    }

    return 0;
}

static async Task<int> RunIgnoreAsync(string[] cmdArgs)
{
    var outDir = GetOptionValue(cmdArgs, "--out") ?? Path.GetFullPath("./STI_Sync");
    var filterPath = CourseFilterStore.GetDefaultFilterPath(outDir);
    var filter = await CourseFilterStore.LoadAsync(filterPath);

    var sub = cmdArgs.Length > 0 ? cmdArgs[0].ToLowerInvariant() : "list";
    switch (sub)
    {
        case "list":
            Console.WriteLine($"Ignore list ({filterPath}):");
            Console.WriteLine(
                "  IDs: "
                    + (
                        filter.IgnoredCourseIds.Count == 0
                            ? "(none)"
                            : string.Join(", ", filter.IgnoredCourseIds)
                    )
            );
            Console.WriteLine(
                "  Title patterns: "
                    + (
                        filter.IgnoredTitlePatterns.Count == 0
                            ? "(none)"
                            : string.Join(", ", filter.IgnoredTitlePatterns)
                    )
            );
            return 0;

        case "add" when cmdArgs.Length > 1 && !cmdArgs[1].StartsWith("--"):
            var addVal = cmdArgs[1].Trim();
            if (addVal.All(char.IsDigit))
                filter.IgnoredCourseIds.Add(addVal);
            else if (
                !filter.IgnoredTitlePatterns.Contains(addVal, StringComparer.OrdinalIgnoreCase)
            )
                filter.IgnoredTitlePatterns.Add(addVal);

            await CourseFilterStore.SaveAsync(filterPath, filter);
            Console.WriteLine($"Added '{addVal}' to ignore list.");
            return 0;

        case "remove" when cmdArgs.Length > 1 && !cmdArgs[1].StartsWith("--"):
            var remVal = cmdArgs[1].Trim();
            var removed =
                filter.IgnoredCourseIds.Remove(remVal)
                | filter.IgnoredTitlePatterns.RemoveAll(p =>
                    p.Equals(remVal, StringComparison.OrdinalIgnoreCase)
                ) > 0;
            await CourseFilterStore.SaveAsync(filterPath, filter);
            Console.WriteLine(
                removed
                    ? $"Removed '{remVal}' from ignore list."
                    : $"'{remVal}' was not in ignore list."
            );
            return 0;

        default:
            Console.Error.WriteLine(
                "Usage: ignore [list | add <id-or-pattern> | remove <id-or-pattern>] [--out <dir>]"
            );
            return 1;
    }
}

static async Task<int> RunSyncAsync(StiElmsClient client, string[] syncArgs)
{
    var outDir = GetOptionValue(syncArgs, "--out") ?? Path.GetFullPath("./STI_Sync");
    var courseFilterArg = GetOptionValue(syncArgs, "--course");
    var dryRun = syncArgs.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    var force = syncArgs.Contains("--force", StringComparer.OrdinalIgnoreCase);
    var includeAdmin = syncArgs.Contains("--include-admin", StringComparer.OrdinalIgnoreCase);

    var statePath = LocalSyncStateStore.GetDefaultStatePath(outDir);
    var localState = await LocalSyncStateStore.LoadAsync(statePath);
    var ignoreFilter = await CourseFilterStore.LoadAsync(
        CourseFilterStore.GetDefaultFilterPath(outDir)
    );

    var courses = await client.GetEnrolledCoursesAsync(
        ignoreFilter,
        includeCompleted: false,
        includeAdministrative: includeAdmin
    );
    if (!string.IsNullOrWhiteSpace(courseFilterArg))
    {
        courses =
        [
            .. courses.Where(c =>
                c.CourseId.Equals(courseFilterArg, StringComparison.OrdinalIgnoreCase)
                || c.Title.Contains(courseFilterArg, StringComparison.OrdinalIgnoreCase)
            ),
        ];
    }

    var resolver = new SyncResolver();
    var executor = new SyncExecutor(client.HttpClient);
    var options = new SyncOptions
    {
        OutputRootDirectory = outDir,
        DryRun = dryRun,
        ForceRedownload = force,
    };

    var progress = new Progress<SyncProgress>(p =>
        Console.WriteLine(
            $"[{p.Stage}] {(p.CourseTitle is not null ? $"({p.CourseTitle}) " : "")}{p.Message}"
        )
    );

    foreach (var course in courses)
    {
        var snapshot = await client.GetCourseSnapshotAsync(course, progress);
        var plan = resolver.Resolve(snapshot, localState, outDir, force);
        var termMap = snapshot.BuildTermMap();
        var termSummary = string.Join(
            ", ",
            termMap
                .Where(kv => kv.Value.Count > 0)
                .Select(kv => $"{kv.Key}: [{string.Join(",", kv.Value)}]")
        );

        Console.WriteLine(
            $"\n=== {course.Title} ({course.CourseId}) -> {course.SanitizedFolderName}/ ==="
        );
        if (!string.IsNullOrEmpty(termSummary))
            Console.WriteLine($"  Terms: {termSummary}");
        Console.WriteLine(
            $"  Modules: {snapshot.Modules.Count}, Handout Pages: {snapshot.Lessons.Count}, Assignments: {snapshot.Assignments.Count}"
        );
        Console.WriteLine(
            $"  Plan: {plan.DownloadCount} to download, {plan.RelocatedCount} moved locally, {plan.UnchangedCount} unchanged, {plan.SkippedCount} skipped"
        );

        foreach (var a in snapshot.Assignments)
        {
            var subFiles =
                a.SubmissionInfo.SubmittedFiles.Count > 0
                    ? $" | SubmittedFiles: {a.SubmissionInfo.SubmittedFiles.Count}"
                    : "";
            Console.WriteLine(
                $"    [{a.Kind} {a.LmsId}] {a.Title} | Due: {a.DueDate?.ToString("yyyy-MM-dd HH:mm") ?? "None"} | Status: {a.Status} | Score: {a.Score ?? (a.MaxScore.HasValue ? $"?/{a.MaxScore}" : "N/A")}{subFiles}"
            );
        }

        if (dryRun)
        {
            foreach (var action in plan.Actions.Where(a => a.Kind != SyncActionKind.Unchanged))
                Console.WriteLine(
                    $"    [{action.Kind}] {action.TargetRelativePath} ({action.Reason})"
                );
        }
        else
        {
            await CourseArtifactWriter.WriteCourseArtifactsAsync(snapshot, outDir);
        }

        var result = await executor.ExecuteAsync(plan, options, localState, progress);
        if (!dryRun)
        {
            Console.WriteLine(
                $"  Done in {result.Duration.TotalSeconds:F1}s: +{result.Downloaded} new, ~{result.Updated} updated, >{result.Relocated} relocated, ={result.Unchanged} unchanged, !{result.Failed} failed"
            );
            foreach (var err in result.Errors)
                Console.Error.WriteLine($"    [ERR] {err}");
        }
    }

    if (!dryRun)
    {
        localState.LastFullSyncAt = DateTimeOffset.UtcNow;
        await LocalSyncStateStore.SaveAsync(statePath, localState);
    }

    return 0;
}

static string? GetOptionValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        ScholarFlow.Elms.Cli — STI eLMS Scraper & Sync Harness

        Commands:
          auth [--interactive]                   Verify/cache eLMS session (supports ELMS_USERNAME / ELMS_PASSWORD)
          courses [--out <dir>] [--all]          List active academic courses (--all includes admin/completed/ignored)
          ignore [list|add|remove] <val> [--out] Manage ignored course IDs or title patterns
          open [url-or-path]                     Open authenticated standalone Chromium web-app window
          inspect [url-or-path]                  Inspect authenticated DOM links/structure
          sync [--out <dir>] [--course <id|name>] [--dry-run] [--force]
        """
    );
}
