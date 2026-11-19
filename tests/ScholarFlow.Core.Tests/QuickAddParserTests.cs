using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;

namespace ScholarFlow.Core.Tests;

public sealed class QuickAddParserTests
{
    [Fact]
    public void Parse_ExtractsNaturalDatePriorityDifficultyCourseAndLabels()
    {
        var courses = new List<BasinCourseEntry>
        {
            new() { Title = "Platform Technology (Operating Systems)", FolderName = "Platform_Technology_(Operating_Systems)", Alias = "ptos" },
            new() { Title = "Software Quality Assurance", FolderName = "Software_Quality_Assurance", Alias = "sqa" }
        };

        var today = new DateOnly(2026, 11, 19); // Thursday
        var result = QuickAddParser.Parse("Finish SQA lab 2 tomorrow p1 d4 #sqa @lab @urgent", courses, today);

        Assert.Equal("Finish SQA lab 2", result.Task.Title);
        Assert.Equal(new DateOnly(2026, 11, 20), result.Task.DueDate);
        Assert.Equal(TaskPriority.P1, result.Task.Priority);
        Assert.Equal(4, result.Task.Difficulty);
        Assert.Equal("Software_Quality_Assurance", result.Task.CourseFolder);
        Assert.Equal("sqa", result.Task.CourseAlias);
        Assert.Equal(["lab", "urgent"], result.Task.Labels);
        Assert.True(result.Chips.Count >= 4);
    }

    [Fact]
    public void Parse_NextWeekdayResolvesToUpcomingOccurrence()
    {
        var today = new DateOnly(2026, 11, 19); // Thursday
        var result = QuickAddParser.Parse("Review notes next mon p2", todayOverride: today);

        Assert.Equal("Review notes", result.Task.Title);
        Assert.Equal(new DateOnly(2026, 11, 23), result.Task.DueDate);
        Assert.Equal(new TimeOnly(0, 0), result.Task.DueTime);
        Assert.Equal(TaskPriority.P2, result.Task.Priority);
        Assert.True(result.Task.IsInbox);
    }

    [Fact]
    public void Parse_ExtractsDueTimeAndDefaultsDateOnlyToMidnight()
    {
        var today = new DateOnly(2026, 11, 19);

        var withTime = QuickAddParser.Parse("Submit SQA quiz tomorrow 5:30pm p1", todayOverride: today);
        Assert.Equal("Submit SQA quiz", withTime.Task.Title);
        Assert.Equal(new DateOnly(2026, 11, 20), withTime.Task.DueDate);
        Assert.Equal(new TimeOnly(17, 30), withTime.Task.DueTime);
        Assert.Contains(withTime.Chips, c => c.Kind == "Time" && c.Text == "5:30 PM");

        var timeOnly = QuickAddParser.Parse("Standup at 9am", todayOverride: today);
        Assert.Equal("Standup", timeOnly.Task.Title);
        Assert.Equal(today, timeOnly.Task.DueDate);
        Assert.Equal(new TimeOnly(9, 0), timeOnly.Task.DueTime);
    }

    [Fact]
    public async Task UpdateTaskInFileAsync_InboxTask_DoesNotDuplicateOnTimeUpdate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"scholarflow_inbox_dup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, ".scholarflow"));
        try
        {
            var inboxPath = Path.Combine(tempRoot, ".scholarflow", "inbox.md");
            var task = QuickAddParser.Parse("Custom inbox task tomorrow", todayOverride: new DateOnly(2026, 11, 19)).Task;
            task.SourceMarkdownPath = inboxPath;
            await ScholarFlow.Core.Markdown.MarkdownTaskCodec.SaveDocumentAsync(inboxPath, [task], "Inbox", syncToDb: true);

            // Simulate task loaded from SQLite where Id may be prefixed with inbox:
            var loaded = (await ScholarFlow.Core.Database.ScholarFlowDatabase.SyncAndLoadAllTasksAsync(tempRoot, []))
                .Single(t => t.Title == "Custom inbox task");
            loaded.DueTime = new TimeOnly(17, 0);

            await ScholarFlow.Core.Markdown.MarkdownTaskCodec.UpdateTaskInFileAsync(tempRoot, loaded);
            var afterUpdate = await ScholarFlow.Core.Markdown.MarkdownTaskCodec.LoadFileAsync(inboxPath);

            var single = Assert.Single(afterUpdate);
            Assert.Equal(new TimeOnly(17, 0), single.DueTime);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
