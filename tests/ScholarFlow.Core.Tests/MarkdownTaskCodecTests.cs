using ScholarFlow.Core.Database;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Tasks;

namespace ScholarFlow.Core.Tests;

public sealed class MarkdownTaskCodecTests
{
    [Fact]
    public void ParseAndSerialize_UsesEmojiFreeInlineFieldsAndRoundTripsAllMetadata()
    {
        var original = new TaskItem
        {
            Title = "05 Task Performance 1",
            IsCompleted = false,
            DueDate = new DateOnly(2026, 11, 22),
            Priority = TaskPriority.P1,
            Difficulty = 4,
            CourseAlias = "sqa",
            Term = "Prefinals",
            Section = "Lab Work",
            Labels = ["essay", "team", "prefinals"],
            InstructionFiles = ["05_Implementing_Automated_Functional_Tests/05_Task_Performance_1.pdf"],
            SubmissionFiles = ["completed/05_TP1_Deliverable.pdf"],
            LinkedFiles = ["ongoing/05_TP1.pdf"],
            NotePath = "ongoing/05_Task_Performance_1.md",
            LmsId = "59104098",
            LmsStatus = "Pending",
            SubTasks =
            [
                new TaskSubItem("Draft outline", true, new DateOnly(2026, 11, 19)),
                new TaskSubItem("Submit PDF", false)
            ]
        };

        var md = MarkdownTaskCodec.RenderDocument([original], "Software Quality Assurance");

        // Verify zero emojis in generated Markdown
        Assert.DoesNotContain("📅", md);
        Assert.DoesNotContain("✅", md);
        Assert.DoesNotContain("🔺", md);
        Assert.Contains("[due:: 2026-11-22]", md);
        Assert.Contains("[p:: 1]", md);
        Assert.Contains("[diff:: 4]", md);
        Assert.Contains("[term:: Prefinals]", md);
        Assert.Contains("[section:: Lab Work]", md);
        Assert.Contains("### Prefinals", md);
        Assert.Contains("[attachment:: 05_Implementing_Automated_Functional_Tests/05_Task_Performance_1.pdf]", md);
        Assert.Contains("[submission:: completed/05_TP1_Deliverable.pdf]", md);

        var parsed = MarkdownTaskCodec.Parse(md, "tasks.md", "Software_Quality_Assurance");
        Assert.Single(parsed);
        var roundTripped = parsed[0];
        Assert.Equal("05 Task Performance 1", roundTripped.Title);
        Assert.False(roundTripped.IsCompleted);
        Assert.Equal(new DateOnly(2026, 11, 22), roundTripped.DueDate);
        Assert.Equal(TaskPriority.P1, roundTripped.Priority);
        Assert.Equal(4, roundTripped.Difficulty);
        Assert.Equal("sqa", roundTripped.CourseAlias);
        Assert.Equal("Prefinals", roundTripped.Term);
        Assert.Equal("Lab Work", roundTripped.Section);
        Assert.Equal(["essay", "team", "prefinals"], roundTripped.Labels);
        Assert.Equal(["05_Implementing_Automated_Functional_Tests/05_Task_Performance_1.pdf"], roundTripped.InstructionFiles);
        Assert.Equal(["completed/05_TP1_Deliverable.pdf"], roundTripped.SubmissionFiles);
        Assert.Equal(["ongoing/05_TP1.pdf"], roundTripped.LinkedFiles);
        Assert.Equal("ongoing/05_Task_Performance_1.md", roundTripped.NotePath);
        Assert.Equal("59104098", roundTripped.LmsId);
        Assert.Equal(2, roundTripped.SubTasks.Count);
        Assert.True(roundTripped.SubTasks[0].IsCompleted);
        Assert.Equal(new DateOnly(2026, 11, 19), roundTripped.SubTasks[0].CompletedDate);
    }

    [Fact]
    public void QuickAddParser_ExtractsTermSectionTagsAndRelativeDate()
    {
        var today = new DateOnly(2026, 11, 19);
        var parsed = QuickAddParser.Parse(
            "Finish 04 Hands-on Activity 1 #sqa prelims /GroupWork @lab @urgent p1 d4 +3d",
            courses: null,
            todayOverride: today);
        var task = parsed.Task;

        Assert.Equal("Finish 04 Hands-on Activity 1", task.Title);
        Assert.Equal("sqa", task.CourseAlias);
        Assert.Equal("Prelims", task.Term);
        Assert.Equal("GroupWork", task.Section);
        Assert.Equal(TaskPriority.P1, task.Priority);
        Assert.Equal(4, task.Difficulty);
        Assert.Equal(new DateOnly(2026, 11, 22), task.DueDate);
        Assert.Contains("lab", task.Labels);
        Assert.Contains("urgent", task.Labels);
        Assert.Contains("prelims", task.Labels);
    }

