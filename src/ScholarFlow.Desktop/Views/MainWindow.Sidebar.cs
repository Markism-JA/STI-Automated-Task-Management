using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private Control BuildSidebar()
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
                () => _vm.ToggleSidebar(),
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
                () => _vm.ToggleNotificationDrawer(),
                15.5,
                _vm.UnreadNotificationsCount > 0
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextSecondary)
            );
            Grid.SetColumn(notifBtn, 1);
            header.Children.Add(notifBtn);

            var collapseBtn = UiBuilder.IconButton(
                MaterialIconKind.DockLeft,
                () => _vm.ToggleSidebar(),
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
                () => _vm.OpenQuickAddModal(),
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
                () => _vm.OpenSearchModal(),
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
                () => _vm.SelectCalendarView(),
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
            coursesStack.Children.Add(UiBuilder.SectionHeader("Courses"));
        else
            coursesStack.Children.Add(UiBuilder.HairlineDivider(new Thickness(4, 6)));

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

            coursesStack.Children.Add(
                BuildNavButton(
                    course.Title,
                    MaterialIconKind.Pound,
                    courseTaskCount,
                    isSelected,
                    () => _vm.SelectCourse(course),
                    compact
                )
            );
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
            footer.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.CloudDownloadOutline,
                    async () => await _vm.RunFullSyncAsync()
                )
            );
            footer.Children.Add(
                UiBuilder.IconButton(
                    ThemeService.CurrentTheme == ThemeMode.Dark
                        ? MaterialIconKind.WeatherSunny
                        : MaterialIconKind.WeatherNight,
                    async () => await _vm.ToggleThemeAsync()
                )
            );
        }
        else
        {
            var bottomRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto,Auto"),
            };
            var syncBtn = UiBuilder.GhostButton(
                _vm.IsSyncing ? "Syncing..." : "Sync eLMS",
                MaterialIconKind.CloudSyncOutline,
                async () => await _vm.RunFullSyncAsync()
            );
            syncBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            bottomRow.Children.Add(syncBtn);

            var pollBtn = UiBuilder.IconButton(
                MaterialIconKind.Refresh,
                async () => await _vm.RunFastPollAsync(),
                14.5
            );
            pollBtn.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(pollBtn, 1);
            bottomRow.Children.Add(pollBtn);

            var themeBtn = UiBuilder.IconButton(
                ThemeService.CurrentTheme == ThemeMode.Dark
                    ? MaterialIconKind.WeatherSunny
                    : MaterialIconKind.WeatherNight,
                async () => await _vm.ToggleThemeAsync(),
                14.5
            );
            Grid.SetColumn(themeBtn, 2);
            bottomRow.Children.Add(themeBtn);

            var settingsBtn = UiBuilder.IconButton(
                MaterialIconKind.CogOutline,
                () => _vm.ToggleSettingsModal(),
                14.5
            );
            ToolTip.SetTip(settingsBtn, "Desktop Settings (Tray, Notifications, Startup, Vault)");
            Grid.SetColumn(settingsBtn, 3);
            bottomRow.Children.Add(settingsBtn);

            var helpBtn = UiBuilder.IconButton(
                MaterialIconKind.KeyboardOutline,
                () => _vm.ToggleShortcutsModal(),
                14.5
            );
            Grid.SetColumn(helpBtn, 4);
            bottomRow.Children.Add(helpBtn);

            footer.Children.Add(bottomRow);
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

    private static Control BuildNavButton(
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
