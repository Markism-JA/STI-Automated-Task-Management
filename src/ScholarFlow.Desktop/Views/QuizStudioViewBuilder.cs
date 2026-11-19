using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Cohesive pure-C# view builder for the Evidence-Grounded PDF Quiz &amp; Active Recall Studio (zero .axaml, zero emojis).
/// Matches the low-noise hairline aesthetic of TaskList, CourseWorkspace, and PdfReaderViewBuilder,
/// featuring a 2-column Split Builder + Live Visual PDF Scope Previewer and a hairline Assessment Catalog.
/// </summary>
public static class QuizStudioViewBuilder
{
    private static readonly string[] AcademicTerms = ["Prelims", "Midterms", "Prefinals", "Finals"];

    public static Control Build(MainWindowViewModel vm)
    {
        var studio = vm.QuizStudio;

        if (studio.ActiveSession is not null)
        {
            return studio.IsShowingResultsSummary
                ? BuildSessionSummaryView(vm, studio)
                : BuildActiveQuestionRunnerView(vm, studio);
        }

        return BuildStudioWorkspaceView(vm, studio);
    }

    // =========================================================================
    // 1. COHESIVE STUDIO WORKSPACE (HEADER + SUBJECT BAR + SUB-TABS)
    // =========================================================================

    private static Control BuildStudioWorkspaceView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*"),
            Margin = new Thickness(20, 14, 20, 14),
        };

        // Top Fixed Header & Subject / Sub-Tab Strip
        var topStack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };

        var titleRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var titleLeft = new StackPanel { Spacing = 2 };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleLine.Children.Add(
            new TextBlock
            {
                Text = "Test & Active Recall",
                FontSize = 20,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );
        if (studio.SelectedCourse is not null)
        {
            titleLine.Children.Add(
                UiBuilder.Pill(
                    $"#{studio.SelectedCourse.Alias} · {studio.SelectedCourse.Title}",
                    ThemeService.Brush(ThemeService.AccentPrimary),
                    ThemeService.Brush(ThemeService.AccentSoftBg)
                )
            );
        }
        titleLeft.Children.Add(titleLine);

        var pdfFileName = string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            ? "No PDF found"
            : Path.GetFileName(studio.SelectedPdfPath);
        titleLeft.Children.Add(
            new TextBlock
            {
                Text =
                    $"Source: {pdfFileName} ({studio.PdfTotalPages} pages)  ·  {studio.ScopePreviewPageCount} content page(s) in active scope",
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            }
        );
        titleRow.Children.Add(titleLeft);

        // Right Utility Actions (Due Deck, Gemini Web Bridge, AI Settings)
        var utilityRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        int dueCount = studio.AllDueReviewQuestions.Count;
        utilityRow.Children.Add(
            UiBuilder.GhostButton(
                dueCount > 0 ? $"Practice Due ({dueCount})" : "Practice Deck",
                MaterialIconKind.CardsOutline,
                () => studio.StartDueSpacedRepetitionDeck(studio.SelectedCourse?.FolderName),
                isActive: dueCount > 0
            )
        );

        utilityRow.Children.Add(
            UiBuilder.GhostButton(
                "Gemini Web JSON",
                MaterialIconKind.CodeJson,
                () => studio.OpenManualJsonBridge(),
                isActive: studio.IsManualJsonModalOpen
            )
        );

        utilityRow.Children.Add(
            UiBuilder.GhostButton(
                "AI Engine",
                MaterialIconKind.CogOutline,
                () => studio.ToggleAiSettings(),
                isActive: studio.IsAiSettingsOpen
            )
        );

        Grid.SetColumn(utilityRow, 1);
        titleRow.Children.Add(utilityRow);
        topStack.Children.Add(titleRow);

        // Subject Switcher + Sub-Tab Switcher Bar
        var navBarGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };

        var subjectWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in vm.Courses.Where(x => !x.IsArchived))
        {
            var captured = c;
            bool isSelected = studio.SelectedCourse?.FolderName == c.FolderName;
            var btn = UiBuilder.GhostButton(
                $"#{c.Alias}",
                isSelected ? MaterialIconKind.Check : null,
                () => studio.SelectCourseScope(captured),
                isActive: isSelected
            );
            btn.Margin = new Thickness(0, 0, 5, 4);
            ToolTip.SetTip(btn, c.Title);
            subjectWrap.Children.Add(btn);
        }
        navBarGrid.Children.Add(subjectWrap);

        var subTabRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        subTabRow.Children.Add(
            UiBuilder.GhostButton(
                "Builder & PDF Preview",
                MaterialIconKind.CreationOutline,
                () => studio.SetSubTab(QuizStudioSubTab.Builder),
                isActive: studio.ActiveSubTab == QuizStudioSubTab.Builder
            )
        );

        subTabRow.Children.Add(
            UiBuilder.GhostButton(
                $"Quiz Catalog ({studio.SavedQuizzes.Count})",
                MaterialIconKind.LibraryShelves,
                () => studio.SetSubTab(QuizStudioSubTab.Catalog),
                isActive: studio.ActiveSubTab == QuizStudioSubTab.Catalog
            )
        );

        subTabRow.Children.Add(
            UiBuilder.GhostButton(
                studio.OrganizerQuiz is not null
                    ? $"Deck Organizer ({studio.OrganizerQuiz.Questions.Count})"
                    : "Deck Organizer",
                MaterialIconKind.TuneVariant,
                () =>
                {
                    if (studio.OrganizerQuiz is null && studio.SavedQuizzes.Count > 0)
                        studio.OpenQuizInOrganizer(studio.SavedQuizzes[0]);
                    else
                        studio.SetSubTab(QuizStudioSubTab.Organizer);
                },
                isActive: studio.ActiveSubTab == QuizStudioSubTab.Organizer
            )
        );

        Grid.SetColumn(subTabRow, 1);
        navBarGrid.Children.Add(subTabRow);
        topStack.Children.Add(navBarGrid);

        // Status / Feedback Line + Active Indeterminate Progress Indicator
        if (!string.IsNullOrWhiteSpace(studio.StatusMessage) || studio.IsBusy)
        {
            var statusRowGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                ColumnSpacing = 8,
            };
            statusRowGrid.Children.Add(
                UiBuilder.Icon(
                    studio.IsStatusError
                        ? MaterialIconKind.AlertCircleOutline
                        : studio.IsBusy
                            ? MaterialIconKind.Autorenew
                            : MaterialIconKind.CheckCircleOutline,
                    14,
                    studio.IsStatusError
                        ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : ThemeService.Brush(ThemeService.AccentPrimary)
                )
            );
            statusRowGrid.Children.Add(
                new TextBlock
                {
                    Text = studio.StatusMessage,
                    FontSize = 12,
                    Foreground = studio.IsStatusError
                        ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : ThemeService.Brush(ThemeService.TextSecondary),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    [Grid.ColumnProperty] = 1,
                }
            );

            var statusContainer = new StackPanel { Spacing = 6 };
            statusContainer.Children.Add(statusRowGrid);
            if (studio.IsBusy)
            {
                statusContainer.Children.Add(
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Height = 3,
                        Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                        Background = ThemeService.Brush(ThemeService.BgElevated),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    }
                );
            }

            topStack.Children.Add(
                new Border
                {
                    Background = studio.IsStatusError
                        ? ThemeService.Brush(ThemeService.PriorityP1Bg)
                        : ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = studio.IsStatusError
                        ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                        : studio.IsBusy
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6),
                    Child = statusContainer,
                }
            );
        }

        if (studio.IsAiSettingsOpen)
            topStack.Children.Add(BuildAiSettingsCard(vm, studio));

        if (studio.IsManualJsonModalOpen)
            topStack.Children.Add(BuildManualJsonBridgeCard(studio));

        topStack.Children.Add(UiBuilder.HairlineDivider());
        root.Children.Add(topStack);

        // Body Content (2-Column Builder + Live PDF Page Preview, Hairline Catalog, or Deck Organizer)
        Control bodyContent = studio.ActiveSubTab switch
        {
            QuizStudioSubTab.Catalog => BuildCatalogView(vm, studio),
            QuizStudioSubTab.Organizer => BuildDeckOrganizerView(vm, studio),
            _ => BuildSplitBuilderAndPreviewView(vm, studio),
        };

        Grid.SetRow(bodyContent, 1);
        root.Children.Add(bodyContent);
        return root;
    }

    // =========================================================================
    // 1A. 2-COLUMN SPLIT BUILDER & LIVE VISUAL PDF SCOPE PREVIEW
    // =========================================================================

    private static Control BuildSplitBuilderAndPreviewView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var splitGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("1.1*,14,0.9*") };

        // LEFT COLUMN: Scope, Outline Tree Picker, Mode & Question Count
        var leftStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 4, 20) };

        // 1. Source Material Bar (Master PDF default + optional Individual Handout switcher)
        var sourceCard = new StackPanel { Spacing = 8 };
        var sourceHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        sourceHeader.Children.Add(
            UiBuilder.SectionHeader("1. Material Source (Defaults to Master PDF)")
        );

        var masterPdfs = studio.AvailablePdfs.Where(p => p.IsMaster).ToList();
        var handoutPdfs = studio.AvailablePdfs.Where(p => !p.IsMaster).ToList();

        if (handoutPdfs.Count > 0)
        {
            var toggleHandoutsBtn = UiBuilder.GhostButton(
                studio.ShowIndividualHandouts ? "Hide Handouts" : $"Handouts ({handoutPdfs.Count})",
                studio.ShowIndividualHandouts
                    ? MaterialIconKind.ChevronUp
                    : MaterialIconKind.ChevronDown,
                () => studio.ToggleShowIndividualHandouts(),
                isActive: studio.ShowIndividualHandouts
            );
            Grid.SetColumn(toggleHandoutsBtn, 1);
            sourceHeader.Children.Add(toggleHandoutsBtn);
        }
        sourceCard.Children.Add(sourceHeader);

        var pdfPills = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, fullPath, _) in masterPdfs)
        {
            var captured = fullPath;
            bool active = string.Equals(
                studio.SelectedPdfPath,
                captured,
                StringComparison.OrdinalIgnoreCase
            );
            var btn = UiBuilder.GhostButton(
                label,
                MaterialIconKind.BookEducationOutline,
                () => studio.SelectPdfScope(captured),
                isActive: active
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            pdfPills.Children.Add(btn);
        }

        if (studio.ShowIndividualHandouts || masterPdfs.Count == 0)
        {
            foreach (var (label, fullPath, _) in handoutPdfs)
            {
                var captured = fullPath;
                bool active = string.Equals(
                    studio.SelectedPdfPath,
                    captured,
                    StringComparison.OrdinalIgnoreCase
                );
                var btn = UiBuilder.GhostButton(
                    label,
                    MaterialIconKind.FilePdfBox,
                    () => studio.SelectPdfScope(captured),
                    isActive: active
                );
                btn.Margin = new Thickness(0, 0, 5, 4);
                pdfPills.Children.Add(btn);
            }
        }
        sourceCard.Children.Add(pdfPills);
        leftStack.Children.Add(UiBuilder.Card(sourceCard, padding: new Thickness(12, 10)));

        // 2. Academic Term, Custom Page Range & Outline Tree Picker
        var scopeCard = new StackPanel { Spacing = 8 };
        var scopeHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        scopeHeader.Children.Add(
            UiBuilder.SectionHeader(
                $"2. Term, Page Range & Section Picker ({studio.ScopePreviewPageCount} pages in scope)"
            )
        );

        if (
            studio.StartPage.HasValue
            || studio.EndPage.HasValue
            || studio.SelectedSectionKeys.Count > 0
            || !string.IsNullOrEmpty(studio.SelectedTerm)
        )
        {
            var resetBtn = UiBuilder.GhostButton(
                "Reset Scope",
                MaterialIconKind.Refresh,
                () => studio.ClearScopeFilters()
            );
            Grid.SetColumn(resetBtn, 1);
            scopeHeader.Children.Add(resetBtn);
        }
        scopeCard.Children.Add(scopeHeader);

        // Term + Page Range Row
        var termRangeWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        var allTermBtn = UiBuilder.GhostButton(
            "All Terms",
            null,
            () => studio.SelectTermScope(null),
            isActive: string.IsNullOrEmpty(studio.SelectedTerm)
        );
        allTermBtn.Margin = new Thickness(0, 0, 4, 4);
        termRangeWrap.Children.Add(allTermBtn);

        foreach (var termName in AcademicTerms)
        {
            var capturedTerm = termName;
            bool isTermActive = string.Equals(
                studio.SelectedTerm,
                capturedTerm,
                StringComparison.OrdinalIgnoreCase
            );
            var tBtn = UiBuilder.GhostButton(
                capturedTerm,
                null,
                () => studio.SelectTermScope(capturedTerm),
                isActive: isTermActive
            );
            tBtn.Margin = new Thickness(0, 0, 4, 4);
            termRangeWrap.Children.Add(tBtn);
        }

        var startPageBox = new TextBox
        {
            Text = studio.StartPage?.ToString() ?? string.Empty,
            Watermark = "From pg",
            Width = 68,
            FontSize = 12,
            Margin = new Thickness(6, 0, 4, 4),
        };
        var endPageBox = new TextBox
        {
            Text = studio.EndPage?.ToString() ?? string.Empty,
            Watermark = "To pg",
            Width = 68,
            FontSize = 12,
            Margin = new Thickness(0, 0, 4, 4),
        };

        void ApplyPageInputs()
        {
            int? s = int.TryParse(startPageBox.Text?.Trim(), out var sv) && sv > 0 ? sv : null;
            int? e = int.TryParse(endPageBox.Text?.Trim(), out var ev) && ev > 0 ? ev : null;
            studio.SetPageRange(s, e);
        }

        startPageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyPageInputs();
            }
        };
        endPageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyPageInputs();
            }
        };

        var applyPagesBtn = UiBuilder.GhostButton(
            "Set Pages",
            MaterialIconKind.Check,
            ApplyPageInputs
        );
        applyPagesBtn.Margin = new Thickness(0, 0, 4, 4);

        termRangeWrap.Children.Add(startPageBox);
        termRangeWrap.Children.Add(endPageBox);
        termRangeWrap.Children.Add(applyPagesBtn);
        scopeCard.Children.Add(termRangeWrap);

        // Clean Hairline Outline Section Tree Picker (like PdfReaderViewBuilder's Document Outline)
        var visibleSections = studio
            .OutlineSections.Where(s => s.Level >= 2 || studio.OutlineSections.Count <= 6)
            .Where(s =>
                string.IsNullOrWhiteSpace(studio.SelectedTerm)
                || string.IsNullOrWhiteSpace(s.Term)
                || string.Equals(s.Term, studio.SelectedTerm, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (visibleSections.Count > 0)
        {
            var treeListStack = new StackPanel { Spacing = 1 };
            foreach (var sec in visibleSections)
            {
                var capturedSec = sec;
                var key = QuizStudioViewModel.GetSectionKey(capturedSec);
                bool isChecked = studio.SelectedSectionKeys.Contains(key);
                bool isCurrentlyPreviewed =
                    studio.CurrentPreviewPdfPage >= capturedSec.StartPage
                    && studio.CurrentPreviewPdfPage <= capturedSec.EndPage;

                var secRow = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
                    ColumnSpacing = 6,
                };

                var chkBtn = UiBuilder.IconButton(
                    isChecked
                        ? MaterialIconKind.CheckboxMarked
                        : MaterialIconKind.CheckboxBlankOutline,
                    () => studio.ToggleOutlineSection(capturedSec),
                    15,
                    isChecked
                        ? ThemeService.Brush(ThemeService.AccentPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted)
                );
                secRow.Children.Add(chkBtn);

                var secTitleBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = capturedSec.Title,
                        FontSize = 12.5,
                        FontWeight =
                            isChecked || isCurrentlyPreviewed
                                ? FontWeight.SemiBold
                                : FontWeight.Normal,
                        Foreground =
                            isChecked ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : isCurrentlyPreviewed ? ThemeService.Brush(ThemeService.TextPrimary)
                            : ThemeService.Brush(ThemeService.TextSecondary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2, 4),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                secTitleBtn.Click += (_, _) => studio.ToggleOutlineSection(capturedSec);
                Grid.SetColumn(secTitleBtn, 1);
                secRow.Children.Add(secTitleBtn);

                if (!string.IsNullOrWhiteSpace(capturedSec.Term))
                {
                    var termBadge = UiBuilder.Pill(
                        capturedSec.Term,
                        ThemeService.Brush(ThemeService.TextMuted),
                        ThemeService.Brush(ThemeService.BgElevated),
                        fontSize: 10
                    );
                    Grid.SetColumn(termBadge, 2);
                    secRow.Children.Add(termBadge);
                }

                var previewSecBtn = UiBuilder.GhostButton(
                    $"p.{capturedSec.StartPage}–{capturedSec.EndPage}",
                    MaterialIconKind.EyeOutline,
                    () => studio.JumpPreviewToSection(capturedSec),
                    isActive: isCurrentlyPreviewed
                );
                Grid.SetColumn(previewSecBtn, 3);
                secRow.Children.Add(previewSecBtn);

                treeListStack.Children.Add(
                    new Border
                    {
                        Background = isCurrentlyPreviewed
                            ? ThemeService.Brush(ThemeService.BgElevated)
                            : Brushes.Transparent,
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 2),
                        Child = secRow,
                    }
                );
            }

            scopeCard.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgBase),
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 4),
                    MaxHeight = 215,
                    Child = UiBuilder.PersistentScrollViewer(
                        $"quiz-outline:{studio.SelectedPdfPath}",
                        treeListStack
                    ),
                }
            );
        }

        leftStack.Children.Add(UiBuilder.Card(scopeCard, padding: new Thickness(12, 10)));

        // 3. Assessment Mode, Custom Question-Type Distribution & Output Engine
        var configCard = new StackPanel { Spacing = 10 };
        configCard.Children.Add(
            UiBuilder.SectionHeader("3. Question-Type Distribution, Item Count & Generator Mode")
        );

        if (!string.IsNullOrWhiteSpace(studio.AppendTargetQuizId))
        {
            var appendBanner = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 8,
            };
            appendBanner.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Appending new non-duplicate questions into: {studio.AppendTargetQuizTitle}",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            var cancelAppendBtn = UiBuilder.GhostButton(
                "Create New Deck Instead",
                MaterialIconKind.Close,
                () => studio.ClearAppendTarget()
            );
            Grid.SetColumn(cancelAppendBtn, 1);
            appendBanner.Children.Add(cancelAppendBtn);

            configCard.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.AccentSoftBg),
                    BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6),
                    Child = appendBanner,
                }
            );
        }

        // Preset Templates Row
        var modeWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (
            var (preset, label, icon) in new[]
            {
                (
                    QuizPresetMode.ExamSimulator,
                    "STI Exam Simulator",
                    MaterialIconKind.ClipboardTextOutline
                ),
                (QuizPresetMode.QuickReview, "Quick Concept Review", MaterialIconKind.FlashOutline),
                (QuizPresetMode.WeakTopicDrill, "Weak-Topic Drill", MaterialIconKind.Target),
                (QuizPresetMode.Custom, "Custom Mix", MaterialIconKind.TuneVariant),
            }
        )
        {
            var capturedPreset = preset;
            var pBtn = UiBuilder.GhostButton(
                label,
                icon,
                () => studio.SetPreset(capturedPreset),
                isActive: studio.SelectedPreset == capturedPreset
            );
            pBtn.Margin = new Thickness(0, 0, 6, 4);
            modeWrap.Children.Add(pBtn);
        }
        configCard.Children.Add(modeWrap);

        // Total Item Count + Allocation Mode Switcher (Weights % vs Exact Counts #)
        var countAndModeHeader = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var countWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (int cnt in new[] { 10, 20, 30, 50, 60, 100 })
        {
            int capturedCnt = cnt;
            var cBtn = UiBuilder.GhostButton(
                $"{cnt}",
                null,
                () => studio.SetQuestionCount(capturedCnt),
                isActive: studio.QuestionCount == capturedCnt
            );
            cBtn.Margin = new Thickness(0, 0, 4, 4);
            countWrap.Children.Add(cBtn);
        }

        var customCountBox = new TextBox
        {
            Text = studio.QuestionCount.ToString(),
            Watermark = "Items (1-150)",
            Width = 78,
            FontSize = 12,
            Margin = new Thickness(4, 0, 4, 4),
        };
        customCountBox.PropertyChanged += (_, e) =>
        {
            if (
                e.Property == TextBox.TextProperty
                && int.TryParse(customCountBox.Text?.Trim(), out var parsed)
                && parsed is >= 1 and <= 150
            )
            {
                studio.SetQuestionCount(parsed, notify: false);
            }
        };
        customCountBox.KeyDown += (_, e) =>
        {
            if (
                e.Key == Key.Enter
                && int.TryParse(customCountBox.Text?.Trim(), out var parsed)
                && parsed > 0
            )
            {
                e.Handled = true;
                studio.SetQuestionCount(parsed, notify: true);
            }
        };
        countWrap.Children.Add(customCountBox);
        countAndModeHeader.Children.Add(countWrap);

        var allocModeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        allocModeRow.Children.Add(
            UiBuilder.GhostButton(
                "Weights (%)",
                MaterialIconKind.PercentOutline,
                () => studio.SetAllocationMode(QuestionAllocationMode.Percentages),
                isActive: studio.AllocationMode == QuestionAllocationMode.Percentages
            )
        );
        allocModeRow.Children.Add(
            UiBuilder.GhostButton(
                "Exact Counts (#)",
                MaterialIconKind.Pound,
                () => studio.SetAllocationMode(QuestionAllocationMode.ExactCounts),
                isActive: studio.AllocationMode == QuestionAllocationMode.ExactCounts
            )
        );
        Grid.SetColumn(allocModeRow, 1);
        countAndModeHeader.Children.Add(allocModeRow);
        configCard.Children.Add(countAndModeHeader);

        // Per-Question-Kind Interactive Allocation Grid
        var resolvedKindCounts = studio.GetResolvedExactKindCounts();
        var kindsGrid = new StackPanel { Spacing = 4 };
        foreach (
            var (kind, title, desc) in new[]
            {
                (
                    QuizQuestionKind.MultipleChoice,
                    "Multiple Choice",
                    "4 options with plausible sibling distractors"
                ),
                (
                    QuizQuestionKind.Situational,
                    "Situational / Case",
                    "Applied troubleshooting & scenario analysis"
                ),
                (
                    QuizQuestionKind.Identification,
                    "Identification",
                    "Direct canonical term or concept recall"
                ),
                (
                    QuizQuestionKind.FillInTheBlank,
                    "Fill in the Blank",
                    "Contextual statement with _____ missing keyword"
                ),
                (
                    QuizQuestionKind.TrueFalse,
                    "True / False",
                    "Claim verification against handout facts"
                ),
                (
                    QuizQuestionKind.Ordering,
                    "Step Ordering",
                    "Chronological / procedural sequence arrangement"
                ),
            }
        )
        {
            var capturedKind = kind;
            int rawVal = studio.KindAllocations.GetValueOrDefault(kind, 0);
            int exactItems = resolvedKindCounts.GetValueOrDefault(kind, 0);
            bool isEnabled = exactItems > 0;

            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 10,
            };

            var labelStack = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            labelStack.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontSize = 12.5,
                    FontWeight = isEnabled ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = isEnabled
                        ? ThemeService.Brush(ThemeService.TextPrimary)
                        : ThemeService.Brush(ThemeService.TextMuted),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
            labelStack.Children.Add(
                new TextBlock
                {
                    Text = desc,
                    FontSize = 11,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextWrapping = TextWrapping.Wrap,
                }
            );
            row.Children.Add(labelStack);

            var stepperGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var minusBtn = UiBuilder.IconButton(
                MaterialIconKind.Minus,
                () => studio.StepKindAllocation(capturedKind, -1),
                13.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            minusBtn.VerticalAlignment = VerticalAlignment.Center;
            stepperGroup.Children.Add(minusBtn);

            var valBox = new TextBox
            {
                Text = rawVal.ToString(),
                Width = 60,
                MinWidth = 60,
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            valBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && int.TryParse(valBox.Text?.Trim(), out int v))
                {
                    e.Handled = true;
                    studio.SetKindAllocation(capturedKind, v, notify: true);
                }
            };
            valBox.LostFocus += (_, _) =>
            {
                if (
                    int.TryParse(valBox.Text?.Trim(), out int v)
                    && studio.KindAllocations.GetValueOrDefault(capturedKind, -1) != v
                )
                {
                    studio.SetKindAllocation(capturedKind, v, notify: true);
                }
            };
            stepperGroup.Children.Add(valBox);

            var plusBtn = UiBuilder.IconButton(
                MaterialIconKind.Plus,
                () => studio.StepKindAllocation(capturedKind, +1),
                13.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            plusBtn.VerticalAlignment = VerticalAlignment.Center;
            stepperGroup.Children.Add(plusBtn);

            Grid.SetColumn(stepperGroup, 1);
            row.Children.Add(stepperGroup);

            var resolvedBadge = UiBuilder.PillBadge(
                studio.AllocationMode == QuestionAllocationMode.Percentages
                    ? $"{rawVal}% -> {exactItems} items"
                    : $"{exactItems} item(s)",
                isEnabled
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted),
                isEnabled
                    ? ThemeService.Brush(ThemeService.AccentSoftBg)
                    : ThemeService.Brush(ThemeService.BgSurface)
            );
            resolvedBadge.HorizontalAlignment = HorizontalAlignment.Right;
            resolvedBadge.VerticalAlignment = VerticalAlignment.Center;

            var badgeSlot = new Border
            {
                MinWidth = 114,
                VerticalAlignment = VerticalAlignment.Center,
                Child = resolvedBadge,
            };
            Grid.SetColumn(badgeSlot, 2);
            row.Children.Add(badgeSlot);

            kindsGrid.Children.Add(
                new Border
                {
                    Background = isEnabled
                        ? ThemeService.Brush(ThemeService.BgElevated)
                        : Brushes.Transparent,
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4),
                    Child = row,
                }
            );
        }

        configCard.Children.Add(
            new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 2),
                Child = kindsGrid,
            }
        );

        // Unified Output Mode Bar (AI API vs Auto-Synced Web JSON vs Offline Smart PDF)
        var outputModeBar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0),
        };
        foreach (
            var (outMode, label, icon) in new[]
            {
                (QuizBuilderOutputMode.AiApi, "AI Engine API", MaterialIconKind.AutoFix),
                (
                    QuizBuilderOutputMode.WebJson,
                    "Gemini Web JSON (Auto-Synced Prompt)",
                    MaterialIconKind.CodeJson
                ),
                (
                    QuizBuilderOutputMode.OfflineSmart,
                    "Instant Offline PDF Analyzer",
                    MaterialIconKind.LightningBoltOutline
                ),
            }
        )
        {
            var capturedOut = outMode;
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                () => studio.SetBuilderOutputMode(capturedOut),
                isActive: studio.BuilderOutputMode == capturedOut
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            outputModeBar.Children.Add(btn);
        }
        configCard.Children.Add(outputModeBar);

        if (studio.BuilderOutputMode == QuizBuilderOutputMode.WebJson)
        {
            if (string.IsNullOrWhiteSpace(studio.GeneratedPromptPreview))
                studio.RefreshLivePromptPreviewIfActive(force: true);
            configCard.Children.Add(BuildManualJsonBridgeCard(studio));
        }
        else
        {
            var generateActions = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 0),
            };
            bool isOfflineMode = studio.BuilderOutputMode == QuizBuilderOutputMode.OfflineSmart;
            var genPrimaryBtn = UiBuilder.PrimaryButton(
                studio.IsGenerating ? "Generating Grounded Assessment..."
                    : isOfflineMode ? $"Generate {studio.QuestionCount}-Item Offline PDF Quiz"
                    : $"Generate {studio.QuestionCount}-Item Grounded Exam",
                isOfflineMode ? MaterialIconKind.LightningBoltOutline : MaterialIconKind.AutoFix,
                async () => await studio.GenerateQuizNowAsync(forceOffline: isOfflineMode)
            );
            genPrimaryBtn.Margin = new Thickness(0, 0, 8, 4);
            generateActions.Children.Add(genPrimaryBtn);

            if (!isOfflineMode)
            {
                var genOfflineBtn = UiBuilder.GhostButton(
                    "Quick Offline Fallback",
                    MaterialIconKind.LightningBoltOutline,
                    async () => await studio.GenerateQuizNowAsync(forceOffline: true)
                );
                genOfflineBtn.Margin = new Thickness(0, 0, 8, 4);
                generateActions.Children.Add(genOfflineBtn);
            }

            configCard.Children.Add(generateActions);
        }

        leftStack.Children.Add(UiBuilder.Card(configCard, padding: new Thickness(12, 10)));

        // Also show quick recent quizzes for this subject right under the builder
        var subjectQuizzes = studio
            .SavedQuizzes.Where(q =>
                studio.SelectedCourse is null
                || string.Equals(
                    q.CourseFolder,
                    studio.SelectedCourse.FolderName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Take(4)
            .ToList();
        if (subjectQuizzes.Count > 0)
        {
            var recentStack = new StackPanel { Spacing = 4 };
            var recentHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            recentHeader.Children.Add(
                UiBuilder.SectionHeader(
                    $"Recent Assessments for {studio.SelectedCourse?.Alias.ToUpperInvariant() ?? "Subject"}"
                )
            );
            var openCatBtn = UiBuilder.GhostButton(
                $"Open Catalog & Combiner ({studio.SavedQuizzes.Count})",
                MaterialIconKind.ArrowRight,
                () => studio.SetSubTab(QuizStudioSubTab.Catalog)
            );
            Grid.SetColumn(openCatBtn, 1);
            recentHeader.Children.Add(openCatBtn);
            recentStack.Children.Add(recentHeader);

            foreach (var qz in subjectQuizzes)
                recentStack.Children.Add(
                    BuildHairlineQuizRow(vm, studio, qz, showCheckbox: false, compact: true)
                );

            leftStack.Children.Add(UiBuilder.Card(recentStack, padding: new Thickness(12, 8)));
        }

        splitGrid.Children.Add(UiBuilder.PersistentScrollViewer("quiz-builder-left", leftStack));

        // RIGHT COLUMN: LIVE VISUAL PDF SCOPE PREVIEWER
        var previewPane = BuildLivePdfPreviewPane(vm, studio);
        Grid.SetColumn(previewPane, 2);
        splitGrid.Children.Add(previewPane);

        return splitGrid;
    }

    private static Control BuildLivePdfPreviewPane(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var grid = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };

        // 1. Top Preview Header Bar
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var leftInfo = new StackPanel { Spacing = 2 };
        leftInfo.Children.Add(
            new TextBlock
            {
                Text = "Live PDF Scope Preview",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            }
        );
        leftInfo.Children.Add(
            new TextBlock
            {
                Text =
                    $"{studio.CurrentPreviewSectionTitle}  ·  PDF Page {studio.CurrentPreviewPdfPage}",
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        headerGrid.Children.Add(leftInfo);

        if (
            !string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            && File.Exists(studio.SelectedPdfPath)
        )
        {
            var openReaderBtn = UiBuilder.GhostButton(
                "Full Reader",
                MaterialIconKind.ArrowExpandAll,
                () =>
                {
                    vm.OpenPdfInReader(studio.SelectedPdfPath);
                    vm.PdfReader.JumpToPage(studio.CurrentPreviewPdfPage);
                }
            );
            Grid.SetColumn(openReaderBtn, 1);
            headerGrid.Children.Add(openReaderBtn);
        }
        grid.Children.Add(headerGrid);

        // 2. Center Visual PDF Page Bitmap Sheet
        var bmp = studio.GetCurrentPreviewBitmap();
        Control pageVisual;
        if (bmp is not null)
        {
            var pageSheet = new Border
            {
                Background = Brushes.White,
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top,
                },
            };

            pageVisual = UiBuilder.PersistentScrollViewer(
                $"quiz-preview:{studio.SelectedPdfPath}:{studio.CurrentPreviewPdfPage}",
                pageSheet
            );
        }
        else if (studio.IsLoadingPreviewPage || studio.IsInspectingPdf)
        {
            pageVisual = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 10,
                Children =
                {
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Width = 160,
                        Height = 4,
                        Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                    },
                    new TextBlock
                    {
                        Text = $"Rendering PDF Page {studio.CurrentPreviewPdfPage}...",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            };
        }
        else
        {
            pageVisual = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    UiBuilder.Icon(
                        MaterialIconKind.FilePdfBox,
                        28,
                        ThemeService.Brush(ThemeService.TextMuted)
                    ),
                    new TextBlock
                    {
                        Text = "No PDF page available in current scope.",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    },
                },
            };
        }

        var canvasBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = pageVisual,
        };
        Grid.SetRow(canvasBorder, 1);
        grid.Children.Add(canvasBorder);

        // 3. Bottom Scope Page Flipper Bar (< Prev in Scope | Page X of Y (PDF p. Z) | Next in Scope >)
        var footerBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var prevBtn = UiBuilder.GhostButton(
            "Prev in Scope",
            MaterialIconKind.ChevronLeft,
            () => studio.PreviousPreviewPage()
        );
        prevBtn.IsEnabled = studio.PreviewScopeIndex > 0;
        footerBar.Children.Add(prevBtn);

        var pageCounter = new TextBlock
        {
            Text =
                studio.ScopePreviewPageCount > 0
                    ? $"Scope Page {studio.PreviewScopeIndex + 1} of {studio.ScopePreviewPageCount}  (PDF p. {studio.CurrentPreviewPdfPage})"
                    : "0 pages in scope",
            FontSize = 11.5,
            FontWeight = FontWeight.Medium,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(pageCounter, 1);
        footerBar.Children.Add(pageCounter);

        var nextBtn = UiBuilder.GhostButton(
            "Next in Scope",
            MaterialIconKind.ChevronRight,
            () => studio.NextPreviewPage()
        );
        nextBtn.IsEnabled = studio.PreviewScopeIndex + 1 < studio.ScopePreviewPageCount;
        Grid.SetColumn(nextBtn, 2);
        footerBar.Children.Add(nextBtn);

        Grid.SetRow(footerBar, 2);
        grid.Children.Add(footerBar);

        return UiBuilder.Card(grid, padding: new Thickness(12, 10));
    }

    // =========================================================================
    // 1B. HAIRLINE QUIZ CATALOG & MULTI-QUIZ COMBINER VIEW
    // =========================================================================

    private static Control BuildCatalogView(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var stack = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 24),
        };

        var filteredQuizzes = (
            studio.CatalogShowAllCourses || studio.SelectedCourse is null
                ? studio.SavedQuizzes
                : studio.SavedQuizzes.Where(q =>
                    string.Equals(
                        q.CourseFolder,
                        studio.SelectedCourse.FolderName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
        )
            .Where(q =>
                string.IsNullOrWhiteSpace(studio.CatalogTermFilter)
                || string.Equals(
                    q.Term,
                    studio.CatalogTermFilter,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        var barGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        barGrid.Children.Add(
            new TextBlock
            {
                Text = studio.CatalogShowAllCourses
                    ? $"All Subjects Catalog ({filteredQuizzes.Count} saved)"
                    : $"{studio.SelectedCourse?.Title ?? "Subject"} Catalog ({filteredQuizzes.Count} saved) — Shuffle in isolation, Organize, or Combine 2+ decks",
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var toggleAllBtn = UiBuilder.GhostButton(
            studio.CatalogShowAllCourses
                ? "Filter: Active Subject"
                : $"Show All Subjects ({studio.SavedQuizzes.Count})",
            MaterialIconKind.FilterVariant,
            () => studio.ToggleCatalogShowAllCourses(),
            isActive: studio.CatalogShowAllCourses
        );
        Grid.SetColumn(toggleAllBtn, 1);
        barGrid.Children.Add(toggleAllBtn);

        var newQuizBtn = UiBuilder.PrimaryButton(
            "New Assessment",
            MaterialIconKind.Plus,
            () => studio.SetSubTab(QuizStudioSubTab.Builder)
        );
        Grid.SetColumn(newQuizBtn, 2);
        barGrid.Children.Add(newQuizBtn);
        stack.Children.Add(barGrid);

        // Term Filter & Isolated Playback Controls Bar
        var filterAndPlaybackGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var termFilterRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var allTermBtn = UiBuilder.GhostButton(
            "All Terms",
            studio.CatalogTermFilter is null ? MaterialIconKind.Check : null,
            () => studio.SetCatalogTermFilter(null),
            isActive: studio.CatalogTermFilter is null
        );
        allTermBtn.Margin = new Thickness(0, 0, 5, 4);
        termFilterRow.Children.Add(allTermBtn);

        foreach (var term in AcademicTerms)
        {
            var capturedTerm = term;
            bool isTermActive = string.Equals(
                studio.CatalogTermFilter,
                term,
                StringComparison.OrdinalIgnoreCase
            );
            var tBtn = UiBuilder.GhostButton(
                term,
                isTermActive ? MaterialIconKind.Check : null,
                () => studio.SetCatalogTermFilter(capturedTerm),
                isActive: isTermActive
            );
            tBtn.Margin = new Thickness(0, 0, 5, 4);
            termFilterRow.Children.Add(tBtn);
        }
        filterAndPlaybackGrid.Children.Add(termFilterRow);

        var playbackToggles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        playbackToggles.Children.Add(
            UiBuilder.GhostButton(
                "Shuffle Questions on Start",
                studio.ShuffleQuestionsOnStart
                    ? MaterialIconKind.CheckboxMarked
                    : MaterialIconKind.CheckboxBlankOutline,
                () => studio.ToggleShuffleQuestionsOnStart(),
                isActive: studio.ShuffleQuestionsOnStart
            )
        );
        playbackToggles.Children.Add(
            UiBuilder.GhostButton(
                "Shuffle MCQ Options",
                studio.ShuffleOptionsOnStart
                    ? MaterialIconKind.CheckboxMarked
                    : MaterialIconKind.CheckboxBlankOutline,
                () => studio.ToggleShuffleOptionsOnStart(),
                isActive: studio.ShuffleOptionsOnStart
            )
        );
        Grid.SetColumn(playbackToggles, 1);
        filterAndPlaybackGrid.Children.Add(playbackToggles);
        stack.Children.Add(filterAndPlaybackGrid);

        // Multi-Quiz Combiner & Organizer Action Bar
        if (studio.SelectedQuizIdsForMerge.Count > 0)
        {
            var mergeGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto"),
                ColumnSpacing = 8,
            };
            var mergeTitleBox = new TextBox
            {
                Watermark = "Composite exam title (e.g. SQA Prelims + Midterms Comprehensive)...",
                FontSize = 12,
            };
            mergeGrid.Children.Add(mergeTitleBox);

            var curateBtn = UiBuilder.GhostButton(
                $"Curate in Organizer ({studio.SelectedQuizIdsForMerge.Count})",
                MaterialIconKind.TuneVariant,
                () => studio.OpenSelectedQuizzesInOrganizer(mergeTitleBox.Text),
                isActive: true
            );
            Grid.SetColumn(curateBtn, 1);
            mergeGrid.Children.Add(curateBtn);

            var mergeShuffleBtn = UiBuilder.PrimaryButton(
                $"Combine & Shuffle ({studio.SelectedQuizIdsForMerge.Count})",
                MaterialIconKind.CallMerge,
                async () =>
                    await studio.CombineSelectedQuizzesAsync(
                        mergeTitleBox.Text,
                        shuffleQuestions: true
                    )
            );
            Grid.SetColumn(mergeShuffleBtn, 2);
            mergeGrid.Children.Add(mergeShuffleBtn);

            var mergeOrderedBtn = UiBuilder.GhostButton(
                "Combine In-Order",
                MaterialIconKind.FormatListNumbered,
                async () =>
                    await studio.CombineSelectedQuizzesAsync(
                        mergeTitleBox.Text,
                        shuffleQuestions: false
                    )
            );
            Grid.SetColumn(mergeOrderedBtn, 3);
            mergeGrid.Children.Add(mergeOrderedBtn);

            stack.Children.Add(UiBuilder.Card(mergeGrid, padding: new Thickness(12, 8)));
        }

        if (filteredQuizzes.Count == 0)
        {
            stack.Children.Add(
                UiBuilder.Card(
                    new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "No assessments saved for this subject yet.",
                                FontSize = 13,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            },
                            new TextBlock
                            {
                                Text =
                                    "Switch to 'Builder & PDF Preview' to generate a grounded exam from your Master PDF, or click 'Show All Subjects'.",
                                FontSize = 12,
                                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            },
                        },
                    }
                )
            );
            return UiBuilder.PersistentScrollViewer("quiz-catalog", stack);
        }

        var listStack = new StackPanel { Spacing = 0 };
        foreach (var quiz in filteredQuizzes)
        {
            listStack.Children.Add(BuildHairlineQuizRow(vm, studio, quiz, showCheckbox: true));
        }
        stack.Children.Add(UiBuilder.Card(listStack, padding: new Thickness(8, 4)));

        return UiBuilder.PersistentScrollViewer("quiz-catalog", stack);
    }

    private static Control BuildHairlineQuizRow(
        MainWindowViewModel vm,
        QuizStudioViewModel studio,
        QuizSessionRecord quiz,
        bool showCheckbox,
        bool compact = false
    )
    {
        bool isChecked = studio.SelectedQuizIdsForMerge.Contains(quiz.Id);

        var rowGrid = new Grid
        {
            ColumnDefinitions = showCheckbox
                ? ColumnDefinitions.Parse("Auto,*,Auto")
                : ColumnDefinitions.Parse("0,*,Auto"),
            ColumnSpacing = 10,
        };

        if (showCheckbox)
        {
            var chk = UiBuilder.IconButton(
                isChecked ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline,
                () => studio.ToggleQuizSelectionForMerge(quiz.Id),
                16,
                isChecked
                    ? ThemeService.Brush(ThemeService.AccentPrimary)
                    : ThemeService.Brush(ThemeService.TextMuted)
            );
            chk.VerticalAlignment = VerticalAlignment.Center;
            rowGrid.Children.Add(chk);
        }

        var centerStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        centerStack.Children.Add(
            new TextBlock
            {
                Text = quiz.Title,
                FontSize = 13.5,
                FontWeight = FontWeight.Medium,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );

        var subParts = new List<string>
        {
            quiz.CourseTitle,
            $"{quiz.Questions.Count} items",
            quiz.ModelUsed,
        };
        if (!string.IsNullOrWhiteSpace(quiz.Term))
            subParts.Insert(1, quiz.Term);
        if (quiz.LastScorePercent.HasValue)
            subParts.Add($"Score: {quiz.LastScorePercent.Value:0.#}% ({quiz.AttemptCount}x)");

        centerStack.Children.Add(
            new TextBlock
            {
                Text = string.Join(" · ", subParts),
                FontSize = 11.5,
                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }
        );
        Grid.SetColumn(centerStack, 1);
        rowGrid.Children.Add(centerStack);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        bool isPdfStale = quiz.IsSourcePdfStale();
        if (isPdfStale && !compact)
        {
            var regroundBtn = UiBuilder.GhostButton(
                "Re-ground PDF",
                MaterialIconKind.Autorenew,
                async () => await studio.RegroundQuizAsync(quiz),
                isActive: true
            );
            ToolTip.SetTip(
                regroundBtn,
                "Source Master PDF was recompiled after this quiz was generated. Click to update page numbers & highlight boxes in place."
            );
            actions.Children.Add(regroundBtn);
        }

        if (quiz.LastScorePercent.HasValue)
        {
            bool high = quiz.LastScorePercent.Value >= 75;
            actions.Children.Add(
                UiBuilder.Pill(
                    $"{quiz.LastScorePercent.Value:0}%",
                    high
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg),
                    high
                        ? ThemeService.Brush(ThemeService.SuccessBg)
                        : ThemeService.Brush(ThemeService.PriorityP2Bg)
                )
            );
        }

        actions.Children.Add(
            UiBuilder.GhostButton(
                "Start",
                MaterialIconKind.PlayOutline,
                () => studio.StartQuizSession(quiz),
                isActive: true
            )
        );

        var shuffleStartBtn = UiBuilder.GhostButton(
            "Shuffle",
            MaterialIconKind.ShuffleVariant,
            () =>
                studio.StartQuizSession(
                    quiz,
                    shuffleQuestionsOverride: true,
                    shuffleOptionsOverride: true
                )
        );
        ToolTip.SetTip(
            shuffleStartBtn,
            "Start this quiz in isolation with both question order and MCQ options shuffled"
        );
        actions.Children.Add(shuffleStartBtn);

        var organizeBtn = UiBuilder.GhostButton(
            "Organize",
            MaterialIconKind.TuneVariant,
            () => studio.OpenQuizInOrganizer(quiz)
        );
        ToolTip.SetTip(
            organizeBtn,
            "Inspect, filter by question kind, reorder, edit, or append questions"
        );
        actions.Children.Add(organizeBtn);

        if (!compact && File.Exists(quiz.SourcePdfPath))
        {
            var pdfBtn = UiBuilder.IconButton(
                MaterialIconKind.FilePdfBox,
                () => vm.OpenPdfInReader(quiz.SourcePdfPath),
                14.5,
                ThemeService.Brush(ThemeService.TextSecondary)
            );
            ToolTip.SetTip(pdfBtn, "Open source PDF in Reader");
            actions.Children.Add(pdfBtn);
        }

        if (!compact)
        {
            actions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.TrashCanOutline,
                    async () => await studio.DeleteQuizAsync(quiz),
                    14.5,
                    ThemeService.Brush(ThemeService.TextMuted)
                )
            );
        }

        Grid.SetColumn(actions, 2);
        rowGrid.Children.Add(actions);

        var rowBorder = new Border
        {
            Background = isChecked
                ? ThemeService.Brush(ThemeService.BgElevated)
                : Brushes.Transparent,
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 7),
            Transitions = UiBuilder.CreateSmoothBrushTransitions(110),
            Child = rowGrid,
        };
        rowBorder.PointerEntered += (_, _) =>
        {
            if (!isChecked)
                rowBorder.Background = ThemeService.Brush(ThemeService.BgHover);
        };
        rowBorder.PointerExited += (_, _) =>
        {
            if (!isChecked)
                rowBorder.Background = Brushes.Transparent;
        };

        return rowBorder;
    }

    // =========================================================================
    // 1C. DECK ORGANIZER & QUESTION CURATION WORKBENCH (SINGLE & MULTI-DECK)
    // =========================================================================

    private static Control BuildDeckOrganizerView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var deck = studio.OrganizerQuiz;
        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 28),
        };

        if (deck is null)
        {
            stack.Children.Add(
                UiBuilder.Card(
                    new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "No quiz deck is currently loaded in the Organizer.",
                                FontSize = 13.5,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                            },
                            new TextBlock
                            {
                                Text =
                                    "Click 'Organize' on any saved quiz in the Catalog, or select 2+ quizzes and click 'Curate in Organizer'.",
                                FontSize = 12,
                                Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            },
                            UiBuilder.GhostButton(
                                "Open Quiz Catalog",
                                MaterialIconKind.LibraryShelves,
                                () => studio.SetSubTab(QuizStudioSubTab.Catalog),
                                isActive: true
                            ),
                        },
                    }
                )
            );
            return UiBuilder.PersistentScrollViewer("quiz-organizer:empty", stack);
        }

        // 1. Deck Metadata & Save / Play / Re-ground Header Card
        var metaCard = new StackPanel { Spacing = 8 };
        var topRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 8,
        };

        var titleBox = new TextBox
        {
            Text = deck.Title,
            Watermark = "Deck Title...",
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
        };
        titleBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !string.IsNullOrWhiteSpace(titleBox.Text))
                deck.Title = titleBox.Text.Trim();
        };
        topRow.Children.Add(titleBox);

        var appendBtn = UiBuilder.GhostButton(
            "+ Generate & Append",
            MaterialIconKind.PlusCircleOutline,
            () => studio.StartAppendToQuiz(deck)
        );
        Grid.SetColumn(appendBtn, 1);
        topRow.Children.Add(appendBtn);

        if (File.Exists(deck.SourcePdfPath))
        {
            var regroundBtn = UiBuilder.GhostButton(
                deck.IsSourcePdfStale() ? "Re-ground Stale PDF" : "Re-ground PDF",
                MaterialIconKind.Autorenew,
                async () => await studio.RegroundQuizAsync(deck),
                isActive: deck.IsSourcePdfStale()
            );
            Grid.SetColumn(regroundBtn, 2);
            topRow.Children.Add(regroundBtn);
        }

        var shufflePlayBtn = UiBuilder.GhostButton(
            "Shuffle & Play",
            MaterialIconKind.ShuffleVariant,
            () =>
                studio.StartQuizSession(
                    deck,
                    shuffleQuestionsOverride: true,
                    shuffleOptionsOverride: true
                )
        );
        Grid.SetColumn(shufflePlayBtn, 3);
        topRow.Children.Add(shufflePlayBtn);

        var saveBtn = UiBuilder.PrimaryButton(
            $"Save Deck ({deck.Questions.Count})",
            MaterialIconKind.ContentSaveOutline,
            async () => await studio.SaveOrganizerDeckAsync()
        );
        Grid.SetColumn(saveBtn, 4);
        topRow.Children.Add(saveBtn);
        metaCard.Children.Add(topRow);

        // Term Assignment + Bulk Reorder Actions
        var subBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var termPills = new WrapPanel { Orientation = Orientation.Horizontal };
        termPills.Children.Add(
            new TextBlock
            {
                Text = "Term:",
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 4),
            }
        );
        foreach (var term in AcademicTerms)
        {
            var capturedTerm = term;
            bool isCurrTerm = string.Equals(deck.Term, term, StringComparison.OrdinalIgnoreCase);
            var tBtn = UiBuilder.GhostButton(
                term,
                isCurrTerm ? MaterialIconKind.Check : null,
                () =>
                {
                    deck.Term = isCurrTerm ? null : capturedTerm;
                    vm.RequestUiRefresh();
                },
                isActive: isCurrTerm
            );
            tBtn.Margin = new Thickness(0, 0, 5, 4);
            termPills.Children.Add(tBtn);
        }
        subBar.Children.Add(termPills);

        var bulkRow = new WrapPanel { Orientation = Orientation.Horizontal };
        bulkRow.Children.Add(
            UiBuilder.GhostButton(
                studio.OrganizerKindFilter.HasValue
                    ? $"Shuffle {studio.OrganizerKindFilter.Value} Only"
                    : "Shuffle All Order",
                MaterialIconKind.ShuffleVariant,
                () => studio.ShuffleOrganizerQuestions(studio.OrganizerKindFilter)
            )
        );
        bulkRow.Children.Add(
            UiBuilder.GhostButton(
                "Group by Kind",
                MaterialIconKind.FormatListGroup,
                () => studio.SortOrganizerQuestionsByKind()
            )
        );
        bulkRow.Children.Add(
            UiBuilder.GhostButton(
                "Sort by PDF Page",
                MaterialIconKind.SortNumericAscending,
                () => studio.SortOrganizerQuestionsByPage()
            )
        );
        Grid.SetColumn(bulkRow, 1);
        subBar.Children.Add(bulkRow);
        metaCard.Children.Add(subBar);

        // Question-Kind Filter & Add Custom Question Bar
        var kindFilterBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };
        var kindFilterWrap = new WrapPanel { Orientation = Orientation.Horizontal };

        var allKindBtn = UiBuilder.GhostButton(
            $"All Kinds ({deck.Questions.Count})",
            studio.OrganizerKindFilter is null ? MaterialIconKind.Check : null,
            () => studio.SetOrganizerKindFilter(null),
            isActive: studio.OrganizerKindFilter is null
        );
        allKindBtn.Margin = new Thickness(0, 0, 5, 4);
        kindFilterWrap.Children.Add(allKindBtn);

        foreach (var kind in Enum.GetValues<QuizQuestionKind>())
        {
            var capturedKind = kind;
            int cnt = deck.Questions.Count(q => q.Kind == kind);
            bool isFilterActive = studio.OrganizerKindFilter == kind;
            var kBtn = UiBuilder.GhostButton(
                $"{kind} ({cnt})",
                isFilterActive ? MaterialIconKind.Check : null,
                () => studio.SetOrganizerKindFilter(capturedKind),
                isActive: isFilterActive
            );
            kBtn.Margin = new Thickness(0, 0, 5, 4);
            kindFilterWrap.Children.Add(kBtn);
        }
        kindFilterBar.Children.Add(kindFilterWrap);

        var addKindTarget = studio.OrganizerKindFilter ?? QuizQuestionKind.MultipleChoice;
        var addCustomBtn = UiBuilder.GhostButton(
            $"+ Add {addKindTarget}",
            MaterialIconKind.Plus,
            () => studio.AddBlankOrganizerQuestion(addKindTarget)
        );
        Grid.SetColumn(addCustomBtn, 1);
        kindFilterBar.Children.Add(addCustomBtn);
        metaCard.Children.Add(kindFilterBar);

        stack.Children.Add(UiBuilder.Card(metaCard, padding: new Thickness(12, 10)));

        // 2. Filtered Question List with Reorder & Inline Kind-Aware Editor
        var visibleQuestions = studio.OrganizerKindFilter.HasValue
            ? deck.Questions.Where(q => q.Kind == studio.OrganizerKindFilter.Value).ToList()
            : deck.Questions;

        var qListStack = new StackPanel { Spacing = 8 };
        foreach (var q in visibleQuestions)
        {
            var capturedQ = q;
            int deckIndex = deck.Questions.IndexOf(q);
            bool isEditing = string.Equals(
                studio.EditingQuestionId,
                q.Id,
                StringComparison.OrdinalIgnoreCase
            );

            var cardStack = new StackPanel { Spacing = 10 };

            // Tier 1: Top Meta Strip (Left Badges + Right Action Buttons)
            var headerGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 12,
            };

            var badges = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            void AddMetaBadge(Control pill)
            {
                pill.Margin = new Thickness(0, 0, 6, 4);
                badges.Children.Add(pill);
            }

            AddMetaBadge(
                UiBuilder.PillBadge(
                    $"#{deckIndex + 1}",
                    ThemeService.Brush(ThemeService.TextPrimary),
                    ThemeService.Brush(ThemeService.BgElevated)
                )
            );
            AddMetaBadge(
                UiBuilder.PillBadge(
                    q.Kind.ToString(),
                    ThemeService.Brush(ThemeService.AccentPrimary),
                    ThemeService.Brush(ThemeService.AccentSoftBg)
                )
            );
            AddMetaBadge(
                UiBuilder.PillBadge(
                    $"Cognitive: {q.CognitiveLevel}",
                    ThemeService.Brush(ThemeService.TextSecondary),
                    ThemeService.Brush(ThemeService.BgElevated)
                )
            );
            AddMetaBadge(
                UiBuilder.PillBadge(
                    $"Page {q.SourcePage}",
                    ThemeService.Brush(ThemeService.PriorityP3Fg),
                    ThemeService.Brush(ThemeService.PriorityP3Bg)
                )
            );
            if (!string.IsNullOrWhiteSpace(q.TopicTag))
            {
                AddMetaBadge(
                    UiBuilder.PillBadge(
                        $"@{q.TopicTag}",
                        ThemeService.Brush(ThemeService.TextMuted),
                        ThemeService.Brush(ThemeService.BgBase)
                    )
                );
            }
            headerGrid.Children.Add(badges);

            var rowActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rowActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.ArrowUp,
                    () => studio.MoveOrganizerQuestion(capturedQ, -1),
                    14,
                    ThemeService.Brush(ThemeService.TextSecondary)
                )
            );
            rowActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.ArrowDown,
                    () => studio.MoveOrganizerQuestion(capturedQ, +1),
                    14,
                    ThemeService.Brush(ThemeService.TextSecondary)
                )
            );
            if (File.Exists(q.SourcePdfPath))
            {
                var pdfJumpBtn = UiBuilder.GhostButton(
                    $"p.{q.SourcePage}",
                    MaterialIconKind.Marker,
                    () => studio.OpenQuestionEvidenceInPdfReader(capturedQ)
                );
                rowActions.Children.Add(pdfJumpBtn);
            }
            rowActions.Children.Add(
                UiBuilder.GhostButton(
                    isEditing ? "Done" : "Edit",
                    MaterialIconKind.PencilOutline,
                    () => studio.ToggleEditQuestion(capturedQ.Id),
                    isActive: isEditing
                )
            );
            rowActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.TrashCanOutline,
                    () => studio.DeleteOrganizerQuestion(capturedQ),
                    14,
                    ThemeService.Brush(ThemeService.PriorityP1Fg)
                )
            );

            Grid.SetColumn(rowActions, 1);
            headerGrid.Children.Add(rowActions);
            cardStack.Children.Add(headerGrid);

            // Tier 2: Full-Width Question Prompt (never squeezed or cut off)
            cardStack.Children.Add(
                new TextBlock
                {
                    Text = q.Prompt,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                }
            );

            // Tier 3: Relative Kind-Aware Preview OR Inline Editor
            if (!isEditing)
            {
                if (
                    q.Kind
                        is QuizQuestionKind.MultipleChoice
                            or QuizQuestionKind.Situational
                            or QuizQuestionKind.TrueFalse
                    && q.Options.Count > 0
                )
                {
                    int colCount = q.Options.Count <= 2 ? 2 : 2;
                    int rowCount = (int)Math.Ceiling(q.Options.Count / (double)colCount);
                    var optGrid = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
                        ColumnSpacing = 8,
                        RowSpacing = 6,
                    };
                    for (int r = 0; r < rowCount; r++)
                        optGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    for (int oi = 0; oi < q.Options.Count; oi++)
                    {
                        var optText = q.Options[oi];
                        bool isCorrectOpt = q.EvaluateAnswer(optText);
                        var optCellGrid = new Grid
                        {
                            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                            ColumnSpacing = 8,
                        };
                        optCellGrid.Children.Add(
                            UiBuilder.Icon(
                                isCorrectOpt
                                    ? MaterialIconKind.CheckCircle
                                    : MaterialIconKind.CircleOutline,
                                13.5,
                                isCorrectOpt
                                    ? ThemeService.Brush(ThemeService.SuccessFg)
                                    : ThemeService.Brush(ThemeService.TextMuted)
                            )
                        );
                        optCellGrid.Children.Add(
                            new TextBlock
                            {
                                Text = optText,
                                FontSize = 12,
                                FontWeight = isCorrectOpt ? FontWeight.SemiBold : FontWeight.Normal,
                                Foreground = isCorrectOpt
                                    ? ThemeService.Brush(ThemeService.SuccessFg)
                                    : ThemeService.Brush(ThemeService.TextSecondary),
                                TextWrapping = TextWrapping.Wrap,
                                VerticalAlignment = VerticalAlignment.Center,
                                [Grid.ColumnProperty] = 1,
                            }
                        );

                        var optBorder = new Border
                        {
                            Background = isCorrectOpt
                                ? ThemeService.Brush(ThemeService.SuccessBg)
                                : ThemeService.Brush(ThemeService.BgBase),
                            BorderBrush = isCorrectOpt
                                ? ThemeService.Brush(ThemeService.SuccessFg)
                                : ThemeService.Brush(ThemeService.BorderSubtle),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(10, 6),
                            Child = optCellGrid,
                        };
                        Grid.SetRow(optBorder, oi / colCount);
                        Grid.SetColumn(optBorder, oi % colCount);
                        optGrid.Children.Add(optBorder);
                    }
                    cardStack.Children.Add(optGrid);
                }
                else if (q.Kind == QuizQuestionKind.Ordering && q.Options.Count > 0)
                {
                    var seqWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                    for (int si = 0; si < q.Options.Count; si++)
                    {
                        var pill = UiBuilder.PillBadge(
                            $"{si + 1}. {q.Options[si]}",
                            ThemeService.Brush(ThemeService.AccentPrimary),
                            ThemeService.Brush(ThemeService.AccentSoftBg)
                        );
                        pill.Margin = new Thickness(0, 0, 6, 4);
                        seqWrap.Children.Add(pill);
                    }
                    cardStack.Children.Add(seqWrap);
                }
                else
                {
                    var ansWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                    var ansBadge = UiBuilder.PillBadge(
                        $"Answer: {q.CorrectAnswer}",
                        ThemeService.Brush(ThemeService.SuccessFg),
                        ThemeService.Brush(ThemeService.SuccessBg)
                    );
                    ansBadge.Margin = new Thickness(0, 0, 6, 4);
                    ansWrap.Children.Add(ansBadge);

                    foreach (var al in q.AcceptedAliases)
                    {
                        var aliasPill = UiBuilder.PillBadge(
                            $"Alias: {al}",
                            ThemeService.Brush(ThemeService.TextSecondary),
                            ThemeService.Brush(ThemeService.BgElevated)
                        );
                        aliasPill.Margin = new Thickness(0, 0, 6, 4);
                        ansWrap.Children.Add(aliasPill);
                    }
                    cardStack.Children.Add(ansWrap);
                }
            }
            else
            {
                // Kind-aware inline editor
                var editStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

                var kindSwitchWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var k in Enum.GetValues<QuizQuestionKind>())
                {
                    var capturedK = k;
                    var kBtn = UiBuilder.GhostButton(
                        k.ToString(),
                        capturedQ.Kind == k ? MaterialIconKind.Check : null,
                        () =>
                        {
                            capturedQ.Kind = capturedK;
                            if (
                                capturedK == QuizQuestionKind.TrueFalse
                                && capturedQ.Options.Count == 0
                            )
                                capturedQ.Options = ["True", "False"];
                            vm.RequestUiRefresh();
                        },
                        isActive: capturedQ.Kind == k
                    );
                    kBtn.Margin = new Thickness(0, 0, 5, 4);
                    kindSwitchWrap.Children.Add(kBtn);
                }
                editStack.Children.Add(kindSwitchWrap);

                var promptBox = new TextBox
                {
                    Text = capturedQ.Prompt,
                    Watermark =
                        capturedQ.Kind == QuizQuestionKind.FillInTheBlank
                            ? "Question prompt (use _____ for the blank)..."
                            : "Question prompt...",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 52,
                    FontSize = 12.5,
                };
                promptBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        capturedQ.Prompt = promptBox.Text ?? string.Empty;
                };
                editStack.Children.Add(promptBox);

                if (
                    capturedQ.Kind
                    is QuizQuestionKind.MultipleChoice
                        or QuizQuestionKind.Situational
                        or QuizQuestionKind.Ordering
                        or QuizQuestionKind.TrueFalse
                )
                {
                    editStack.Children.Add(
                        new TextBlock
                        {
                            Text =
                                capturedQ.Kind == QuizQuestionKind.Ordering
                                    ? "Steps in CORRECT chronological order (one step per line — shuffled automatically during quiz):"
                                    : "Options (one option per line):",
                            FontSize = 11.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        }
                    );

                    var optsBox = new TextBox
                    {
                        Text = string.Join("\n", capturedQ.Options),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        MinHeight = 72,
                        FontSize = 12,
                    };
                    optsBox.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == TextBox.TextProperty)
                        {
                            capturedQ.Options = (optsBox.Text ?? string.Empty)
                                .Split(
                                    '\n',
                                    StringSplitOptions.RemoveEmptyEntries
                                        | StringSplitOptions.TrimEntries
                                )
                                .ToList();
                            if (
                                capturedQ.Kind == QuizQuestionKind.Ordering
                                && capturedQ.Options.Count >= 2
                            )
                                capturedQ.CorrectAnswer = string.Join(" -> ", capturedQ.Options);
                        }
                    };
                    editStack.Children.Add(optsBox);
                }

                var ansGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
                    ColumnSpacing = 8,
                };
                var correctBox = new TextBox
                {
                    Text = capturedQ.CorrectAnswer,
                    Watermark = "Correct Answer...",
                    FontSize = 12,
                };
                correctBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        capturedQ.CorrectAnswer = (correctBox.Text ?? string.Empty).Trim();
                };
                ansGrid.Children.Add(correctBox);

                var aliasBox = new TextBox
                {
                    Text = string.Join(", ", capturedQ.AcceptedAliases),
                    Watermark = "Accepted synonyms/aliases (comma-separated)...",
                    FontSize = 12,
                };
                aliasBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                    {
                        capturedQ.AcceptedAliases = (aliasBox.Text ?? string.Empty)
                            .Split(
                                ',',
                                StringSplitOptions.RemoveEmptyEntries
                                    | StringSplitOptions.TrimEntries
                            )
                            .ToList();
                    }
                };
                Grid.SetColumn(aliasBox, 1);
                ansGrid.Children.Add(aliasBox);
                editStack.Children.Add(ansGrid);

                var expBox = new TextBox
                {
                    Text = capturedQ.Explanation,
                    Watermark = "Explanation / rationale...",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                };
                expBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        capturedQ.Explanation = (expBox.Text ?? string.Empty).Trim();
                };
                editStack.Children.Add(expBox);

                cardStack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.BgBase),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 8),
                        Child = editStack,
                    }
                );
            }

            qListStack.Children.Add(UiBuilder.Card(cardStack, padding: new Thickness(12, 10)));
        }

        stack.Children.Add(qListStack);
        return UiBuilder.PersistentScrollViewer($"quiz-organizer:{deck.Id}", stack);
    }

    private static Control BuildAiSettingsCard(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var settings = vm.Settings;
        var cardStack = new StackPanel { Spacing = 8 };

        cardStack.Children.Add(
            UiBuilder.SectionHeader("Unified OpenAI-Compatible AI Engine Configuration")
        );

        var presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddProviderPreset(string id, string label, MaterialIconKind icon)
        {
            bool active = string.Equals(
                settings.AiProviderPreset,
                id,
                StringComparison.OrdinalIgnoreCase
            );
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                async () => await studio.ApplyAiProviderPresetAsync(id),
                isActive: active
            );
            btn.Margin = new Thickness(0, 0, 6, 4);
            presetRow.Children.Add(btn);
        }

        AddProviderPreset(
            "GeminiAiStudio",
            "Google AI Studio (Gemini)",
            MaterialIconKind.CreationOutline
        );
        AddProviderPreset(
            "OllamaLocal",
            "Local Ollama (localhost:11434)",
            MaterialIconKind.ServerNetwork
        );
        AddProviderPreset(
            "OfflineSmart",
            "Offline Smart PDF Analyzer",
            MaterialIconKind.FileSearchOutline
        );
        cardStack.Children.Add(presetRow);

        var fieldsGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("2*,1.2*,1.5*,Auto"),
            ColumnSpacing = 8,
        };

        var urlBox = new TextBox
        {
            Text = settings.AiBaseUrl,
            Watermark = "OpenAI-compatible endpoint URL",
            FontSize = 12,
        };
        fieldsGrid.Children.Add(urlBox);

        var modelBox = new TextBox
        {
            Text = settings.AiModelName,
            Watermark = "Model (e.g. gemini-2.5-flash)",
            FontSize = 12,
        };
        Grid.SetColumn(modelBox, 1);
        fieldsGrid.Children.Add(modelBox);

        var keyBox = new TextBox
        {
            Text = settings.AiApiKey ?? string.Empty,
            Watermark = "API Key (or GEMINI_API_KEY env)",
            PasswordChar = '•',
            FontSize = 12,
        };
        Grid.SetColumn(keyBox, 2);
        fieldsGrid.Children.Add(keyBox);

        var saveBtn = UiBuilder.PrimaryButton(
            "Save",
            MaterialIconKind.ContentSaveOutline,
            async () =>
                await studio.SaveAiCredentialsAsync(
                    urlBox.Text ?? "",
                    modelBox.Text ?? "",
                    keyBox.Text
                )
        );
        Grid.SetColumn(saveBtn, 3);
        fieldsGrid.Children.Add(saveBtn);

        cardStack.Children.Add(fieldsGrid);
        return UiBuilder.Card(cardStack, padding: new Thickness(12, 10));
    }

    private static Control BuildManualJsonBridgeCard(QuizStudioViewModel studio)
    {
        var cardStack = new StackPanel { Spacing = 8 };
        cardStack.Children.Add(
            UiBuilder.SectionHeader("Gemini Student Pro Web Bridge (Auto-Synced Grounded Prompt)")
        );

        var split = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,12,*"),
            RowDefinitions = RowDefinitions.Parse("*,Auto"),
            RowSpacing = 8,
        };

        var promptBox = new TextBox
        {
            Text = studio.IsBuildingPrompt
                ? "Building grounded prompt from selected PDF pages..."
                : studio.GeneratedPromptPreview,
            IsReadOnly = true,
            AcceptsReturn = true,
            MinHeight = 125,
            MaxHeight = 220,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        split.Children.Add(promptBox);

        var copyBtn = UiBuilder.GhostButton(
            "Copy Grounded Prompt",
            MaterialIconKind.ContentCopy,
            async () =>
            {
                var top = TopLevel.GetTopLevel(promptBox);
                if (top?.Clipboard is not null)
                    await top.Clipboard.SetTextAsync(studio.GeneratedPromptPreview);
            }
        );
        Grid.SetRow(copyBtn, 1);
        Grid.SetColumn(copyBtn, 0);
        split.Children.Add(copyBtn);

        var jsonInputBox = new TextBox
        {
            Watermark = "Paste Gemini's JSON response here...",
            AcceptsReturn = true,
            MinHeight = 125,
            MaxHeight = 220,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(jsonInputBox, 2);
        split.Children.Add(jsonInputBox);

        var importBtn = UiBuilder.PrimaryButton(
            studio.IsGenerating ? "Importing & Grounding..." : "Import JSON & Ground to PDF",
            MaterialIconKind.CheckDecagramOutline,
            async () => await studio.ImportPastedJsonQuizAsync(jsonInputBox.Text ?? string.Empty)
        );
        Grid.SetRow(importBtn, 1);
        Grid.SetColumn(importBtn, 2);
        split.Children.Add(importBtn);

        cardStack.Children.Add(split);
        return UiBuilder.Card(cardStack, padding: new Thickness(12, 10));
    }

    // =========================================================================
    // 2. INTERACTIVE QUESTION RUNNER & GROUNDED PDF EVIDENCE VIEW
    // =========================================================================

    private static Control BuildActiveQuestionRunnerView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var session = studio.ActiveSession!;
        var q = studio.CurrentQuestion;
        if (q is null)
            return BuildStudioWorkspaceView(vm, studio);

        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(12, 8, 12, 28),
        };

        var topBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 12,
        };
        topBar.Children.Add(
            UiBuilder.GhostButton(
                "Exit to Studio",
                MaterialIconKind.ArrowLeft,
                () => studio.ExitActiveSession()
            )
        );

        var centerTitle = new TextBlock
        {
            Text =
                $"{session.Title}  ·  Question {studio.CurrentQuestionIndex + 1} of {session.Questions.Count}",
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(centerTitle, 1);
        topBar.Children.Add(centerTitle);

        var metaBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        metaBadges.Children.Add(
            UiBuilder.PillBadge(
                q.Kind.ToString(),
                ThemeService.Brush(ThemeService.AccentPrimary),
                ThemeService.Brush(ThemeService.AccentSoftBg)
            )
        );
        metaBadges.Children.Add(
            UiBuilder.PillBadge(
                $"Cognitive: {q.CognitiveLevel}",
                ThemeService.Brush(ThemeService.PriorityP3Fg),
                ThemeService.Brush(ThemeService.PriorityP3Bg)
            )
        );
        metaBadges.Children.Add(
            UiBuilder.PillBadge(
                $"@{q.TopicTag}",
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgElevated)
            )
        );

        Grid.SetColumn(metaBadges, 2);
        topBar.Children.Add(metaBadges);
        stack.Children.Add(topBar);

        // Progress Bar + Interactive Question Navigator Strip (1..N)
        var progressCardStack = new StackPanel { Spacing = 8 };
        int totalQ = Math.Max(1, session.Questions.Count);
        int answeredQ = session.Questions.Count(qq => studio.SessionResults.ContainsKey(qq.Id));

        progressCardStack.Children.Add(
            new ProgressBar
            {
                Minimum = 0,
                Maximum = totalQ,
                Value = answeredQ,
                Height = 4,
                Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                Background = ThemeService.Brush(ThemeService.BgElevated),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }
        );

        var qNavWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int qi = 0; qi < session.Questions.Count; qi++)
        {
            int capturedIndex = qi;
            var itemQ = session.Questions[qi];
            bool isCurrent = qi == studio.CurrentQuestionIndex;
            bool hasRes = studio.SessionResults.TryGetValue(itemQ.Id, out var qRes);

            var navBtn = UiBuilder.GhostButton(
                $"{qi + 1}",
                hasRes
                    ? (qRes.IsCorrect ? MaterialIconKind.Check : MaterialIconKind.Close)
                    : null,
                () => studio.JumpToQuestion(capturedIndex),
                isActive: isCurrent || hasRes
            );
            navBtn.Margin = new Thickness(0, 0, 4, 4);
            qNavWrap.Children.Add(navBtn);
        }
        progressCardStack.Children.Add(qNavWrap);
        stack.Children.Add(UiBuilder.Card(progressCardStack, padding: new Thickness(12, 8)));

        var qCardStack = new StackPanel { Spacing = 14 };

        if (!string.IsNullOrWhiteSpace(q.SectionTitle))
        {
            qCardStack.Children.Add(
                new TextBlock
                {
                    Text = $"Source Section: {q.SectionTitle} (Page {q.SourcePage})",
                    FontSize = 11.5,
                    FontWeight = FontWeight.Medium,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                }
            );
        }

        qCardStack.Children.Add(
            new TextBlock
            {
                Text = q.Prompt,
                FontSize = 15.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 23,
            }
        );

        if (
            q.Kind
                is QuizQuestionKind.MultipleChoice
                    or QuizQuestionKind.Situational
                    or QuizQuestionKind.TrueFalse
            && q.Options.Count > 0
        )
        {
            var optsStack = new StackPanel { Spacing = 8 };
            for (int i = 0; i < q.Options.Count; i++)
            {
                var optText = q.Options[i];
                var letter = (char)('A' + i);
                bool isChosen = string.Equals(
                    studio.CurrentAnswerDraft,
                    optText,
                    StringComparison.OrdinalIgnoreCase
                );
                bool isThisCorrect = q.EvaluateAnswer(optText);

                IBrush borderBrush = ThemeService.Brush(ThemeService.BorderSubtle);
                IBrush bgBrush = ThemeService.Brush(ThemeService.BgElevated);
                IBrush fgBrush = ThemeService.Brush(ThemeService.TextPrimary);

                if (studio.IsAnswerRevealed)
                {
                    if (isThisCorrect)
                    {
                        borderBrush = ThemeService.Brush(ThemeService.SuccessFg);
                        bgBrush = ThemeService.Brush(ThemeService.SuccessBg);
                        fgBrush = ThemeService.Brush(ThemeService.SuccessFg);
                    }
                    else if (isChosen)
                    {
                        borderBrush = ThemeService.Brush(ThemeService.PriorityP1Fg);
                        bgBrush = ThemeService.Brush(ThemeService.PriorityP1Bg);
                        fgBrush = ThemeService.Brush(ThemeService.PriorityP1Fg);
                    }
                }
                else if (isChosen)
                {
                    borderBrush = ThemeService.Brush(ThemeService.AccentPrimary);
                    bgBrush = ThemeService.Brush(ThemeService.AccentSoftBg);
                }

                var optContentGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
                    ColumnSpacing = 12,
                };
                optContentGrid.Children.Add(
                    new Border
                    {
                        Width = 24,
                        Height = 24,
                        CornerRadius = new CornerRadius(12),
                        Background = ThemeService.Brush(ThemeService.BgSurface),
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = letter.ToString(),
                            FontSize = 11.5,
                            FontWeight = FontWeight.Bold,
                            Foreground = fgBrush,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    }
                );
                optContentGrid.Children.Add(
                    new TextBlock
                    {
                        Text = optText,
                        FontSize = 13.5,
                        Foreground = fgBrush,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    }
                );

                var optBtn = new Button
                {
                    Content = optContentGrid,
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 11),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };

                var capturedOpt = optText;
                optBtn.Click += async (_, _) =>
                {
                    if (!studio.IsAnswerRevealed)
                        await studio.SubmitAnswerAsync(capturedOpt);
                };
                optsStack.Children.Add(optBtn);
            }
            qCardStack.Children.Add(optsStack);
        }
        else if (q.Kind == QuizQuestionKind.Ordering)
        {
            var steps = studio.GetOrderingItemsForQuestion(q);
            var orderStack = new StackPanel { Spacing = 8 };
            orderStack.Children.Add(
                new TextBlock
                {
                    Text =
                        "Use the Up / Down arrows to arrange these steps into the correct sequence, then click Check Sequence:",
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                }
            );

            for (int i = 0; i < steps.Count; i++)
            {
                int capturedIdx = i;
                var stepText = steps[i];
                bool isStepInRightSpot =
                    studio.IsAnswerRevealed
                    && i < q.Options.Count
                    && string.Equals(
                        stepText.Trim(),
                        q.Options[i].Trim(),
                        StringComparison.OrdinalIgnoreCase
                    );

                IBrush borderBrush = studio.IsAnswerRevealed
                    ? (
                        isStepInRightSpot
                            ? ThemeService.Brush(ThemeService.SuccessFg)
                            : ThemeService.Brush(ThemeService.PriorityP1Fg)
                    )
                    : ThemeService.Brush(ThemeService.BorderSubtle);
                IBrush bgBrush = studio.IsAnswerRevealed
                    ? (
                        isStepInRightSpot
                            ? ThemeService.Brush(ThemeService.SuccessBg)
                            : ThemeService.Brush(ThemeService.PriorityP1Bg)
                    )
                    : ThemeService.Brush(ThemeService.BgElevated);

                var stepGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
                    ColumnSpacing = 10,
                };
                stepGrid.Children.Add(
                    UiBuilder.PillBadge(
                        $"Step {i + 1}",
                        ThemeService.Brush(ThemeService.AccentPrimary),
                        ThemeService.Brush(ThemeService.AccentSoftBg)
                    )
                );

                stepGrid.Children.Add(
                    new TextBlock
                    {
                        Text = stepText,
                        FontSize = 13.5,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        [Grid.ColumnProperty] = 1,
                    }
                );

                var moveBtns = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                moveBtns.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.ArrowUp,
                        () => studio.MoveOrderingStep(q, capturedIdx, -1),
                        14.5,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    )
                );
                moveBtns.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.ArrowDown,
                        () => studio.MoveOrderingStep(q, capturedIdx, +1),
                        14.5,
                        ThemeService.Brush(ThemeService.TextSecondary)
                    )
                );
                Grid.SetColumn(moveBtns, 2);
                stepGrid.Children.Add(moveBtns);

                orderStack.Children.Add(
                    new Border
                    {
                        Background = bgBrush,
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(12, 9),
                        Child = stepGrid,
                    }
                );
            }

            if (!studio.IsAnswerRevealed)
            {
                orderStack.Children.Add(
                    UiBuilder.PrimaryButton(
                        "Check Sequence",
                        MaterialIconKind.Check,
                        async () =>
                        {
                            var currSeq = string.Join(
                                " -> ",
                                studio.GetOrderingItemsForQuestion(q)
                            );
                            await studio.SubmitAnswerAsync(currSeq);
                        }
                    )
                );
            }

            qCardStack.Children.Add(orderStack);
        }
        else
        {
            if (q.Kind == QuizQuestionKind.FillInTheBlank)
            {
                qCardStack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.AccentSoftBg),
                        BorderBrush = ThemeService.Brush(ThemeService.AccentPrimary),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 6),
                        Child = new TextBlock
                        {
                            Text =
                                "Fill in the missing term or phrase for the blank ( _____ ) above:",
                            FontSize = 12,
                            FontWeight = FontWeight.Medium,
                            Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
                        },
                    }
                );
            }

            var idRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                ColumnSpacing = 8,
            };
            var ansBox = new TextBox
            {
                Text = studio.CurrentAnswerDraft,
                Watermark =
                    q.Kind == QuizQuestionKind.FillInTheBlank
                        ? "Type the missing word/phrase for _____ and press Enter..."
                        : "Type your answer and press Enter...",
                FontSize = 14,
                IsEnabled = !studio.IsAnswerRevealed,
            };
            ansBox.KeyDown += async (_, e) =>
            {
                if (
                    e.Key == Key.Enter
                    && !studio.IsAnswerRevealed
                    && !string.IsNullOrWhiteSpace(ansBox.Text)
                )
                {
                    e.Handled = true;
                    await studio.SubmitAnswerAsync(ansBox.Text);
                }
            };
            idRow.Children.Add(ansBox);

            var submitBtn = UiBuilder.PrimaryButton(
                "Check Answer",
                MaterialIconKind.Check,
                async () =>
                {
                    if (!studio.IsAnswerRevealed && !string.IsNullOrWhiteSpace(ansBox.Text))
                        await studio.SubmitAnswerAsync(ansBox.Text);
                }
            );
            Grid.SetColumn(submitBtn, 1);
            idRow.Children.Add(submitBtn);
            qCardStack.Children.Add(idRow);
        }

        stack.Children.Add(UiBuilder.Card(qCardStack, padding: new Thickness(18, 16)));

        if (studio.IsAnswerRevealed)
        {
            var feedbackStack = new StackPanel { Spacing = 10 };

            var verdictRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            verdictRow.Children.Add(
                UiBuilder.Icon(
                    studio.IsCurrentAnswerCorrect
                        ? MaterialIconKind.CheckCircle
                        : MaterialIconKind.CloseCircle,
                    18,
                    studio.IsCurrentAnswerCorrect
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.PriorityP1Fg)
                )
            );
            verdictRow.Children.Add(
                new TextBlock
                {
                    Text = studio.IsCurrentAnswerCorrect
                        ? "Correct!"
                        : $"Incorrect  ·  Correct Answer: {q.CorrectAnswer}",
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Foreground = studio.IsCurrentAnswerCorrect
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.PriorityP1Fg),
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            feedbackStack.Children.Add(verdictRow);

            if (!string.IsNullOrWhiteSpace(q.Explanation))
            {
                feedbackStack.Children.Add(
                    new TextBlock
                    {
                        Text = q.Explanation,
                        FontSize = 13,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
            }

            var evidenceBox = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.PriorityP2Fg),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text =
                                $"Master PDF Evidence  ·  {q.SectionTitle}  ·  Page {q.SourcePage}",
                            FontSize = 11.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg),
                        },
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(q.VerbatimQuote)
                                ? "(Grounded to Page " + q.SourcePage + ")"
                                : $"\"{q.VerbatimQuote}\"",
                            FontSize = 12.5,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        UiBuilder.GhostButton(
                            $"Jump to Highlighted Statement on Page {q.SourcePage} in PDF Reader",
                            MaterialIconKind.Marker,
                            () => studio.OpenQuestionEvidenceInPdfReader(q),
                            isActive: true
                        ),
                    },
                },
            };
            feedbackStack.Children.Add(evidenceBox);

            var srsRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            var prevBtn = UiBuilder.GhostButton(
                "Previous",
                MaterialIconKind.ChevronLeft,
                () => studio.PreviousQuestion()
            );
            srsRow.Children.Add(prevBtn);

            var rateGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            rateGroup.Children.Add(
                UiBuilder.GhostButton(
                    "Again (1d)",
                    MaterialIconKind.Refresh,
                    async () => await studio.RateAndAdvanceAsync(1)
                )
            );
            rateGroup.Children.Add(
                UiBuilder.GhostButton("Hard", null, async () => await studio.RateAndAdvanceAsync(3))
            );
            rateGroup.Children.Add(
                UiBuilder.GhostButton("Good", null, async () => await studio.RateAndAdvanceAsync(4))
            );
            rateGroup.Children.Add(
                UiBuilder.GhostButton("Easy", null, async () => await studio.RateAndAdvanceAsync(5))
            );
            Grid.SetColumn(rateGroup, 1);
            srsRow.Children.Add(rateGroup);

            bool isLast = studio.CurrentQuestionIndex + 1 >= session.Questions.Count;
            var nextBtn = UiBuilder.PrimaryButton(
                isLast ? "Finish & View Summary" : "Next Question",
                MaterialIconKind.ArrowRight,
                async () => await studio.AdvanceNextQuestionAsync()
            );
            Grid.SetColumn(nextBtn, 2);
            srsRow.Children.Add(nextBtn);

            feedbackStack.Children.Add(srsRow);
            stack.Children.Add(UiBuilder.Card(feedbackStack, padding: new Thickness(16, 14)));
        }

        return UiBuilder.PersistentScrollViewer(
            $"quiz-runner:{session.Id}:{studio.CurrentQuestionIndex}",
            stack
        );
    }

    private static Control BuildSessionSummaryView(
        MainWindowViewModel vm,
        QuizStudioViewModel studio
    )
    {
        var session = studio.ActiveSession!;
        int total = Math.Max(1, session.Questions.Count);
        int correct = session.Questions.Count(q =>
            studio.SessionResults.TryGetValue(q.Id, out var r) && r.IsCorrect
        );
        double pct = (100.0 * correct) / total;

        var stack = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(12, 8, 12, 28),
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        headerRow.Children.Add(
            new TextBlock
            {
                Text = $"{session.Title} — Results & Evidence Review",
                FontSize = 19,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var retryBtn = UiBuilder.GhostButton(
            "Retake Assessment",
            MaterialIconKind.Restart,
            () => studio.StartQuizSession(session)
        );
        Grid.SetColumn(retryBtn, 1);
        headerRow.Children.Add(retryBtn);

        var doneBtn = UiBuilder.PrimaryButton(
            "Back to Studio",
            MaterialIconKind.Check,
            () => studio.ExitActiveSession()
        );
        Grid.SetColumn(doneBtn, 2);
        headerRow.Children.Add(doneBtn);
        stack.Children.Add(headerRow);

        stack.Children.Add(
            UiBuilder.Card(
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Score: {correct} / {session.Questions.Count} ({pct:0.#}%)",
                            FontSize = 17,
                            FontWeight = FontWeight.Bold,
                            Foreground =
                                pct >= 75
                                    ? ThemeService.Brush(ThemeService.SuccessFg)
                                    : ThemeService.Brush(ThemeService.PriorityP2Fg),
                        },
                        new TextBlock
                        {
                            Text =
                                "Click 'Highlight on Page N' on any question below to inspect the exact grounded statement inside the Built-in PDF Reader.",
                            FontSize = 12,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                        },
                    },
                },
                padding: new Thickness(16, 12)
            )
        );

        for (int i = 0; i < session.Questions.Count; i++)
        {
            var q = session.Questions[i];
            studio.SessionResults.TryGetValue(q.Id, out var res);
            bool ok = res.IsCorrect;

            var qStack = new StackPanel { Spacing = 6 };
            var topGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            topGrid.Children.Add(
                new TextBlock
                {
                    Text = $"{i + 1}. {q.Prompt}",
                    FontSize = 13.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                }
            );

            var jumpBtn = UiBuilder.GhostButton(
                $"Highlight on Page {q.SourcePage}",
                MaterialIconKind.Marker,
                () => studio.OpenQuestionEvidenceInPdfReader(q),
                isActive: true
            );
            Grid.SetColumn(jumpBtn, 1);
            topGrid.Children.Add(jumpBtn);
            qStack.Children.Add(topGrid);

            qStack.Children.Add(
                new TextBlock
                {
                    Text = ok
                        ? $"Your Answer: {res.Answer} (Correct)"
                        : $"Your Answer: {(string.IsNullOrWhiteSpace(res.Answer) ? "Skipped" : res.Answer)}  ·  Correct Answer: {q.CorrectAnswer}",
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    Foreground = ok
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.PriorityP1Fg),
                }
            );

            if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
            {
                qStack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"Evidence ({q.SectionTitle}, p. {q.SourcePage}): \"{q.VerbatimQuote}\"",
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        TextWrapping = TextWrapping.Wrap,
                    }
                );
            }

            stack.Children.Add(UiBuilder.Card(qStack, padding: new Thickness(14, 10)));
        }

        return UiBuilder.PersistentScrollViewer($"quiz-summary:{session.Id}", stack);
    }
}
