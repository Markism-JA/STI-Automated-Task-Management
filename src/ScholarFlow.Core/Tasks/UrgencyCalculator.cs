using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Tasks;

public enum UrgencyTier
{
    Low,
    Normal,
    Elevated,
    High,
    Critical,
}

public sealed record UrgencyBreakdown(
    int Score,
    UrgencyTier Tier,
    double DeadlinePoints,
    double LeadTimePoints,
    double PriorityPoints,
    double DifficultyPoints,
    double LmsPenaltyPoints,
    string Summary
);

/// <summary>
/// Continuous Academic Urgency Calculator v2 (0-100 scale):
/// Combines smooth exponential deadline decay, Difficulty x Lead-Time pressure,
/// Todoist P1-P4 priority weights, base difficulty, and LMS missing/overdue signals.
/// </summary>
public static class UrgencyCalculator
{
    public static int CalculateScore(TaskItem task, DateOnly? today = null) =>
        Explain(task, today).Score;

    public static UrgencyBreakdown Explain(TaskItem task, DateOnly? today = null)
    {
        if (task.IsCompleted)
            return new UrgencyBreakdown(0, UrgencyTier.Low, 0, 0, 0, 0, 0, "0 · Completed");

        var refDate = today ?? DateOnly.FromDateTime(DateTime.Today);
        var diff = Math.Clamp(task.Difficulty, 1, 5);

        double deadlinePts = 0;
        double leadTimePts = 0;
        var factors = new List<string>();

        if (task.DueDate.HasValue)
        {
            int daysRemaining = task.DueDate.Value.DayNumber - refDate.DayNumber;
            if (daysRemaining < 0)
            {
                deadlinePts = 36.0 + Math.Min(6.0, Math.Abs(daysRemaining) * 1.2);
                leadTimePts = (diff - 1) * 4.5;
                factors.Add($"Overdue {Math.Abs(daysRemaining)}d (+{deadlinePts:F0})");
            }
            else if (daysRemaining == 0)
            {
                deadlinePts = 34.0;
                leadTimePts = (diff - 1) * 4.5;
                factors.Add($"Due today (+{deadlinePts:F0})");
            }
            else if (daysRemaining <= 21)
            {
                deadlinePts = 34.0 * Math.Exp(-0.14 * daysRemaining);
                leadTimePts = Math.Clamp(
                    (diff - 1) * 4.5 / Math.Max(1.0, daysRemaining / 2.0),
                    0.0,
                    18.0
                );
                factors.Add($"Due in {daysRemaining}d (+{deadlinePts:F0})");
            }
            else
            {
                deadlinePts = 2.0;
            }
        }

        if (leadTimePts >= 1.0)
            factors.Add($"D{diff} Lead (+{leadTimePts:F0})");

        double priorityPts = task.Priority switch
        {
            TaskPriority.P1 => 24.0,
            TaskPriority.P2 => 16.0,
            TaskPriority.P3 => 8.0,
            _ => 0.0,
        };
        if (priorityPts > 0)
            factors.Add($"P{(int)task.Priority} (+{priorityPts:F0})");

        double difficultyPts = diff * 2.0;
        factors.Add($"D{diff} (+{difficultyPts:F0})");

        double lmsPenalty = string.Equals(
            task.LmsStatus,
            "Missing",
            StringComparison.OrdinalIgnoreCase
        )
            ? 6.0
            : 0.0;
        if (lmsPenalty > 0)
            factors.Add($"Missing (+{lmsPenalty:F0})");

        int total = (int)
            Math.Clamp(
                Math.Round(deadlinePts + leadTimePts + priorityPts + difficultyPts + lmsPenalty),
                0,
                100
            );
        var tier = total switch
        {
            >= 75 => UrgencyTier.Critical,
            >= 55 => UrgencyTier.High,
            >= 35 => UrgencyTier.Elevated,
            >= 15 => UrgencyTier.Normal,
            _ => UrgencyTier.Low,
        };

        var summary = $"{total} · {tier} ({string.Join(" · ", factors)})";
        return new UrgencyBreakdown(
            total,
            tier,
            deadlinePts,
            leadTimePts,
            priorityPts,
            difficultyPts,
            lmsPenalty,
            summary
        );
    }
}