    [Fact]
    public async Task BasinService_EnrichesRealBasinTasksWithTermsTagsAndAttachments()
    {
        const string basinRoot = "/home/marky/Documents/School-Files/YR4_01";
        if (!Directory.Exists(basinRoot))
            return;

        var svc = new BasinService();
        var tasks = await svc.LoadAllTasksAsync(basinRoot);

        Assert.NotEmpty(tasks);
        Assert.True(File.Exists(ScholarFlowDatabase.GetDatabasePath(basinRoot)));
        Assert.Contains(tasks, t => t.Term is "Prelims" or "Midterms" or "Prefinals" or "Finals");
        Assert.Contains(tasks, t => t.Labels.Count > 0);
        Assert.Contains(tasks, t => t.InstructionFiles.Count > 0);

        // Verify FTS5 full-text search finds tasks in SQLite
        var ftsMatches = ScholarFlowDatabase.SearchTaskIdsFts(basinRoot, "Performance");
        Assert.NotEmpty(ftsMatches);

        // Verify EnrichTaskMetadataAndFiles is 100% idempotent (second pass returns false so it never loops)
        var courses = await svc.LoadCoursesAsync(basinRoot);
        foreach (var course in courses.Where(c => !c.IsArchived))
        {
            var termMap = BasinService.LoadCourseTermMap(basinRoot, course.FolderName);
            foreach (var t in tasks.Where(x => x.CourseFolder == course.FolderName))
            {
                bool changedAgain = BasinService.EnrichTaskMetadataAndFiles(basinRoot, course, t, termMap);
                Assert.False(changedAgain, $"Task '{t.Title}' in '{course.FolderName}' was not idempotent on second enrichment.");
            }
        }
    }

    [Fact]
    public async Task FirstClassChildTasks_RoundTripMetadataAndPersistInSqliteDatabase()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-db-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var svc = new BasinService();
            await svc.InitializeOrOpenAsync(tempRoot, "TestBasin");

            var parent = new TaskItem
            {
                Title = "05 Task Performance 1",
                DueDate = new DateOnly(2026, 11, 25),
                Priority = TaskPriority.P1,
                Term = "Prefinals",
                Labels = ["tp"],
                SourceMarkdownPath = BasinService.GetInboxPath(tempRoot),
                ChildTasks =
                [
                    new TaskItem
                    {
                        Title = "Write automated test cases",
                        DueDate = new DateOnly(2026, 11, 22),
                        Priority = TaskPriority.P2,
                        Difficulty = 3,
                        Labels = ["qa", "unit-test"],
                        InstructionFiles = ["ongoing/test_plan.pdf"]
                    }
                ]
            };

            await MarkdownTaskCodec.UpdateTaskInFileAsync(tempRoot, parent);

            var loaded = await svc.LoadAllTasksAsync(tempRoot);
            Assert.Single(loaded);
            Assert.Single(loaded[0].ChildTasks);

            var child = loaded[0].ChildTasks[0];
            Assert.Equal("Write automated test cases", child.Title);
            Assert.Equal(loaded[0].Id, child.ParentTaskId);
            Assert.Equal(new DateOnly(2026, 11, 22), child.DueDate);
            Assert.Equal(TaskPriority.P2, child.Priority);
            Assert.Equal(3, child.Difficulty);
            Assert.Contains("qa", child.Labels);
            Assert.Contains("ongoing/test_plan.pdf", child.InstructionFiles);

            // Update child task directly via UpdateTaskInFileAsync
            child.IsCompleted = true;
            child.CompletedDate = new DateOnly(2026, 11, 20);
            await MarkdownTaskCodec.UpdateTaskInFileAsync(tempRoot, child);

            var reloaded = await svc.LoadAllTasksAsync(tempRoot);
            Assert.True(reloaded[0].ChildTasks[0].IsCompleted);
            Assert.Equal(new DateOnly(2026, 11, 20), reloaded[0].ChildTasks[0].CompletedDate);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void MergeLmsTasks_PreservesUserSubtasksDifficultyAndManualCompletion()
    {
        var existing = new TaskItem
        {
            Title = "03 Task Performance 1",
            IsCompleted = true,
            CompletedDate = new DateOnly(2026, 11, 18),
            Difficulty = 5,
            Term = "Midterms",
            Labels = ["custom-tag"],
            SubmissionFiles = ["completed/my_work.pdf"],
            LmsId = "59104093",
            LmsStatus = "Pending",
            SubTasks = [new TaskSubItem("My personal checklist item", true)]
        };

        var incoming = new TaskItem
        {
            Title = "03 Task Performance 1 (Updated)",
            IsCompleted = false,
            DueDate = new DateOnly(2026, 11, 25),
            Difficulty = 1,
            InstructionFiles = ["03_Module/03_Task_Performance_1.pdf"],
            LmsId = "59104093",
            LmsStatus = "Submitted",
            LmsScore = "48/50"
        };

        var merged = MarkdownTaskCodec.MergeLmsTasks([existing], [incoming]);

        Assert.Single(merged);
        Assert.True(merged[0].IsCompleted);
        Assert.Equal(5, merged[0].Difficulty);
        Assert.Equal("Midterms", merged[0].Term);
        Assert.Contains("custom-tag", merged[0].Labels);
        Assert.Single(merged[0].InstructionFiles);
        Assert.Single(merged[0].SubmissionFiles);
        Assert.Single(merged[0].SubTasks);
        Assert.Equal("03 Task Performance 1 (Updated)", merged[0].Title);
        Assert.Equal("48/50", merged[0].LmsScore);
        Assert.Equal(new DateOnly(2026, 11, 25), merged[0].DueDate);
    }
}
