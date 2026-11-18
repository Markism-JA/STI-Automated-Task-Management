using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;

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
            Labels = ["essay", "team"],
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

        var parsed = MarkdownTaskCodec.Parse(md, "tasks.md", "Software_Quality_Assurance");
        Assert.Single(parsed);
        var roundTripped = parsed[0];
        Assert.Equal("05 Task Performance 1", roundTripped.Title);
        Assert.False(roundTripped.IsCompleted);
        Assert.Equal(new DateOnly(2026, 11, 22), roundTripped.DueDate);
        Assert.Equal(TaskPriority.P1, roundTripped.Priority);
        Assert.Equal(4, roundTripped.Difficulty);
        Assert.Equal("sqa", roundTripped.CourseAlias);
        Assert.Equal(["essay", "team"], roundTripped.Labels);
        Assert.Equal(["ongoing/05_TP1.pdf"], roundTripped.LinkedFiles);
        Assert.Equal("ongoing/05_Task_Performance_1.md", roundTripped.NotePath);
        Assert.Equal("59104098", roundTripped.LmsId);
        Assert.Equal(2, roundTripped.SubTasks.Count);
        Assert.True(roundTripped.SubTasks[0].IsCompleted);
        Assert.Equal(new DateOnly(2026, 11, 19), roundTripped.SubTasks[0].CompletedDate);
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
            LmsId = "59104093",
            LmsStatus = "Submitted",
            LmsScore = "48/50"
        };

        var merged = MarkdownTaskCodec.MergeLmsTasks([existing], [incoming]);

        Assert.Single(merged);
        Assert.True(merged[0].IsCompleted);
        Assert.Equal(5, merged[0].Difficulty);
        Assert.Single(merged[0].SubTasks);
        Assert.Equal("03 Task Performance 1 (Updated)", merged[0].Title);
        Assert.Equal("48/50", merged[0].LmsScore);
        Assert.Equal(new DateOnly(2026, 11, 25), merged[0].DueDate);
    }
}
