using ScholarFlow.Core.Database;
using ScholarFlow.Core.Quiz;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class QuizStudioViewModel
{
    public void ToggleCatalogShowAllCourses()
    {
        CatalogShowAllCourses = !CatalogShowAllCourses;
        _mainVm.RequestUiRefresh();
    }

    public void SetCatalogTermFilter(string? term)
    {
        CatalogTermFilter = string.Equals(
            CatalogTermFilter,
            term,
            StringComparison.OrdinalIgnoreCase
        )
            ? null
            : term;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleShuffleQuestionsOnStart()
    {
        ShuffleQuestionsOnStart = !ShuffleQuestionsOnStart;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleShuffleOptionsOnStart()
    {
        ShuffleOptionsOnStart = !ShuffleOptionsOnStart;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleQuizSelectionForMerge(string quizId)
    {
        if (!SelectedQuizIdsForMerge.Add(quizId))
            SelectedQuizIdsForMerge.Remove(quizId);
        _mainVm.RequestUiRefresh();
    }

    public async Task CombineSelectedQuizzesAsync(string? customTitle, bool shuffleQuestions)
    {
        var selectedQuizzes = SavedQuizzes
            .Where(q => SelectedQuizIdsForMerge.Contains(q.Id))
            .ToList();

        if (selectedQuizzes.Count < 2)
        {
            StatusMessage = "Select at least 2 saved quizzes in the catalog to combine.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        var combinedQuestions = new List<QuizQuestionRecord>();
        foreach (var qz in selectedQuizzes)
        {
            foreach (var orig in qz.Questions)
            {
                combinedQuestions.Add(
                    new QuizQuestionRecord
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Kind = orig.Kind,
                        CognitiveLevel = orig.CognitiveLevel,
                        Difficulty = orig.Difficulty,
                        TopicTag = orig.TopicTag,
                        Prompt = orig.Prompt,
                        Options = [.. orig.Options],
                        CorrectAnswer = orig.CorrectAnswer,
                        AcceptedAliases = [.. orig.AcceptedAliases],
                        Explanation = orig.Explanation,
                        CodeSnippet = orig.CodeSnippet,
                        CodeLanguage = orig.CodeLanguage,
                        MathLatex = orig.MathLatex,
                        Diagram = orig.Diagram,
                        SourcePdfPath = orig.SourcePdfPath,
                        SourcePage = orig.SourcePage,
                        SectionTitle = orig.SectionTitle,
                        VerbatimQuote = orig.VerbatimQuote,
                        HighlightRects = [.. orig.HighlightRects],
                        SrsDueDate = orig.SrsDueDate ?? DateOnly.FromDateTime(DateTime.Today),
                    }
                );
            }
        }

        if (shuffleQuestions)
        {
            var rng = new Random();
            combinedQuestions = [.. combinedQuestions.OrderBy(_ => rng.Next())];
        }

        var first = selectedQuizzes[0];
        var distinctTerms = selectedQuizzes
            .Select(q => q.Term)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mergedSession = new QuizSessionRecord
        {
            CourseFolder = selectedQuizzes.All(q => q.CourseFolder == first.CourseFolder)
                ? first.CourseFolder
                : (SelectedCourse?.FolderName ?? first.CourseFolder),
            CourseTitle = selectedQuizzes.All(q => q.CourseTitle == first.CourseTitle)
                ? first.CourseTitle
                : "Combined Multi-Subject Assessment",
            Term = distinctTerms.Count == 1 ? distinctTerms[0] : "Combined",
            Title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle.Trim()
                : $"Combined Exam ({string.Join(" + ", selectedQuizzes.Select(q => q.Term ?? q.Title).Take(3))})",
            SourcePdfPath = first.SourcePdfPath,
            ModelUsed = "Combined Catalog",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Questions = combinedQuestions,
        };

        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, mergedSession);
        SelectedQuizIdsForMerge.Clear();
        await RefreshAsync(SelectedCourse, SelectedPdfPath);
        StatusMessage =
            $"Combined {selectedQuizzes.Count} quizzes into '{mergedSession.Title}' ({combinedQuestions.Count} questions).";
        IsStatusError = false;
        StartQuizSession(mergedSession);
    }

    public async Task DeleteQuizAsync(QuizSessionRecord quiz)
    {
        await ScholarFlowDatabase.DeleteQuizSessionAsync(_mainVm.BasinRoot, quiz.Id);
        if (ActiveSession?.Id == quiz.Id)
            ActiveSession = null;
        await RefreshAsync(SelectedCourse, SelectedPdfPath);
        StatusMessage = $"Deleted quiz '{quiz.Title}'.";
        _mainVm.RequestUiRefresh();
    }

    public async Task RegroundQuizAsync(QuizSessionRecord quiz)
    {
        if (string.IsNullOrWhiteSpace(quiz.SourcePdfPath) || !File.Exists(quiz.SourcePdfPath))
        {
            StatusMessage = $"Cannot re-ground: PDF not found at {quiz.SourcePdfPath}";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        IsGenerating = true;
        IsStatusError = false;
        StatusMessage =
            $"Re-grounding '{quiz.Title}' against {Path.GetFileName(quiz.SourcePdfPath)}...";
        _mainVm.RequestUiRefresh();
        await Task.Yield();

        try
        {
            int matched = await Task.Run(() => PdfQuizGrounder.RegroundSessionToPdf(quiz));
            await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, quiz);
            SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
            if (OrganizerQuiz?.Id == quiz.Id)
                OrganizerQuiz = SavedQuizzes.FirstOrDefault(x => x.Id == quiz.Id) ?? quiz;

            StatusMessage =
                $"Re-grounded '{quiz.Title}' against updated PDF ({matched}/{quiz.Questions.Count} statements anchored).";
            IsStatusError = false;
        }
        finally
        {
            IsGenerating = false;
            _mainVm.RequestUiRefresh();
        }
    }

    // =========================================================================
    // DECK ORGANIZER (SINGLE & MULTI-QUIZ QUESTION REORGANIZATION)
    // =========================================================================

    public void OpenQuizInOrganizer(QuizSessionRecord quiz)
    {
        OrganizerQuiz = new QuizSessionRecord
        {
            Id = quiz.Id,
            CourseFolder = quiz.CourseFolder,
            CourseTitle = quiz.CourseTitle,
            Term = quiz.Term,
            Title = quiz.Title,
            SourcePdfPath = quiz.SourcePdfPath,
            SourcePdfMtimeTicks = quiz.SourcePdfMtimeTicks,
            ModelUsed = quiz.ModelUsed,
            CreatedAtUtc = quiz.CreatedAtUtc,
            AttemptCount = quiz.AttemptCount,
            LastScorePercent = quiz.LastScorePercent,
            Questions = [.. quiz.Questions.Select(CloneQuestionForEdit)],
        };
        OrganizerKindFilter = null;
        EditingQuestionId = null;
        ActiveSubTab = QuizStudioSubTab.Organizer;
        StatusMessage =
            $"Organizing '{quiz.Title}' ({quiz.Questions.Count} items). Reorder, filter by kind, edit, or shuffle.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void OpenSelectedQuizzesInOrganizer(string? customTitle = null)
    {
        var selected = SavedQuizzes.Where(q => SelectedQuizIdsForMerge.Contains(q.Id)).ToList();
        if (selected.Count == 0)
        {
            StatusMessage =
                "Select 1 or more quizzes in the catalog to open in the Deck Organizer.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        if (selected.Count == 1)
        {
            OpenQuizInOrganizer(selected[0]);
            return;
        }

        var first = selected[0];
        var distinctTerms = selected
            .Select(q => q.Term)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mergedQuestions = new List<QuizQuestionRecord>();
        foreach (var qz in selected)
        {
            foreach (var orig in qz.Questions)
            {
                var copy = CloneQuestionForEdit(orig);
                copy.Id = Guid.NewGuid().ToString("N")[..12];
                copy.OrderIndex = mergedQuestions.Count;
                mergedQuestions.Add(copy);
            }
        }

        OrganizerQuiz = new QuizSessionRecord
        {
            CourseFolder = selected.All(q => q.CourseFolder == first.CourseFolder)
                ? first.CourseFolder
                : (SelectedCourse?.FolderName ?? first.CourseFolder),
            CourseTitle = selected.All(q => q.CourseTitle == first.CourseTitle)
                ? first.CourseTitle
                : "Combined Multi-Subject Assessment",
            Term = distinctTerms.Count == 1 ? distinctTerms[0] : "Combined",
            Title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle.Trim()
                : $"Combined Deck ({string.Join(" + ", selected.Select(q => q.Term ?? q.Title).Take(3))})",
            SourcePdfPath = first.SourcePdfPath,
            SourcePdfMtimeTicks = first.SourcePdfMtimeTicks,
            ModelUsed = "Curated Multi-Deck",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Questions = mergedQuestions,
        };
        OrganizerKindFilter = null;
        EditingQuestionId = null;
        ActiveSubTab = QuizStudioSubTab.Organizer;
        StatusMessage =
            $"Curating combined deck from {selected.Count} quizzes ({mergedQuestions.Count} items). Customize and click Save Deck.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    private static QuizQuestionRecord CloneQuestionForEdit(QuizQuestionRecord orig) =>
        new()
        {
            Id = orig.Id,
            QuizId = orig.QuizId,
            OrderIndex = orig.OrderIndex,
            Kind = orig.Kind,
            CognitiveLevel = orig.CognitiveLevel,
            Difficulty = orig.Difficulty,
            TopicTag = orig.TopicTag,
            Prompt = orig.Prompt,
            Options = [.. orig.Options],
            CorrectAnswer = orig.CorrectAnswer,
            AcceptedAliases = [.. orig.AcceptedAliases],
            Explanation = orig.Explanation,
            CodeSnippet = orig.CodeSnippet,
            CodeLanguage = orig.CodeLanguage,
            MathLatex = orig.MathLatex,
            Diagram = orig.Diagram,
            SourcePdfPath = orig.SourcePdfPath,
            SourcePage = orig.SourcePage,
            SectionTitle = orig.SectionTitle,
            VerbatimQuote = orig.VerbatimQuote,
            HighlightRects = [.. orig.HighlightRects],
            SrsIntervalDays = orig.SrsIntervalDays,
            SrsEaseFactor = orig.SrsEaseFactor,
            SrsDueDate = orig.SrsDueDate,
            SrsReps = orig.SrsReps,
            SrsLapses = orig.SrsLapses,
            LastAnsweredCorrect = orig.LastAnsweredCorrect,
        };

    public void SetOrganizerKindFilter(QuizQuestionKind? kind)
    {
        OrganizerKindFilter = OrganizerKindFilter == kind ? null : kind;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleEditQuestion(string questionId)
    {
        EditingQuestionId = string.Equals(
            EditingQuestionId,
            questionId,
            StringComparison.OrdinalIgnoreCase
        )
            ? null
            : questionId;
        _mainVm.RequestUiRefresh();
    }

    public void MoveOrganizerQuestion(QuizQuestionRecord q, int delta)
    {
        if (OrganizerQuiz is null)
            return;
        int idx = OrganizerQuiz.Questions.IndexOf(q);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= OrganizerQuiz.Questions.Count)
            return;

        (OrganizerQuiz.Questions[idx], OrganizerQuiz.Questions[target]) = (
            OrganizerQuiz.Questions[target],
            OrganizerQuiz.Questions[idx]
        );
        ReindexOrganizerQuestions();
        _mainVm.RequestUiRefresh();
    }

    public void ShuffleOrganizerQuestions(QuizQuestionKind? onlyKind = null)
    {
        if (OrganizerQuiz is null || OrganizerQuiz.Questions.Count <= 1)
            return;
        var rng = new Random();

        if (onlyKind.HasValue)
        {
            var indices = new List<int>();
            var items = new List<QuizQuestionRecord>();
            for (int i = 0; i < OrganizerQuiz.Questions.Count; i++)
            {
                if (OrganizerQuiz.Questions[i].Kind == onlyKind.Value)
                {
                    indices.Add(i);
                    items.Add(OrganizerQuiz.Questions[i]);
                }
            }
            var shuffled = items.OrderBy(_ => rng.Next()).ToList();
            for (int i = 0; i < indices.Count; i++)
                OrganizerQuiz.Questions[indices[i]] = shuffled[i];
            StatusMessage = $"Shuffled {items.Count} {onlyKind.Value} question(s) in place.";
        }
        else
        {
            OrganizerQuiz.Questions = [.. OrganizerQuiz.Questions.OrderBy(_ => rng.Next())];
            StatusMessage = $"Shuffled all {OrganizerQuiz.Questions.Count} questions in deck.";
        }

        ReindexOrganizerQuestions();
        _mainVm.RequestUiRefresh();
    }

    public void SortOrganizerQuestionsByKind()
    {
        if (OrganizerQuiz is null)
            return;
        OrganizerQuiz.Questions =
        [
            .. OrganizerQuiz.Questions.OrderBy(q => (int)q.Kind).ThenBy(q => q.SourcePage),
        ];
        ReindexOrganizerQuestions();
        StatusMessage = "Grouped and ordered deck questions by Question Kind.";
        _mainVm.RequestUiRefresh();
    }

    public void SortOrganizerQuestionsByPage()
    {
        if (OrganizerQuiz is null)
            return;
        OrganizerQuiz.Questions =
        [
            .. OrganizerQuiz.Questions.OrderBy(q => q.SourcePage).ThenBy(q => q.OrderIndex),
        ];
        ReindexOrganizerQuestions();
        StatusMessage = "Ordered deck questions chronologically by PDF Page.";
        _mainVm.RequestUiRefresh();
    }

    public void DeleteOrganizerQuestion(QuizQuestionRecord q)
    {
        if (OrganizerQuiz is null)
            return;
        OrganizerQuiz.Questions.Remove(q);
        ReindexOrganizerQuestions();
        if (EditingQuestionId == q.Id)
            EditingQuestionId = null;
        _mainVm.RequestUiRefresh();
    }

    public void AddBlankOrganizerQuestion(QuizQuestionKind kind)
    {
        if (OrganizerQuiz is null)
            return;
        var q = new QuizQuestionRecord
        {
            QuizId = OrganizerQuiz.Id,
            OrderIndex = OrganizerQuiz.Questions.Count,
            Kind = kind,
            Prompt =
                kind == QuizQuestionKind.FillInTheBlank
                    ? "Enter statement with _____ for the missing concept..."
                    : "Enter question prompt...",
            Options = kind switch
            {
                QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational =>
                [
                    "Option A",
                    "Option B",
                    "Option C",
                    "Option D",
                ],
                QuizQuestionKind.TrueFalse => ["True", "False"],
                QuizQuestionKind.Ordering => ["Step 1", "Step 2", "Step 3"],
                _ => [],
            },
            CorrectAnswer = kind switch
            {
                QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational => "Option A",
                QuizQuestionKind.TrueFalse => "True",
                QuizQuestionKind.Ordering => "Step 1 -> Step 2 -> Step 3",
                _ => "Answer",
            },
            SourcePdfPath = OrganizerQuiz.SourcePdfPath,
            SourcePage = 1,
            SrsDueDate = DateOnly.FromDateTime(DateTime.Today),
        };
        OrganizerQuiz.Questions.Add(q);
        EditingQuestionId = q.Id;
        _mainVm.RequestUiRefresh();
    }

    public async Task SaveOrganizerDeckAsync()
    {
        if (OrganizerQuiz is null)
            return;
        ReindexOrganizerQuestions();
        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, OrganizerQuiz);
        SelectedQuizIdsForMerge.Clear();
        SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
        StatusMessage =
            $"Saved deck '{OrganizerQuiz.Title}' ({OrganizerQuiz.Questions.Count} questions).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    private void ReindexOrganizerQuestions()
    {
        if (OrganizerQuiz is null)
            return;
        for (int i = 0; i < OrganizerQuiz.Questions.Count; i++)
        {
            OrganizerQuiz.Questions[i].QuizId = OrganizerQuiz.Id;
            OrganizerQuiz.Questions[i].OrderIndex = i;
        }
    }

    public async Task ExportQuizToJsonFileAsync(QuizSessionRecord quiz, string filePath)
    {
        var json = QuizExportService.ExportDeckToJson(quiz);
        await File.WriteAllTextAsync(filePath, json);
        StatusMessage = $"Exported JSON deck to {Path.GetFileName(filePath)}.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task ExportQuizToPdfFileAsync(QuizSessionRecord quiz, string filePath)
    {
        await Task.Run(() => QuizExportService.ExportDeckToPrintablePdf(quiz, filePath));
        StatusMessage = $"Exported printable PDF to {Path.GetFileName(filePath)}.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task ImportDeckJsonToSelectedCourseAsync(string rawJson)
    {
        var course = SelectedCourse ?? _mainVm.Courses.FirstOrDefault(c => !c.IsArchived);
        var courseFolder = course?.FolderName ?? string.Empty;
        var courseTitle = course?.Title ?? "Imported Course";

        var imported = await Task.Run(() =>
            QuizExportService.ImportDeckFromJson(
                rawJson,
                courseFolder,
                courseTitle,
                SelectedPdfPath
            )
        );

        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, imported);
        SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
        StatusMessage =
            $"Imported '{imported.Title}' ({imported.Questions.Count} questions) into {courseTitle}.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleFlagQuestionForAiFix(string questionId)
    {
        if (string.Equals(FlaggingQuestionId, questionId, StringComparison.OrdinalIgnoreCase))
        {
            FlaggingQuestionId = null;
            FlagProblemDescription = string.Empty;
            FlagFixPromptPreview = string.Empty;
            FlagFixJsonInput = string.Empty;
        }
        else
        {
            FlaggingQuestionId = questionId;
            EditingQuestionId = null;
            FlagProblemDescription = string.Empty;
            FlagFixPromptPreview = string.Empty;
            FlagFixJsonInput = string.Empty;
        }
        _mainVm.RequestUiRefresh();
    }

    public string BuildQuestionAiFixPrompt(QuizQuestionRecord question, string problemDescription)
    {
        FlagProblemDescription = problemDescription;
        var deck =
            OrganizerQuiz
            ?? new QuizSessionRecord
            {
                Title = "Quiz Deck",
                CourseTitle = SelectedCourse?.Title ?? "Course",
            };
        FlagFixPromptPreview = QuizExportService.BuildSingleQuestionFixPrompt(
            deck,
            question,
            problemDescription
        );
        _mainVm.RequestUiRefresh();
        return FlagFixPromptPreview;
    }

    public async Task ApplyQuestionAiFixAsync(QuizQuestionRecord question, string rawJson)
    {
        try
        {
            await Task.Run(() => QuizExportService.ApplySingleQuestionFixJson(question, rawJson));
            if (OrganizerQuiz is not null)
                await SaveOrganizerDeckAsync();
            FlaggingQuestionId = null;
            FlagFixPromptPreview = string.Empty;
            FlagFixJsonInput = string.Empty;
            StatusMessage = "Applied AI repair to question and re-grounded PDF evidence.";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Fix JSON error: {ex.Message}";
            IsStatusError = true;
        }
        _mainVm.RequestUiRefresh();
    }
}
