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

public static partial class QuizStudioViewBuilder
{
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

            // Tier 2: Full-Width Question Prompt + Specialized Rich Cards (Code, Math LaTeX, Diagram)
            cardStack.Children.Add(
                new TextBlock
                {
                    Text = QuizRichContentRenderer.FormatInlineMath(q.Prompt),
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                }
            );

            if (!string.IsNullOrWhiteSpace(q.CodeSnippet))
                cardStack.Children.Add(QuizRichContentRenderer.BuildCodeSnippetCard(q.CodeSnippet, q.CodeLanguage));

            if (!string.IsNullOrWhiteSpace(q.MathLatex))
                cardStack.Children.Add(QuizRichContentRenderer.BuildMathLatexCard(q.MathLatex));

            if (!string.IsNullOrWhiteSpace(q.Diagram))
                cardStack.Children.Add(QuizRichContentRenderer.BuildDiagramCard(q.Diagram));

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
                    bool isOrdering = capturedQ.Kind == QuizQuestionKind.Ordering;
                    var optsHeader = new Grid
                    {
                        ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
                        ColumnSpacing = 8,
                    };
                    optsHeader.Children.Add(
                        new TextBlock
                        {
                            Text = isOrdering
                                ? "Steps in CORRECT chronological order (shuffled automatically during quiz):"
                                : "Options (click the circle to mark the correct answer):",
                            FontSize = 11.5,
                            Foreground = ThemeService.Brush(ThemeService.TextMuted),
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                    var addOptBtn = UiBuilder.GhostButton(
                        isOrdering ? "Add Step" : "Add Option",
                        MaterialIconKind.Plus,
                        () =>
                        {
                            capturedQ.Options.Add(
                                isOrdering
                                    ? $"Step {capturedQ.Options.Count + 1}"
                                    : $"Option {capturedQ.Options.Count + 1}"
                            );
                            if (isOrdering && capturedQ.Options.Count >= 2)
                                capturedQ.CorrectAnswer = string.Join(" -> ", capturedQ.Options);
                            vm.RequestUiRefresh();
                        }
                    );
                    Grid.SetColumn(addOptBtn, 1);
                    optsHeader.Children.Add(addOptBtn);
                    editStack.Children.Add(optsHeader);

                    var optsListStack = new StackPanel { Spacing = 6 };
                    for (int oi = 0; oi < capturedQ.Options.Count; oi++)
                    {
                        int capturedIdx = oi;
                        var currentVal = capturedQ.Options[oi];
                        bool isCorrectChoice = !isOrdering && string.Equals(
                            currentVal.Trim(),
                            capturedQ.CorrectAnswer.Trim(),
                            StringComparison.OrdinalIgnoreCase);

                        var optRow = new Grid
                        {
                            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto,Auto"),
                            ColumnSpacing = 6,
                        };

                        if (isOrdering)
                        {
                            var numPill = UiBuilder.PillBadge(
                                $"{oi + 1}",
                                ThemeService.Brush(ThemeService.AccentPrimary),
                                ThemeService.Brush(ThemeService.AccentSoftBg)
                            );
                            numPill.VerticalAlignment = VerticalAlignment.Center;
                            optRow.Children.Add(numPill);
                        }
                        else
                        {
                            var markBtn = UiBuilder.IconButton(
                                isCorrectChoice
                                    ? MaterialIconKind.CheckCircle
                                    : MaterialIconKind.CircleOutline,
                                () =>
                                {
                                    capturedQ.CorrectAnswer = capturedQ.Options[capturedIdx];
                                    vm.RequestUiRefresh();
                                },
                                16,
                                isCorrectChoice
                                    ? ThemeService.Brush(ThemeService.SuccessFg)
                                    : ThemeService.Brush(ThemeService.TextMuted)
                            );
                            markBtn.VerticalAlignment = VerticalAlignment.Center;
                            optRow.Children.Add(markBtn);
                        }

                        var optItemBox = new TextBox
                        {
                            Text = currentVal,
                            Watermark = isOrdering ? $"Step {oi + 1}..." : $"Option {oi + 1}...",
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                        };
                        optItemBox.PropertyChanged += (_, e) =>
                        {
                            if (e.Property == TextBox.TextProperty && capturedIdx < capturedQ.Options.Count)
                            {
                                var wasCorrect = !isOrdering && string.Equals(
                                    capturedQ.Options[capturedIdx].Trim(),
                                    capturedQ.CorrectAnswer.Trim(),
                                    StringComparison.OrdinalIgnoreCase);
                                var nextText = optItemBox.Text ?? string.Empty;
                                capturedQ.Options[capturedIdx] = nextText;
                                if (wasCorrect)
                                    capturedQ.CorrectAnswer = nextText.Trim();
                                else if (isOrdering && capturedQ.Options.Count >= 2)
                                    capturedQ.CorrectAnswer = string.Join(" -> ", capturedQ.Options);
                            }
                        };
                        Grid.SetColumn(optItemBox, 1);
                        optRow.Children.Add(optItemBox);

                        if (isOrdering)
                        {
                            var upStepBtn = UiBuilder.IconButton(
                                MaterialIconKind.ArrowUp,
                                () =>
                                {
                                    if (capturedIdx <= 0) return;
                                    (capturedQ.Options[capturedIdx - 1], capturedQ.Options[capturedIdx]) =
                                        (capturedQ.Options[capturedIdx], capturedQ.Options[capturedIdx - 1]);
                                    capturedQ.CorrectAnswer = string.Join(" -> ", capturedQ.Options);
                                    vm.RequestUiRefresh();
                                },
                                13,
                                ThemeService.Brush(ThemeService.TextSecondary)
                            );
                            upStepBtn.VerticalAlignment = VerticalAlignment.Center;
                            Grid.SetColumn(upStepBtn, 2);
                            optRow.Children.Add(upStepBtn);

                            var downStepBtn = UiBuilder.IconButton(
                                MaterialIconKind.ArrowDown,
                                () =>
                                {
                                    if (capturedIdx >= capturedQ.Options.Count - 1) return;
                                    (capturedQ.Options[capturedIdx + 1], capturedQ.Options[capturedIdx]) =
                                        (capturedQ.Options[capturedIdx], capturedQ.Options[capturedIdx + 1]);
                                    capturedQ.CorrectAnswer = string.Join(" -> ", capturedQ.Options);
                                    vm.RequestUiRefresh();
                                },
                                13,
                                ThemeService.Brush(ThemeService.TextSecondary)
                            );
                            downStepBtn.VerticalAlignment = VerticalAlignment.Center;
                            Grid.SetColumn(downStepBtn, 3);
                            optRow.Children.Add(downStepBtn);
                        }

                        var delOptBtn = UiBuilder.IconButton(
                            MaterialIconKind.Close,
                            () =>
                            {
                                if (capturedIdx < capturedQ.Options.Count)
                                {
                                    capturedQ.Options.RemoveAt(capturedIdx);
                                    if (isOrdering && capturedQ.Options.Count >= 2)
                                        capturedQ.CorrectAnswer = string.Join(" -> ", capturedQ.Options);
                                    vm.RequestUiRefresh();
                                }
                            },
                            13,
                            ThemeService.Brush(ThemeService.PriorityP1Fg)
                        );
                        delOptBtn.VerticalAlignment = VerticalAlignment.Center;
                        Grid.SetColumn(delOptBtn, 4);
                        optRow.Children.Add(delOptBtn);

                        optsListStack.Children.Add(optRow);
                    }
                    editStack.Children.Add(optsListStack);
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

                // Optional Rich Content Editors (Code Snippet, Math LaTeX, Diagram)
                var richGrid = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
                    ColumnSpacing = 8,
                };
                var mathBox = new TextBox
                {
                    Text = capturedQ.MathLatex ?? string.Empty,
                    Watermark = "Optional Math LaTeX (e.g. \\frac{a+b}{n} or O(n \\log n))...",
                    FontSize = 12,
                };
                mathBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        capturedQ.MathLatex = string.IsNullOrWhiteSpace(mathBox.Text) ? null : mathBox.Text.Trim();
                };
                richGrid.Children.Add(mathBox);

