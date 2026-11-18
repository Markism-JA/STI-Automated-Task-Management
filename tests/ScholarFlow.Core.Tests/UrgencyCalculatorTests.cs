using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;

namespace ScholarFlow.Core.Tests;

public sealed class UrgencyCalculatorTests
{
    [Fact]
    public void Explain_HighDifficultyTaskDueIn4DaysOutranksEasyTaskDueIn3Days()
    {
        var today = new DateOnly(2026, 11, 19);

        var hardDueIn4Days = new TaskItem
        {
            Title = "Hard Project",
            DueDate = today.AddDays(4),
            Difficulty = 5,
            Priority = TaskPriority.P3
        };

        var easyDueIn3Days = new TaskItem
        {
            Title = "Quick Quiz",
            DueDate = today.AddDays(3),
            Difficulty = 1,
            Priority = TaskPriority.P3
        };

        var hardBreakdown = UrgencyCalculator.Explain(hardDueIn4Days, today);
        var easyBreakdown = UrgencyCalculator.Explain(easyDueIn3Days, today);

        Assert.True(hardBreakdown.Score > easyBreakdown.Score, $"Expected hard ({hardBreakdown.Score}) > easy ({easyBreakdown.Score})");
        Assert.True(hardBreakdown.LeadTimePoints > 0);
        Assert.Contains("D5 Lead", hardBreakdown.Summary);
    }

    [Fact]
    public void Explain_OverdueP1TaskReachesCriticalTier()
    {
        var today = new DateOnly(2026, 11, 19);
        var overdue = new TaskItem
        {
            Title = "Overdue Lab",
            DueDate = today.AddDays(-2),
            Priority = TaskPriority.P1,
            Difficulty = 4,
            LmsStatus = "Missing"
        };

        var breakdown = UrgencyCalculator.Explain(overdue, today);
        Assert.Equal(UrgencyTier.Critical, breakdown.Tier);
        Assert.True(breakdown.Score >= 85);
    }
}
