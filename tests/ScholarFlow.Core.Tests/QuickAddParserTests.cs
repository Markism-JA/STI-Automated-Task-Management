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
        Assert.Equal(TaskPriority.P2, result.Task.Priority);
        Assert.True(result.Task.IsInbox);
    }
}
