using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
/// 100% pure C# Todoist + Obsidian Desktop Window (zero .axaml, zero emojis).
/// Features a responsive collapsible sidebar, low-noise hairline task rows, a contextual slide-over
/// Task Inspector drawer with rendered Markdown, a floating centered Todoist Quick-Add modal (Q),
/// and a floating Quick-Search &amp; Navigation command palette (/ or Ctrl+K).
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    private readonly Grid _rootOverlayGrid;
    private readonly Grid _shellGrid;
    private readonly Border _sidebarContainer = new();
    private readonly Border _centerBodyContainer = new();
    private readonly Border _inspectorContainer = new();
    private readonly Border _statusBarContainer = new();
    private readonly Border _modalOverlayContainer = new() { IsVisible = false };

    private readonly TextBox _quickAddBox;
    private StackPanel _quickAddChipsContainer = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
    };
    private readonly TextBox _searchBox;
    private StackPanel _searchResultsStack = new() { Spacing = 2 };

    private readonly Dictionary<
        string,
        (Border Row, TextBlock TitleBlock, StackPanel HoverActions)
    > _taskRowsById = new(StringComparer.Ordinal);
    private string? _lastHighlightedTaskId;
    private string _mountedModalKind = string.Empty;
    private bool _suppressTextCallbacks;
    private bool _lastAutoSidebarCollapse;
    private Vector _sidebarScrollOffset;
    private Vector _taskListScrollOffset;

    public MainWindow(MainWindowViewModel vm)
    {
        _vm = vm;
        Title = "ScholarFlow";
        Width = 1360;
        Height = 860;
        MinWidth = 640;
        MinHeight = 480;

        _quickAddBox = new TextBox
        {
            Watermark = "Task name — e.g. Finish SQA lab 2 tomorrow p1 d4 #sqa @lab",
            FontSize = 14.5,
            FontWeight = FontWeight.Medium,
        };
        _quickAddBox.KeyDown += OnQuickAddKeyDown;
        _quickAddBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_suppressTextCallbacks)
                _vm.UpdateQuickAddText(_quickAddBox.Text ?? string.Empty);
        };

        _searchBox = new TextBox
        {
            Watermark = "Search tasks, courses (#sqa), or Master PDFs...",
            FontSize = 14,
        };
        _searchBox.KeyDown += OnSearchBoxKeyDown;
        _searchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_suppressTextCallbacks)
                _vm.UpdateSearchQuery(_searchBox.Text ?? string.Empty);
        };

        _shellGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("225,*,0"),
            RowDefinitions = RowDefinitions.Parse("*,26"),
        };

        _shellGrid.Children.Add(_sidebarContainer);

        Grid.SetColumn(_centerBodyContainer, 1);
        _shellGrid.Children.Add(_centerBodyContainer);

        Grid.SetColumn(_inspectorContainer, 2);
        _shellGrid.Children.Add(_inspectorContainer);

        Grid.SetRow(_statusBarContainer, 1);
        Grid.SetColumnSpan(_statusBarContainer, 3);
        _shellGrid.Children.Add(_statusBarContainer);

        _rootOverlayGrid = new Grid();
        _rootOverlayGrid.Children.Add(_shellGrid);
        _rootOverlayGrid.Children.Add(_modalOverlayContainer);

        Content = _rootOverlayGrid;

        _vm.UiChanged += OnVmUiChanged;
        _vm.Calendar.Changed += (_, _) => RefreshCenterBodyAndInspector();
        _vm.PdfReader.Changed += (_, _) => RefreshCenterBodyAndInspector();
        ThemeService.ThemeChanged += (_, _) =>
        {
            _mountedModalKind = string.Empty;
            RebuildAllSections();
        };

        RebuildAllSections();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        bool shouldAutoCollapse = e.NewSize.Width < 860;
        if (shouldAutoCollapse != _lastAutoSidebarCollapse)
        {
            _lastAutoSidebarCollapse = shouldAutoCollapse;
            RefreshSidebar();
        }
        UpdateResponsiveShellColumns(e.NewSize.Width);
        if (_vm.IsInspectorOpen)
            RefreshOverlayModal();
    }

    private bool IsSidebarCurrentlyCompact =>
        _vm.IsSidebarCollapsed || Bounds.Width is > 0 and < 860;
    private bool ShouldDockInspectorInline =>
        _vm.IsInspectorOpen
        && _vm.ActiveMode is not (WorkspaceTabMode.PdfReader or WorkspaceTabMode.QuizStudio)
        && (Bounds.Width <= 0 || Bounds.Width >= 1060);
    private bool ShouldFloatInspectorOverlay =>
        _vm.IsInspectorOpen
        && _vm.ActiveMode is not (WorkspaceTabMode.PdfReader or WorkspaceTabMode.QuizStudio)
        && Bounds.Width is > 0 and < 1060;

    private void UpdateResponsiveShellColumns(double width)
    {
        int sideW = (_vm.IsSidebarCollapsed || (width is > 0 and < 860)) ? 54 : 225;
        int inspW =
            (
                _vm.IsInspectorOpen
                && _vm.ActiveMode is not (WorkspaceTabMode.PdfReader or WorkspaceTabMode.QuizStudio)
                && (width <= 0 || width >= 1060)
            )
                ? 360
                : 0;

        _shellGrid.ColumnDefinitions = ColumnDefinitions.Parse($"{sideW},*,{inspW}");
        _inspectorContainer.IsVisible = inspW > 0;
    }

    private void OnVmUiChanged(object? sender, UiChangeKind kind)
    {
        switch (kind)
        {
            case UiChangeKind.QuickAddOnly:
                SyncTextBoxesFromVm();
                RefreshQuickAddChips();
                break;

            case UiChangeKind.StatusOnly:
                RefreshStatusBar();
                break;

            case UiChangeKind.NotificationsOnly:
                RefreshSidebar();
                RefreshOverlayModal();
                break;

            case UiChangeKind.OverlayOnly:
                SyncTextBoxesFromVm();
                RefreshOverlayModal();
                break;

            case UiChangeKind.SelectionOnly:
                UpdateTaskSelectionInPlace();
                if (_vm.IsInspectorOpen)
                {
                    RefreshInspector();
                    if (ShouldFloatInspectorOverlay)
                        RefreshOverlayModal();
                }
                break;

            case UiChangeKind.SearchOrTaskList:
                SyncTextBoxesFromVm();
                if (_vm.IsSearchModalOpen)
                    RefreshSearchResultsList();
                else
                    RefreshCenterBodyAndInspector();
                break;

            default:
                RebuildAllSections();
                break;
        }
    }

    private void RebuildAllSections()
    {
        Background = ThemeService.Brush(ThemeService.BgBase);
        UpdateResponsiveShellColumns(Bounds.Width);
        SyncTextBoxesFromVm();
        RefreshSidebar();
        RefreshCenterBodyAndInspector();
        RefreshOverlayModal();
        RefreshStatusBar();
    }

    private void SyncTextBoxesFromVm()
    {
        _suppressTextCallbacks = true;
        try
        {
            if (
                !string.Equals(
                    _quickAddBox.Text ?? string.Empty,
                    _vm.QuickAddText,
                    StringComparison.Ordinal
                )
            )
            {
                _quickAddBox.Text = _vm.QuickAddText;
                _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
            }

            if (
                !string.Equals(
                    _searchBox.Text ?? string.Empty,
                    _vm.SearchQuery,
                    StringComparison.Ordinal
                )
            )
            {
                _searchBox.Text = _vm.SearchQuery;
                _searchBox.CaretIndex = _searchBox.Text?.Length ?? 0;
            }
        }
        finally
        {
            _suppressTextCallbacks = false;
        }
    }

    private void RefreshSidebar()
    {
        _sidebarContainer.Child = BuildSidebar();
    }

    private void RefreshCenterBodyAndInspector()
    {
        _taskRowsById.Clear();
        _lastHighlightedTaskId = _vm.SelectedTask?.Id;

        _centerBodyContainer.Child = _vm.ActiveMode switch
        {
            WorkspaceTabMode.Calendar => CalendarViewBuilder.Build(_vm),
            WorkspaceTabMode.PdfReader => PdfReaderViewBuilder.Build(_vm),
            WorkspaceTabMode.QuizStudio => QuizStudioViewBuilder.Build(_vm),
            WorkspaceTabMode.CourseWorkspace when _vm.ActiveCourse is not null =>
                CourseWorkspaceViewBuilder.Build(_vm, _vm.ActiveCourse, BuildTaskListView),
            _ => BuildTaskListView(_vm),
        };

        RefreshInspector();
    }

    private void RefreshInspector()
    {
        if (ShouldDockInspectorInline)
        {
            _inspectorContainer.Child = InspectorPaneBuilder.Build(_vm);
        }
        else
        {
            _inspectorContainer.Child = null;
        }
    }

    private void RefreshStatusBar()
    {
        _statusBarContainer.Child = BuildStatusBar();
    }

    private void UpdateTaskSelectionInPlace()
    {
        var newId = _vm.SelectedTask?.Id;
        if (
            _lastHighlightedTaskId is not null
            && _taskRowsById.TryGetValue(_lastHighlightedTaskId, out var prevRow)
        )
        {
            prevRow.Row.Background = Brushes.Transparent;
            prevRow.TitleBlock.FontWeight = FontWeight.Normal;
            prevRow.HoverActions.IsVisible = false;
        }

        if (newId is not null && _taskRowsById.TryGetValue(newId, out var nextRow))
        {
            nextRow.Row.Background = ThemeService.Brush(ThemeService.BgElevated);
            nextRow.TitleBlock.FontWeight = FontWeight.Medium;
            nextRow.HoverActions.IsVisible = true;
            nextRow.Row.BringIntoView();
        }

        _lastHighlightedTaskId = newId;
    }

    // =========================================================================
    // 1. RESPONSIVE LEFT NAVIGATION SIDEBAR (FULL 225px OR COMPACT 54px RAIL)
    // =========================================================================

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
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto"),
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

            var helpBtn = UiBuilder.IconButton(
                MaterialIconKind.KeyboardOutline,
                () => _vm.ToggleShortcutsModal(),
                14.5
            );
            Grid.SetColumn(helpBtn, 3);
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

    // =========================================================================
    // 2. SLEEK TODOIST HAIRLINE TASK LIST CANVAS (LOW-NOISE, CENTERED)
    // =========================================================================

    private Control BuildTaskListView(MainWindowViewModel vm)
    {
        var isCourseSubView = vm.ActiveMode == WorkspaceTabMode.CourseWorkspace;
        var stack = new StackPanel
        {
            Spacing = 2,
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = isCourseSubView ? new Thickness(0, 4, 0, 24) : new Thickness(28, 22, 28, 28),
        };

        // Top View Header (Only for top-level smart views; CourseWorkspace has its own header)
        if (!isCourseSubView)
        {
            var headerGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                Margin = new Thickness(0, 0, 0, 16),
            };

            var titleLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var headingText = vm.ActiveSmartView switch
            {
                SmartViewKind.All => "All Active",
                SmartViewKind.Label when !string.IsNullOrWhiteSpace(vm.ActiveLabelFilter) =>
                    $"@{vm.ActiveLabelFilter}",
                _ => vm.ActiveSmartView.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(vm.ActiveTermFilter))
                headingText += $" · {vm.ActiveTermFilter}";

            titleLeft.Children.Add(
                new TextBlock
                {
                    Text = headingText,
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                }
            );

            var subtitleText =
                vm.ActiveSmartView == SmartViewKind.Today
                    ? $"{DateTime.Today:ddd, MMM d} · {vm.VisibleTasks.Count} task(s)"
                    : $"{vm.VisibleTasks.Count} task(s)";

            titleLeft.Children.Add(
                new TextBlock
                {
                    Text = subtitleText,
                    FontSize = 12.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 3),
                }
            );
            headerGrid.Children.Add(titleLeft);

            var headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            headerActions.Children.Add(
                UiBuilder.GhostButton(
                    "Add task",
                    MaterialIconKind.Plus,
                    () => vm.OpenQuickAddModal(),
                    isActive: true
                )
            );
            headerActions.Children.Add(
                UiBuilder.IconButton(MaterialIconKind.Magnify, () => vm.OpenSearchModal(), 16)
            );
            if (vm.SelectedTask is not null)
            {
                headerActions.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.DockRight,
                        () => vm.ToggleInspector(),
                        16,
                        vm.IsInspectorOpen ? ThemeService.Brush(ThemeService.AccentPrimary) : null
                    )
                );
            }
            Grid.SetColumn(headerActions, 1);
            headerGrid.Children.Add(headerActions);

            stack.Children.Add(headerGrid);
        }

        // Academic Term Filter & Grouping Bar
        var filterBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var termPills = new WrapPanel { Orientation = Orientation.Horizontal };
        var allTermsBtn = UiBuilder.GhostButton(
            "All Terms",
            null,
            () => vm.SelectTermView(null),
            isActive: string.IsNullOrEmpty(vm.ActiveTermFilter)
        );
        allTermsBtn.Margin = new Thickness(0, 0, 4, 4);
        termPills.Children.Add(allTermsBtn);

        foreach (var termName in new[] { "Prelims", "Midterms", "Prefinals", "Finals" })
        {
            var capturedTerm = termName;
            bool isTermActive = string.Equals(
                vm.ActiveTermFilter,
                capturedTerm,
                StringComparison.OrdinalIgnoreCase
            );
            var tBtn = UiBuilder.GhostButton(
                capturedTerm,
                null,
                () => vm.SelectTermView(capturedTerm),
                isActive: isTermActive
            );
            tBtn.Margin = new Thickness(0, 0, 4, 4);
            termPills.Children.Add(tBtn);
        }
        filterBar.Children.Add(termPills);

        var effectiveGrouping =
            vm.ActiveGrouping == TaskGroupingMode.Default
                ? (isCourseSubView ? TaskGroupingMode.Term : TaskGroupingMode.DueDate)
                : vm.ActiveGrouping;

        var groupPills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Top,
        };
        groupPills.Children.Add(
            UiBuilder.GhostButton(
                "By Term",
                MaterialIconKind.SchoolOutline,
                () => vm.SetGroupingMode(TaskGroupingMode.Term),
                isActive: effectiveGrouping == TaskGroupingMode.Term
            )
        );
        groupPills.Children.Add(
            UiBuilder.GhostButton(
                "By Date",
                MaterialIconKind.CalendarBlankOutline,
                () => vm.SetGroupingMode(TaskGroupingMode.DueDate),
                isActive: effectiveGrouping == TaskGroupingMode.DueDate
            )
        );
        groupPills.Children.Add(
            UiBuilder.GhostButton(
                "By Priority",
                MaterialIconKind.FlagOutline,
                () => vm.SetGroupingMode(TaskGroupingMode.Priority),
                isActive: effectiveGrouping == TaskGroupingMode.Priority
            )
        );

        Grid.SetColumn(groupPills, 1);
        filterBar.Children.Add(groupPills);
        stack.Children.Add(filterBar);

        // Unregistered Ongoing Files Banner (quiet, compact)
        var relevantUnregistered =
            vm.ActiveCourse is not null
                ? vm
                    .UnregisteredFiles.Where(u =>
                        u.CourseFolder.Equals(
                            vm.ActiveCourse.FolderName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToList()
            : vm.ActiveSmartView is SmartViewKind.Inbox or SmartViewKind.All
                ? vm.UnregisteredFiles.ToList()
            : [];

        if (relevantUnregistered.Count > 0)
        {
            var unregStack = new StackPanel { Spacing = 4 };
            unregStack.Children.Add(
                new TextBlock
                {
                    Text = $"{relevantUnregistered.Count} untracked file(s) in ongoing/",
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                }
            );

            foreach (var file in relevantUnregistered.Take(4))
            {
                var fRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
                fRow.Children.Add(
                    new TextBlock
                    {
                        Text = $"{file.CourseTitle} · {file.FileName}",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    }
                );
                var trackBtn = UiBuilder.GhostButton(
                    "Track",
                    MaterialIconKind.Plus,
                    async () => await vm.RegisterOngoingFileAsync(file)
                );
                Grid.SetColumn(trackBtn, 1);
                fRow.Children.Add(trackBtn);
                unregStack.Children.Add(fRow);
            }

            stack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(12, 8),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = unregStack,
                }
            );
        }

        if (vm.VisibleTasks.Count == 0)
        {
            stack.Children.Add(
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 64, 0, 0),
                    Spacing = 10,
                    Children =
                    {
                        UiBuilder.Icon(
                            MaterialIconKind.CheckCircleOutline,
                            30,
                            ThemeService.Brush(ThemeService.TextMuted)
                        ),
                        new TextBlock
                        {
                            Text = "All clear in this view",
                            FontSize = 14,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "Press Q to add a task or / to search across courses and PDFs.",
                            FontSize = 12.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                }
            );
        }
        else
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var groups = GroupTasksTodoistStyle(
                vm.VisibleTasks,
                today,
                vm.ActiveSmartView,
                effectiveGrouping
            );

            foreach (var (sectionTitle, sectionTasks, isUrgentSection) in groups)
            {
                if (groups.Count > 1)
                {
                    var secHeader = new StackPanel
                    {
                        Spacing = 4,
                        Margin = new Thickness(0, 14, 0, 2),
                    };
                    var secTitleRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                    };
                    secTitleRow.Children.Add(
                        new TextBlock
                        {
                            Text = sectionTitle,
                            FontSize = 12.5,
                            FontWeight = FontWeight.Bold,
                            Foreground = isUrgentSection
                                ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                                : ThemeService.Brush(ThemeService.TextPrimary),
                        }
                    );
                    secTitleRow.Children.Add(
                        new TextBlock
                        {
                            Text = sectionTasks.Count.ToString(),
                            FontSize = 12,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        }
                    );
                    secHeader.Children.Add(secTitleRow);
                    secHeader.Children.Add(UiBuilder.HairlineDivider());
                    stack.Children.Add(secHeader);
                }

                foreach (var task in sectionTasks)
                {
                    stack.Children.Add(BuildHairlineTaskRow(vm, task, today));
                }
            }

            // Quiet "+ Add task" inline button at the bottom of the list just like Todoist
            var inlineAddBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        UiBuilder.Icon(
                            MaterialIconKind.Plus,
                            15,
                            ThemeService.Brush(ThemeService.AccentPrimary)
                        ),
                        new TextBlock
                        {
                            Text = "Add task",
                            FontSize = 13,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        },
                    },
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 8),
                Margin = new Thickness(0, 4, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            inlineAddBtn.Click += (_, _) => vm.OpenQuickAddModal();
            stack.Children.Add(inlineAddBtn);
        }

        var savedTaskListOffset = _taskListScrollOffset;
        var taskListScroll = new ScrollViewer { Content = stack, Offset = savedTaskListOffset };
        taskListScroll.ScrollChanged += (_, _) => _taskListScrollOffset = taskListScroll.Offset;
        Dispatcher.UIThread.Post(
            () => taskListScroll.Offset = savedTaskListOffset,
            DispatcherPriority.Loaded
        );
        return taskListScroll;
    }

    private static List<(string Title, List<TaskItem> Tasks, bool IsUrgent)> GroupTasksTodoistStyle(
        IReadOnlyList<TaskItem> tasks,
        DateOnly today,
        SmartViewKind viewKind,
        TaskGroupingMode groupingMode
    )
    {
        if (viewKind == SmartViewKind.Completed && groupingMode == TaskGroupingMode.DueDate)
            return [("Completed", tasks.ToList(), false)];

        if (groupingMode == TaskGroupingMode.Term)
        {
            var termGroups = new List<(string Title, List<TaskItem> Tasks, bool IsUrgent)>();
            foreach (var termName in new[] { "Prelims", "Midterms", "Prefinals", "Finals" })
            {
                var matched = tasks.Where(t => TaskQueryService.MatchesTerm(t, termName)).ToList();
                if (matched.Count > 0)
                    termGroups.Add((termName, matched, false));
            }

            var uncategorized = tasks
                .Where(t =>
                    string.IsNullOrWhiteSpace(TaskItem.NormalizeTerm(t.Term))
                    && !t.Labels.Any(l => TaskItem.NormalizeTerm(l) is not null)
                )
                .ToList();
            if (uncategorized.Count > 0)
                termGroups.Add(("General / Uncategorized", uncategorized, false));

            return termGroups;
        }

        if (groupingMode == TaskGroupingMode.Priority)
        {
            var priGroups = new List<(string Title, List<TaskItem> Tasks, bool IsUrgent)>();
            foreach (
                var (p, label, urgent) in new[]
                {
                    (TaskPriority.P1, "Priority 1 — Urgent", true),
                    (TaskPriority.P2, "Priority 2 — High", false),
                    (TaskPriority.P3, "Priority 3 — Medium", false),
                    (TaskPriority.P4, "Priority 4 — Normal", false),
                }
            )
            {
                var matched = tasks.Where(t => t.Priority == p).ToList();
                if (matched.Count > 0)
                    priGroups.Add((label, matched, urgent));
            }
            return priGroups;
        }

        var overdue = new List<TaskItem>();
        var dueToday = new List<TaskItem>();
        var next7Days = new List<TaskItem>();
        var upcomingLater = new List<TaskItem>();
        var noDueDate = new List<TaskItem>();

        foreach (var t in tasks)
        {
            if (!t.DueDate.HasValue)
            {
                noDueDate.Add(t);
            }
            else if (!t.IsCompleted && t.DueDate.Value < today)
            {
                overdue.Add(t);
            }
            else if (t.DueDate.Value == today)
            {
                dueToday.Add(t);
            }
            else if (t.DueDate.Value <= today.AddDays(7))
            {
                next7Days.Add(t);
            }
            else
            {
                upcomingLater.Add(t);
            }
        }

        var result = new List<(string Title, List<TaskItem> Tasks, bool IsUrgent)>();
        if (overdue.Count > 0)
            result.Add(("Overdue", overdue, true));
        if (dueToday.Count > 0)
            result.Add(("Today", dueToday, false));
        if (next7Days.Count > 0)
            result.Add(("Next 7 Days", next7Days, false));
        if (upcomingLater.Count > 0)
            result.Add(("Upcoming", upcomingLater, false));
        if (noDueDate.Count > 0)
            result.Add(("No Due Date", noDueDate, false));
        return result;
    }

    private Control BuildHairlineTaskRow(MainWindowViewModel vm, TaskItem task, DateOnly today)
    {
        bool isSelected = vm.SelectedTask?.Id == task.Id;
        var (pFg, pBg) = ThemeService.GetPriorityBrushes(task.Priority);

        var rowGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };

        // 1. Left Circular Priority Ring
        var checkIcon = task.IsCompleted
            ? MaterialIconKind.CheckCircle
            : MaterialIconKind.CircleOutline;
        var checkBtn = new Button
        {
            Content = UiBuilder.Icon(
                checkIcon,
                18.5,
                task.IsCompleted ? ThemeService.Brush(ThemeService.SuccessFg) : pFg
            ),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(3),
            Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        checkBtn.PointerEntered += (_, _) => checkBtn.Background = pBg;
        checkBtn.PointerExited += (_, _) => checkBtn.Background = Brushes.Transparent;
        checkBtn.Click += async (_, e) =>
        {
            e.Handled = true;
            await vm.ToggleTaskCompletedAsync(task);
        };
        rowGrid.Children.Add(checkBtn);

        // 2. Center Title + Quiet Single-Line Subtitle
        var centerStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleBlock = new TextBlock
        {
            Text = task.Title,
            FontSize = 13.5,
            FontWeight = isSelected ? FontWeight.Medium : FontWeight.Normal,
            Foreground = task.IsCompleted
                ? ThemeService.Brush(ThemeService.TextMuted)
                : ThemeService.Brush(ThemeService.TextPrimary),
            TextDecorations = task.IsCompleted ? TextDecorations.Strikethrough : null,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        centerStack.Children.Add(titleBlock);

        var subParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.LmsScore))
            subParts.Add($"Score: {task.LmsScore}");
        else if (
            !string.IsNullOrWhiteSpace(task.LmsStatus)
            && !task.LmsStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase)
        )
            subParts.Add(task.LmsStatus);
        if (task.InstructionFiles.Count > 0)
            subParts.Add($"{task.InstructionFiles.Count} attachment(s)");
        if (task.SubmissionFiles.Count > 0)
            subParts.Add($"{task.SubmissionFiles.Count} submission(s)");
        foreach (
            var lbl in task.Labels.Where(l =>
                !l.Equals("assignment", StringComparison.OrdinalIgnoreCase)
                && !l.Equals("freeform", StringComparison.OrdinalIgnoreCase)
                && !l.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
                && TaskItem.NormalizeTerm(l) is null
            )
        )
        {
            subParts.Add($"@{lbl}");
        }

        if (subParts.Count > 0)
        {
            centerStack.Children.Add(
                new TextBlock
                {
                    Text = string.Join(" · ", subParts),
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }
            );
        }

        Grid.SetColumn(centerStack, 1);
        rowGrid.Children.Add(centerStack);

        // 3. Right Minimal Metadata (Hover Actions + Subtasks Toggle + Attachment PDF Chip + Term Pill + Due Date + #Course)
        var rightRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Progressive disclosure hover actions
        var hoverActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            IsVisible = isSelected,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!string.IsNullOrWhiteSpace(task.LmsUrl) || !string.IsNullOrWhiteSpace(task.LmsId))
        {
            hoverActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.OpenInApp,
                    async () => await vm.OpenTaskInElmsAppAsync(task),
                    14
                )
            );
        }

        hoverActions.Children.Add(
            UiBuilder.IconButton(MaterialIconKind.TuneVariant, () => vm.OpenInspector(task), 14)
        );

        rightRow.Children.Add(hoverActions);

        // Inline Subtasks Expand/Collapse Button
        task.EnsureChildTasksSynced();
        if (task.ChildTasks.Count > 0)
        {
            int doneSubs = task.ChildTasks.Count(s => s.IsCompleted);
            rightRow.Children.Add(
                UiBuilder.GhostButton(
                    $"{doneSubs}/{task.ChildTasks.Count}",
                    task.IsSubtasksExpanded
                        ? MaterialIconKind.ChevronDown
                        : MaterialIconKind.ChevronRight,
                    () => vm.ToggleTaskSubtasksExpanded(task)
                )
            );
        }

        // Direct Attachment / Submission PDF Viewer Chip
        var primaryPdf = task.PrimaryPdfFile;
        if (!string.IsNullOrWhiteSpace(primaryPdf))
        {
            rightRow.Children.Add(
                UiBuilder.GhostButton(
                    "PDF",
                    MaterialIconKind.FilePdfBox,
                    () => vm.OpenTaskFile(task, primaryPdf)
                )
            );
        }
        else if (task.AllFiles.Count > 0)
        {
            rightRow.Children.Add(
                UiBuilder.GhostButton(
                    "File",
                    MaterialIconKind.Paperclip,
                    () => vm.OpenTaskFile(task, task.AllFiles[0])
                )
            );
        }

        // Academic Term Badge
        var normTerm = TaskItem.NormalizeTerm(task.Term);
        if (!string.IsNullOrWhiteSpace(normTerm))
        {
            rightRow.Children.Add(
                UiBuilder.Pill(
                    normTerm,
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated),
                    fontSize: 10.5
                )
            );
        }

        if (task.DueDate.HasValue)
        {
            bool isOverdue = !task.IsCompleted && task.DueDate.Value < today;
            bool isDueToday = !task.IsCompleted && task.DueDate.Value == today;
            var dFg =
                isOverdue ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                : isDueToday ? ThemeService.Brush(ThemeService.SuccessFg)
                : ThemeService.Brush(ThemeService.TextSecondary);

            var dateText = isDueToday ? "Today" : task.DueDate.Value.ToString("MMM d");
            var dateRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dateRow.Children.Add(UiBuilder.Icon(MaterialIconKind.CalendarBlankOutline, 12, dFg));
            dateRow.Children.Add(
                new TextBlock
                {
                    Text = dateText,
                    FontSize = 11.5,
                    FontWeight = isOverdue || isDueToday ? FontWeight.Medium : FontWeight.Normal,
                    Foreground = dFg,
                }
            );
            rightRow.Children.Add(dateRow);
        }

        if (!string.IsNullOrWhiteSpace(task.CourseAlias) && vm.ActiveCourse is null)
        {
            rightRow.Children.Add(
                new TextBlock
                {
                    Text = $"#{task.CourseAlias}",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        Grid.SetColumn(rightRow, 2);
        rowGrid.Children.Add(rightRow);

        var rowContainer = new StackPanel { Spacing = 4 };
        rowContainer.Children.Add(rowGrid);

        // Inline Nested Subtasks Tree directly under the parent task row (First-Class Child TaskItems)
        task.EnsureChildTasksSynced();
        if (task.ChildTasks.Count > 0 && task.IsSubtasksExpanded)
        {
            var subTreeStack = new StackPanel { Spacing = 2, Margin = new Thickness(28, 4, 4, 2) };

            for (int i = 0; i < task.ChildTasks.Count; i++)
            {
                int subIdx = i;
                var child = task.ChildTasks[i];
                bool isChildSelected = _vm.SelectedTask?.Id == child.Id;

                var childRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*") };

                var checkSubBtn = new Button
                {
                    Content = UiBuilder.Icon(
                        child.IsCompleted
                            ? MaterialIconKind.CheckCircle
                            : MaterialIconKind.CircleOutline,
                        14.5,
                        child.IsCompleted
                            ? ThemeService.Brush(ThemeService.SuccessFg)
                            : ThemeService.PriorityBrush(child.Priority)
                    ),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 2.5),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                checkSubBtn.Click += async (_, e) =>
                {
                    e.Handled = true;
                    await vm.ToggleSubtaskAsync(task, subIdx);
                };
                childRow.Children.Add(checkSubBtn);

                var childInfoStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                childInfoStack.Children.Add(
                    new TextBlock
                    {
                        Text = child.Title,
                        FontSize = 12,
                        FontWeight = isChildSelected ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground =
                            child.IsCompleted ? ThemeService.Brush(ThemeService.TextMuted)
                            : isChildSelected ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        TextDecorations = child.IsCompleted ? TextDecorations.Strikethrough : null,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                );

                if (child.DueDate.HasValue)
                {
                    childInfoStack.Children.Add(
                        new TextBlock
                        {
                            Text = child.DueDate.Value.ToString("MMM d"),
                            FontSize = 11,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                }

                if (child.Priority != TaskPriority.P4)
                {
                    childInfoStack.Children.Add(
                        new TextBlock
                        {
                            Text = $"P{(int)child.Priority}",
                            FontSize = 10.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.PriorityBrush(child.Priority),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                }

                foreach (var lbl in child.Labels.Take(2))
                {
                    childInfoStack.Children.Add(
                        new TextBlock
                        {
                            Text = $"@{lbl}",
                            FontSize = 10.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                }

                var selectChildBtn = new Button
                {
                    Content = childInfoStack,
                    Background = isChildSelected
                        ? ThemeService.Brush(ThemeService.BgHover)
                        : Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2.5),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                selectChildBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    vm.SelectTask(child, openInspector: true);
                };
                Grid.SetColumn(selectChildBtn, 1);
                childRow.Children.Add(selectChildBtn);

                subTreeStack.Children.Add(childRow);
            }

            rowContainer.Children.Add(
                new Border
                {
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1, 0, 0, 0),
                    Margin = new Thickness(10, 0, 0, 0),
                    Child = subTreeStack,
                }
            );
        }

        var rowBorder = new Border
        {
            Background = isSelected
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent,
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Padding = new Thickness(6, 8.5),
            Transitions = UiBuilder.CreateSmoothBrushTransitions(110),
            Child = rowContainer,
        };

        rowBorder.PointerEntered += (_, _) =>
        {
            hoverActions.IsVisible = true;
            if (_vm.SelectedTask?.Id != task.Id)
                rowBorder.Background = ThemeService.Brush(ThemeService.BgHover);
        };
        rowBorder.PointerExited += (_, _) =>
        {
            if (_vm.SelectedTask?.Id != task.Id)
            {
                hoverActions.IsVisible = false;
                rowBorder.Background = Brushes.Transparent;
            }
        };
        rowBorder.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(rowBorder).Properties.IsLeftButtonPressed)
            {
                vm.SelectTask(task, openInspector: true);
            }
        };

        _taskRowsById[task.Id] = (rowBorder, titleBlock, hoverActions);
        return rowBorder;
    }

    // =========================================================================
    // 3. FLOATING CENTERED MODALS (QUICK ADD, QUICK SEARCH, NOTIFICATIONS, HELP)
    // =========================================================================

    private static void DetachFromParent(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl contentControl
                when ReferenceEquals(contentControl.Content, control):
                contentControl.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, control):
                presenter.Content = null;
                break;
        }
    }

    private void RefreshOverlayModal()
    {
        string targetKind =
            _vm.IsQuickAddModalOpen ? "QuickAdd"
            : _vm.IsSearchModalOpen ? "Search"
            : _vm.IsNotificationDrawerOpen ? "Notifications"
            : _vm.IsShortcutsModalOpen ? "Shortcuts"
            : ShouldFloatInspectorOverlay ? "FloatingInspector"
            : string.Empty;

        if (string.IsNullOrEmpty(targetKind))
        {
            _mountedModalKind = string.Empty;
            _modalOverlayContainer.IsVisible = false;
            _modalOverlayContainer.Child = null;
            return;
        }

        // If QuickAdd or Search modal is already mounted, update its inner dynamic list in-place
        if (targetKind == _mountedModalKind && _modalOverlayContainer.IsVisible)
        {
            if (targetKind == "QuickAdd")
            {
                RefreshQuickAddChips();
                return;
            }
            if (targetKind == "Search")
            {
                RefreshSearchResultsList();
                return;
            }
        }

        _mountedModalKind = targetKind;

        var backdrop = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBackdrop),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        backdrop.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, backdrop))
            {
                e.Handled = true;
                _vm.CloseTopOverlayOrDrawer();
                Focus();
            }
        };

        if (_vm.IsQuickAddModalOpen)
        {
            backdrop.Child = BuildFloatingQuickAddModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                _quickAddBox.Focus();
                _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
            });
            return;
        }

        if (_vm.IsSearchModalOpen)
        {
            backdrop.Child = BuildFloatingSearchModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            RefreshSearchResultsList();
            Dispatcher.UIThread.Post(() =>
            {
                _searchBox.Focus();
                _searchBox.SelectAll();
            });
            return;
        }

        if (_vm.IsNotificationDrawerOpen)
        {
            backdrop.Child = BuildFloatingNotificationsModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (_vm.IsShortcutsModalOpen)
        {
            backdrop.Child = BuildFloatingShortcutsModal();
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
            return;
        }

        if (ShouldFloatInspectorOverlay)
        {
            var drawer = InspectorPaneBuilder.Build(_vm);
            drawer.Width = Math.Min(360, Math.Max(280, Bounds.Width - 60));
            drawer.HorizontalAlignment = HorizontalAlignment.Right;
            drawer.VerticalAlignment = VerticalAlignment.Stretch;
            backdrop.Child = drawer;
            _modalOverlayContainer.Child = backdrop;
            _modalOverlayContainer.IsVisible = true;
        }
    }

    private Control BuildFloatingQuickAddModal()
    {
        DetachFromParent(_quickAddBox);
        _quickAddChipsContainer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        RefreshQuickAddChips();

        var stack = new StackPanel { Spacing = 10 };

        var topHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        topHeader.Children.Add(
            new TextBlock
            {
                Text = "Quick Add Task",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var escChip = UiBuilder.KbdChip("Esc");
        Grid.SetColumn(escChip, 1);
        topHeader.Children.Add(escChip);
        stack.Children.Add(topHeader);

        stack.Children.Add(_quickAddBox);
        stack.Children.Add(_quickAddChipsContainer);

        // Compact Attribute Selector Strip (Due Date, Priority, Course)
        var selectorsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddTokenBtn(string label, MaterialIconKind? icon, string token, string replaceRegex)
        {
            var b = UiBuilder.GhostButton(
                label,
                icon,
                () =>
                {
                    _vm.InsertQuickAddToken(token, replaceRegex);
                    _quickAddBox.Focus();
                    _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
                }
            );
            b.Margin = new Thickness(0, 0, 5, 5);
            selectorsWrap.Children.Add(b);
        }

        var dateRegex = @"\b(today|tomorrow|tmrw|next\s+week|\+\d+d|\d{4}-\d{2}-\d{2})\b";
        var quickDatePicker = new CalendarDatePicker
        {
            Watermark = "Pick date...",
            Width = 135,
            Margin = new Thickness(0, 0, 5, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        quickDatePicker.SelectedDateChanged += (_, _) =>
        {
            if (quickDatePicker.SelectedDate.HasValue)
            {
                var iso = quickDatePicker.SelectedDate.Value.ToString("yyyy-MM-dd");
                _vm.InsertQuickAddToken(iso, dateRegex);
                _quickAddBox.Focus();
                _quickAddBox.CaretIndex = _quickAddBox.Text?.Length ?? 0;
            }
        };
        selectorsWrap.Children.Add(quickDatePicker);

        AddTokenBtn("Today", MaterialIconKind.CalendarToday, "today", dateRegex);
        AddTokenBtn("Tomorrow", MaterialIconKind.CalendarArrowRight, "tomorrow", dateRegex);
        AddTokenBtn("Next week", MaterialIconKind.CalendarWeek, "+7d", dateRegex);

        var termRegex = @"\b@?(prelims?|midterms?|pre-?finals?|finals?)\b";
        foreach (var termName in new[] { "Prelims", "Midterms", "Prefinals", "Finals" })
        {
            AddTokenBtn(
                termName,
                MaterialIconKind.SchoolOutline,
                termName.ToLowerInvariant(),
                termRegex
            );
        }

        foreach (
            var p in new[] { TaskPriority.P1, TaskPriority.P2, TaskPriority.P3, TaskPriority.P4 }
        )
        {
            AddTokenBtn($"P{(int)p}", MaterialIconKind.FlagOutline, $"p{(int)p}", @"\bp[1-4]\b");
        }

        foreach (
            var c in _vm
                .Courses.Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.Alias))
                .Take(8)
        )
        {
            AddTokenBtn($"#{c.Alias}", null, $"#{c.Alias}", @"#[a-z0-9_-]+");
        }

        stack.Children.Add(selectorsWrap);
        stack.Children.Add(UiBuilder.HairlineDivider());

        // Footer (Cancel + Add task)
        var footer = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto") };
        footer.Children.Add(
            new TextBlock
            {
                Text = _vm.ActiveCourse is not null
                    ? $"Default course: #{_vm.ActiveCourse.Alias}"
                    : "Default: Inbox",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var cancelBtn = UiBuilder.GhostButton("Cancel", null, () => _vm.CloseQuickAddModal());
        cancelBtn.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(cancelBtn, 1);
        footer.Children.Add(cancelBtn);

        var submitBtn = UiBuilder.ActionButton(
            "Add task",
            MaterialIconKind.Check,
            async () => await _vm.SubmitQuickAddAsync(),
            isPrimary: true
        );
        Grid.SetColumn(submitBtn, 2);
        footer.Children.Add(submitBtn);

        stack.Children.Add(footer);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 540,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 85, 24, 24),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private Control BuildFloatingSearchModal()
    {
        DetachFromParent(_searchBox);
        _searchResultsStack = new StackPanel { Spacing = 2 };

        var stack = new StackPanel { Spacing = 8 };

        var searchHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        var icon = UiBuilder.Icon(
            MaterialIconKind.Magnify,
            18,
            ThemeService.Brush(ThemeService.TextSecondary)
        );
        icon.Margin = new Thickness(4, 0, 8, 0);
        searchHeader.Children.Add(icon);

        Grid.SetColumn(_searchBox, 1);
        searchHeader.Children.Add(_searchBox);

        var escChip = UiBuilder.KbdChip("Esc");
        escChip.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(escChip, 2);
        searchHeader.Children.Add(escChip);

        stack.Children.Add(searchHeader);
        stack.Children.Add(UiBuilder.HairlineDivider());

        var scroll = new ScrollViewer { MaxHeight = 360, Content = _searchResultsStack };
        stack.Children.Add(scroll);

        stack.Children.Add(UiBuilder.HairlineDivider());
        stack.Children.Add(
            new TextBlock
            {
                Text = "Up/Down to navigate · Enter to jump to match · Esc to close",
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
            }
        );

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 70, 24, 24),
            Padding = new Thickness(14, 12),
            Child = stack,
        };
    }

    private void RefreshSearchResultsList()
    {
        _searchResultsStack.Children.Clear();
        if (_vm.SearchResults.Count == 0)
        {
            _searchResultsStack.Children.Add(
                new TextBlock
                {
                    Text = "No matching tasks, courses, or PDFs found.",
                    FontSize = 12.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    Margin = new Thickness(8, 16),
                }
            );
            return;
        }

        for (int i = 0; i < _vm.SearchResults.Count; i++)
        {
            int idx = i;
            var item = _vm.SearchResults[i];
            bool isSelected = i == _vm.SelectedSearchResultIndex;

            var iconKind = item.Kind switch
            {
                SearchResultKind.Course => MaterialIconKind.Pound,
                SearchResultKind.PdfDocument => MaterialIconKind.FilePdfBox,
                _ => MaterialIconKind.CheckboxBlankCircleOutline,
            };

            var iconFg = item.Kind switch
            {
                SearchResultKind.PdfDocument => ThemeService.Brush(ThemeService.AccentPrimary),
                SearchResultKind.Course => ThemeService.Brush(ThemeService.PriorityP3Fg),
                _ => ThemeService.Brush(ThemeService.TextSecondary),
            };

            var rowGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("24,*,Auto,Auto"),
            };
            rowGrid.Children.Add(UiBuilder.Icon(iconKind, 15.5, iconFg));

            rowGrid.Children.Add(
                new TextBlock
                {
                    Text = item.Title,
                    FontSize = 13,
                    FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [Grid.ColumnProperty] = 1,
                }
            );

            rowGrid.Children.Add(
                new TextBlock
                {
                    Text = item.Subtitle,
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0),
                    [Grid.ColumnProperty] = 2,
                }
            );

            var badge = UiBuilder.Pill(
                item.Badge,
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgElevated),
                fontSize: 10.5
            );
            Grid.SetColumn(badge, 3);
            rowGrid.Children.Add(badge);

            var normalBg = isSelected
                ? ThemeService.Brush(ThemeService.AccentSoftBg)
                : Brushes.Transparent;
            var btn = new Button
            {
                Content = rowGrid,
                Background = normalBg,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 7),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            btn.PointerEntered += (_, _) =>
                btn.Background = isSelected ? normalBg : ThemeService.Brush(ThemeService.BgHover);
            btn.PointerExited += (_, _) => btn.Background = normalBg;
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                _vm.ExecuteSearchResult(item);
            };

            _searchResultsStack.Children.Add(btn);
        }
    }

    private Control BuildFloatingNotificationsModal()
    {
        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new TextBlock
            {
                Text = $"Notifications ({_vm.Notifications.Count})",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.IconButton(
            MaterialIconKind.Close,
            () => _vm.ToggleNotificationDrawer()
        );
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        if (_vm.Notifications.Count == 0)
        {
            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        "No notifications yet. Background eLMS polling will post new deadlines and grades here.",
                    FontSize = 12.5,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    Margin = new Thickness(0, 12),
                }
            );
        }
        else
        {
            foreach (var n in _vm.Notifications.Take(8))
            {
                var nRow = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    Margin = new Thickness(0, 4),
                };
                nRow.Children.Add(
                    UiBuilder.Icon(
                        MaterialIconKind.BellOutline,
                        15,
                        ThemeService.Brush(ThemeService.AccentPrimary)
                    )
                );
                nRow.Children.Add(
                    new StackPanel
                    {
                        Margin = new Thickness(8, 0),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = n.Title,
                                FontSize = 12.5,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            },
                            new TextBlock
                            {
                                Text = n.Message,
                                FontSize = 11.5,
                                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                            },
                        },
                        [Grid.ColumnProperty] = 1,
                    }
                );
                nRow.Children.Add(
                    new TextBlock
                    {
                        Text = n.OccurredAt.LocalDateTime.ToString("h:mm tt"),
                        FontSize = 11,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        [Grid.ColumnProperty] = 2,
                    }
                );
                stack.Children.Add(nRow);
            }
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 70, 24, 24),
            Padding = new Thickness(16, 12),
            Child = stack,
        };
    }

    private Control BuildFloatingShortcutsModal()
    {
        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        header.Children.Add(
            new TextBlock
            {
                Text = "Keyboard Shortcuts",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var closeBtn = UiBuilder.IconButton(
            MaterialIconKind.Close,
            () => _vm.ToggleShortcutsModal()
        );
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        stack.Children.Add(header);
        stack.Children.Add(UiBuilder.HairlineDivider());

        (string Key, string Desc)[] items =
        [
            ("Q", "Open floating Quick-Add Task modal"),
            ("/ or Ctrl+K", "Open Quick-Search & Navigation palette"),
            ("Esc", "Close active modal or Task Inspector drawer"),
            ("j / k or Up / Down", "Move task selection or turn PDF pages"),
            ("g / Shift+G", "Jump to top / bottom item"),
            ("Enter or E", "Inspect selected task (or open primary file/PDF)"),
            ("Space or D", "Complete / reopen selected task"),
            ("O", "Open selected assignment in STI eLMS app"),
            ("[ or Ctrl+B", "Collapse / expand left navigation sidebar"),
            ("1 - 5", "Switch smart views (Inbox, Today, Upcoming, Calendar, Completed)"),
            ("R / Ctrl+S", "Fast poll assignments / Run full eLMS sync"),
            ("T", "Toggle Dark / Light mode"),
        ];

        foreach (var (key, desc) in items)
        {
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("130,*"),
                Margin = new Thickness(0, 2),
            };
            var chip = UiBuilder.KbdChip(key);
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(chip);
            row.Children.Add(
                new TextBlock
                {
                    Text = desc,
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    [Grid.ColumnProperty] = 1,
                }
            );
            stack.Children.Add(row);
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = BoxShadows.Parse("0 16 40 0 #66000000"),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 70, 24, 24),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private void RefreshQuickAddChips()
    {
        _quickAddChipsContainer.Children.Clear();
        if (_vm.QuickAddChips.Count == 0)
        {
            _quickAddChipsContainer.IsVisible = false;
            return;
        }

        _quickAddChipsContainer.IsVisible = true;
        foreach (var chip in _vm.QuickAddChips)
        {
            _quickAddChipsContainer.Children.Add(
                UiBuilder.Pill(
                    $"{chip.Kind}: {chip.Text}",
                    ThemeService.Brush(ThemeService.AccentPrimary),
                    ThemeService.Brush(ThemeService.AccentSoftBg)
                )
            );
        }
    }

    private async void OnQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.SubmitQuickAddAsync();
            Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CloseQuickAddModal();
            Focus();
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CloseSearchModal();
            Focus();
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            _vm.MoveSearchSelection(+1);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            _vm.MoveSearchSelection(-1);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.ExecuteSelectedSearchResult();
            Focus();
        }
    }

    // =========================================================================
    // 4. QUIET MINIMAL STATUS BAR & KEYBOARD NAVIGATION
    // =========================================================================

    private Control BuildStatusBar()
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(12, 0),
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(
            UiBuilder.Icon(
                _vm.IsSyncing ? MaterialIconKind.Sync : MaterialIconKind.CheckCircleOutline,
                12,
                _vm.IsSyncing
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted)
            )
        );
        left.Children.Add(
            new TextBlock
            {
                Text = _vm.StatusText,
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        grid.Children.Add(left);

        var rightHints = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightHints.Children.Add(
            new TextBlock
            {
                Text = "Q Add · / Search · j/k Nav · Enter Inspect · ? Help",
                FontSize = 11,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        Grid.SetColumn(rightHints, 1);
        grid.Children.Add(rightHints);

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgSurface),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    private async Task PromptOpenBasinFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select Academic Basin Directory (e.g. ~/Documents/School-Files/YR4_01)",
                AllowMultiple = false,
            }
        );

        if (folders.Count > 0)
        {
            var localPath = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
                await _vm.OpenBasinAsync(localPath);
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        // Global Escape always untoggles/closes the top-most floating modal or inspector drawer
        if (e.Key == Key.Escape)
        {
            if (_vm.CloseTopOverlayOrDrawer())
            {
                e.Handled = true;
                Focus();
                return;
            }
        }

        // Global Ctrl+K / Ctrl+F / Ctrl+B / Ctrl+S shortcuts
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key is Key.K or Key.F)
            {
                e.Handled = true;
                _vm.ToggleSearchModal();
                return;
            }
            if (e.Key == Key.B)
            {
                e.Handled = true;
                _vm.ToggleSidebar();
                return;
            }
            if (e.Key == Key.S)
            {
                e.Handled = true;
                await _vm.RunFullSyncAsync();
                return;
            }
        }

        // Skip single-key Vim shortcuts when typing inside any TextBox
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        switch (e.Key)
        {
            case Key.J or Key.Down:
                e.Handled = true;
                if (_vm.IsSearchModalOpen)
                    _vm.MoveSearchSelection(+1);
                else if (_vm.ActiveMode == WorkspaceTabMode.PdfReader)
                    _vm.PdfReader.NextPage();
                else
                    _vm.MoveSelection(+1);
                break;

            case Key.K
            or Key.Up:
                e.Handled = true;
                if (_vm.IsSearchModalOpen)
                    _vm.MoveSearchSelection(-1);
                else if (_vm.ActiveMode == WorkspaceTabMode.PdfReader)
                    _vm.PdfReader.PreviousPage();
                else
                    _vm.MoveSelection(-1);
                break;

            case Key.PageDown when _vm.ActiveMode == WorkspaceTabMode.PdfReader:
                e.Handled = true;
                _vm.PdfReader.NextPage();
                break;

            case Key.PageUp when _vm.ActiveMode == WorkspaceTabMode.PdfReader:
                e.Handled = true;
                _vm.PdfReader.PreviousPage();
                break;

            case Key.G:
                e.Handled = true;
                if (_vm.ActiveMode == WorkspaceTabMode.PdfReader)
                    _vm.PdfReader.JumpToPage(
                        e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _vm.PdfReader.PageCount : 1
                    );
                else
                    _vm.JumpSelection(toEnd: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;

            case Key.D
            or Key.Space
                when _vm.SelectedTask is not null && _vm.ActiveMode != WorkspaceTabMode.PdfReader:
                e.Handled = true;
                await _vm.ToggleTaskCompletedAsync(_vm.SelectedTask);
                break;

            case Key.E
                when _vm.SelectedTask is not null && _vm.ActiveMode != WorkspaceTabMode.PdfReader:
                e.Handled = true;
                _vm.ToggleInspector();
                break;

            case Key.Enter
                when _vm.SelectedTask is not null && _vm.ActiveMode != WorkspaceTabMode.PdfReader:
                e.Handled = true;
                if (!_vm.IsInspectorOpen)
                    _vm.OpenInspector(_vm.SelectedTask);
                else
                    _vm.OpenPrimaryTaskFileOrNote(_vm.SelectedTask);
                break;

            case Key.O:
                e.Handled = true;
                await _vm.OpenTaskInElmsAppAsync(_vm.SelectedTask);
                break;

            case Key.Q:
                e.Handled = true;
                _vm.ToggleQuickAddModal();
                break;

            case Key.OemQuestion
            or Key.Divide:
                e.Handled = true;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _vm.ToggleShortcutsModal();
                else
                    _vm.ToggleSearchModal();
                break;

            case Key.OemOpenBrackets:
                e.Handled = true;
                if (_vm.ActiveMode == WorkspaceTabMode.Calendar)
                    _vm.Calendar.PreviousMonth();
                else
                    _vm.ToggleSidebar();
                break;

            case Key.OemCloseBrackets when _vm.ActiveMode == WorkspaceTabMode.Calendar:
                e.Handled = true;
                _vm.Calendar.NextMonth();
                break;

            case Key.T:
                e.Handled = true;
                await _vm.ToggleThemeAsync();
                break;

            case Key.R:
                e.Handled = true;
                await _vm.RunFastPollAsync();
                break;

            case Key.D1:
                e.Handled = true;
                _vm.SelectSmartView(SmartViewKind.Inbox);
                break;

            case Key.D2:
                e.Handled = true;
                _vm.SelectSmartView(SmartViewKind.Today);
                break;

            case Key.D3:
                e.Handled = true;
                _vm.SelectSmartView(SmartViewKind.Upcoming);
                break;

            case Key.D4:
                e.Handled = true;
                _vm.SelectCalendarView();
                break;

            case Key.D5:
                e.Handled = true;
                _vm.SelectSmartView(SmartViewKind.Completed);
                break;
        }
    }
}
