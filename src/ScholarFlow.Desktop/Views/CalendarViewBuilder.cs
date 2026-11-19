using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Builds the interactive Material Month Grid + Selected-Day Agenda Calendar View in pure C#.
/// </summary>
public static class CalendarViewBuilder
{
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static Control Build(MainWindowViewModel vm)
    {
        var cal = vm.Calendar;

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,2.8*,1.35*"),
            MaxWidth = 960,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(20, 12, 20, 12),
        };

        // 1. Month Header Bar
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleStack.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.CalendarMonth,
                22,
                ThemeService.Brush(ThemeService.AccentPrimary)
            )
        );
        titleStack.Children.Add(
            new TextBlock
            {
                Text = cal.DisplayedMonth.ToString("MMMM yyyy"),
                FontSize = 19,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        header.Children.Add(titleStack);

        var navBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        navBtns.Children.Add(
            UiBuilder.ActionButton(
                "",
                MaterialIconKind.ChevronLeft,
                () =>
                {
                    cal.PreviousMonth();
                },
                tooltip: "Previous Month ([)"
            )
        );
        navBtns.Children.Add(
            UiBuilder.ActionButton(
                "Today",
                MaterialIconKind.CalendarToday,
                () =>
                {
                    cal.GoToToday();
                }
            )
        );
        navBtns.Children.Add(
            UiBuilder.ActionButton(
                "",
                MaterialIconKind.ChevronRight,
                () =>
                {
                    cal.NextMonth();
                },
                tooltip: "Next Month (])"
            )
        );
        Grid.SetColumn(navBtns, 1);
        header.Children.Add(navBtns);
        root.Children.Add(header);

        // 2. Day-of-week header row
        var dowGrid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var d in DayNames)
        {
            dowGrid.Children.Add(
                new TextBlock
                {
                    Text = d,
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            );
        }
        Grid.SetRow(dowGrid, 1);
        root.Children.Add(dowGrid);

        // 3. 42-cell Month Grid
        var monthGrid = new UniformGrid { Columns = 7, Rows = 6 };
        foreach (var cell in cal.MonthCells)
        {
            monthGrid.Children.Add(BuildDayCell(vm, cal, cell));
        }
        Grid.SetRow(monthGrid, 2);
        root.Children.Add(monthGrid);

        // 4. Selected-Day Agenda Strip
        var agendaCard = BuildDayAgenda(vm, cal);
        Grid.SetRow(agendaCard, 3);
        root.Children.Add(agendaCard);

        return root;
    }

    private static Control BuildDayCell(
        MainWindowViewModel vm,
        CalendarViewModel cal,
        CalendarDayCell cell
    )
    {
        var borderBrush =
            cell.IsSelected ? ThemeService.Brush(ThemeService.AccentPrimary)
            : cell.IsToday ? ThemeService.Brush(ThemeService.PriorityP2Fg)
            : ThemeService.Brush(ThemeService.BorderSubtle);

        var bg =
            cell.IsSelected ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : cell.IsCurrentMonth ? ThemeService.Brush(ThemeService.BgSurface)
            : ThemeService.Brush(ThemeService.BgBase);

        var stack = new StackPanel { Spacing = 3 };

        var topRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        topRow.Children.Add(
            new TextBlock
            {
                Text = cell.Date.Day.ToString(),
                FontSize = 12,
                FontWeight = cell.IsToday || cell.IsSelected ? FontWeight.Bold : FontWeight.Medium,
                Foreground = cell.IsCurrentMonth
                    ? ThemeService.Brush(ThemeService.TextPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary),
            }
        );

        if (cell.TotalUrgency > 0)
        {
            var (fg, pillBg) = cell.HighestPriority.HasValue
                ? ThemeService.GetPriorityBrushes(cell.HighestPriority.Value)
                : (
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated)
                );

            var badge = UiBuilder.Pill(
                $"{cell.Tasks.Count(t => !t.IsCompleted)}",
                fg,
                pillBg,
                fontSize: 10
            );
            Grid.SetColumn(badge, 1);
            topRow.Children.Add(badge);
        }
        stack.Children.Add(topRow);

        foreach (var t in cell.Tasks.Take(2))
        {
            var capturedTask = t;
            var (pFg, pBg) = t.IsCompleted
                ? (
                    ThemeService.Brush(ThemeService.SuccessFg),
                    ThemeService.Brush(ThemeService.SuccessBg)
                )
                : ThemeService.GetPriorityBrushes(t.Priority);

            var pillBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = t.Title,
                    FontSize = 10.5,
                    Foreground = pFg,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                },
                Background = pBg,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1.5),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            pillBtn.Click += (_, e) =>
            {
                e.Handled = true;
                cal.SelectDate(cell.Date);
                vm.SelectTask(capturedTask, openInspector: true);
            };
            stack.Children.Add(pillBtn);
        }

        if (cell.Tasks.Count > 2)
        {
            stack.Children.Add(
                new TextBlock
                {
                    Text = $"+{cell.Tasks.Count - 2} more",
                    FontSize = 10,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                }
            );
        }

        var btn = new Button
        {
            Content = stack,
            Background = bg,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(cell.IsSelected || cell.IsToday ? 1.5 : 0.5),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(2),
            Padding = new Thickness(6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        btn.Click += (_, _) =>
        {
            cal.SelectDate(cell.Date);
            if (cell.Tasks.Count > 0)
                vm.SelectTask(cell.Tasks[0], openInspector: true);
            else
                vm.SelectTask(null);
        };
        btn.DoubleTapped += (_, _) =>
        {
            if (cell.Tasks.Count > 0)
                vm.OpenInspector(cell.Tasks[0]);
            else
                vm.OpenQuickAddModal();
        };

        return btn;
    }

    private static Control BuildDayAgenda(MainWindowViewModel vm, CalendarViewModel cal)
    {
        var panel = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*"),
            Margin = new Thickness(0, 10, 0, 0),
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 6),
        };
        header.Children.Add(
            UiBuilder.Icon(
                MaterialIconKind.FormatListChecks,
                16,
                ThemeService.Brush(ThemeService.AccentPrimary)
            )
        );
        header.Children.Add(
            new TextBlock
            {
                Text =
                    $"Agenda for {cal.SelectedDate:dddd, MMM d, yyyy} ({cal.SelectedDayTasks.Count} task(s))",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );
        panel.Children.Add(header);

        var listStack = new StackPanel { Spacing = 4 };
        if (cal.SelectedDayTasks.Count == 0)
        {
            listStack.Children.Add(
                new TextBlock
                {
                    Text =
                        "No tasks due on this date. Type in the Quick Add bar above to schedule a task for this day.",
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    Margin = new Thickness(4, 8, 0, 0),
                }
            );
        }
        else
        {
            foreach (var task in cal.SelectedDayTasks)
            {
                var (pFg, pBg) = ThemeService.GetPriorityBrushes(task.Priority);
                var row = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
                };

                var checkBtn = UiBuilder.ActionButton(
                    "",
                    task.IsCompleted
                        ? MaterialIconKind.CheckCircle
                        : MaterialIconKind.CircleOutline,
                    async () => await vm.ToggleTaskCompletedAsync(task)
                );
                row.Children.Add(checkBtn);

                var titleBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = task.Title,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        FontSize = 12.5,
                        TextDecorations = task.IsCompleted ? TextDecorations.Strikethrough : null,
                    },
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                titleBtn.Click += (_, _) => vm.SelectTask(task, openInspector: true);
                Grid.SetColumn(titleBtn, 1);
                row.Children.Add(titleBtn);

                var rightBadges = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (!string.IsNullOrWhiteSpace(task.PrimaryPdfFile))
                {
                    rightBadges.Children.Add(UiBuilder.GhostButton(
                        "View PDF",
                        MaterialIconKind.FilePdfBox,
                        () => vm.OpenTaskFile(task, task.PrimaryPdfFile!)));
                }
                if (!string.IsNullOrWhiteSpace(task.Term))
                {
                    rightBadges.Children.Add(UiBuilder.Pill(
                        task.Term,
                        ThemeService.Brush(ThemeService.TextSecondary),
                        ThemeService.Brush(ThemeService.BgElevated)));
                }
                if (!string.IsNullOrWhiteSpace(task.CourseAlias))
                {
                    rightBadges.Children.Add(UiBuilder.Pill(
                        $"#{task.CourseAlias}",
                        ThemeService.Brush(ThemeService.AccentPrimary),
                        ThemeService.Brush(ThemeService.AccentSoftBg)));
                }
                rightBadges.Children.Add(UiBuilder.Pill($"P{(int)task.Priority}", pFg, pBg));
                rightBadges.Children.Add(UiBuilder.GhostButton(
                    "Inspect",
                    MaterialIconKind.TuneVariant,
                    () => vm.SelectTask(task, openInspector: true)));

                Grid.SetColumn(rightBadges, 2);
                Grid.SetColumnSpan(rightBadges, 2);
                row.Children.Add(rightBadges);

                listStack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.BgSurface),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 4),
                        Child = row,
                    }
                );
            }
        }

        var scroll = new ScrollViewer { Content = listStack };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Child = panel,
        };
    }
}
