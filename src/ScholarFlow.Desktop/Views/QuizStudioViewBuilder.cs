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

    private static Control BuildStudioWorkspaceView(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*"),
            Margin = new Thickness(20, 14, 20, 14)
        };

        // Top Fixed Header & Subject / Sub-Tab Strip
        var topStack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };

        var titleRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        var titleLeft = new StackPanel { Spacing = 2 };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleLine.Children.Add(new TextBlock
        {
            Text = "Test & Active Recall",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary)
        });
        if (studio.SelectedCourse is not null)
        {
            titleLine.Children.Add(UiBuilder.Pill(
                $"#{studio.SelectedCourse.Alias} · {studio.SelectedCourse.Title}",
                ThemeService.Brush(ThemeService.AccentPrimary),
                ThemeService.Brush(ThemeService.AccentSoftBg)));
        }
        titleLeft.Children.Add(titleLine);

        var pdfFileName = string.IsNullOrWhiteSpace(studio.SelectedPdfPath)
            ? "No PDF found"
            : Path.GetFileName(studio.SelectedPdfPath);
        titleLeft.Children.Add(new TextBlock
        {
            Text = $"Source: {pdfFileName} ({studio.PdfTotalPages} pages)  ·  {studio.ScopePreviewPageCount} content page(s) in active scope",
            FontSize = 12,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary)
        });
        titleRow.Children.Add(titleLeft);

        // Right Utility Actions (Due Deck, Gemini Web Bridge, AI Settings)
        var utilityRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        int dueCount = studio.AllDueReviewQuestions.Count;
        utilityRow.Children.Add(UiBuilder.GhostButton(
            dueCount > 0 ? $"Practice Due ({dueCount})" : "Practice Deck",
            MaterialIconKind.CardsOutline,
            () => studio.StartDueSpacedRepetitionDeck(studio.SelectedCourse?.FolderName),
            isActive: dueCount > 0));

        utilityRow.Children.Add(UiBuilder.GhostButton(
            "Gemini Web JSON",
            MaterialIconKind.CodeJson,
            () => studio.OpenManualJsonBridge(),
            isActive: studio.IsManualJsonModalOpen));

        utilityRow.Children.Add(UiBuilder.GhostButton(
            "AI Engine",
            MaterialIconKind.CogOutline,
            () => studio.ToggleAiSettings(),
            isActive: studio.IsAiSettingsOpen));

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
                isActive: isSelected);
            btn.Margin = new Thickness(0, 0, 5, 4);
            ToolTip.SetTip(btn, c.Title);
            subjectWrap.Children.Add(btn);
        }
        navBarGrid.Children.Add(subjectWrap);

        var subTabRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        subTabRow.Children.Add(UiBuilder.GhostButton(
            "Builder & PDF Preview",
            MaterialIconKind.CreationOutline,
            () => studio.SetSubTab(QuizStudioSubTab.Builder),
            isActive: studio.ActiveSubTab == QuizStudioSubTab.Builder));

        subTabRow.Children.Add(UiBuilder.GhostButton(
            $"Quiz Catalog ({studio.SavedQuizzes.Count})",
            MaterialIconKind.LibraryShelves,
            () => studio.SetSubTab(QuizStudioSubTab.Catalog),
            isActive: studio.ActiveSubTab == QuizStudioSubTab.Catalog));

        Grid.SetColumn(subTabRow, 1);
        navBarGrid.Children.Add(subTabRow);
        topStack.Children.Add(navBarGrid);

        // Status / Feedback Line
        if (!string.IsNullOrWhiteSpace(studio.StatusMessage))
        {
            topStack.Children.Add(new Border
            {
                Background = studio.IsStatusError
                    ? ThemeService.Brush(ThemeService.PriorityP1Bg)
                    : ThemeService.Brush(ThemeService.BgSurface),
                BorderBrush = studio.IsStatusError
                    ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                    : ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        UiBuilder.Icon(
                            studio.IsStatusError ? MaterialIconKind.AlertCircleOutline : MaterialIconKind.CheckCircleOutline,
                            14,
                            studio.IsStatusError
                                ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                                : ThemeService.Brush(ThemeService.AccentPrimary)),
                        new TextBlock
                        {
                            Text = studio.StatusMessage,
                            FontSize = 12,
                            Foreground = studio.IsStatusError
                                ? ThemeService.Brush(ThemeService.PriorityP1Fg)
                                : ThemeService.Brush(ThemeService.TextSecondary),
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            });
        }

        if (studio.IsAiSettingsOpen)
            topStack.Children.Add(BuildAiSettingsCard(vm, studio));

        if (studio.IsManualJsonModalOpen)
            topStack.Children.Add(BuildManualJsonBridgeCard(studio));

        topStack.Children.Add(UiBuilder.HairlineDivider());
        root.Children.Add(topStack);

        // Body Content (2-Column Builder + Live PDF Page Preview OR Hairline Catalog)
        Control bodyContent = studio.ActiveSubTab == QuizStudioSubTab.Catalog
            ? BuildCatalogView(vm, studio)
            : BuildSplitBuilderAndPreviewView(vm, studio);

        Grid.SetRow(bodyContent, 1);
        root.Children.Add(bodyContent);
        return root;
    }

    // =========================================================================
    // 1A. 2-COLUMN SPLIT BUILDER & LIVE VISUAL PDF SCOPE PREVIEW
    // =========================================================================

    private static Control BuildSplitBuilderAndPreviewView(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var splitGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("1.1*,14,0.9*")
        };

        // LEFT COLUMN: Scope, Outline Tree Picker, Mode & Question Count
        var leftStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 4, 20) };

        // 1. Source Material Bar (Master PDF default + optional Individual Handout switcher)
        var sourceCard = new StackPanel { Spacing = 8 };
        var sourceHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        sourceHeader.Children.Add(UiBuilder.SectionHeader("1. Material Source (Defaults to Master PDF)"));

        var masterPdfs = studio.AvailablePdfs.Where(p => p.IsMaster).ToList();
        var handoutPdfs = studio.AvailablePdfs.Where(p => !p.IsMaster).ToList();

        if (handoutPdfs.Count > 0)
        {
            var toggleHandoutsBtn = UiBuilder.GhostButton(
                studio.ShowIndividualHandouts ? "Hide Handouts" : $"Handouts ({handoutPdfs.Count})",
                studio.ShowIndividualHandouts ? MaterialIconKind.ChevronUp : MaterialIconKind.ChevronDown,
                () => studio.ToggleShowIndividualHandouts(),
                isActive: studio.ShowIndividualHandouts);
            Grid.SetColumn(toggleHandoutsBtn, 1);
            sourceHeader.Children.Add(toggleHandoutsBtn);
        }
        sourceCard.Children.Add(sourceHeader);

        var pdfPills = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, fullPath, _) in masterPdfs)
        {
            var captured = fullPath;
            bool active = string.Equals(studio.SelectedPdfPath, captured, StringComparison.OrdinalIgnoreCase);
            var btn = UiBuilder.GhostButton(
                label,
                MaterialIconKind.BookEducationOutline,
                () => studio.SelectPdfScope(captured),
                isActive: active);
            btn.Margin = new Thickness(0, 0, 6, 4);
            pdfPills.Children.Add(btn);
        }

        if (studio.ShowIndividualHandouts || masterPdfs.Count == 0)
        {
            foreach (var (label, fullPath, _) in handoutPdfs)
            {
                var captured = fullPath;
                bool active = string.Equals(studio.SelectedPdfPath, captured, StringComparison.OrdinalIgnoreCase);
                var btn = UiBuilder.GhostButton(
                    label,
                    MaterialIconKind.FilePdfBox,
                    () => studio.SelectPdfScope(captured),
                    isActive: active);
                btn.Margin = new Thickness(0, 0, 5, 4);
                pdfPills.Children.Add(btn);
            }
        }
        sourceCard.Children.Add(pdfPills);
        leftStack.Children.Add(UiBuilder.Card(sourceCard, padding: new Thickness(12, 10)));

        // 2. Academic Term, Custom Page Range & Outline Tree Picker
        var scopeCard = new StackPanel { Spacing = 8 };
        var scopeHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        scopeHeader.Children.Add(UiBuilder.SectionHeader($"2. Term, Page Range & Section Picker ({studio.ScopePreviewPageCount} pages in scope)"));

        if (studio.StartPage.HasValue || studio.EndPage.HasValue || studio.SelectedSectionKeys.Count > 0 || !string.IsNullOrEmpty(studio.SelectedTerm))
        {
            var resetBtn = UiBuilder.GhostButton("Reset Scope", MaterialIconKind.Refresh, () => studio.ClearScopeFilters());
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
            isActive: string.IsNullOrEmpty(studio.SelectedTerm));
        allTermBtn.Margin = new Thickness(0, 0, 4, 4);
        termRangeWrap.Children.Add(allTermBtn);

        foreach (var termName in AcademicTerms)
        {
            var capturedTerm = termName;
            bool isTermActive = string.Equals(studio.SelectedTerm, capturedTerm, StringComparison.OrdinalIgnoreCase);
            var tBtn = UiBuilder.GhostButton(
                capturedTerm,
                null,
                () => studio.SelectTermScope(capturedTerm),
                isActive: isTermActive);
            tBtn.Margin = new Thickness(0, 0, 4, 4);
            termRangeWrap.Children.Add(tBtn);
        }

        var startPageBox = new TextBox
        {
            Text = studio.StartPage?.ToString() ?? string.Empty,
            Watermark = "From pg",
            Width = 68,
            FontSize = 12,
            Margin = new Thickness(6, 0, 4, 4)
        };
        var endPageBox = new TextBox
        {
            Text = studio.EndPage?.ToString() ?? string.Empty,
            Watermark = "To pg",
            Width = 68,
            FontSize = 12,
            Margin = new Thickness(0, 0, 4, 4)
        };

        void ApplyPageInputs()
        {
            int? s = int.TryParse(startPageBox.Text?.Trim(), out var sv) && sv > 0 ? sv : null;
            int? e = int.TryParse(endPageBox.Text?.Trim(), out var ev) && ev > 0 ? ev : null;
            studio.SetPageRange(s, e);
        }

        startPageBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ApplyPageInputs(); } };
        endPageBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ApplyPageInputs(); } };

        var applyPagesBtn = UiBuilder.GhostButton("Set Pages", MaterialIconKind.Check, ApplyPageInputs);
        applyPagesBtn.Margin = new Thickness(0, 0, 4, 4);

        termRangeWrap.Children.Add(startPageBox);
        termRangeWrap.Children.Add(endPageBox);
        termRangeWrap.Children.Add(applyPagesBtn);
        scopeCard.Children.Add(termRangeWrap);

        // Clean Hairline Outline Section Tree Picker (like PdfReaderViewBuilder's Document Outline)
        var visibleSections = studio.OutlineSections
            .Where(s => s.Level >= 2 || studio.OutlineSections.Count <= 6)
            .Where(s => string.IsNullOrWhiteSpace(studio.SelectedTerm) ||
                        string.IsNullOrWhiteSpace(s.Term) ||
                        string.Equals(s.Term, studio.SelectedTerm, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (visibleSections.Count > 0)
        {
            var treeListStack = new StackPanel { Spacing = 1 };
            foreach (var sec in visibleSections)
            {
                var capturedSec = sec;
                var key = QuizStudioViewModel.GetSectionKey(capturedSec);
                bool isChecked = studio.SelectedSectionKeys.Contains(key);
                bool isCurrentlyPreviewed = studio.CurrentPreviewPdfPage >= capturedSec.StartPage &&
                                            studio.CurrentPreviewPdfPage <= capturedSec.EndPage;

                var secRow = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
                    ColumnSpacing = 6
                };

                var chkBtn = UiBuilder.IconButton(
                    isChecked ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline,
                    () => studio.ToggleOutlineSection(capturedSec),
                    15,
                    isChecked ? ThemeService.Brush(ThemeService.AccentPrimary) : ThemeService.Brush(ThemeService.TextMuted));
                secRow.Children.Add(chkBtn);

                var secTitleBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = capturedSec.Title,
                        FontSize = 12.5,
                        FontWeight = isChecked || isCurrentlyPreviewed ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = isChecked
                            ? ThemeService.Brush(ThemeService.AccentPrimary)
                            : isCurrentlyPreviewed
                                ? ThemeService.Brush(ThemeService.TextPrimary)
                                : ThemeService.Brush(ThemeService.TextSecondary),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(2, 4),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left
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
                        fontSize: 10);
                    Grid.SetColumn(termBadge, 2);
                    secRow.Children.Add(termBadge);
                }

                var previewSecBtn = UiBuilder.GhostButton(
                    $"p.{capturedSec.StartPage}–{capturedSec.EndPage}",
                    MaterialIconKind.EyeOutline,
                    () => studio.JumpPreviewToSection(capturedSec),
                    isActive: isCurrentlyPreviewed);
                Grid.SetColumn(previewSecBtn, 3);
                secRow.Children.Add(previewSecBtn);

                treeListStack.Children.Add(new Border
                {
                    Background = isCurrentlyPreviewed ? ThemeService.Brush(ThemeService.BgElevated) : Brushes.Transparent,
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 2),
                    Child = secRow
                });
            }

            scopeCard.Children.Add(new Border
            {
                Background = ThemeService.Brush(ThemeService.BgBase),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4),
                MaxHeight = 215,
                Child = new ScrollViewer { Content = treeListStack }
            });
        }

        leftStack.Children.Add(UiBuilder.Card(scopeCard, padding: new Thickness(12, 10)));

        // 3. Assessment Mode & Question Count (Presets + Live Custom Input up to 150 items)
        var configCard = new StackPanel { Spacing = 8 };
        configCard.Children.Add(UiBuilder.SectionHeader("3. Assessment Mode & Item Count (1–150 Items)"));

        var modeWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (preset, label, icon) in new[]
                 {
                     (QuizPresetMode.ExamSimulator, "STI Exam Simulator", MaterialIconKind.ClipboardTextOutline),
                     (QuizPresetMode.QuickReview, "Quick Concept Review", MaterialIconKind.FlashOutline),
                     (QuizPresetMode.WeakTopicDrill, "Weak-Topic Drill", MaterialIconKind.Target)
                 })
        {
            var capturedPreset = preset;
            var pBtn = UiBuilder.GhostButton(
                label,
                icon,
                () => studio.SetPreset(capturedPreset),
                isActive: studio.SelectedPreset == capturedPreset);
            pBtn.Margin = new Thickness(0, 0, 6, 4);
            modeWrap.Children.Add(pBtn);
        }
        configCard.Children.Add(modeWrap);

        var countWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (int cnt in new[] { 10, 20, 30, 50, 60, 100 })
        {
            int capturedCnt = cnt;
            var cBtn = UiBuilder.GhostButton(
                $"{cnt}",
                null,
                () => studio.SetQuestionCount(capturedCnt),
                isActive: studio.QuestionCount == capturedCnt);
            cBtn.Margin = new Thickness(0, 0, 4, 4);
            countWrap.Children.Add(cBtn);
        }

        var customCountBox = new TextBox
        {
            Text = studio.QuestionCount.ToString(),
            Watermark = "Items (1-150)",
            Width = 85,
            FontSize = 12,
            Margin = new Thickness(6, 0, 4, 4)
        };
        customCountBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty &&
                int.TryParse(customCountBox.Text?.Trim(), out var parsed) &&
                parsed is >= 1 and <= 150)
            {
                studio.SetQuestionCount(parsed, notify: false);
            }
        };
        customCountBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && int.TryParse(customCountBox.Text?.Trim(), out var parsed) && parsed > 0)
            {
                e.Handled = true;
                studio.SetQuestionCount(parsed, notify: true);
            }
        };
        countWrap.Children.Add(customCountBox);
        configCard.Children.Add(countWrap);

        // Generate Action Buttons
        var generateActions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var genAiBtn = UiBuilder.PrimaryButton(
            studio.IsGenerating ? "Generating Grounded Exam..." : $"Generate {studio.QuestionCount}-Item Exam",
            MaterialIconKind.AutoFix,
            async () => await studio.GenerateQuizNowAsync(forceOffline: false));
        genAiBtn.Margin = new Thickness(0, 0, 8, 4);
        generateActions.Children.Add(genAiBtn);

        var genOfflineBtn = UiBuilder.GhostButton(
            "Instant Offline PDF Quiz",
            MaterialIconKind.LightningBoltOutline,
            async () => await studio.GenerateQuizNowAsync(forceOffline: true));
        genOfflineBtn.Margin = new Thickness(0, 0, 8, 4);
        generateActions.Children.Add(genOfflineBtn);

        configCard.Children.Add(generateActions);
        leftStack.Children.Add(UiBuilder.Card(configCard, padding: new Thickness(12, 10)));

        // Also show quick recent quizzes for this subject right under the builder
        var subjectQuizzes = studio.SavedQuizzes
            .Where(q => studio.SelectedCourse is null || string.Equals(q.CourseFolder, studio.SelectedCourse.FolderName, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();
        if (subjectQuizzes.Count > 0)
        {
            var recentStack = new StackPanel { Spacing = 4 };
            var recentHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            recentHeader.Children.Add(UiBuilder.SectionHeader($"Recent Assessments for {studio.SelectedCourse?.Alias.ToUpperInvariant() ?? "Subject"}"));
            var openCatBtn = UiBuilder.GhostButton(
                $"Open Catalog & Combiner ({studio.SavedQuizzes.Count})",
                MaterialIconKind.ArrowRight,
                () => studio.SetSubTab(QuizStudioSubTab.Catalog));
            Grid.SetColumn(openCatBtn, 1);
            recentHeader.Children.Add(openCatBtn);
            recentStack.Children.Add(recentHeader);

            foreach (var qz in subjectQuizzes)
                recentStack.Children.Add(BuildHairlineQuizRow(vm, studio, qz, showCheckbox: false));

            leftStack.Children.Add(UiBuilder.Card(recentStack, padding: new Thickness(12, 8)));
        }

        splitGrid.Children.Add(new ScrollViewer { Content = leftStack });

        // RIGHT COLUMN: LIVE VISUAL PDF SCOPE PREVIEWER
        var previewPane = BuildLivePdfPreviewPane(vm, studio);
        Grid.SetColumn(previewPane, 2);
        splitGrid.Children.Add(previewPane);

        return splitGrid;
    }

    private static Control BuildLivePdfPreviewPane(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var grid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto")
        };

        // 1. Top Preview Header Bar
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var leftInfo = new StackPanel { Spacing = 2 };
        leftInfo.Children.Add(new TextBlock
        {
            Text = "Live PDF Scope Preview",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary)
        });
        leftInfo.Children.Add(new TextBlock
        {
            Text = $"{studio.CurrentPreviewSectionTitle}  ·  PDF Page {studio.CurrentPreviewPdfPage}",
            FontSize = 11.5,
            Foreground = ThemeService.Brush(ThemeService.AccentPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        headerGrid.Children.Add(leftInfo);

        if (!string.IsNullOrWhiteSpace(studio.SelectedPdfPath) && File.Exists(studio.SelectedPdfPath))
        {
            var openReaderBtn = UiBuilder.GhostButton(
                "Full Reader",
                MaterialIconKind.ArrowExpandAll,
                () =>
                {
                    vm.OpenPdfInReader(studio.SelectedPdfPath);
                    vm.PdfReader.JumpToPage(studio.CurrentPreviewPdfPage);
                });
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
                    VerticalAlignment = VerticalAlignment.Top
                }
            };

            pageVisual = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = pageSheet
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
                    UiBuilder.Icon(MaterialIconKind.FilePdfBox, 28, ThemeService.Brush(ThemeService.TextMuted)),
                    new TextBlock
                    {
                        Text = "No PDF page available in current scope.",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted)
                    }
                }
            };
        }

        var canvasBorder = new Border
        {
            Background = ThemeService.Brush(ThemeService.BgBase),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = pageVisual
        };
        Grid.SetRow(canvasBorder, 1);
        grid.Children.Add(canvasBorder);

        // 3. Bottom Scope Page Flipper Bar (< Prev in Scope | Page X of Y (PDF p. Z) | Next in Scope >)
        var footerBar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(0, 8, 0, 0)
        };

        var prevBtn = UiBuilder.GhostButton(
            "Prev in Scope",
            MaterialIconKind.ChevronLeft,
            () => studio.PreviousPreviewPage());
        prevBtn.IsEnabled = studio.PreviewScopeIndex > 0;
        footerBar.Children.Add(prevBtn);

        var pageCounter = new TextBlock
        {
            Text = studio.ScopePreviewPageCount > 0
                ? $"Scope Page {studio.PreviewScopeIndex + 1} of {studio.ScopePreviewPageCount}  (PDF p. {studio.CurrentPreviewPdfPage})"
                : "0 pages in scope",
            FontSize = 11.5,
            FontWeight = FontWeight.Medium,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pageCounter, 1);
        footerBar.Children.Add(pageCounter);

        var nextBtn = UiBuilder.GhostButton(
            "Next in Scope",
            MaterialIconKind.ChevronRight,
            () => studio.NextPreviewPage());
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
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 24)
        };

        var filteredQuizzes = studio.CatalogShowAllCourses || studio.SelectedCourse is null
            ? studio.SavedQuizzes
            : studio.SavedQuizzes
                .Where(q => string.Equals(q.CourseFolder, studio.SelectedCourse.FolderName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var barGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), ColumnSpacing = 8 };
        barGrid.Children.Add(new TextBlock
        {
            Text = studio.CatalogShowAllCourses
                ? $"All Subjects Catalog ({filteredQuizzes.Count} saved)"
                : $"{studio.SelectedCourse?.Title ?? "Subject"} Catalog ({filteredQuizzes.Count} saved) — Check 2+ quizzes to combine into a composite exam",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center
        });

        var toggleAllBtn = UiBuilder.GhostButton(
            studio.CatalogShowAllCourses ? "Filter: Active Subject" : $"Show All Subjects ({studio.SavedQuizzes.Count})",
            MaterialIconKind.FilterVariant,
            () => studio.ToggleCatalogShowAllCourses(),
            isActive: studio.CatalogShowAllCourses);
        Grid.SetColumn(toggleAllBtn, 1);
        barGrid.Children.Add(toggleAllBtn);

        var newQuizBtn = UiBuilder.PrimaryButton(
            "New Assessment",
            MaterialIconKind.Plus,
            () => studio.SetSubTab(QuizStudioSubTab.Builder));
        Grid.SetColumn(newQuizBtn, 2);
        barGrid.Children.Add(newQuizBtn);
        stack.Children.Add(barGrid);

        // Multi-Quiz Combiner Action Bar
        if (studio.SelectedQuizIdsForMerge.Count > 0)
        {
            var mergeGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 8
            };
            var mergeTitleBox = new TextBox
            {
                Watermark = "Composite exam title (e.g. SQA Prelims + Midterms Comprehensive)...",
                FontSize = 12
            };
            mergeGrid.Children.Add(mergeTitleBox);

            var mergeShuffleBtn = UiBuilder.PrimaryButton(
                $"Combine & Shuffle ({studio.SelectedQuizIdsForMerge.Count})",
                MaterialIconKind.CallMerge,
                async () => await studio.CombineSelectedQuizzesAsync(mergeTitleBox.Text, shuffleQuestions: true));
            Grid.SetColumn(mergeShuffleBtn, 1);
            mergeGrid.Children.Add(mergeShuffleBtn);

            var mergeOrderedBtn = UiBuilder.GhostButton(
                "Combine In-Order",
                MaterialIconKind.FormatListNumbered,
                async () => await studio.CombineSelectedQuizzesAsync(mergeTitleBox.Text, shuffleQuestions: false));
            Grid.SetColumn(mergeOrderedBtn, 2);
            mergeGrid.Children.Add(mergeOrderedBtn);

            stack.Children.Add(UiBuilder.Card(mergeGrid, padding: new Thickness(12, 8)));
        }

        if (filteredQuizzes.Count == 0)
        {
            stack.Children.Add(UiBuilder.Card(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "No assessments saved for this subject yet.",
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary)
                    },
                    new TextBlock
                    {
                        Text = "Switch to 'Builder & PDF Preview' to generate a grounded exam from your Master PDF, or click 'Show All Subjects'.",
                        FontSize = 12,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted)
                    }
                }
            }));
            return new ScrollViewer { Content = stack };
        }

        var listStack = new StackPanel { Spacing = 0 };
        foreach (var quiz in filteredQuizzes)
        {
            listStack.Children.Add(BuildHairlineQuizRow(vm, studio, quiz, showCheckbox: true));
        }
        stack.Children.Add(UiBuilder.Card(listStack, padding: new Thickness(8, 4)));

        return new ScrollViewer { Content = stack };
    }

    private static Control BuildHairlineQuizRow(
        MainWindowViewModel vm,
        QuizStudioViewModel studio,
        QuizSessionRecord quiz,
        bool showCheckbox)
    {
        bool isChecked = studio.SelectedQuizIdsForMerge.Contains(quiz.Id);

        var rowGrid = new Grid
        {
            ColumnDefinitions = showCheckbox
                ? ColumnDefinitions.Parse("Auto,*,Auto")
                : ColumnDefinitions.Parse("0,*,Auto"),
            ColumnSpacing = 10
        };

        if (showCheckbox)
        {
            var chk = UiBuilder.IconButton(
                isChecked ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline,
                () => studio.ToggleQuizSelectionForMerge(quiz.Id),
                16,
                isChecked ? ThemeService.Brush(ThemeService.AccentPrimary) : ThemeService.Brush(ThemeService.TextMuted));
            rowGrid.Children.Add(chk);
        }

        var centerStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        centerStack.Children.Add(new TextBlock
        {
            Text = quiz.Title,
            FontSize = 13.5,
            FontWeight = FontWeight.Medium,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var subParts = new List<string>
        {
            quiz.CourseTitle,
            $"{quiz.Questions.Count} items",
            quiz.ModelUsed
        };
        if (!string.IsNullOrWhiteSpace(quiz.Term))
            subParts.Insert(1, quiz.Term);
        if (quiz.LastScorePercent.HasValue)
            subParts.Add($"Score: {quiz.LastScorePercent.Value:0.#}% ({quiz.AttemptCount}x)");

        centerStack.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", subParts),
            FontSize = 11.5,
            Foreground = ThemeService.Brush(ThemeService.TextMuted),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(centerStack, 1);
        rowGrid.Children.Add(centerStack);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (quiz.LastScorePercent.HasValue)
        {
            bool high = quiz.LastScorePercent.Value >= 75;
            actions.Children.Add(UiBuilder.Pill(
                $"{quiz.LastScorePercent.Value:0}%",
                high ? ThemeService.Brush(ThemeService.SuccessFg) : ThemeService.Brush(ThemeService.PriorityP2Fg),
                high ? ThemeService.Brush(ThemeService.SuccessBg) : ThemeService.Brush(ThemeService.PriorityP2Bg)));
        }

        actions.Children.Add(UiBuilder.GhostButton(
            "Start",
            MaterialIconKind.PlayOutline,
            () => studio.StartQuizSession(quiz),
            isActive: true));

        if (File.Exists(quiz.SourcePdfPath))
        {
            actions.Children.Add(UiBuilder.IconButton(
                MaterialIconKind.FilePdfBox,
                () => vm.OpenPdfInReader(quiz.SourcePdfPath),
                14.5,
                ThemeService.Brush(ThemeService.TextSecondary)));
        }

        actions.Children.Add(UiBuilder.IconButton(
            MaterialIconKind.TrashCanOutline,
            async () => await studio.DeleteQuizAsync(quiz),
            14.5,
            ThemeService.Brush(ThemeService.TextMuted)));

        Grid.SetColumn(actions, 2);
        rowGrid.Children.Add(actions);

        var rowBorder = new Border
        {
            Background = isChecked ? ThemeService.Brush(ThemeService.BgElevated) : Brushes.Transparent,
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 7),
            Transitions = UiBuilder.CreateSmoothBrushTransitions(110),
            Child = rowGrid
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

    private static Control BuildAiSettingsCard(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var settings = vm.Settings;
        var cardStack = new StackPanel { Spacing = 8 };

        cardStack.Children.Add(UiBuilder.SectionHeader("Unified OpenAI-Compatible AI Engine Configuration"));

        var presetRow = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddProviderPreset(string id, string label, MaterialIconKind icon)
        {
            bool active = string.Equals(settings.AiProviderPreset, id, StringComparison.OrdinalIgnoreCase);
            var btn = UiBuilder.GhostButton(
                label,
                icon,
                async () => await studio.ApplyAiProviderPresetAsync(id),
                isActive: active);
            btn.Margin = new Thickness(0, 0, 6, 4);
            presetRow.Children.Add(btn);
        }

        AddProviderPreset("GeminiAiStudio", "Google AI Studio (Gemini)", MaterialIconKind.CreationOutline);
        AddProviderPreset("OllamaLocal", "Local Ollama (localhost:11434)", MaterialIconKind.ServerNetwork);
        AddProviderPreset("OfflineSmart", "Offline Smart PDF Analyzer", MaterialIconKind.FileSearchOutline);
        cardStack.Children.Add(presetRow);

        var fieldsGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("2*,1.2*,1.5*,Auto"),
            ColumnSpacing = 8
        };

        var urlBox = new TextBox
        {
            Text = settings.AiBaseUrl,
            Watermark = "OpenAI-compatible endpoint URL",
            FontSize = 12
        };
        fieldsGrid.Children.Add(urlBox);

        var modelBox = new TextBox
        {
            Text = settings.AiModelName,
            Watermark = "Model (e.g. gemini-2.5-flash)",
            FontSize = 12
        };
        Grid.SetColumn(modelBox, 1);
        fieldsGrid.Children.Add(modelBox);

        var keyBox = new TextBox
        {
            Text = settings.AiApiKey ?? string.Empty,
            Watermark = "API Key (or GEMINI_API_KEY env)",
            PasswordChar = '•',
            FontSize = 12
        };
        Grid.SetColumn(keyBox, 2);
        fieldsGrid.Children.Add(keyBox);

        var saveBtn = UiBuilder.PrimaryButton(
            "Save",
            MaterialIconKind.ContentSaveOutline,
            async () => await studio.SaveAiCredentialsAsync(urlBox.Text ?? "", modelBox.Text ?? "", keyBox.Text));
        Grid.SetColumn(saveBtn, 3);
        fieldsGrid.Children.Add(saveBtn);

        cardStack.Children.Add(fieldsGrid);
        return UiBuilder.Card(cardStack, padding: new Thickness(12, 10));
    }

    private static Control BuildManualJsonBridgeCard(QuizStudioViewModel studio)
    {
        var cardStack = new StackPanel { Spacing = 8 };
        cardStack.Children.Add(UiBuilder.SectionHeader("Gemini Student Pro Web Bridge (Zero API Quota)"));

        var split = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,12,*") };

        var leftCol = new StackPanel { Spacing = 6 };
        var promptBox = new TextBox
        {
            Text = studio.GeneratedPromptPreview,
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 90,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        leftCol.Children.Add(promptBox);
        leftCol.Children.Add(UiBuilder.GhostButton(
            "1. Copy Grounded Prompt to Clipboard",
            MaterialIconKind.ContentCopy,
            async () =>
            {
                var top = TopLevel.GetTopLevel(promptBox);
                if (top?.Clipboard is not null)
                    await top.Clipboard.SetTextAsync(studio.GeneratedPromptPreview);
            }));
        split.Children.Add(leftCol);

        var rightCol = new StackPanel { Spacing = 6 };
        var jsonInputBox = new TextBox
        {
            Watermark = "2. Paste Gemini's JSON response here...",
            AcceptsReturn = true,
            Height = 90,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        rightCol.Children.Add(jsonInputBox);
        rightCol.Children.Add(UiBuilder.PrimaryButton(
            "Import JSON & Ground to PDF Highlights",
            MaterialIconKind.CheckDecagramOutline,
            async () => await studio.ImportPastedJsonQuizAsync(jsonInputBox.Text ?? string.Empty)));
        Grid.SetColumn(rightCol, 2);
        split.Children.Add(rightCol);

        cardStack.Children.Add(split);
        return UiBuilder.Card(cardStack, padding: new Thickness(12, 10));
    }

    // =========================================================================
    // 2. INTERACTIVE QUESTION RUNNER & GROUNDED PDF EVIDENCE VIEW
    // =========================================================================

    private static Control BuildActiveQuestionRunnerView(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var session = studio.ActiveSession!;
        var q = studio.CurrentQuestion;
        if (q is null)
            return BuildStudioWorkspaceView(vm, studio);

        var stack = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(28, 20, 28, 36)
        };

        var topBar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        topBar.Children.Add(UiBuilder.GhostButton(
            "Exit to Studio",
            MaterialIconKind.ArrowLeft,
            () => studio.ExitActiveSession()));

        var centerTitle = new TextBlock
        {
            Text = $"{session.Title}  ·  Question {studio.CurrentQuestionIndex + 1} of {session.Questions.Count}",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(centerTitle, 1);
        topBar.Children.Add(centerTitle);

        var metaBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        metaBadges.Children.Add(UiBuilder.PillBadge(
            q.Kind.ToString(),
            ThemeService.Brush(ThemeService.AccentPrimary),
            ThemeService.Brush(ThemeService.AccentSoftBg)));
        metaBadges.Children.Add(UiBuilder.PillBadge(
            $"Cognitive: {q.CognitiveLevel}",
            ThemeService.Brush(ThemeService.PriorityP3Fg),
            ThemeService.Brush(ThemeService.PriorityP3Bg)));
        metaBadges.Children.Add(UiBuilder.PillBadge(
            $"@{q.TopicTag}",
            ThemeService.Brush(ThemeService.TextSecondary),
            ThemeService.Brush(ThemeService.BgElevated)));

        Grid.SetColumn(metaBadges, 2);
        topBar.Children.Add(metaBadges);
        stack.Children.Add(topBar);

        var qCardStack = new StackPanel { Spacing = 14 };

        if (!string.IsNullOrWhiteSpace(q.SectionTitle))
        {
            qCardStack.Children.Add(new TextBlock
            {
                Text = $"Source Section: {q.SectionTitle} (Page {q.SourcePage})",
                FontSize = 11.5,
                FontWeight = FontWeight.Medium,
                Foreground = ThemeService.Brush(ThemeService.TextMuted)
            });
        }

        qCardStack.Children.Add(new TextBlock
        {
            Text = q.Prompt,
            FontSize = 15.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23
        });

        if (q.Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational or QuizQuestionKind.TrueFalse &&
            q.Options.Count > 0)
        {
            var optsStack = new StackPanel { Spacing = 8 };
            for (int i = 0; i < q.Options.Count; i++)
            {
                var optText = q.Options[i];
                var letter = (char)('A' + i);
                bool isChosen = string.Equals(studio.CurrentAnswerDraft, optText, StringComparison.OrdinalIgnoreCase);
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

                var optBtn = new Button
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new Border
                            {
                                Width = 24,
                                Height = 24,
                                CornerRadius = new CornerRadius(12),
                                Background = ThemeService.Brush(ThemeService.BgSurface),
                                BorderBrush = borderBrush,
                                BorderThickness = new Thickness(1),
                                Child = new TextBlock
                                {
                                    Text = letter.ToString(),
                                    FontSize = 11.5,
                                    FontWeight = FontWeight.Bold,
                                    Foreground = fgBrush,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            },
                            new TextBlock
                            {
                                Text = optText,
                                FontSize = 13.5,
                                Foreground = fgBrush,
                                TextWrapping = TextWrapping.Wrap,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        }
                    },
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left
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
        else
        {
            var idRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
            var ansBox = new TextBox
            {
                Text = studio.CurrentAnswerDraft,
                Watermark = "Type your answer and press Enter...",
                FontSize = 14,
                IsEnabled = !studio.IsAnswerRevealed
            };
            ansBox.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter && !studio.IsAnswerRevealed && !string.IsNullOrWhiteSpace(ansBox.Text))
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
                });
            Grid.SetColumn(submitBtn, 1);
            idRow.Children.Add(submitBtn);
            qCardStack.Children.Add(idRow);
        }

        stack.Children.Add(UiBuilder.Card(qCardStack, padding: new Thickness(18, 16)));

        if (studio.IsAnswerRevealed)
        {
            var feedbackStack = new StackPanel { Spacing = 10 };

            var verdictRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            verdictRow.Children.Add(UiBuilder.Icon(
                studio.IsCurrentAnswerCorrect ? MaterialIconKind.CheckCircle : MaterialIconKind.CloseCircle,
                18,
                studio.IsCurrentAnswerCorrect
                    ? ThemeService.Brush(ThemeService.SuccessFg)
                    : ThemeService.Brush(ThemeService.PriorityP1Fg)));
            verdictRow.Children.Add(new TextBlock
            {
                Text = studio.IsCurrentAnswerCorrect
                    ? "Correct!"
                    : $"Incorrect  ·  Correct Answer: {q.CorrectAnswer}",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = studio.IsCurrentAnswerCorrect
                    ? ThemeService.Brush(ThemeService.SuccessFg)
                    : ThemeService.Brush(ThemeService.PriorityP1Fg),
                VerticalAlignment = VerticalAlignment.Center
            });
            feedbackStack.Children.Add(verdictRow);

            if (!string.IsNullOrWhiteSpace(q.Explanation))
            {
                feedbackStack.Children.Add(new TextBlock
                {
                    Text = q.Explanation,
                    FontSize = 13,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextWrapping = TextWrapping.Wrap
                });
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
                            Text = $"Master PDF Evidence  ·  {q.SectionTitle}  ·  Page {q.SourcePage}",
                            FontSize = 11.5,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = ThemeService.Brush(ThemeService.PriorityP2Fg)
                        },
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(q.VerbatimQuote)
                                ? "(Grounded to Page " + q.SourcePage + ")"
                                : $"\"{q.VerbatimQuote}\"",
                            FontSize = 12.5,
                            Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                            TextWrapping = TextWrapping.Wrap
                        },
                        UiBuilder.GhostButton(
                            $"Jump to Highlighted Statement on Page {q.SourcePage} in PDF Reader",
                            MaterialIconKind.Marker,
                            () => studio.OpenQuestionEvidenceInPdfReader(q),
                            isActive: true)
                    }
                }
            };
            feedbackStack.Children.Add(evidenceBox);

            var srsRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
            var prevBtn = UiBuilder.GhostButton(
                "Previous",
                MaterialIconKind.ChevronLeft,
                () => studio.PreviousQuestion());
            srsRow.Children.Add(prevBtn);

            var rateGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            rateGroup.Children.Add(UiBuilder.GhostButton("Again (1d)", MaterialIconKind.Refresh, async () => await studio.RateAndAdvanceAsync(1)));
            rateGroup.Children.Add(UiBuilder.GhostButton("Hard", null, async () => await studio.RateAndAdvanceAsync(3)));
            rateGroup.Children.Add(UiBuilder.GhostButton("Good", null, async () => await studio.RateAndAdvanceAsync(4)));
            rateGroup.Children.Add(UiBuilder.GhostButton("Easy", null, async () => await studio.RateAndAdvanceAsync(5)));
            Grid.SetColumn(rateGroup, 1);
            srsRow.Children.Add(rateGroup);

            bool isLast = studio.CurrentQuestionIndex + 1 >= session.Questions.Count;
            var nextBtn = UiBuilder.PrimaryButton(
                isLast ? "Finish & View Summary" : "Next Question",
                MaterialIconKind.ArrowRight,
                async () => await studio.AdvanceNextQuestionAsync());
            Grid.SetColumn(nextBtn, 2);
            srsRow.Children.Add(nextBtn);

            feedbackStack.Children.Add(srsRow);
            stack.Children.Add(UiBuilder.Card(feedbackStack, padding: new Thickness(16, 14)));
        }

        return new ScrollViewer { Content = stack };
    }

    // =========================================================================
    // 3. POST-QUIZ SUMMARY & EVIDENCE REVIEW
    // =========================================================================

    private static Control BuildSessionSummaryView(MainWindowViewModel vm, QuizStudioViewModel studio)
    {
        var session = studio.ActiveSession!;
        int total = Math.Max(1, session.Questions.Count);
        int correct = session.Questions.Count(q =>
            studio.SessionResults.TryGetValue(q.Id, out var r) && r.IsCorrect);
        double pct = (100.0 * correct) / total;

        var stack = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(28, 20, 28, 36)
        };

        var headerRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), ColumnSpacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = $"{session.Title} — Results & Evidence Review",
            FontSize = 19,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        });

        var retryBtn = UiBuilder.GhostButton(
            "Retake Assessment",
            MaterialIconKind.Restart,
            () => studio.StartQuizSession(session));
        Grid.SetColumn(retryBtn, 1);
        headerRow.Children.Add(retryBtn);

        var doneBtn = UiBuilder.PrimaryButton(
            "Back to Studio",
            MaterialIconKind.Check,
            () => studio.ExitActiveSession());
        Grid.SetColumn(doneBtn, 2);
        headerRow.Children.Add(doneBtn);
        stack.Children.Add(headerRow);

        stack.Children.Add(UiBuilder.Card(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = $"Score: {correct} / {session.Questions.Count} ({pct:0.#}%)",
                    FontSize = 17,
                    FontWeight = FontWeight.Bold,
                    Foreground = pct >= 75
                        ? ThemeService.Brush(ThemeService.SuccessFg)
                        : ThemeService.Brush(ThemeService.PriorityP2Fg)
                },
                new TextBlock
                {
                    Text = "Click 'Highlight on Page N' on any question below to inspect the exact grounded statement inside the Built-in PDF Reader.",
                    FontSize = 12,
                    Foreground = ThemeService.Brush(ThemeService.TextSecondary)
                }
            }
        }, padding: new Thickness(16, 12)));

        for (int i = 0; i < session.Questions.Count; i++)
        {
            var q = session.Questions[i];
            studio.SessionResults.TryGetValue(q.Id, out var res);
            bool ok = res.IsCorrect;

            var qStack = new StackPanel { Spacing = 6 };
            var topGrid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
            topGrid.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {q.Prompt}",
                FontSize = 13.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                TextWrapping = TextWrapping.Wrap
            });

            var jumpBtn = UiBuilder.GhostButton(
                $"Highlight on Page {q.SourcePage}",
                MaterialIconKind.Marker,
                () => studio.OpenQuestionEvidenceInPdfReader(q),
                isActive: true);
            Grid.SetColumn(jumpBtn, 1);
            topGrid.Children.Add(jumpBtn);
            qStack.Children.Add(topGrid);

            qStack.Children.Add(new TextBlock
            {
                Text = ok
                    ? $"Your Answer: {res.Answer} (Correct)"
                    : $"Your Answer: {(string.IsNullOrWhiteSpace(res.Answer) ? "Skipped" : res.Answer)}  ·  Correct Answer: {q.CorrectAnswer}",
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = ok ? ThemeService.Brush(ThemeService.SuccessFg) : ThemeService.Brush(ThemeService.PriorityP1Fg)
            });

            if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
            {
                qStack.Children.Add(new TextBlock
                {
                    Text = $"Evidence ({q.SectionTitle}, p. {q.SourcePage}): \"{q.VerbatimQuote}\"",
                    FontSize = 11.5,
                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            stack.Children.Add(UiBuilder.Card(qStack, padding: new Thickness(14, 10)));
        }

        return new ScrollViewer { Content = stack };
    }
}
