using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// MainWindow partial: Responsive collapsible navigation sidebar.
/// </summary>
public sealed partial class MainWindow
{
    private Border BuildSidebar()
    {
        bool compact = IsSidebarCurrentlyCompact;
        var grid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto"),
            Margin = new Thickness(compact ? 6 : 10, 10),
        };

        // Top Basin Switcher & Collapse Toggle
        var header = new Grid
        {
            ColumnDefinitions = compact
                ? ColumnDefinitions.Parse("*")
                : ColumnDefinitions.Parse("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        if (compact)
        {
            var expandBtn = UiBuilder.IconButton(
                MaterialIconKind.DockLeft,
                _vm.ToggleSidebar,
                17,
                ThemeService.Brush(ThemeService.TextPrimary)
            );
            expandBtn.HorizontalAlignment = HorizontalAlignment.Center;
            header.Children.Add(expandBtn);
        }
        else
        {
            var basinBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        UiBuilder.Icon(
                            MaterialIconKind.FolderOutline,
                            16,
                            ThemeService.Brush(ThemeService.AccentPrimary)
                        ),
                        new TextBlock
                        {
                            Text = string.IsNullOrEmpty(_vm.BasinConfig.Name)
                                ? "Basin"
                                : _vm.BasinConfig.Name,
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Cursor = new Cursor(StandardCursorType.Hand),
                Padding = new Thickness(6, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            basinBtn.PointerEntered += (_, _) =>
                basinBtn.Background = ThemeService.Brush(ThemeService.BgHover);
            basinBtn.PointerExited += (_, _) => basinBtn.Background = Brushes.Transparent;
            basinBtn.Click += async (_, _) => await PromptOpenBasinFolderAsync();
            header.Children.Add(basinBtn);

            var bellIcon =
                _vm.UnreadNotificationsCount > 0
                    ? MaterialIconKind.BellBadgeOutline
                    : MaterialIconKind.BellOutline;
            var notifBtn = UiBuilder.IconButton(
                bellIcon,
                _vm.ToggleNotificationDrawer,
                15.5,
                _vm.UnreadNotificationsCount > 0
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
            Grid.SetColumn(notifBtn, 1);
            header.Children.Add(notifBtn);

            var collapseBtn = UiBuilder.IconButton(
                MaterialIconKind.DockLeft,
                _vm.ToggleSidebar,
                15.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            Grid.SetColumn(collapseBtn, 2);
            header.Children.Add(collapseBtn);
        }

        grid.Children.Add(header);

        // Quick Add & Search Triggers + Smart Views
        var today = DateOnly.FromDateTime(DateTime.Today);
        var viewsStack = new StackPanel { Spacing = 2 };

        // Todoist-style prominent "+ Add task" button at the top of the sidebar
        viewsStack.Children.Add(
            BuildNavButton(
                "Add task",
                MaterialIconKind.PlusCircle,
                0,
                isActive: false,
                _vm.OpenQuickAddModal,
                compact: compact,
                customAccentFg: ThemeService.Brush(ThemeService.AccentPrimary)
            )
        );

        viewsStack.Children.Add(
            BuildNavButton(
                "Search",
                MaterialIconKind.Magnify,
                0,
                isActive: false,
                _vm.OpenSearchModal,
                compact: compact,
                subtitleTag: "/"
            )
        );

        int inboxCount =
            _vm.AllTasks.Count(t => !t.IsCompleted && t.IsInbox) + _vm.UnregisteredFiles.Count;
        int todayCount = _vm.AllTasks.Count(t =>
            !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value <= today
        );
        int upcomingCount = _vm.AllTasks.Count(t =>
            !t.IsCompleted && t.DueDate.HasValue && t.DueDate.Value > today
        );
        int allActiveCount = _vm.AllTasks.Count(t => !t.IsCompleted);
        int completedCount = _vm.AllTasks.Count(t => t.IsCompleted);

        viewsStack.Children.Add(
            BuildNavButton(
                "Inbox",
                MaterialIconKind.InboxOutline,
                inboxCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Inbox,
                () => _vm.SelectSmartView(SmartViewKind.Inbox),
                compact
            )
        );

        viewsStack.Children.Add(
            BuildNavButton(
                "Today",
                MaterialIconKind.CalendarToday,
                todayCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Today,
                () => _vm.SelectSmartView(SmartViewKind.Today),
                compact
            )
        );

        viewsStack.Children.Add(
            BuildNavButton(
                "Upcoming",
                MaterialIconKind.CalendarClock,
                upcomingCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Upcoming,
                () => _vm.SelectSmartView(SmartViewKind.Upcoming),
                compact
            )
        );

        viewsStack.Children.Add(
            BuildNavButton(
                "Calendar",
                MaterialIconKind.CalendarMonthOutline,
                0,
                _vm.ActiveMode == WorkspaceTabMode.Calendar,
                _vm.SelectCalendarView,
                compact
            )
        );

        int dueCardsOrQuizzes =
            _vm.QuizStudio.AllDueReviewQuestions.Count > 0
                ? _vm.QuizStudio.AllDueReviewQuestions.Count
                : _vm.QuizStudio.SavedQuizzes.Count;
        viewsStack.Children.Add(
            BuildNavButton(
                "Test & Recall",
                MaterialIconKind.HeadLightbulbOutline,
                dueCardsOrQuizzes,
                _vm.ActiveMode == WorkspaceTabMode.QuizStudio,
                async () => await _vm.SelectQuizStudioViewAsync(),
                compact
            )
        );

        viewsStack.Children.Add(
            BuildNavButton(
                "All Active",
                MaterialIconKind.FormatListBulleted,
                allActiveCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.All,
                () => _vm.SelectSmartView(SmartViewKind.All),
                compact
            )
        );

        viewsStack.Children.Add(
            BuildNavButton(
                "Completed",
                MaterialIconKind.CheckCircleOutline,
                completedCount,
                _vm.ActiveMode == WorkspaceTabMode.TaskList
                    && _vm.ActiveSmartView == SmartViewKind.Completed,
                () => _vm.SelectSmartView(SmartViewKind.Completed),
                compact
            )
        );

        Grid.SetRow(viewsStack, 1);
        grid.Children.Add(viewsStack);

        // Courses Section
        var coursesStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        if (!compact)
        {
            var coursesHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            coursesHeader.Children.Add(UiBuilder.SectionHeader("Courses"));
            var addCourseBtn = UiBuilder.IconButton(
                MaterialIconKind.Plus,
                _vm.ToggleAddCourseModal,
                14,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(addCourseBtn, "Create a new local course / subject workspace");
            Grid.SetColumn(addCourseBtn, 1);
            coursesHeader.Children.Add(addCourseBtn);
            coursesStack.Children.Add(coursesHeader);
        }
        else
        {
            coursesStack.Children.Add(UiBuilder.HairlineDivider(new Thickness(4, 6)));
        }

        foreach (var course in _vm.Courses.Where(c => !c.IsArchived))
        {
            int courseTaskCount = _vm.AllTasks.Count(t =>
                !t.IsCompleted
                && string.Equals(
                    t.CourseFolder,
                    course.FolderName,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            bool isSelected =
                _vm.ActiveMode == WorkspaceTabMode.CourseWorkspace
                && _vm.ActiveCourse?.FolderName == course.FolderName;

            var courseBtn = BuildNavButton(
                course.Title,
                MaterialIconKind.Pound,
                courseTaskCount,
                isSelected,
                () => _vm.SelectCourse(course),
                compact
            );

            if (_vm.Settings.EnableElmsIntegration && !string.IsNullOrWhiteSpace(course.CourseId))
            {
                var capturedCourse = course;
                var syncCourseItem = new MenuItem
                {
                    Header = $"Sync {capturedCourse.Title} from eLMS",
                };
                syncCourseItem.Click += async (_, _) =>
                    await _vm.RunCourseSyncAsync(capturedCourse);
                courseBtn.ContextMenu = new ContextMenu { Items = { syncCourseItem } };
            }

            coursesStack.Children.Add(courseBtn);
        }

        // Academic Terms Quick-Filter Section in Sidebar
        if (!compact)
        {
            coursesStack.Children.Add(UiBuilder.SectionHeader("Academic Terms"));
            foreach (var termName in new[] { "Prelims", "Midterms", "Prefinals", "Finals" })
            {
                var capturedTerm = termName;
                int termCount = _vm.AllTasks.Count(t =>
                    !t.IsCompleted && TaskQueryService.MatchesTerm(t, capturedTerm)
                );
                bool isTermActive = string.Equals(
                    _vm.ActiveTermFilter,
                    capturedTerm,
                    StringComparison.OrdinalIgnoreCase
                );

                coursesStack.Children.Add(
                    BuildNavButton(
                        capturedTerm,
                        MaterialIconKind.SchoolOutline,
                        termCount,
                        isTermActive,
                        () => _vm.SelectTermView(capturedTerm),
                        compact: false
                    )
                );
            }

            // Custom Tags (@) Section in Sidebar
            var customLabels = _vm
                .AllKnownLabels.Where(l => TaskItem.NormalizeTerm(l) is null)
                .ToList();

            if (customLabels.Count > 0)
            {
                coursesStack.Children.Add(UiBuilder.SectionHeader("Tags"));
                foreach (var lbl in customLabels)
                {
                    var capturedLbl = lbl;
                    int lblCount = _vm.AllTasks.Count(t =>
                        !t.IsCompleted
                        && t.Labels.Contains(capturedLbl, StringComparer.OrdinalIgnoreCase)
                    );
                    bool isLblActive =
                        _vm.ActiveMode == WorkspaceTabMode.TaskList
                        && _vm.ActiveSmartView == SmartViewKind.Label
                        && string.Equals(
                            _vm.ActiveLabelFilter,
                            capturedLbl,
                            StringComparison.OrdinalIgnoreCase
                        );

                    coursesStack.Children.Add(
                        BuildNavButton(
                            $"@{capturedLbl}",
                            MaterialIconKind.TagOutline,
                            lblCount,
                            isLblActive,
                            () => _vm.SelectLabelView(capturedLbl),
                            compact: false
                        )
                    );
                }
            }
        }

        var savedSidebarOffset = _sidebarScrollOffset;
        var coursesScroll = new ScrollViewer
        {
            Content = coursesStack,
            Offset = savedSidebarOffset,
        };
        coursesScroll.ScrollChanged += (_, _) => _sidebarScrollOffset = coursesScroll.Offset;
        Dispatcher.UIThread.Post(
            () => coursesScroll.Offset = savedSidebarOffset,
            DispatcherPriority.Loaded
        );
        Grid.SetRow(coursesScroll, 2);
        grid.Children.Add(coursesScroll);

        // Bottom Quiet Utility Strip (Sync, Theme, Shortcuts)
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(UiBuilder.HairlineDivider(new Thickness(0, 0, 0, 4)));

        if (compact)
        {
            static Control CenterCompact(Control c)
            {
                c.HorizontalAlignment = HorizontalAlignment.Center;
                return c;
            }

            if (_vm.Settings.EnableElmsIntegration)
            {
                var syncCompact = UiBuilder.IconButton(
                    MaterialIconKind.CloudSyncOutline,
                    () => { }
                );
                syncCompact.Flyout = BuildElmsSyncFlyout();
                ToolTip.SetTip(
                    syncCompact,
                    "eLMS Sync options (Fast Poll, Course Sync, Full Sync)"
                );
                footer.Children.Add(CenterCompact(syncCompact));

                var pollCompact = UiBuilder.IconButton(
                    MaterialIconKind.Refresh,
                    async () => await _vm.RunFastPollAsync()
                );
                ToolTip.SetTip(pollCompact, "Fast poll eLMS assignments");
                footer.Children.Add(CenterCompact(pollCompact));

                var pauseCompact = UiBuilder.IconButton(
                    _vm.IsPollingPaused
                        ? MaterialIconKind.PlayCircleOutline
                        : MaterialIconKind.PauseCircleOutline,
                    _vm.TogglePollingPaused,
                    15,
                    _vm.IsPollingPaused
                        ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                        : ThemeService.Brush(ThemeService.TextSecondary)
                );
                ToolTip.SetTip(
                    pauseCompact,
                    _vm.IsPollingPaused
                        ? "eLMS Polling PAUSED — click to resume (Shift+P)"
                        : "Pause eLMS polling (Shift+P)"
                );
                if (_vm.IsPollingPaused)
                {
                    footer.Children.Add(
                        CenterCompact(
                            new Border
                            {
                                Background = ThemeService.Brush(ThemeService.PriorityP2Bg),
                                BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(6),
                                Child = pauseCompact,
                            }
                        )
                    );
                }
                else
                {
                    footer.Children.Add(CenterCompact(pauseCompact));
                }
            }
            else
            {
                var addCourseCompact = UiBuilder.IconButton(
                    MaterialIconKind.FolderPlusOutline,
                    _vm.ToggleAddCourseModal
                );
                ToolTip.SetTip(addCourseCompact, "Add Course");
                footer.Children.Add(CenterCompact(addCourseCompact));
            }

            var themeCompact = UiBuilder.IconButton(
                ThemeService.CurrentTheme == ThemeMode.Dark
                    ? MaterialIconKind.WeatherSunny
                    : MaterialIconKind.WeatherNight,
                async () => await _vm.ToggleThemeAsync()
            );
            ToolTip.SetTip(themeCompact, "Toggle Dark / Light theme (T)");
            footer.Children.Add(CenterCompact(themeCompact));

            var settingsCompact = UiBuilder.IconButton(
                MaterialIconKind.CogOutline,
                _vm.ToggleSettingsModal
            );
            ToolTip.SetTip(settingsCompact, "Desktop Settings");
            footer.Children.Add(CenterCompact(settingsCompact));

            var helpCompact = UiBuilder.IconButton(
                MaterialIconKind.KeyboardOutline,
                _vm.ToggleShortcutsModal
            );
            ToolTip.SetTip(helpCompact, "Keyboard Shortcuts (?)");
            footer.Children.Add(CenterCompact(helpCompact));
        }
        else
        {
            if (_vm.Settings.EnableElmsIntegration && _vm.IsPollingPaused)
            {
                var pausedBannerBtn = new Button
                {
                    Background = ThemeService.Brush(ThemeService.PriorityP2Bg),
                    BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            UiBuilder.Icon(
                                MaterialIconKind.PauseCircle,
                                13.5,
                                ThemeService.Brush(ThemeService.PriorityP2Fg)
                            ),
                            new TextBlock
                            {
                                Text = "Polling Paused · Resume",
                                FontSize = 11.5,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                        },
                    },
                };
                pausedBannerBtn.Click += (_, _) => _vm.TogglePollingPaused();
                ToolTip.SetTip(
                    pausedBannerBtn,
                    "Background eLMS polling is paused (Shift+P to resume)"
                );
                footer.Children.Add(pausedBannerBtn);
            }

            // Row 1: Primary Sync / Add Course + Fast Poll (plenty of width so text never truncates)
            var syncRow = new Grid
            {
                ColumnDefinitions = _vm.Settings.EnableElmsIntegration
                    ? ColumnDefinitions.Parse("*,Auto")
                    : ColumnDefinitions.Parse("*"),
                ColumnSpacing = 4,
            };
            Button syncBtn;
            if (_vm.Settings.EnableElmsIntegration)
            {
                syncBtn = UiBuilder.GhostButton(
                    _vm.IsSyncing ? "Syncing..." : "eLMS Sync ▾",
                    MaterialIconKind.CloudSyncOutline,
                    () => { }
                );
                syncBtn.IsEnabled = !_vm.IsSyncing;
                syncBtn.Flyout = BuildElmsSyncFlyout();
                ToolTip.SetTip(syncBtn, "Choose Fast Poll, Per-Course Sync, or Full eLMS Sync");
            }
            else
            {
                syncBtn = UiBuilder.GhostButton(
                    "Add Course",
                    MaterialIconKind.FolderPlusOutline,
                    _vm.ToggleAddCourseModal
                );
            }
            syncBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            syncRow.Children.Add(syncBtn);

            if (_vm.Settings.EnableElmsIntegration)
            {
                var pollBtn = UiBuilder.IconButton(
                    MaterialIconKind.Refresh,
                    async () => await _vm.RunFastPollAsync(),
                    14.5
                );
                ToolTip.SetTip(pollBtn, "Fast poll eLMS assignments now (R)");
                Grid.SetColumn(pollBtn, 1);
                syncRow.Children.Add(pollBtn);
            }
            footer.Children.Add(syncRow);

            // Row 2: Evenly spaced Utility Strip (Pause/Resume, Theme, Settings, Shortcuts)
            var utilRow = new Grid
            {
                ColumnDefinitions = _vm.Settings.EnableElmsIntegration
                    ? ColumnDefinitions.Parse("*,*,*,*")
                    : ColumnDefinitions.Parse("*,*,*"),
            };

            int utilCol = 0;
            if (_vm.Settings.EnableElmsIntegration)
            {
                var pauseBtn = UiBuilder.IconButton(
                    _vm.IsPollingPaused
                        ? MaterialIconKind.PlayCircleOutline
                        : MaterialIconKind.PauseCircleOutline,
                    _vm.TogglePollingPaused,
                    15,
                    _vm.IsPollingPaused
                        ? ThemeService.Brush(ThemeService.PriorityP2Fg)
                        : ThemeService.Brush(ThemeService.TextSecondary)
                );
                pauseBtn.HorizontalAlignment = HorizontalAlignment.Center;
                ToolTip.SetTip(
                    pauseBtn,
                    _vm.IsPollingPaused
                        ? "Resume background eLMS polling (Shift+P)"
                        : "Pause background eLMS polling (Shift+P)"
                );
                Grid.SetColumn(pauseBtn, utilCol++);
                utilRow.Children.Add(pauseBtn);
            }

            var themeBtn = UiBuilder.IconButton(
                ThemeService.CurrentTheme == ThemeMode.Dark
                    ? MaterialIconKind.WeatherSunny
                    : MaterialIconKind.WeatherNight,
                async () => await _vm.ToggleThemeAsync(),
                15
            );
            themeBtn.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(themeBtn, "Toggle Dark / Light theme (T)");
            Grid.SetColumn(themeBtn, utilCol++);
            utilRow.Children.Add(themeBtn);

            var settingsBtn = UiBuilder.IconButton(
                MaterialIconKind.CogOutline,
                _vm.ToggleSettingsModal,
                15
            );
            settingsBtn.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(settingsBtn, "Desktop Settings (Tray, Notifications, Startup, Vault)");
            Grid.SetColumn(settingsBtn, utilCol++);
            utilRow.Children.Add(settingsBtn);

            var helpBtn = UiBuilder.IconButton(
                MaterialIconKind.KeyboardOutline,
                _vm.ToggleShortcutsModal,
                15
            );
            helpBtn.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(helpBtn, "Keyboard Shortcuts (?)");
            Grid.SetColumn(helpBtn, utilCol);
            utilRow.Children.Add(helpBtn);

            footer.Children.Add(utilRow);
        }

        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = grid,
        };
    }

    private MenuFlyout BuildElmsSyncFlyout()
    {
        var flyout = new MenuFlyout();

        var pollItem = new MenuItem
        {
            Header = "Fast Poll Assignments (R)",
            IsEnabled = !_vm.IsSyncing,
        };
        pollItem.Click += async (_, _) => await _vm.RunFastPollAsync();
        flyout.Items.Add(pollItem);

        var fullSyncItem = new MenuItem
        {
            Header = "Full eLMS Sync (All Courses)",
            IsEnabled = !_vm.IsSyncing,
        };
        fullSyncItem.Click += async (_, _) => await _vm.RunFullSyncAsync();
        flyout.Items.Add(fullSyncItem);

        var syncableCourses = _vm
            .Courses.Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.CourseId))
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (syncableCourses.Count > 0)
        {
            flyout.Items.Add(new Separator());
            foreach (var course in syncableCourses)
            {
                var captured = course;
                var courseItem = new MenuItem
                {
                    Header = $"Sync Course: {captured.Title}",
                    IsEnabled = !_vm.IsSyncing,
                };
                courseItem.Click += async (_, _) => await _vm.RunCourseSyncAsync(captured);
                flyout.Items.Add(courseItem);
            }
        }

        flyout.Items.Add(new Separator());
        var pauseItem = new MenuItem
        {
            Header = _vm.IsPollingPaused
                ? "Resume Background Polling (Shift+P)"
                : "Pause Background Polling (Shift+P)",
        };
        pauseItem.Click += (_, _) => _vm.TogglePollingPaused();
        flyout.Items.Add(pauseItem);

        return flyout;
    }

    private static Button BuildNavButton(
        string title,
        MaterialIconKind icon,
        int badgeCount,
        bool isActive,
        Action onClick,
        bool compact = false,
        string? subtitleTag = null,
        IBrush? customAccentFg = null
    )
    {
        var fg =
            customAccentFg
            ?? (
                isActive
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextPrimary)
            );
        var iconFg =
            customAccentFg
            ?? (
                isActive
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
        var normalBg = isActive
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : Brushes.Transparent;
        var hoverBg = isActive
            ? ThemeService.Brush(ThemeService.AccentSoftBg)
            : ThemeService.Brush(ThemeService.BgHover);

        Control content;
        if (compact)
        {
            content = UiBuilder.Icon(icon, 17, iconFg);
        }
        else
        {
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("20,*,Auto") };
            row.Children.Add(UiBuilder.Icon(icon, 15.5, iconFg));

            row.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontSize = 13,
                    FontWeight =
                        isActive || customAccentFg is not null
                            ? FontWeight.SemiBold
                            : FontWeight.Normal,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 4, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [Grid.ColumnProperty] = 1,
                }
            );

            if (badgeCount > 0)
            {
                row.Children.Add(
                    new TextBlock
                    {
                        Text = badgeCount.ToString(),
                        FontSize = 11.5,
                        Foreground = isActive ? fg : ThemeService.Brush(ThemeService.TextMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 2,
                    }
                );
            }
            else if (!string.IsNullOrEmpty(subtitleTag))
            {
                row.Children.Add(
                    new TextBlock
                    {
                        Text = subtitleTag,
                        FontSize = 11,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 2,
                    }
                );
            }

            content = row;
        }

        var btn = new Button
        {
            Content = content,
            Background = normalBg,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = compact ? new Thickness(8, 8) : new Thickness(8, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = compact
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Stretch,
        };
        btn.PointerEntered += (_, _) => btn.Background = hoverBg;
        btn.PointerExited += (_, _) => btn.Background = normalBg;
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
