using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Builds the Contextual Right Slide-Over Task Inspector Drawer with rendered Markdown preview,
/// subtasks checklist, explainable Urgency v2 breakdown, and eLMS submission launcher.
/// </summary>
public static class InspectorPaneBuilder
{
    public static Control Build(MainWindowViewModel vm)
    {
        var task = vm.SelectedTask;
        if (task is null)
        {
            return new Border
            {
                Background = ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 8,
                    Children =
                    {
                        UiBuilder.Icon(MaterialIconKind.CursorDefaultClickOutline, 24, ThemeService.Brush(ThemeService.TextMuted)),
                        new TextBlock
                        {
                            Text = "Select a task to inspect details.",
                            FontSize = 12.5,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                            TextAlignment = TextAlignment.Center
                        }
                    }
                }
            };
        }

        var rootGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*")
        };

        // 1. Drawer Top Header Bar (Course Breadcrumb + Actions + Close X)
        var headerBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto"),
            Margin = new Thickness(14, 10, 10, 8)
        };

        var crumbRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        crumbRow.Children.Add(UiBuilder.Icon(
            string.IsNullOrWhiteSpace(task.CourseAlias) ? MaterialIconKind.InboxOutline : MaterialIconKind.BookOutline,
            14,
            ThemeService.Brush(ThemeService.TextSecondary)));
        crumbRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(task.CourseAlias) ? "Inbox" : $"#{task.CourseAlias}",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center
        });
        headerBar.Children.Add(crumbRow);

        var completeBtn = UiBuilder.GhostButton(
            task.IsCompleted ? "Reopen" : "Complete",
            task.IsCompleted ? MaterialIconKind.Restore : MaterialIconKind.Check,
            async () => await vm.ToggleTaskCompletedAsync(task),
            isActive: task.IsCompleted);
        completeBtn.Margin = new Thickness(0, 0, 4, 0);
        Grid.SetColumn(completeBtn, 1);
        headerBar.Children.Add(completeBtn);

        var delBtn = UiBuilder.IconButton(
            MaterialIconKind.TrashCanOutline,
            async () => await vm.DeleteTaskAsync(task),
            15,
            ThemeService.Brush(ThemeService.TextMuted));
        Grid.SetColumn(delBtn, 2);
        headerBar.Children.Add(delBtn);

        var closeBtn = UiBuilder.IconButton(
            MaterialIconKind.Close,
            () => vm.CloseInspector(),
            16,
            ThemeService.Brush(ThemeService.TextSecondary));
        Grid.SetColumn(closeBtn, 3);
        headerBar.Children.Add(closeBtn);

        rootGrid.Children.Add( new StackPanel
        {
            Children =
            {
                headerBar,
                UiBuilder.HairlineDivider()
            }
        });

        // 2. Scrollable Drawer Body
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(14, 12, 14, 18) };

        // Editable Title & Due Date
        var titleBox = new TextBox
        {
            Text = task.Title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Watermark = "Task title"
        };
        stack.Children.Add(titleBox);

        var dueGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        var dueBox = new TextBox
        {
            Text = task.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            Watermark = "Due Date (YYYY-MM-DD)",
            FontSize = 12
        };
        dueGrid.Children.Add(dueBox);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var quickDateRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 0, 0, 0)
        };
        quickDateRow.Children.Add(UiBuilder.GhostButton("Today", null, () => dueBox.Text = today.ToString("yyyy-MM-dd")));
        quickDateRow.Children.Add(UiBuilder.GhostButton("Tmrw", null, () => dueBox.Text = today.AddDays(1).ToString("yyyy-MM-dd")));
        quickDateRow.Children.Add(UiBuilder.GhostButton("+7d", null, () => dueBox.Text = today.AddDays(7).ToString("yyyy-MM-dd")));
        Grid.SetColumn(quickDateRow, 1);
        dueGrid.Children.Add(quickDateRow);
        stack.Children.Add(dueGrid);

        // Priority & Difficulty Compact Row
        var selectedPriority = task.Priority;
        var selectedDifficulty = task.Difficulty;

        var metaSelectors = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var p in new[] { TaskPriority.P1, TaskPriority.P2, TaskPriority.P3, TaskPriority.P4 })
        {
            var isCur = p == selectedPriority;
            var (pFg, _) = ThemeService.GetPriorityBrushes(p);
            var btn = UiBuilder.GhostButton(
                $"P{(int)p}",
                MaterialIconKind.FlagOutline,
                async () => await vm.SaveInspectorEditsAsync(task, titleBox.Text ?? task.Title, dueBox.Text, p, selectedDifficulty),
                isActive: isCur,
                customFg: isCur ? pFg : null);
            btn.Margin = new Thickness(0, 0, 4, 4);
            metaSelectors.Children.Add(btn);
        }

        for (int d = 1; d <= 5; d++)
        {
            int capturedD = d;
            var btn = UiBuilder.GhostButton(
                $"D{d}",
                null,
                async () => await vm.SaveInspectorEditsAsync(task, titleBox.Text ?? task.Title, dueBox.Text, selectedPriority, capturedD),
                isActive: d == selectedDifficulty);
            btn.Margin = new Thickness(0, 0, 4, 4);
            metaSelectors.Children.Add(btn);
        }

        var saveBtn = UiBuilder.ActionButton(
            "Save",
            MaterialIconKind.Check,
            async () => await vm.SaveInspectorEditsAsync(task, titleBox.Text ?? task.Title, dueBox.Text, selectedPriority, selectedDifficulty),
            isPrimary: true);
        saveBtn.Margin = new Thickness(4, 0, 0, 4);
        metaSelectors.Children.Add(saveBtn);
        stack.Children.Add(metaSelectors);

        // Explainable Urgency v2 Summary Strip
        var urgency = UrgencyCalculator.Explain(task);
        var (uFg, uBg) = urgency.Tier switch
        {
            UrgencyTier.Critical => ThemeService.GetPriorityBrushes(TaskPriority.P1),
            UrgencyTier.High => ThemeService.GetPriorityBrushes(TaskPriority.P2),
            UrgencyTier.Elevated => ThemeService.GetPriorityBrushes(TaskPriority.P3),
            _ => ThemeService.GetPriorityBrushes(TaskPriority.P4)
        };

        stack.Children.Add(new Border
        {
            Background = uBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Urgency {urgency.Score}/100 · {urgency.Tier}",
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = uFg
                    },
                    new TextBlock
                    {
                        Text = urgency.Summary,
                        FontSize = 11,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        });

        // eLMS Submission & Grading Integration Card
        if (!string.IsNullOrWhiteSpace(task.LmsId) || !string.IsNullOrWhiteSpace(task.LmsUrl))
        {
            var lmsStack = new StackPanel { Spacing = 6 };
            var lmsHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            lmsHeader.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    UiBuilder.Icon(MaterialIconKind.SchoolOutline, 15, ThemeService.Brush(ThemeService.TextSecondary)),
                    new TextBlock
                    {
                        Text = $"eLMS #{task.LmsId}",
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary)
                    }
                }
            });

            var statusBadgeText = !string.IsNullOrWhiteSpace(task.LmsScore)
                ? $"{task.LmsStatus ?? "Synced"} · {task.LmsScore}"
                : (task.LmsStatus ?? "Synced");
            var statusPill = UiBuilder.Pill(
                statusBadgeText,
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgSurface));
            Grid.SetColumn(statusPill, 1);
            lmsHeader.Children.Add(statusPill);
            lmsStack.Children.Add(lmsHeader);

            lmsStack.Children.Add(UiBuilder.ActionButton(
                "Open & Submit in eLMS App (O)",
                MaterialIconKind.OpenInApp,
                async () => await vm.OpenTaskInElmsAppAsync(task)));

            stack.Children.Add(new Border
            {
                Background = ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = lmsStack
            });
        }

        // Subtasks Checklist
        stack.Children.Add(UiBuilder.HairlineDivider());
        stack.Children.Add(UiBuilder.SectionHeader($"Subtasks ({task.SubTasks.Count(s => s.IsCompleted)}/{task.SubTasks.Count})"));
        for (int i = 0; i < task.SubTasks.Count; i++)
        {
            int idx = i;
            var sub = task.SubTasks[i];
            var subBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        UiBuilder.Icon(
                            sub.IsCompleted ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline,
                            14.5,
                            sub.IsCompleted ? ThemeService.Brush(ThemeService.SuccessFg) : ThemeService.Brush(ThemeService.TextSecondary)),
                        new TextBlock
                        {
                            Text = sub.Title,
                            FontSize = 12.5,
                            Foreground = sub.IsCompleted ? ThemeService.Brush(ThemeService.TextSecondary) : ThemeService.Brush(ThemeService.TextPrimary),
                            TextDecorations = sub.IsCompleted ? TextDecorations.Strikethrough : null
                        }
                    }
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 3),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            subBtn.Click += async (_, _) => await vm.ToggleSubtaskAsync(task, idx);
            stack.Children.Add(subBtn);
        }

        var addSubGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var newSubBox = new TextBox { Watermark = "Add a subtask...", FontSize = 12 };
        newSubBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(newSubBox.Text))
            {
                e.Handled = true;
                await vm.AddSubtaskAsync(task, newSubBox.Text);
                newSubBox.Text = string.Empty;
            }
        };
        var addSubBtn = UiBuilder.GhostButton("Add", MaterialIconKind.Plus, async () =>
        {
            if (!string.IsNullOrWhiteSpace(newSubBox.Text))
            {
                await vm.AddSubtaskAsync(task, newSubBox.Text);
                newSubBox.Text = string.Empty;
            }
        });
        addSubBtn.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(addSubBtn, 1);
        addSubGrid.Children.Add(newSubBox);
        addSubGrid.Children.Add(addSubBtn);
        stack.Children.Add(addSubGrid);

        // Linked Files & Notes
        if (task.LinkedFiles.Count > 0 || !string.IsNullOrWhiteSpace(task.NotePath))
        {
            stack.Children.Add(UiBuilder.HairlineDivider());
            stack.Children.Add(UiBuilder.SectionHeader("Attachments & Notes"));
            var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder) ? vm.BasinRoot : Path.Combine(vm.BasinRoot, task.CourseFolder);

            foreach (var relFile in task.LinkedFiles)
            {
                var full = Path.Combine(courseDir, relFile);
                var isPdf = full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

                var fRow = new WrapPanel { Orientation = Orientation.Horizontal };
                if (isPdf)
                {
                    var pdfBtn = UiBuilder.GhostButton(
                        Path.GetFileName(relFile),
                        MaterialIconKind.FilePdfBox,
                        () => vm.OpenPdfInReader(full));
                    pdfBtn.Margin = new Thickness(0, 0, 6, 4);
                    fRow.Children.Add(pdfBtn);
                }
                var extBtn = UiBuilder.GhostButton(
                    isPdf ? "External" : Path.GetFileName(relFile),
                    MaterialIconKind.OpenInNew,
                    () => ExternalFileLauncher.OpenLocalFile(full));
                extBtn.Margin = new Thickness(0, 0, 0, 4);
                fRow.Children.Add(extBtn);

                stack.Children.Add(fRow);
            }

            if (!string.IsNullOrWhiteSpace(task.NotePath))
            {
                var noteFull = Path.Combine(courseDir, task.NotePath);
                stack.Children.Add(UiBuilder.GhostButton(
                    $"Edit in Neovim ({Path.GetFileName(task.NotePath)})",
                    MaterialIconKind.FileEditOutline,
                    () => ExternalFileLauncher.OpenLocalFile(noteFull)));
            }
        }

        // Rendered Markdown Instructions / Note Preview
        if (!string.IsNullOrWhiteSpace(vm.SelectedTaskNoteContent))
        {
            stack.Children.Add(UiBuilder.HairlineDivider());
            stack.Children.Add(UiBuilder.SectionHeader("Note & Instructions"));
            stack.Children.Add(new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 10),
                Child = MarkdownRenderer.Render(vm.SelectedTaskNoteContent)
            });
        }

        var scroll = new ScrollViewer { Content = stack };
        Grid.SetRow(scroll, 1);
        rootGrid.Children.Add(scroll);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = rootGrid
        };
    }
}
