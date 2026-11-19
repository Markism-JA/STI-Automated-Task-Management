using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Database;

/// <summary>
/// Relational SQLite + FTS5 database engine (.scholarflow/scholarflow.db) for first-class tasks,
/// hierarchical subtasks (parent_id), labels, instruction/submission file attachments, and full-text search.
/// Synchronizes bidirectionally with inbox.md and per-course tasks.md files.
/// </summary>
public static class ScholarFlowDatabase
{
    private static readonly SemaphoreSlim DbLock = new(1, 1);

    static ScholarFlowDatabase()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public static string GetDatabasePath(string basinRoot) =>
        Path.Combine(BasinService.GetMetaDir(basinRoot), "scholarflow.db");

    private static string BuildConnectionString(string basinRoot)
    {
        var dbPath = GetDatabasePath(basinRoot);
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public static async Task EnsureInitializedAsync(string basinRoot, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
        await conn.OpenAsync(ct);
        await EnsureSchemaOnConnectionAsync(conn, ct);
    }

    private static async Task EnsureSchemaOnConnectionAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string sql = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS indexed_files (
                path TEXT PRIMARY KEY,
                course_folder TEXT NULL,
                last_write_ticks INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                parent_id TEXT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                source_path TEXT NOT NULL,
                line_number INTEGER NOT NULL DEFAULT 0,
                course_folder TEXT NULL,
                course_alias TEXT NULL,
                title TEXT NOT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0,
                due_date TEXT NULL,
                due_time TEXT NULL,
                completed_date TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 4,
                difficulty INTEGER NOT NULL DEFAULT 1,
                term TEXT NULL,
                section TEXT NULL,
                lms_kind TEXT NULL,
                weight_percent REAL NULL,
                lms_id TEXT NULL,
                lms_status TEXT NULL,
                lms_score TEXT NULL,
                lms_url TEXT NULL,
                note_path TEXT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_tasks_source_path ON tasks(source_path);
            CREATE INDEX IF NOT EXISTS idx_tasks_parent_id ON tasks(parent_id);
            CREATE INDEX IF NOT EXISTS idx_tasks_course_folder ON tasks(course_folder);
            CREATE INDEX IF NOT EXISTS idx_tasks_term ON tasks(term);

            CREATE TABLE IF NOT EXISTS task_labels (
                task_id TEXT NOT NULL,
                label TEXT NOT NULL,
                PRIMARY KEY (task_id, label)
            );

            CREATE TABLE IF NOT EXISTS task_files (
                task_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                rel_path TEXT NOT NULL,
                PRIMARY KEY (task_id, kind, rel_path)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS tasks_fts USING fts5(
                task_id UNINDEXED,
                title,
                course_alias,
                term,
                section,
                labels,
                note_body
            );

            CREATE TABLE IF NOT EXISTS quizzes (
                id TEXT PRIMARY KEY,
                course_folder TEXT NOT NULL,
                course_title TEXT NOT NULL,
                term TEXT NULL,
                title TEXT NOT NULL,
                source_pdf_path TEXT NOT NULL,
                model_used TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                last_score_percent REAL NULL,
                source_pdf_mtime_ticks INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS quiz_questions (
                id TEXT PRIMARY KEY,
                quiz_id TEXT NOT NULL,
                order_index INTEGER NOT NULL DEFAULT 0,
                kind TEXT NOT NULL,
                cognitive_level TEXT NOT NULL,
                difficulty INTEGER NOT NULL DEFAULT 3,
                topic_tag TEXT NOT NULL,
                prompt TEXT NOT NULL,
                options_json TEXT NOT NULL,
                correct_answer TEXT NOT NULL,
                aliases_json TEXT NOT NULL,
                explanation TEXT NOT NULL,
                source_pdf_path TEXT NOT NULL,
                source_page INTEGER NOT NULL DEFAULT 1,
                section_title TEXT NOT NULL,
                verbatim_quote TEXT NOT NULL,
                highlight_rects_json TEXT NOT NULL,
                srs_interval_days REAL NOT NULL DEFAULT 0,
                srs_ease_factor REAL NOT NULL DEFAULT 2.5,
                srs_due_date TEXT NULL,
                srs_reps INTEGER NOT NULL DEFAULT 0,
                srs_lapses INTEGER NOT NULL DEFAULT 0,
                last_answered_correct INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS idx_quiz_questions_quiz_id ON quiz_questions(quiz_id);
            CREATE INDEX IF NOT EXISTS idx_quiz_questions_srs_due ON quiz_questions(srs_due_date);

            CREATE TABLE IF NOT EXISTS quiz_attempts (
                id TEXT PRIMARY KEY,
                quiz_id TEXT NOT NULL,
                question_id TEXT NOT NULL,
                user_answer TEXT NOT NULL,
                is_correct INTEGER NOT NULL,
                quality_rating INTEGER NOT NULL,
                answered_at_utc TEXT NOT NULL
            );
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);

        try
        {
            await using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE quizzes ADD COLUMN source_pdf_mtime_ticks INTEGER NULL;";
            await alterCmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Column already exists
        }
    }

    /// <summary>
    /// Synchronizes all Markdown task files (inbox.md + each course's tasks.md) with the SQLite database
    /// incrementally based on file modification timestamps, and loads the canonical hierarchical TaskItem tree from SQLite.
    /// </summary>
    public static async Task<IReadOnlyList<TaskItem>> SyncAndLoadAllTasksAsync(
        string basinRoot,
        IReadOnlyList<BasinCourseEntry> courses,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !Directory.Exists(basinRoot))
            return [];

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            var indexedTicks = await LoadIndexedFileTicksAsync(conn, ct);
            var activeSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Inbox
            var inboxPath = Path.GetFullPath(BasinService.GetInboxPath(basinRoot));
            if (File.Exists(inboxPath))
            {
                activeSourcePaths.Add(inboxPath);
                var currentTicks = File.GetLastWriteTimeUtc(inboxPath).Ticks;
                if (!indexedTicks.TryGetValue(inboxPath, out var savedTicks) || savedTicks != currentTicks)
                {
                    var inboxTasks = await MarkdownTaskCodec.LoadFileAsync(inboxPath, defaultCourseFolder: null, ct);
                    await ReplaceFileTasksInConnectionAsync(conn, basinRoot, inboxPath, courseFolder: null, inboxTasks, currentTicks, ct);
                }
            }

            // 2. Active Courses
            foreach (var course in courses.Where(c => !c.IsArchived))
            {
                var tasksPath = Path.GetFullPath(BasinService.GetCourseTasksPath(basinRoot, course.FolderName));
                if (!File.Exists(tasksPath))
                    continue;

                activeSourcePaths.Add(tasksPath);
                var termMap = BasinService.LoadCourseTermMap(basinRoot, course.FolderName);
                var termJsonPath = Path.Combine(basinRoot, course.FolderName, "term.json");
                long termTicks = File.Exists(termJsonPath) ? File.GetLastWriteTimeUtc(termJsonPath).Ticks : 0L;
                var currentTicks = File.GetLastWriteTimeUtc(tasksPath).Ticks ^ termTicks;
                if (!indexedTicks.TryGetValue(tasksPath, out var savedTicks) || savedTicks != currentTicks)
                {
                    var courseTasks = (await MarkdownTaskCodec.LoadFileAsync(tasksPath, course.FolderName, ct)).ToList();
                    bool anyEnriched = false;

                    foreach (var t in courseTasks)
                    {
                        t.CourseFolder ??= course.FolderName;
                        t.CourseAlias ??= course.Alias;
                        if (BasinService.EnrichTaskMetadataAndFiles(basinRoot, course, t, termMap))
                            anyEnriched = true;
                    }

                    if (anyEnriched)
                    {
                        BasinWatcher.SuppressGlobal(TimeSpan.FromMilliseconds(750));
                        await MarkdownTaskCodec.SaveDocumentAsync(tasksPath, courseTasks, course.Title, ct, syncToDb: false);
                        currentTicks = File.GetLastWriteTimeUtc(tasksPath).Ticks ^ termTicks;
                    }

                    await ReplaceFileTasksInConnectionAsync(conn, basinRoot, tasksPath, course.FolderName, courseTasks, currentTicks, ct);
                }
            }

            // 3. Load canonical task hierarchy from SQLite
            return await LoadAllTasksFromConnectionAsync(conn, activeSourcePaths, ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    /// <summary>
    /// Updates SQLite immediately whenever a Markdown task file (tasks.md or inbox.md) is saved by the application.
    /// </summary>
    public static async Task SyncMarkdownFileToDbAsync(
        string basinRoot,
        string filePath,
        string? courseFolder,
        IReadOnlyList<TaskItem> tasks,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !Directory.Exists(basinRoot))
            return;

        var fullPath = Path.GetFullPath(filePath);
        var ticks = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : DateTime.UtcNow.Ticks;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);
            await ReplaceFileTasksInConnectionAsync(conn, basinRoot, fullPath, courseFolder, tasks, ticks, ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    /// <summary>
    /// Performs a ranked SQLite FTS5 full-text search across task titles, subtasks, course aliases, terms, sections, tags, and Markdown notes.
    /// </summary>
    public static IReadOnlyList<string> SearchTaskIdsFts(string basinRoot, string rawQuery, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || string.IsNullOrWhiteSpace(rawQuery))
            return [];

        var dbPath = GetDatabasePath(basinRoot);
        if (!File.Exists(dbPath))
            return [];

        var ftsQuery = BuildFts5PrefixQuery(rawQuery);
        if (string.IsNullOrWhiteSpace(ftsQuery))
            return [];

        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT task_id
                FROM tasks_fts
                WHERE tasks_fts MATCH $q
                ORDER BY bm25(tasks_fts, 0.0, 10.0, 5.0, 4.0, 3.0, 5.0, 1.5)
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", ftsQuery);
            cmd.Parameters.AddWithValue("$limit", limit);

            var ids = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
            return ids;
        }
        catch
        {
            return [];
        }
    }

    private static string BuildFts5PrefixQuery(string raw)
    {
        var tokens = raw
            .Split([' ', '\t', '\r', '\n', '#', '@', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray()))
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0)
            return string.Empty;

        return string.Join(" AND ", tokens.Select(t => $"\"{t}\"*"));
    }

    private static async Task<Dictionary<string, long>> LoadIndexedFileTicksAsync(SqliteConnection conn, CancellationToken ct)
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, last_write_ticks FROM indexed_files;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            dict[reader.GetString(0)] = reader.GetInt64(1);
        }
        return dict;
    }

    private static async Task ReplaceFileTasksInConnectionAsync(
        SqliteConnection conn,
        string basinRoot,
        string fullSourcePath,
        string? courseFolder,
        IReadOnlyList<TaskItem> rootTasks,
        long lastWriteTicks,
        CancellationToken ct)
    {
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var delLabels = conn.CreateCommand())
        {
            delLabels.Transaction = tx;
            delLabels.CommandText = "DELETE FROM task_labels WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);";
            delLabels.Parameters.AddWithValue("$path", fullSourcePath);
            await delLabels.ExecuteNonQueryAsync(ct);
        }

        await using (var delFiles = conn.CreateCommand())
        {
            delFiles.Transaction = tx;
            delFiles.CommandText = "DELETE FROM task_files WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);";
            delFiles.Parameters.AddWithValue("$path", fullSourcePath);
            await delFiles.ExecuteNonQueryAsync(ct);
        }

        await using (var delFts = conn.CreateCommand())
        {
            delFts.Transaction = tx;
            delFts.CommandText = "DELETE FROM tasks_fts WHERE task_id IN (SELECT id FROM tasks WHERE source_path = $path);";
            delFts.Parameters.AddWithValue("$path", fullSourcePath);
            await delFts.ExecuteNonQueryAsync(ct);
        }

        await using (var delTasks = conn.CreateCommand())
        {
            delTasks.Transaction = tx;
            delTasks.CommandText = "DELETE FROM tasks WHERE source_path = $path;";
            delTasks.Parameters.AddWithValue("$path", fullSourcePath);
            await delTasks.ExecuteNonQueryAsync(ct);
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rootTasks.Count; i++)
        {
            rootTasks[i].EnsureChildTasksSynced();
            await InsertTaskTreeNodeAsync(
                conn,
                tx,
                basinRoot,
                fullSourcePath,
                courseFolder,
                rootTasks[i],
                parentId: null,
                sortOrder: i,
                usedIds,
                ct);
        }

        await using (var upsertFile = conn.CreateCommand())
        {
            upsertFile.Transaction = tx;
            upsertFile.CommandText = """
                INSERT INTO indexed_files (path, course_folder, last_write_ticks)
                VALUES ($path, $course, $ticks)
                ON CONFLICT(path) DO UPDATE SET
                    course_folder = excluded.course_folder,
                    last_write_ticks = excluded.last_write_ticks;
                """;
            upsertFile.Parameters.AddWithValue("$path", fullSourcePath);
            upsertFile.Parameters.AddWithValue("$course", (object?)courseFolder ?? DBNull.Value);
            upsertFile.Parameters.AddWithValue("$ticks", lastWriteTicks);
            await upsertFile.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task InsertTaskTreeNodeAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string basinRoot,
        string fullSourcePath,
        string? defaultCourseFolder,
        TaskItem task,
        string? parentId,
        int sortOrder,
        HashSet<string> usedIds,
        CancellationToken ct)
    {
        var effectiveCourse = task.CourseFolder ?? defaultCourseFolder;
        var baseId = string.IsNullOrWhiteSpace(task.Id)
            ? $"{effectiveCourse ?? "inbox"}:{task.LineNumber}:{sortOrder}"
            : task.Id;

        if (!string.IsNullOrWhiteSpace(effectiveCourse) &&
            !baseId.StartsWith($"{effectiveCourse}:", StringComparison.OrdinalIgnoreCase) &&
            !baseId.StartsWith("lms-", StringComparison.OrdinalIgnoreCase))
        {
            baseId = $"{effectiveCourse}:{baseId}";
        }

        var uniqueId = baseId;
        int dup = 2;
        while (!usedIds.Add(uniqueId))
        {
            uniqueId = $"{baseId}#{dup++}";
        }

        task.Id = uniqueId;
        task.ParentTaskId = parentId;
        task.SourceMarkdownPath = fullSourcePath;
        task.CourseFolder = effectiveCourse;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO tasks (
                    id, parent_id, sort_order, source_path, line_number,
                    course_folder, course_alias, title, is_completed,
                    due_date, due_time, completed_date, priority, difficulty,
                    term, section, lms_kind, weight_percent,
                    lms_id, lms_status, lms_score, lms_url, note_path, updated_at_utc
                ) VALUES (
                    $id, $parent_id, $sort_order, $source_path, $line_number,
                    $course_folder, $course_alias, $title, $is_completed,
                    $due_date, $due_time, $completed_date, $priority, $difficulty,
                    $term, $section, $lms_kind, $weight_percent,
                    $lms_id, $lms_status, $lms_score, $lms_url, $note_path, $updated_at_utc
                );
                """;
            cmd.Parameters.AddWithValue("$id", uniqueId);
            cmd.Parameters.AddWithValue("$parent_id", (object?)parentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sort_order", sortOrder);
            cmd.Parameters.AddWithValue("$source_path", fullSourcePath);
            cmd.Parameters.AddWithValue("$line_number", task.LineNumber);
            cmd.Parameters.AddWithValue("$course_folder", (object?)effectiveCourse ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$course_alias", (object?)task.CourseAlias ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", task.Title);
            cmd.Parameters.AddWithValue("$is_completed", task.IsCompleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$due_date", task.DueDate.HasValue ? task.DueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$due_time", task.DueTime.HasValue ? task.DueTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$completed_date", task.CompletedDate.HasValue ? task.CompletedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$priority", (int)task.Priority);
            cmd.Parameters.AddWithValue("$difficulty", task.Difficulty);
            cmd.Parameters.AddWithValue("$term", (object?)task.Term ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$section", (object?)task.Section ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_kind", (object?)task.LmsKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$weight_percent", task.WeightPercent.HasValue ? task.WeightPercent.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_id", (object?)task.LmsId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_status", (object?)task.LmsStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_score", (object?)task.LmsScore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lms_url", (object?)task.LmsUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$note_path", (object?)task.NotePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var lbl in task.Labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var lblCmd = conn.CreateCommand();
            lblCmd.Transaction = tx;
            lblCmd.CommandText = "INSERT OR IGNORE INTO task_labels (task_id, label) VALUES ($tid, $lbl);";
            lblCmd.Parameters.AddWithValue("$tid", uniqueId);
            lblCmd.Parameters.AddWithValue("$lbl", lbl.Trim());
            await lblCmd.ExecuteNonQueryAsync(ct);
        }

        async Task InsertFilesOfKindAsync(IEnumerable<string> paths, string kind)
        {
            foreach (var p in paths.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await using var fCmd = conn.CreateCommand();
                fCmd.Transaction = tx;
                fCmd.CommandText = "INSERT OR IGNORE INTO task_files (task_id, kind, rel_path) VALUES ($tid, $kind, $path);";
                fCmd.Parameters.AddWithValue("$tid", uniqueId);
                fCmd.Parameters.AddWithValue("$kind", kind);
                fCmd.Parameters.AddWithValue("$path", p.Trim());
                await fCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await InsertFilesOfKindAsync(task.InstructionFiles, "instruction");
        await InsertFilesOfKindAsync(task.SubmissionFiles, "submission");
        await InsertFilesOfKindAsync(task.LinkedFiles, "linked");

        var noteBody = TryReadNoteBody(basinRoot, effectiveCourse, task.NotePath);
        await using (var ftsCmd = conn.CreateCommand())
        {
            ftsCmd.Transaction = tx;
            ftsCmd.CommandText = """
                INSERT INTO tasks_fts (task_id, title, course_alias, term, section, labels, note_body)
                VALUES ($tid, $title, $alias, $term, $section, $labels, $note);
                """;
            ftsCmd.Parameters.AddWithValue("$tid", uniqueId);
            ftsCmd.Parameters.AddWithValue("$title", task.Title);
            ftsCmd.Parameters.AddWithValue("$alias", task.CourseAlias ?? string.Empty);
            ftsCmd.Parameters.AddWithValue("$term", task.Term ?? string.Empty);
            ftsCmd.Parameters.AddWithValue("$section", task.Section ?? string.Empty);
            ftsCmd.Parameters.AddWithValue("$labels", string.Join(' ', task.Labels));
            ftsCmd.Parameters.AddWithValue("$note", noteBody);
            await ftsCmd.ExecuteNonQueryAsync(ct);
        }

        for (int cIdx = 0; cIdx < task.ChildTasks.Count; cIdx++)
        {
            await InsertTaskTreeNodeAsync(
                conn,
                tx,
                basinRoot,
                fullSourcePath,
                effectiveCourse,
                task.ChildTasks[cIdx],
                parentId: uniqueId,
                sortOrder: cIdx,
                usedIds,
                ct);
        }
    }

    private static string TryReadNoteBody(string basinRoot, string? courseFolder, string? notePath)
    {
        if (string.IsNullOrWhiteSpace(notePath))
            return string.Empty;

        try
        {
            var baseDir = string.IsNullOrWhiteSpace(courseFolder)
                ? basinRoot
                : Path.Combine(basinRoot, courseFolder);
            var full = Path.Combine(baseDir, notePath);
            if (!File.Exists(full))
                return string.Empty;

            return File.ReadAllText(full);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<IReadOnlyList<TaskItem>> LoadAllTasksFromConnectionAsync(
        SqliteConnection conn,
        HashSet<string> activeSourcePaths,
        CancellationToken ct)
    {
        var byId = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        var orderedNodes = new List<TaskItem>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    id, parent_id, sort_order, source_path, line_number,
                    course_folder, course_alias, title, is_completed,
                    due_date, due_time, completed_date, priority, difficulty,
                    term, section, lms_kind, weight_percent,
                    lms_id, lms_status, lms_score, lms_url, note_path
                FROM tasks
                ORDER BY source_path, parent_id NULLS FIRST, sort_order, line_number;
                """;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var sourcePath = r.GetString(3);
                if (!activeSourcePaths.Contains(sourcePath))
                    continue;

                var item = new TaskItem
                {
                    Id = r.GetString(0),
                    ParentTaskId = r.IsDBNull(1) ? null : r.GetString(1),
                    SourceMarkdownPath = sourcePath,
                    LineNumber = r.GetInt32(4),
                    CourseFolder = r.IsDBNull(5) ? null : r.GetString(5),
                    CourseAlias = r.IsDBNull(6) ? null : r.GetString(6),
                    Title = r.GetString(7),
                    IsCompleted = r.GetInt32(8) != 0,
                    DueDate = !r.IsDBNull(9) && DateOnly.TryParseExact(r.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd) ? dd : null,
                    DueTime = !r.IsDBNull(10) && TimeOnly.TryParseExact(r.GetString(10), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null,
                    CompletedDate = !r.IsDBNull(11) && DateOnly.TryParseExact(r.GetString(11), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd) ? cd : null,
                    Priority = (TaskPriority)Math.Clamp(r.GetInt32(12), 1, 4),
                    Difficulty = Math.Clamp(r.GetInt32(13), 1, 5),
                    Term = r.IsDBNull(14) ? null : r.GetString(14),
                    Section = r.IsDBNull(15) ? null : r.GetString(15),
                    LmsKind = r.IsDBNull(16) ? null : r.GetString(16),
                    WeightPercent = r.IsDBNull(17) ? null : r.GetDouble(17),
                    LmsId = r.IsDBNull(18) ? null : r.GetString(18),
                    LmsStatus = r.IsDBNull(19) ? null : r.GetString(19),
                    LmsScore = r.IsDBNull(20) ? null : r.GetString(20),
                    LmsUrl = r.IsDBNull(21) ? null : r.GetString(21),
                    NotePath = r.IsDBNull(22) ? null : r.GetString(22)
                };

                byId[item.Id] = item;
                orderedNodes.Add(item);
            }
        }

        await using (var lblCmd = conn.CreateCommand())
        {
            lblCmd.CommandText = "SELECT task_id, label FROM task_labels;";
            await using var r = await lblCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (byId.TryGetValue(r.GetString(0), out var t))
                    t.Labels.Add(r.GetString(1));
            }
        }

        await using (var fCmd = conn.CreateCommand())
        {
            fCmd.CommandText = "SELECT task_id, kind, rel_path FROM task_files;";
            await using var r = await fCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!byId.TryGetValue(r.GetString(0), out var t))
                    continue;
                var kind = r.GetString(1);
                var path = r.GetString(2);
                switch (kind)
                {
                    case "instruction":
                        t.InstructionFiles.Add(path);
                        break;
                    case "submission":
                        t.SubmissionFiles.Add(path);
                        break;
                    case "linked":
                        t.LinkedFiles.Add(path);
                        break;
                }
            }
        }

        var roots = new List<TaskItem>();
        foreach (var node in orderedNodes)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentTaskId) &&
                byId.TryGetValue(node.ParentTaskId, out var parent))
            {
                parent.ChildTasks.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        foreach (var root in roots)
            root.EnsureChildTasksSynced();

        return roots;
    }

    // =========================================================================
    // QUIZ & SPACED REPETITION (SM-2 / FSRS) DATABASE OPERATIONS
    // =========================================================================

    public static async Task SaveQuizSessionAsync(
        string basinRoot,
        QuizSessionRecord quiz,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var qCmd = conn.CreateCommand())
            {
                qCmd.Transaction = tx;
                qCmd.CommandText = """
                    INSERT OR REPLACE INTO quizzes (
                        id, course_folder, course_title, term, title,
                        source_pdf_path, model_used, created_at_utc, attempt_count, last_score_percent, source_pdf_mtime_ticks
                    ) VALUES (
                        $id, $course_folder, $course_title, $term, $title,
                        $source_pdf_path, $model_used, $created_at_utc, $attempt_count, $last_score_percent, $source_pdf_mtime_ticks
                    );
                    """;
                qCmd.Parameters.AddWithValue("$id", quiz.Id);
                qCmd.Parameters.AddWithValue("$course_folder", quiz.CourseFolder);
                qCmd.Parameters.AddWithValue("$course_title", quiz.CourseTitle);
                qCmd.Parameters.AddWithValue("$term", (object?)quiz.Term ?? DBNull.Value);
                qCmd.Parameters.AddWithValue("$title", quiz.Title);
                qCmd.Parameters.AddWithValue("$source_pdf_path", quiz.SourcePdfPath);
                qCmd.Parameters.AddWithValue("$model_used", quiz.ModelUsed);
                qCmd.Parameters.AddWithValue("$created_at_utc", quiz.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                qCmd.Parameters.AddWithValue("$attempt_count", quiz.AttemptCount);
                qCmd.Parameters.AddWithValue("$last_score_percent", quiz.LastScorePercent.HasValue ? quiz.LastScorePercent.Value : DBNull.Value);
                qCmd.Parameters.AddWithValue("$source_pdf_mtime_ticks", quiz.SourcePdfMtimeTicks.HasValue ? quiz.SourcePdfMtimeTicks.Value : DBNull.Value);
                await qCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var delOld = conn.CreateCommand())
            {
                delOld.Transaction = tx;
                delOld.CommandText = "DELETE FROM quiz_questions WHERE quiz_id = $qid;";
                delOld.Parameters.AddWithValue("$qid", quiz.Id);
                await delOld.ExecuteNonQueryAsync(ct);
            }

            for (int i = 0; i < quiz.Questions.Count; i++)
            {
                var q = quiz.Questions[i];
                q.QuizId = quiz.Id;
                q.OrderIndex = i;

                await using var itemCmd = conn.CreateCommand();
                itemCmd.Transaction = tx;
                itemCmd.CommandText = """
                    INSERT OR REPLACE INTO quiz_questions (
                        id, quiz_id, order_index, kind, cognitive_level, difficulty, topic_tag,
                        prompt, options_json, correct_answer, aliases_json, explanation,
                        source_pdf_path, source_page, section_title, verbatim_quote, highlight_rects_json,
                        srs_interval_days, srs_ease_factor, srs_due_date, srs_reps, srs_lapses, last_answered_correct
                    ) VALUES (
                        $id, $quiz_id, $order_index, $kind, $cognitive_level, $difficulty, $topic_tag,
                        $prompt, $options_json, $correct_answer, $aliases_json, $explanation,
                        $source_pdf_path, $source_page, $section_title, $verbatim_quote, $highlight_rects_json,
                        $srs_interval_days, $srs_ease_factor, $srs_due_date, $srs_reps, $srs_lapses, $last_answered_correct
                    );
                    """;
                itemCmd.Parameters.AddWithValue("$id", q.Id);
                itemCmd.Parameters.AddWithValue("$quiz_id", quiz.Id);
                itemCmd.Parameters.AddWithValue("$order_index", i);
                itemCmd.Parameters.AddWithValue("$kind", q.Kind.ToString());
                itemCmd.Parameters.AddWithValue("$cognitive_level", q.CognitiveLevel.ToString());
                itemCmd.Parameters.AddWithValue("$difficulty", q.Difficulty);
                itemCmd.Parameters.AddWithValue("$topic_tag", q.TopicTag);
                itemCmd.Parameters.AddWithValue("$prompt", q.Prompt);
                itemCmd.Parameters.AddWithValue("$options_json", JsonSerializer.Serialize(q.Options));
                itemCmd.Parameters.AddWithValue("$correct_answer", q.CorrectAnswer);
                itemCmd.Parameters.AddWithValue("$aliases_json", JsonSerializer.Serialize(q.AcceptedAliases));
                itemCmd.Parameters.AddWithValue("$explanation", q.Explanation);
                itemCmd.Parameters.AddWithValue("$source_pdf_path", q.SourcePdfPath);
                itemCmd.Parameters.AddWithValue("$source_page", q.SourcePage);
                itemCmd.Parameters.AddWithValue("$section_title", q.SectionTitle);
                itemCmd.Parameters.AddWithValue("$verbatim_quote", q.VerbatimQuote);
                itemCmd.Parameters.AddWithValue("$highlight_rects_json", JsonSerializer.Serialize(q.HighlightRects));
                itemCmd.Parameters.AddWithValue("$srs_interval_days", q.SrsIntervalDays);
                itemCmd.Parameters.AddWithValue("$srs_ease_factor", q.SrsEaseFactor);
                itemCmd.Parameters.AddWithValue("$srs_due_date", q.SrsDueDate.HasValue ? q.SrsDueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
                itemCmd.Parameters.AddWithValue("$srs_reps", q.SrsReps);
                itemCmd.Parameters.AddWithValue("$srs_lapses", q.SrsLapses);
                itemCmd.Parameters.AddWithValue("$last_answered_correct", q.LastAnsweredCorrect.HasValue ? (q.LastAnsweredCorrect.Value ? 1 : 0) : DBNull.Value);
                await itemCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<IReadOnlyList<QuizSessionRecord>> LoadQuizSessionsAsync(
        string basinRoot,
        string? courseFolder = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !File.Exists(GetDatabasePath(basinRoot)))
            return [];

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            var sessions = new List<QuizSessionRecord>();
            var byId = new Dictionary<string, QuizSessionRecord>(StringComparer.OrdinalIgnoreCase);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = string.IsNullOrWhiteSpace(courseFolder)
                    ? "SELECT id, course_folder, course_title, term, title, source_pdf_path, model_used, created_at_utc, attempt_count, last_score_percent, source_pdf_mtime_ticks FROM quizzes ORDER BY created_at_utc DESC;"
                    : "SELECT id, course_folder, course_title, term, title, source_pdf_path, model_used, created_at_utc, attempt_count, last_score_percent, source_pdf_mtime_ticks FROM quizzes WHERE course_folder = $cf ORDER BY created_at_utc DESC;";
                if (!string.IsNullOrWhiteSpace(courseFolder))
                    cmd.Parameters.AddWithValue("$cf", courseFolder);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var s = new QuizSessionRecord
                    {
                        Id = r.GetString(0),
                        CourseFolder = r.GetString(1),
                        CourseTitle = r.GetString(2),
                        Term = r.IsDBNull(3) ? null : r.GetString(3),
                        Title = r.GetString(4),
                        SourcePdfPath = r.GetString(5),
                        ModelUsed = r.GetString(6),
                        CreatedAtUtc = DateTimeOffset.TryParse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTimeOffset.UtcNow,
                        AttemptCount = r.GetInt32(8),
                        LastScorePercent = r.IsDBNull(9) ? null : r.GetDouble(9),
                        SourcePdfMtimeTicks = r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetInt64(10) : null
                    };
                    sessions.Add(s);
                    byId[s.Id] = s;
                }
            }

            if (sessions.Count == 0)
                return sessions;

            await using (var qCmd = conn.CreateCommand())
            {
                qCmd.CommandText = """
                    SELECT
                        id, quiz_id, order_index, kind, cognitive_level, difficulty, topic_tag,
                        prompt, options_json, correct_answer, aliases_json, explanation,
                        source_pdf_path, source_page, section_title, verbatim_quote, highlight_rects_json,
                        srs_interval_days, srs_ease_factor, srs_due_date, srs_reps, srs_lapses, last_answered_correct
                    FROM quiz_questions
                    ORDER BY quiz_id, order_index;
                    """;

                await using var r = await qCmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var quizId = r.GetString(1);
                    if (!byId.TryGetValue(quizId, out var parentQuiz))
                        continue;

                    var q = new QuizQuestionRecord
                    {
                        Id = r.GetString(0),
                        QuizId = quizId,
                        OrderIndex = r.GetInt32(2),
                        Kind = Enum.TryParse<QuizQuestionKind>(r.GetString(3), ignoreCase: true, out var k) ? k : QuizQuestionKind.MultipleChoice,
                        CognitiveLevel = Enum.TryParse<CognitiveLevel>(r.GetString(4), ignoreCase: true, out var cl) ? cl : CognitiveLevel.Comprehension,
                        Difficulty = r.GetInt32(5),
                        TopicTag = r.GetString(6),
                        Prompt = r.GetString(7),
                        Options = TryDeserializeList<string>(r.GetString(8)),
                        CorrectAnswer = r.GetString(9),
                        AcceptedAliases = TryDeserializeList<string>(r.GetString(10)),
                        Explanation = r.GetString(11),
                        SourcePdfPath = r.GetString(12),
                        SourcePage = r.GetInt32(13),
                        SectionTitle = r.GetString(14),
                        VerbatimQuote = r.GetString(15),
                        HighlightRects = TryDeserializeList<NormalizedHighlightRect>(r.GetString(16)),
                        SrsIntervalDays = r.GetDouble(17),
                        SrsEaseFactor = r.GetDouble(18),
                        SrsDueDate = !r.IsDBNull(19) && DateOnly.TryParseExact(r.GetString(19), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sd) ? sd : null,
                        SrsReps = r.GetInt32(20),
                        SrsLapses = r.GetInt32(21),
                        LastAnsweredCorrect = r.IsDBNull(22) ? null : r.GetInt32(22) != 0
                    };

                    parentQuiz.Questions.Add(q);
                }
            }

            return sessions;
        }
        finally
        {
            DbLock.Release();
        }
    }

    /// <summary>
    /// Records a student's question attempt and updates SM-2 / FSRS spaced repetition interval and due date.
    /// qualityRating: 1 = Again (Fail), 3 = Hard, 4 = Good, 5 = Easy.
    /// </summary>
    public static async Task RecordQuestionAttemptAsync(
        string basinRoot,
        QuizQuestionRecord question,
        string userAnswer,
        bool isCorrect,
        int qualityRating = 4,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        int q = !isCorrect ? 1 : Math.Clamp(qualityRating, 3, 5);
        var today = DateOnly.FromDateTime(DateTime.Today);

        if (q < 3)
        {
            question.SrsReps = 0;
            question.SrsLapses++;
            question.SrsIntervalDays = 1.0;
            question.SrsEaseFactor = Math.Max(1.3, question.SrsEaseFactor - 0.2);
            question.SrsDueDate = today.AddDays(1);
        }
        else
        {
            question.SrsReps++;
            if (question.SrsReps == 1)
                question.SrsIntervalDays = q == 5 ? 3.0 : 1.0;
            else if (question.SrsReps == 2)
                question.SrsIntervalDays = q == 5 ? 6.0 : 4.0;
            else
                question.SrsIntervalDays = Math.Round(question.SrsIntervalDays * question.SrsEaseFactor, 1);

            double easeDelta = 0.1 - (5 - q) * (0.08 + (5 - q) * 0.02);
            question.SrsEaseFactor = Math.Clamp(question.SrsEaseFactor + easeDelta, 1.3, 3.2);
            question.SrsDueDate = today.AddDays(Math.Max(1, (int)Math.Round(question.SrsIntervalDays)));
        }

        question.LastAnsweredCorrect = isCorrect;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using (var upd = conn.CreateCommand())
            {
                upd.CommandText = """
                    UPDATE quiz_questions
                    SET srs_interval_days = $ivl,
                        srs_ease_factor = $ef,
                        srs_due_date = $due,
                        srs_reps = $reps,
                        srs_lapses = $lapses,
                        last_answered_correct = $ok
                    WHERE id = $id;
                    """;
                upd.Parameters.AddWithValue("$ivl", question.SrsIntervalDays);
                upd.Parameters.AddWithValue("$ef", question.SrsEaseFactor);
                upd.Parameters.AddWithValue("$due", question.SrsDueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                upd.Parameters.AddWithValue("$reps", question.SrsReps);
                upd.Parameters.AddWithValue("$lapses", question.SrsLapses);
                upd.Parameters.AddWithValue("$ok", isCorrect ? 1 : 0);
                upd.Parameters.AddWithValue("$id", question.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }

            await using (var att = conn.CreateCommand())
            {
                att.CommandText = """
                    INSERT INTO quiz_attempts (id, quiz_id, question_id, user_answer, is_correct, quality_rating, answered_at_utc)
                    VALUES ($id, $qid, $question_id, $ans, $ok, $qual, $ts);
                    """;
                att.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")[..12]);
                att.Parameters.AddWithValue("$qid", question.QuizId);
                att.Parameters.AddWithValue("$question_id", question.Id);
                att.Parameters.AddWithValue("$ans", userAnswer ?? string.Empty);
                att.Parameters.AddWithValue("$ok", isCorrect ? 1 : 0);
                att.Parameters.AddWithValue("$qual", q);
                att.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await att.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task UpdateQuizSessionScoreAsync(
        string basinRoot,
        QuizSessionRecord quiz,
        double scorePercent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        quiz.AttemptCount++;
        quiz.LastScorePercent = Math.Round(scorePercent, 1);

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE quizzes
                SET attempt_count = $cnt,
                    last_score_percent = $score
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$cnt", quiz.AttemptCount);
            cmd.Parameters.AddWithValue("$score", quiz.LastScorePercent.Value);
            cmd.Parameters.AddWithValue("$id", quiz.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task DeleteQuizSessionAsync(
        string basinRoot,
        string quizId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basinRoot))
            return;

        await DbLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            await conn.OpenAsync(ct);
            await EnsureSchemaOnConnectionAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM quiz_attempts WHERE quiz_id = $id;
                DELETE FROM quiz_questions WHERE quiz_id = $id;
                DELETE FROM quizzes WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", quizId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static IReadOnlyList<string> GetWeakTopicTags(string basinRoot, string? courseFolder = null, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(basinRoot) || !File.Exists(GetDatabasePath(basinRoot)))
            return [];

        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(basinRoot));
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(courseFolder)
                ? """
                  SELECT topic_tag
                  FROM quiz_questions
                  WHERE srs_lapses > 0 OR last_answered_correct = 0
                  GROUP BY topic_tag
                  ORDER BY SUM(srs_lapses) DESC, COUNT(*) DESC
                  LIMIT $limit;
                  """
                : """
                  SELECT qq.topic_tag
                  FROM quiz_questions qq
                  JOIN quizzes q ON q.id = qq.quiz_id
                  WHERE q.course_folder = $cf AND (qq.srs_lapses > 0 OR qq.last_answered_correct = 0)
                  GROUP BY qq.topic_tag
                  ORDER BY SUM(qq.srs_lapses) DESC, COUNT(*) DESC
                  LIMIT $limit;
                  """;

            if (!string.IsNullOrWhiteSpace(courseFolder))
                cmd.Parameters.AddWithValue("$cf", courseFolder);
            cmd.Parameters.AddWithValue("$limit", limit);

            var tags = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var tag = r.GetString(0);
                if (!string.IsNullOrWhiteSpace(tag))
                    tags.Add(tag);
            }
            return tags;
        }
        catch
        {
            return [];
        }
    }

    private static List<T> TryDeserializeList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
