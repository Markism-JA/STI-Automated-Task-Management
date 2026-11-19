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

        if (quiz.HasInProgressAttempt)
        {
            int resumeQ = Math.Clamp((quiz.InProgressQuestionIndex ?? 0) + 1, 1, quiz.Questions.Count);
            actions.Children.Add(
                UiBuilder.PrimaryButton(
                    $"Continue (Q{resumeQ}/{quiz.Questions.Count})",
                    MaterialIconKind.PlayOutline,
                    () => studio.StartQuizSession(quiz, resumeSavedProgress: true)
                )
            );
            actions.Children.Add(
                UiBuilder.GhostButton(
                    "Restart",
                    MaterialIconKind.Restart,
                    () => studio.StartQuizSession(quiz, resumeSavedProgress: false)
                )
            );
        }
        else
        {
            actions.Children.Add(
                UiBuilder.GhostButton(
                    "Start",
                    MaterialIconKind.PlayOutline,
                    () => studio.StartQuizSession(quiz, resumeSavedProgress: false),
                    isActive: true
                )
            );
        }

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

}
