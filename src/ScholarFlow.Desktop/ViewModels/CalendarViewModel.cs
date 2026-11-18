using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;

namespace ScholarFlow.Desktop.ViewModels;

public sealed record CalendarDayCell(
    DateOnly Date,
    bool IsCurrentMonth,
    bool IsToday,
    bool IsSelected,
    IReadOnlyList<TaskItem> Tasks,
    int TotalUrgency,
    TaskPriority? HighestPriority);

/// <summary>
/// Manages the 42-cell Month Grid and Selected-Day Agenda for the Calendar view.
/// </summary>
public sealed class CalendarViewModel
{
    public DateOnly DisplayedMonth { get; private set; }
    public DateOnly SelectedDate { get; private set; }
    public IReadOnlyList<CalendarDayCell> MonthCells { get; private set; } = [];
    public IReadOnlyList<TaskItem> SelectedDayTasks { get; private set; } = [];

    private IReadOnlyList<TaskItem> _allTasks = [];

    public event EventHandler? Changed;

    public CalendarViewModel()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        DisplayedMonth = new DateOnly(today.Year, today.Month, 1);
        SelectedDate = today;
    }

    public void UpdateTasks(IReadOnlyList<TaskItem> allTasks)
    {
        _allTasks = allTasks;
        Rebuild();
    }

    public void PreviousMonth()
    {
        DisplayedMonth = DisplayedMonth.AddMonths(-1);
        Rebuild();
    }

    public void NextMonth()
    {
        DisplayedMonth = DisplayedMonth.AddMonths(1);
        Rebuild();
    }

    public void GoToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        DisplayedMonth = new DateOnly(today.Year, today.Month, 1);
        SelectedDate = today;
        Rebuild();
    }

    public void SelectDate(DateOnly date)
    {
        SelectedDate = date;
        if (date.Year != DisplayedMonth.Year || date.Month != DisplayedMonth.Month)
            DisplayedMonth = new DateOnly(date.Year, date.Month, 1);
        Rebuild();
    }

    private void Rebuild()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstOfMonth = new DateOnly(DisplayedMonth.Year, DisplayedMonth.Month, 1);
        int offset = (int)firstOfMonth.DayOfWeek; // Sunday = 0
        var gridStart = firstOfMonth.AddDays(-offset);

        var tasksByDate = _allTasks
            .Where(t => t.DueDate.HasValue)
            .GroupBy(t => t.DueDate!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TaskItem>)g
                    .OrderBy(t => t.IsCompleted)
                    .ThenByDescending(t => UrgencyCalculator.CalculateScore(t, today))
                    .ToList());

        var cells = new List<CalendarDayCell>(42);
        for (int i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            tasksByDate.TryGetValue(date, out var dayTasks);
            dayTasks ??= [];

            var activeTasks = dayTasks.Where(t => !t.IsCompleted).ToList();
            int totalUrgency = activeTasks.Sum(t => UrgencyCalculator.CalculateScore(t, today));
            TaskPriority? highest = activeTasks.Count > 0
                ? activeTasks.Min(t => t.Priority)
                : null;

            cells.Add(new CalendarDayCell(
                Date: date,
                IsCurrentMonth: date.Month == DisplayedMonth.Month && date.Year == DisplayedMonth.Year,
                IsToday: date == today,
                IsSelected: date == SelectedDate,
                Tasks: dayTasks,
                TotalUrgency: totalUrgency,
                HighestPriority: highest));
        }

        MonthCells = cells;
        SelectedDayTasks = tasksByDate.TryGetValue(SelectedDate, out var sel) ? sel : [];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
