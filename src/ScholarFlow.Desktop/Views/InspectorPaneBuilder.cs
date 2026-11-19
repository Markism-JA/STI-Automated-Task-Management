using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Builds the Contextual Right Slide-Over Task Inspector Drawer with native CalendarDatePicker,
/// Academic Term selector (Prelims/Midterms/Prefinals/Finals), Custom @Tag editor,
/// separated Instruction Attachments vs. Student Submissions cards with in-app PDF viewing,
/// subtasks checklist, explainable Urgency v2 breakdown, and rendered Markdown preview.
/// </summary>
public static class InspectorPaneBuilder
{
    private static readonly string[] AcademicTerms = ["Prelims", "Midterms", "Prefinals", "Finals"];

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

        // 1. Drawer Top Header Bar (Course & Term Breadcrumb + Actions + Close X)
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
            string.IsNullOrWhiteSpace(task.CourseAlias) ? MaterialIconKind.InboxOutline : MaterialIconKind.Pound,
            14,
            ThemeService.Brush(ThemeService.TextSecondary)));
        var crumbText = string.IsNullOrWhiteSpace(task.CourseAlias) ? "Inbox" : task.CourseAlias;
        if (!string.IsNullOrWhiteSpace(task.Term))
            crumbText += $" / {task.Term}";
        crumbRow.Children.Add(new TextBlock
        {
            Text = crumbText,
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

        rootGrid.Children.Add(new StackPanel
        {
            Children =
            {
                headerBar,
                UiBuilder.HairlineDivider()
            }
        });

        // 2. Scrollable Drawer Body
        var stack = new StackPanel { Spacing = 11, Margin = new Thickness(14, 12, 14, 22) };

        if (vm.GetParentTask(task) is { } parentTask)
        {
            var parentBackBtn = UiBuilder.GhostButton(
                $"Subtask of: {parentTask.Title}",
                MaterialIconKind.SubdirectoryArrowLeft,
                () => vm.SelectTask(parentTask, openInspector: true));
            parentBackBtn.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(parentBackBtn);
        }

        // Editable Title (Enter saves immediately)
        var titleBox = new TextBox
        {
            Text = task.Title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Watermark = "Task title"
        };
        titleBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await vm.SaveInspectorEditsAsync(
                    task,
                    titleBox.Text ?? task.Title,
                    task.DueDate?.ToString("yyyy-MM-dd"),
                    task.Priority,
                    task.Difficulty);
            }
        };
        stack.Children.Add(titleBox);

        // Native Interactive CalendarDatePicker + Quick Date Presets
        stack.Children.Add(UiBuilder.SectionHeader("Due Date"));
        var dateRow = new WrapPanel { Orientation = Orientation.Horizontal };

        var datePicker = new CalendarDatePicker
        {
            SelectedDate = task.DueDate.HasValue ? task.DueDate.Value.ToDateTime(TimeOnly.MinValue) : null,
            Watermark = "Select date...",
            Width = 145,
            Margin = new Thickness(0, 0, 6, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        bool suppressDateEvent = false;
        datePicker.SelectedDateChanged += async (_, _) =>
        {
            if (suppressDateEvent)
                return;
            var picked = datePicker.SelectedDate.HasValue
                ? DateOnly.FromDateTime(datePicker.SelectedDate.Value)
                : (DateOnly?)null;
            await vm.SetTaskDueDateAsync(task, picked);
        };
        dateRow.Children.Add(datePicker);

        var today = DateOnly.FromDateTime(DateTime.Today);
        void AddQuickDateBtn(string label, DateOnly? targetDate)
        {
            var btn = UiBuilder.GhostButton(
                label,
                null,
                async () =>
                {
                    suppressDateEvent = true;
                    datePicker.SelectedDate = targetDate?.ToDateTime(TimeOnly.MinValue);
                    suppressDateEvent = false;
                    await vm.SetTaskDueDateAsync(task, targetDate);
                },
                isActive: task.DueDate == targetDate && targetDate.HasValue);
            btn.Margin = new Thickness(0, 0, 4, 4);
            dateRow.Children.Add(btn);
        }

        AddQuickDateBtn("Today", today);
        AddQuickDateBtn("Tmrw", today.AddDays(1));
        AddQuickDateBtn("+7d", today.AddDays(7));
        if (task.DueDate.HasValue)
            AddQuickDateBtn("Clear", null);

        stack.Children.Add(dateRow);

        // Due Time Editor + Quick Presets
        stack.Children.Add(UiBuilder.SectionHeader("Due Time"));
        var timeRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var timeBox = new TextBox
        {
            Text = task.DueTime?.ToString("h:mm tt") ?? string.Empty,
            Watermark = "12:00 AM / 23:59",
            Width = 135,
            Margin = new Thickness(0, 0, 6, 4),
            VerticalAlignment = VerticalAlignment.Center
        };

        async Task ApplyTimeInputAsync()
        {
            var raw = timeBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                await vm.SetTaskDueTimeAsync(task, null);
                return;
            }
            if (TimeOnly.TryParse(raw, System.Globalization.CultureInfo.CurrentCulture, out var parsed) ||
                TimeOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out parsed))
            {
                await vm.SetTaskDueTimeAsync(task, parsed);
            }
        }

        timeBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await ApplyTimeInputAsync();
            }
        };
        timeBox.LostFocus += async (_, _) => await ApplyTimeInputAsync();
        timeRow.Children.Add(timeBox);

        void AddQuickTimeBtn(string label, TimeOnly? targetTime)
        {
            var btn = UiBuilder.GhostButton(
                label,
                null,
                async () =>
                {
                    timeBox.Text = targetTime?.ToString("h:mm tt") ?? string.Empty;
                    await vm.SetTaskDueTimeAsync(task, targetTime);
                },
                isActive: task.DueTime == targetTime && targetTime.HasValue);
            btn.Margin = new Thickness(0, 0, 4, 4);
            timeRow.Children.Add(btn);
        }

        AddQuickTimeBtn("12:00 AM", new TimeOnly(0, 0));
        AddQuickTimeBtn("12:00 PM", new TimeOnly(12, 0));
        AddQuickTimeBtn("5:00 PM", new TimeOnly(17, 0));
        AddQuickTimeBtn("11:59 PM", new TimeOnly(23, 59));
        if (task.DueTime.HasValue)
            AddQuickTimeBtn("Clear", null);

        stack.Children.Add(timeRow);

        // Academic Term Selector (Prelims / Midterms / Prefinals / Finals)
        stack.Children.Add(UiBuilder.SectionHeader("Academic Term"));
        var termRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var currentTerm = TaskItem.NormalizeTerm(task.Term);
        foreach (var termName in AcademicTerms)
        {
            var isCurrentTerm = string.Equals(currentTerm, termName, StringComparison.OrdinalIgnoreCase);
            var termBtn = UiBuilder.GhostButton(
                termName,
                MaterialIconKind.SchoolOutline,
                async () => await vm.SetTaskTermAsync(task, isCurrentTerm ? null : termName),
                isActive: isCurrentTerm);
            termBtn.Margin = new Thickness(0, 0, 4, 4);
            termRow.Children.Add(termBtn);
        }
        stack.Children.Add(termRow);

        // Priority & Difficulty Compact Selectors
        stack.Children.Add(UiBuilder.SectionHeader("Priority & Difficulty"));
        var metaSelectors = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var p in new[] { TaskPriority.P1, TaskPriority.P2, TaskPriority.P3, TaskPriority.P4 })
        {
            var isCur = p == task.Priority;
            var (pFg, _) = ThemeService.GetPriorityBrushes(p);
            var btn = UiBuilder.GhostButton(
                $"P{(int)p}",
                MaterialIconKind.FlagOutline,
                async () => await vm.SaveInspectorEditsAsync(task, titleBox.Text ?? task.Title, task.DueDate?.ToString("yyyy-MM-dd"), p, task.Difficulty),
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
                async () => await vm.SaveInspectorEditsAsync(task, titleBox.Text ?? task.Title, task.DueDate?.ToString("yyyy-MM-dd"), task.Priority, capturedD),
                isActive: d == task.Difficulty);
            btn.Margin = new Thickness(0, 0, 4, 4);
            metaSelectors.Children.Add(btn);
        }
        stack.Children.Add(metaSelectors);

        // Custom Tags (@tag) Editor
        stack.Children.Add(UiBuilder.SectionHeader("Tags & Attributes"));
        var tagsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var lbl in task.Labels)
        {
            var capturedLbl = lbl;
            var tagChip = UiBuilder.GhostButton(
                $"@{lbl} ×",
                MaterialIconKind.TagOutline,
                async () => await vm.RemoveTaskLabelAsync(task, capturedLbl),
                isActive: true);
            tagChip.Margin = new Thickness(0, 0, 4, 4);
            tagsWrap.Children.Add(tagChip);
        }

        // Quick suggestions from existing Basin tags not yet applied
        foreach (var suggested in vm.AllKnownLabels
                     .Where(l => !task.Labels.Contains(l, StringComparer.OrdinalIgnoreCase) && TaskItem.NormalizeTerm(l) is null)
                     .Take(5))
        {
            var capturedSug = suggested;
            var sugBtn = UiBuilder.GhostButton(
                $"+ @{suggested}",
                null,
                async () => await vm.AddTaskLabelAsync(task, capturedSug));
            sugBtn.Margin = new Thickness(0, 0, 4, 4);
            tagsWrap.Children.Add(sugBtn);
        }
        stack.Children.Add(tagsWrap);

        var addTagGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var newTagBox = new TextBox { Watermark = "Add custom @tag (e.g. review, group-project)...", FontSize = 12 };
        newTagBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(newTagBox.Text))
            {
                e.Handled = true;
                await vm.AddTaskLabelAsync(task, newTagBox.Text);
                newTagBox.Text = string.Empty;
            }
        };
        var addTagBtn = UiBuilder.GhostButton("Add Tag", MaterialIconKind.Plus, async () =>
        {
            if (!string.IsNullOrWhiteSpace(newTagBox.Text))
            {
                await vm.AddTaskLabelAsync(task, newTagBox.Text);
                newTagBox.Text = string.Empty;
            }
        });
        addTagBtn.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(addTagBtn, 1);
        addTagGrid.Children.Add(newTagBox);
        addTagGrid.Children.Add(addTagBtn);
        stack.Children.Add(addTagGrid);

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

        // CARD A: Task Instruction Attachments (Teacher/eLMS Prompt PDFs & Worksheets)
        stack.Children.Add(UiBuilder.HairlineDivider());
        stack.Children.Add(BuildInstructionAttachmentsCard(vm, task));

        // CARD B: Your Submission & eLMS Grading Integration
        stack.Children.Add(BuildSubmissionAndLmsCard(vm, task));

        // Subtasks (First-Class Nested TaskItems)
        task.EnsureChildTasksSynced();
        stack.Children.Add(UiBuilder.HairlineDivider());
        stack.Children.Add(UiBuilder.SectionHeader($"Subtasks ({task.ChildTasks.Count(s => s.IsCompleted)}/{task.ChildTasks.Count})"));
        for (int i = 0; i < task.ChildTasks.Count; i++)
        {
            int idx = i;
            var child = task.ChildTasks[i];

            var subRowGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                Margin = new Thickness(0, 1)
            };

            var checkBtn = new Button
            {
                Content = UiBuilder.Icon(
                    child.IsCompleted ? MaterialIconKind.CheckCircle : MaterialIconKind.CircleOutline,
                    15,
                    child.IsCompleted ? ThemeService.Brush(ThemeService.SuccessFg) : ThemeService.PriorityBrush(child.Priority)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 4),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center
            };
            checkBtn.Click += async (_, e) =>
            {
                e.Handled = true;
                await vm.ToggleSubtaskAsync(task, idx);
            };
            subRowGrid.Children.Add(checkBtn);

            var titleAndMeta = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleAndMeta.Children.Add(new TextBlock
            {
                Text = child.Title,
                FontSize = 12.5,
                Foreground = child.IsCompleted ? ThemeService.Brush(ThemeService.TextMuted) : ThemeService.Brush(ThemeService.TextPrimary),
                TextDecorations = child.IsCompleted ? TextDecorations.Strikethrough : null,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (child.DueDate.HasValue)
            {
                titleAndMeta.Children.Add(new TextBlock
                {
                    Text = child.DueDate.Value.ToString("MMM d"),
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (child.Priority != TaskPriority.P4)
            {
                titleAndMeta.Children.Add(new TextBlock
                {
                    Text = $"P{(int)child.Priority}",
                    FontSize = 10.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.PriorityBrush(child.Priority),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            foreach (var lbl in child.Labels.Take(2))
            {
                titleAndMeta.Children.Add(new TextBlock
                {
                    Text = $"@{lbl}",
                    FontSize = 10.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var inspectSubBtn = new Button
            {
                Content = titleAndMeta,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 3),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            inspectSubBtn.Click += (_, e) =>
            {
                e.Handled = true;
                vm.SelectTask(child, openInspector: true);
            };
            Grid.SetColumn(inspectSubBtn, 1);
            subRowGrid.Children.Add(inspectSubBtn);

            var openChevronBtn = UiBuilder.IconButton(
                MaterialIconKind.ChevronRight,
                () => vm.SelectTask(child, openInspector: true),
                14,
                ThemeService.Brush(ThemeService.TextMuted));
            Grid.SetColumn(openChevronBtn, 2);
            subRowGrid.Children.Add(openChevronBtn);

            stack.Children.Add(subRowGrid);
        }

        var addSubGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var newSubBox = new TextBox
        {
            Watermark = "Add subtask (e.g. Draft outline tomorrow p1 @tag)...",
            FontSize = 12
        };
        bool isAddingSubtask = false;
        async Task SubmitSubtaskAsync()
        {
            if (isAddingSubtask || string.IsNullOrWhiteSpace(newSubBox.Text))
                return;
            isAddingSubtask = true;
            try
            {
                var raw = newSubBox.Text;
                newSubBox.Text = string.Empty;
                await vm.AddSubtaskAsync(task, raw);
            }
            finally
            {
                isAddingSubtask = false;
            }
        }

        newSubBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(newSubBox.Text))
            {
                e.Handled = true;
                await SubmitSubtaskAsync();
            }
        };
        var addSubBtn = UiBuilder.GhostButton("Add", MaterialIconKind.Plus, async () => await SubmitSubtaskAsync());
        addSubBtn.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(addSubBtn, 1);
        addSubGrid.Children.Add(newSubBox);
        addSubGrid.Children.Add(addSubBtn);
        stack.Children.Add(addSubGrid);

        // Rendered Markdown Instructions / Note Preview
        if (!string.IsNullOrWhiteSpace(vm.SelectedTaskNoteContent))
        {
            stack.Children.Add(UiBuilder.HairlineDivider());
            var noteHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            noteHeader.Children.Add(UiBuilder.SectionHeader("Note & Instructions"));
            if (!string.IsNullOrWhiteSpace(task.NotePath))
            {
                var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder) ? vm.BasinRoot : Path.Combine(vm.BasinRoot, task.CourseFolder);
                var noteFull = Path.Combine(courseDir, task.NotePath);
                var configuredEditor = ExternalFileLauncher.GetConfiguredEditor();
                var editLabel = !string.IsNullOrWhiteSpace(configuredEditor)
                    ? $"Edit in {Path.GetFileNameWithoutExtension(configuredEditor.Split(' ')[0])}"
                    : OperatingSystem.IsLinux() && File.Exists("/usr/bin/kitty")
                        ? "Edit in Neovim"
                        : "Open Externally";
                var nvimBtn = UiBuilder.GhostButton(editLabel, MaterialIconKind.FileEditOutline, () => ExternalFileLauncher.OpenLocalFile(noteFull));
                Grid.SetColumn(nvimBtn, 1);
                noteHeader.Children.Add(nvimBtn);
            }
            stack.Children.Add(noteHeader);

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

        var scroll = UiBuilder.PersistentScrollViewer($"inspector:{vm.SelectedTask?.Id}", stack);
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

    private static Control BuildInstructionAttachmentsCard(MainWindowViewModel vm, TaskItem task)
    {
        var cardStack = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                UiBuilder.Icon(MaterialIconKind.FilePdfBox, 15, ThemeService.Brush(ThemeService.AccentPrimary)),
                new TextBlock
                {
                    Text = $"Instruction Attachments ({task.InstructionFiles.Count})",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary)
                }
            }
        });

        Control? hostControl = null;
        var linkBtn = UiBuilder.GhostButton("Link File...", MaterialIconKind.PaperclipPlus, async () =>
        {
            var top = TopLevel.GetTopLevel(hostControl);
            if (top is null)
                return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Task Instruction / Worksheet PDF or File",
                AllowMultiple = false
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } localPath)
            {
                await vm.AttachFileToTaskAsync(task, localPath, isSubmission: false);
            }
        });
        Grid.SetColumn(linkBtn, 1);
        header.Children.Add(linkBtn);
        cardStack.Children.Add(header);

        if (task.InstructionFiles.Count == 0)
        {
            cardStack.Children.Add(new TextBlock
            {
                Text = "No worksheet or instruction PDF linked yet. Click 'Link File...' or run Sync eLMS.",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var relFile in task.InstructionFiles)
            {
                cardStack.Children.Add(BuildFileActionRow(vm, task, relFile));
            }
        }

        var border = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = cardStack
        };
        hostControl = border;
        return border;
    }

    private static Control BuildSubmissionAndLmsCard(MainWindowViewModel vm, TaskItem task)
    {
        var cardStack = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                UiBuilder.Icon(MaterialIconKind.SchoolOutline, 15, ThemeService.Brush(ThemeService.SuccessFg)),
                new TextBlock
                {
                    Text = !string.IsNullOrWhiteSpace(task.LmsId)
                        ? $"Submission & eLMS #{task.LmsId}"
                        : "Submission & Deliverables",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary)
                }
            }
        });

        var statusParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.LmsStatus)) statusParts.Add(task.LmsStatus);
        if (!string.IsNullOrWhiteSpace(task.LmsScore)) statusParts.Add(task.LmsScore);
        if (task.WeightPercent.HasValue) statusParts.Add($"{task.WeightPercent.Value:0.#}%");

        if (statusParts.Count > 0)
        {
            var statusPill = UiBuilder.Pill(
                string.Join(" · ", statusParts),
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgSurface));
            Grid.SetColumn(statusPill, 1);
            header.Children.Add(statusPill);
        }
        cardStack.Children.Add(header);

        foreach (var subFile in task.SubmissionFiles)
        {
            cardStack.Children.Add(BuildFileActionRow(vm, task, subFile));
        }

        Control? hostControl = null;
        var actionWrap = new WrapPanel { Orientation = Orientation.Horizontal };

        var attachSubBtn = UiBuilder.GhostButton("Attach Deliverable...", MaterialIconKind.UploadOutline, async () =>
        {
            var top = TopLevel.GetTopLevel(hostControl);
            if (top is null)
                return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach Student Submission / Working File",
                AllowMultiple = false
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } localPath)
            {
                await vm.AttachFileToTaskAsync(task, localPath, isSubmission: true);
            }
        });
        attachSubBtn.Margin = new Thickness(0, 2, 6, 2);
        actionWrap.Children.Add(attachSubBtn);

        if (!string.IsNullOrWhiteSpace(task.LmsUrl) || !string.IsNullOrWhiteSpace(task.LmsId))
        {
            var elmsBtn = UiBuilder.ActionButton(
                "Submit in eLMS (O)",
                MaterialIconKind.OpenInApp,
                async () => await vm.OpenTaskInElmsAppAsync(task),
                isPrimary: true);
            elmsBtn.Margin = new Thickness(0, 2, 0, 2);
            actionWrap.Children.Add(elmsBtn);
        }

        cardStack.Children.Add(actionWrap);

        var border = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = cardStack
        };
        hostControl = border;
        return border;
    }

    private static Control BuildFileActionRow(MainWindowViewModel vm, TaskItem task, string relFile)
    {
        var isPdf = relFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        var fileName = Path.GetFileName(relFile);

        var row = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("18,*,Auto,Auto"),
            Margin = new Thickness(0, 2)
        };
        row.Children.Add(UiBuilder.Icon(
            isPdf ? MaterialIconKind.FilePdfBox : MaterialIconKind.FileDocumentOutline,
            14,
            isPdf ? ThemeService.Brush(ThemeService.AccentPrimary) : ThemeService.Brush(ThemeService.TextSecondary)));

        row.Children.Add(new TextBlock
        {
            Text = fileName,
            FontSize = 12,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [Grid.ColumnProperty] = 1
        });

        if (isPdf)
        {
            var viewBtn = UiBuilder.GhostButton(
                "View PDF",
                MaterialIconKind.EyeOutline,
                () => vm.OpenTaskFile(task, relFile),
                isActive: true);
            viewBtn.Margin = new Thickness(4, 0);
            Grid.SetColumn(viewBtn, 2);
            row.Children.Add(viewBtn);
        }

        var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder) ? vm.BasinRoot : Path.Combine(vm.BasinRoot, task.CourseFolder);
        var fullPath = Path.Combine(courseDir, relFile);
        var extBtn = UiBuilder.IconButton(
            MaterialIconKind.OpenInNew,
            () => ExternalFileLauncher.OpenLocalFile(fullPath),
            13.5);
        Grid.SetColumn(extBtn, 3);
        row.Children.Add(extBtn);

        return row;
    }
}
