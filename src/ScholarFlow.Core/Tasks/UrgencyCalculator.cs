using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Tasks;

/// <summary>
/// Combines Todoist P1-P4 priority with the due-date urgency curve and task difficulty.
/// </summary>
public static class UrgencyCalculator
{
    public static int CalculateUrgency(DateOnly? dueDate, DateOnly? today = null)
    {
        if (!dueDate.HasValue)
            return 0;

        var refDate = today ?? DateOnly.FromDateTime(DateTime.Today);
        var daysRemaining = dueDate.Value.DayNumber - refDate.DayNumber;

        return daysRemaining switch
        {
            < 0 => 6,
            <= 2 => 5,
            <= 7 => 4,
            <= 14 => 3,
            <= 30 => 2,
            _ => 1
        };
    }

    public static int CalculateScore(TaskItem task, DateOnly? today = null)
    {
        if (task.IsCompleted)
            return 0;

        var urgency = CalculateUrgency(task.DueDate, today);
        var priorityBoost = task.Priority switch
        {
            TaskPriority.P1 => 10,
            TaskPriority.P2 => 6,
            TaskPriority.P3 => 3,
            _ => 0
        };

        return (urgency * 2) + Math.Clamp(task.Difficulty, 1, 5) + priorityBoost;
    }
}