                var diagBox = new TextBox
                {
                    Text = capturedQ.Diagram ?? string.Empty,
                    Watermark = "Optional Flow Diagram (e.g. [Client] -> [API] -> [DB])...",
                    FontSize = 12,
                };
                diagBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        capturedQ.Diagram = string.IsNullOrWhiteSpace(diagBox.Text) ? null : diagBox.Text.Trim();
                };
                Grid.SetColumn(diagBox, 1);
                richGrid.Children.Add(diagBox);
                editStack.Children.Add(richGrid);

                var codeEditBox = new TextBox
                {
                    Text = capturedQ.CodeSnippet ?? string.Empty,
                    Watermark = "Optional Code Snippet block...",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    MinHeight = 46,
                    FontSize = 12,
                };
                codeEditBox.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        capturedQ.CodeSnippet = string.IsNullOrWhiteSpace(codeEditBox.Text) ? null : codeEditBox.Text;
                };
                editStack.Children.Add(codeEditBox);

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

        var bottomAddRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 8),
        };
        var bottomAddBtn = UiBuilder.ActionButton(
            $"Add {addKindTarget} Question",
            MaterialIconKind.Plus,
            () => studio.AddBlankOrganizerQuestion(addKindTarget),
            isPrimary: false
        );
        bottomAddRow.Children.Add(bottomAddBtn);
        stack.Children.Add(bottomAddRow);

        return UiBuilder.PersistentScrollViewer($"quiz-organizer:{deck.Id}", stack);
    }

}
