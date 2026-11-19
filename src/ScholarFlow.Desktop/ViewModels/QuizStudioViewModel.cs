using Avalonia.Media.Imaging;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Desktop.ViewModels;

public enum QuizStudioSubTab
{
    Builder,
    Catalog
}

/// <summary>
/// ViewModel for the Evidence-Grounded PDF Quiz &amp; Active Recall Studio.
/// Supports Google AI Studio (Gemini OpenAI-compatible API), Local Ollama, Manual Gemini Web JSON Import,
/// Offline Smart PDF Analysis, SM-2/FSRS Spaced Repetition, live visual PDF scope previewing,
/// and one-click jump to highlighted statements in the PDF Reader.
/// </summary>
public sealed class QuizStudioViewModel
{
    private readonly MainWindowViewModel _mainVm;
    private readonly QuizLlmService _llmService = new();
    private readonly Dictionary<string, (int TotalPages, IReadOnlyList<PdfOutlineSectionInfo> Sections)> _outlineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, int Page), Bitmap> _previewBitmapCache = [];
    private string? _cachedBytesPath;
    private byte[]? _cachedPdfBytes;

    public QuizStudioSubTab ActiveSubTab { get; private set; } = QuizStudioSubTab.Builder;
    public BasinCourseEntry? SelectedCourse { get; private set; }
    public IReadOnlyList<(string Label, string FullPath, bool IsMaster)> AvailablePdfs { get; private set; } = [];
    public bool ShowIndividualHandouts { get; private set; }
    public string? SelectedPdfPath { get; private set; }
    public int PdfTotalPages { get; private set; }
    public IReadOnlyList<PdfOutlineSectionInfo> OutlineSections { get; private set; } = [];
    public HashSet<string> SelectedSectionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SelectedTerm { get; private set; }
    public int? StartPage { get; set; }
    public int? EndPage { get; set; }
    public int QuestionCount { get; private set; } = 10;
    public QuizPresetMode SelectedPreset { get; private set; } = QuizPresetMode.ExamSimulator;

    public IReadOnlyList<(int PageNumber, string SectionTitle, string? Term)> MatchedScopePages { get; private set; } = [];
    public int PreviewScopeIndex { get; private set; }
    public int ScopePreviewPageCount => MatchedScopePages.Count;
    public int CurrentPreviewPdfPage =>
        MatchedScopePages.Count > 0
            ? MatchedScopePages[Math.Clamp(PreviewScopeIndex, 0, MatchedScopePages.Count - 1)].PageNumber
            : Math.Clamp(StartPage ?? 1, 1, Math.Max(1, PdfTotalPages));
    public string CurrentPreviewSectionTitle =>
        MatchedScopePages.Count > 0
            ? MatchedScopePages[Math.Clamp(PreviewScopeIndex, 0, MatchedScopePages.Count - 1)].SectionTitle
            : (string.IsNullOrWhiteSpace(SelectedPdfPath) ? "No PDF Selected" : Path.GetFileNameWithoutExtension(SelectedPdfPath));

    public bool CatalogShowAllCourses { get; private set; }
    public HashSet<string> SelectedQuizIdsForMerge { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAiSettingsOpen { get; private set; }
    public bool IsManualJsonModalOpen { get; private set; }
    public string ManualJsonInput { get; set; } = string.Empty;
    public string GeneratedPromptPreview { get; private set; } = string.Empty;

    public bool IsGenerating { get; private set; }
    public string StatusMessage { get; private set; } = "Select a course & PDF to generate an evidence-grounded assessment.";
    public bool IsStatusError { get; private set; }

    public IReadOnlyList<QuizSessionRecord> SavedQuizzes { get; private set; } = [];
    public IReadOnlyList<QuizQuestionRecord> AllDueReviewQuestions { get; private set; } = [];

    // Active Runner State
    public QuizSessionRecord? ActiveSession { get; private set; }
    public int CurrentQuestionIndex { get; private set; }
    public QuizQuestionRecord? CurrentQuestion =>
        ActiveSession is not null && CurrentQuestionIndex >= 0 && CurrentQuestionIndex < ActiveSession.Questions.Count
            ? ActiveSession.Questions[CurrentQuestionIndex]
            : null;

    public string CurrentAnswerDraft { get; set; } = string.Empty;
    public bool IsAnswerRevealed { get; private set; }
    public bool IsCurrentAnswerCorrect { get; private set; }
    public bool IsShowingResultsSummary { get; private set; }

    public Dictionary<string, (string Answer, bool IsCorrect)> SessionResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public QuizStudioViewModel(MainWindowViewModel mainVm)
    {
        _mainVm = mainVm;
    }

    public async Task RefreshAsync(BasinCourseEntry? preferredCourse = null, string? preferredPdfPath = null, int? focusPage = null)
    {
        var activeCourses = _mainVm.Courses.Where(c => !c.IsArchived).ToList();
        if (preferredCourse is not null)
        {
            SelectedCourse = preferredCourse;
        }
        else if (SelectedCourse is null || !activeCourses.Any(c => c.FolderName == SelectedCourse.FolderName))
        {
            SelectedCourse = _mainVm.ActiveCourse ?? activeCourses.FirstOrDefault();
        }

        RebuildAvailablePdfsForCourse(preferredPdfPath);

        if (focusPage.HasValue && focusPage.Value > 0)
        {
            StartPage = focusPage.Value;
            EndPage = Math.Min(Math.Max(focusPage.Value, PdfTotalPages), focusPage.Value + 4);
            SelectedSectionKeys.Clear();
            RecomputeScopePreview();
        }

        if (!string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
        {
            SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
            var today = DateOnly.FromDateTime(DateTime.Today);
            AllDueReviewQuestions = SavedQuizzes
                .SelectMany(q => q.Questions)
                .Where(qq => !qq.SrsDueDate.HasValue || qq.SrsDueDate.Value <= today || qq.LastAnsweredCorrect == false)
                .ToList();
        }
    }

    public void SetSubTab(QuizStudioSubTab tab)
    {
        ActiveSubTab = tab;
        _mainVm.RequestUiRefresh();
    }

    public void SelectCourseScope(BasinCourseEntry course)
    {
        SelectedCourse = course;
        SelectedPdfPath = null;
        StartPage = null;
        EndPage = null;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RebuildAvailablePdfsForCourse(null);
        _mainVm.RequestUiRefresh();
    }

    public void ToggleShowIndividualHandouts()
    {
        ShowIndividualHandouts = !ShowIndividualHandouts;
        _mainVm.RequestUiRefresh();
    }

    public void SelectPdfScope(string pdfPath)
    {
        SelectedPdfPath = pdfPath;
        StartPage = null;
        EndPage = null;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        InspectSelectedPdfOutlineAndPreview();
        _mainVm.RequestUiRefresh();
    }

    public void SelectTermScope(string? term)
    {
        SelectedTerm = string.Equals(SelectedTerm, term, StringComparison.OrdinalIgnoreCase) ? null : term;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public void SetPageRange(int? start, int? end)
    {
        StartPage = start is > 0 ? start : null;
        EndPage = end is > 0 ? end : null;
        if (StartPage.HasValue && EndPage.HasValue && EndPage.Value < StartPage.Value)
            EndPage = StartPage.Value;
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public static string GetSectionKey(PdfOutlineSectionInfo s) => $"{s.StartPage}-{s.EndPage}:{s.Title}";

    public void ToggleOutlineSection(PdfOutlineSectionInfo sec)
    {
        var key = GetSectionKey(sec);
        bool added = SelectedSectionKeys.Add(key);
        if (!added)
            SelectedSectionKeys.Remove(key);
        RecomputeScopePreview();
        if (added)
        {
            var idx = MatchedScopePages
                .Select((p, i) => (p.PageNumber, Index: i))
                .FirstOrDefault(x => x.PageNumber >= sec.StartPage).Index;
            PreviewScopeIndex = idx;
        }
        _mainVm.RequestUiRefresh();
    }

    public void JumpPreviewToSection(PdfOutlineSectionInfo sec)
    {
        var matchIdx = -1;
        for (int i = 0; i < MatchedScopePages.Count; i++)
        {
            if (MatchedScopePages[i].PageNumber >= sec.StartPage)
            {
                matchIdx = i;
                break;
            }
        }

        if (matchIdx >= 0)
        {
            PreviewScopeIndex = matchIdx;
            _mainVm.RequestUiRefresh();
        }
    }

    public void PreviousPreviewPage()
    {
        if (PreviewScopeIndex > 0)
        {
            PreviewScopeIndex--;
            _mainVm.RequestUiRefresh();
        }
    }

    public void NextPreviewPage()
    {
        if (PreviewScopeIndex + 1 < MatchedScopePages.Count)
        {
            PreviewScopeIndex++;
            _mainVm.RequestUiRefresh();
        }
    }

    public void ClearScopeFilters()
    {
        SelectedTerm = null;
        StartPage = null;
        EndPage = null;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public void SetQuestionCount(int count, bool notify = true)
    {
        QuestionCount = Math.Clamp(count, 1, 150);
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void ToggleCatalogShowAllCourses()
    {
        CatalogShowAllCourses = !CatalogShowAllCourses;
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
                combinedQuestions.Add(new QuizQuestionRecord
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
                    SourcePdfPath = orig.SourcePdfPath,
                    SourcePage = orig.SourcePage,
                    SectionTitle = orig.SectionTitle,
                    VerbatimQuote = orig.VerbatimQuote,
                    HighlightRects = [.. orig.HighlightRects],
                    SrsDueDate = orig.SrsDueDate ?? DateOnly.FromDateTime(DateTime.Today)
                });
            }
        }

        if (shuffleQuestions)
        {
            var rng = new Random();
            combinedQuestions = combinedQuestions.OrderBy(_ => rng.Next()).ToList();
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
            Questions = combinedQuestions
        };

        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, mergedSession);
        SelectedQuizIdsForMerge.Clear();
        await RefreshAsync(SelectedCourse, SelectedPdfPath);
        StatusMessage = $"Combined {selectedQuizzes.Count} quizzes into '{mergedSession.Title}' ({combinedQuestions.Count} questions).";
        IsStatusError = false;
        StartQuizSession(mergedSession);
    }

    public void SetPreset(QuizPresetMode preset)
    {
        SelectedPreset = preset;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleAiSettings()
    {
        IsAiSettingsOpen = !IsAiSettingsOpen;
        _mainVm.RequestUiRefresh();
    }

    public async Task ApplyAiProviderPresetAsync(string presetName)
    {
        var settings = _mainVm.Settings;
        settings.AiProviderPreset = presetName;
        switch (presetName)
        {
            case "GeminiAiStudio":
                settings.AiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                settings.AiModelName = "gemini-2.5-flash";
                break;
            case "OllamaLocal":
                settings.AiBaseUrl = "http://localhost:11434/v1/chat/completions";
                settings.AiModelName = "qwen2.5:7b";
                break;
            case "OfflineSmart":
                break;
        }

        await AppSettingsStore.SaveAsync(settings);
        StatusMessage = $"Switched AI generator preset to {presetName}.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task SaveAiCredentialsAsync(string baseUrl, string modelName, string? apiKey)
    {
        var settings = _mainVm.Settings;
        if (!string.IsNullOrWhiteSpace(baseUrl))
            settings.AiBaseUrl = baseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(modelName))
            settings.AiModelName = modelName.Trim();
        settings.AiApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        await AppSettingsStore.SaveAsync(settings);
        StatusMessage = "Saved AI endpoint & credentials.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void OpenManualJsonBridge()
    {
        var req = BuildCurrentRequest();
        if (string.IsNullOrWhiteSpace(req.SourcePdfPath) || !File.Exists(req.SourcePdfPath))
        {
            StatusMessage = "Please select a valid PDF file first.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        GeneratedPromptPreview = _llmService.BuildPromptForManualCopy(req, out var pages);
        IsManualJsonModalOpen = !IsManualJsonModalOpen;
        StatusMessage = $"Prepared grounded prompt across {pages.Count} PDF page(s) for Gemini Pro web.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task GenerateQuizNowAsync(bool forceOffline = false)
    {
        if (IsGenerating)
            return;

        var req = BuildCurrentRequest();
        if (forceOffline || string.Equals(_mainVm.Settings.AiProviderPreset, "OfflineSmart", StringComparison.OrdinalIgnoreCase))
            req.UseOfflineHeuristicFallback = true;

        if (string.IsNullOrWhiteSpace(req.SourcePdfPath) || !File.Exists(req.SourcePdfPath))
        {
            StatusMessage = "No valid PDF selected for quiz generation.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        IsGenerating = true;
        IsStatusError = false;
        StatusMessage = req.UseOfflineHeuristicFallback
            ? $"Analyzing {Path.GetFileName(req.SourcePdfPath)} and grounding statements..."
            : $"Requesting {req.QuestionCount} grounded questions from {req.ModelName}...";
        _mainVm.RequestUiRefresh();

        try
        {
            var session = await _llmService.GenerateQuizAsync(req);
            await RefreshAsync(SelectedCourse, SelectedPdfPath);
            StartQuizSession(session);
            StatusMessage = $"Generated '{session.Title}' ({session.Questions.Count} grounded questions).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Generation failed: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            IsGenerating = false;
            _mainVm.RequestUiRefresh();
        }
    }

    public async Task ImportPastedJsonQuizAsync(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            StatusMessage = "Paste the JSON output from Gemini Pro before importing.";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        var req = BuildCurrentRequest();
        try
        {
            var session = await _llmService.ImportQuizFromJsonAsync(req, rawJson);
            IsManualJsonModalOpen = false;
            ManualJsonInput = string.Empty;
            await RefreshAsync(SelectedCourse, SelectedPdfPath);
            StartQuizSession(session);
            StatusMessage = $"Imported and PDF-grounded {session.Questions.Count} questions from Gemini JSON.";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"JSON Import error: {ex.Message}";
            IsStatusError = true;
        }

        _mainVm.RequestUiRefresh();
    }

    public void StartQuizSession(QuizSessionRecord session)
    {
        ActiveSession = session;
        CurrentQuestionIndex = 0;
        CurrentAnswerDraft = string.Empty;
        IsAnswerRevealed = false;
        IsCurrentAnswerCorrect = false;
        IsShowingResultsSummary = false;
        SessionResults.Clear();
        _mainVm.RequestUiRefresh();
    }

    public void StartDueSpacedRepetitionDeck(string? courseFolderFilter = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dueQuestions = SavedQuizzes
            .Where(q => string.IsNullOrWhiteSpace(courseFolderFilter) || q.CourseFolder.Equals(courseFolderFilter, StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Questions)
            .Where(qq => !qq.SrsDueDate.HasValue || qq.SrsDueDate.Value <= today || qq.LastAnsweredCorrect == false)
            .OrderBy(qq => qq.LastAnsweredCorrect == false ? 0 : 1)
            .ThenBy(qq => qq.SrsDueDate ?? today)
            .Take(25)
            .ToList();

        if (dueQuestions.Count == 0)
        {
            StatusMessage = "No due spaced-repetition cards right now. Start any saved quiz below!";
            IsStatusError = false;
            _mainVm.RequestUiRefresh();
            return;
        }

        var deckSession = new QuizSessionRecord
        {
            Id = "srs-deck",
            CourseFolder = courseFolderFilter ?? (SelectedCourse?.FolderName ?? ""),
            CourseTitle = SelectedCourse?.Title ?? "All Courses",
            Title = $"Active Recall & Spaced Repetition Deck ({dueQuestions.Count} due)",
            SourcePdfPath = dueQuestions[0].SourcePdfPath,
            ModelUsed = "SM-2 / FSRS Scheduler",
            Questions = dueQuestions
        };

        StartQuizSession(deckSession);
    }

    public void ExitActiveSession()
    {
        ActiveSession = null;
        IsShowingResultsSummary = false;
        _mainVm.RequestUiRefresh();
    }

    public async Task SubmitAnswerAsync(string answer, int qualityRating = 4)
    {
        var q = CurrentQuestion;
        if (q is null)
            return;

        CurrentAnswerDraft = answer;
        bool isCorrect = q.EvaluateAnswer(answer);
        IsCurrentAnswerCorrect = isCorrect;
        IsAnswerRevealed = true;
        SessionResults[q.Id] = (answer, isCorrect);

        await ScholarFlowDatabase.RecordQuestionAttemptAsync(
            _mainVm.BasinRoot,
            q,
            answer,
            isCorrect,
            isCorrect ? qualityRating : 1);

        _mainVm.RequestUiRefresh();
    }

    public async Task RateAndAdvanceAsync(int qualityRating)
    {
        var q = CurrentQuestion;
        if (q is not null && IsAnswerRevealed)
        {
            await ScholarFlowDatabase.RecordQuestionAttemptAsync(
                _mainVm.BasinRoot,
                q,
                CurrentAnswerDraft,
                IsCurrentAnswerCorrect,
                qualityRating);
        }

        await AdvanceNextQuestionAsync();
    }

    public async Task AdvanceNextQuestionAsync()
    {
        if (ActiveSession is null)
            return;

        if (CurrentQuestionIndex + 1 < ActiveSession.Questions.Count)
        {
            CurrentQuestionIndex++;
            var nextQ = CurrentQuestion;
            if (nextQ is not null && SessionResults.TryGetValue(nextQ.Id, out var prev))
            {
                CurrentAnswerDraft = prev.Answer;
                IsCurrentAnswerCorrect = prev.IsCorrect;
                IsAnswerRevealed = true;
            }
            else
            {
                CurrentAnswerDraft = string.Empty;
                IsCurrentAnswerCorrect = false;
                IsAnswerRevealed = false;
            }
        }
        else
        {
            IsShowingResultsSummary = true;
            if (ActiveSession.Id != "srs-deck" && ActiveSession.Questions.Count > 0)
            {
                int correct = ActiveSession.Questions.Count(qq =>
                    SessionResults.TryGetValue(qq.Id, out var r) && r.IsCorrect);
                double pct = (100.0 * correct) / ActiveSession.Questions.Count;
                await ScholarFlowDatabase.UpdateQuizSessionScoreAsync(_mainVm.BasinRoot, ActiveSession, pct);
                SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
            }
        }

        _mainVm.RequestUiRefresh();
    }

    public void PreviousQuestion()
    {
        if (ActiveSession is null || CurrentQuestionIndex <= 0)
            return;

        CurrentQuestionIndex--;
        var q = CurrentQuestion;
        if (q is not null && SessionResults.TryGetValue(q.Id, out var prev))
        {
            CurrentAnswerDraft = prev.Answer;
            IsCurrentAnswerCorrect = prev.IsCorrect;
            IsAnswerRevealed = true;
        }
        else
        {
            CurrentAnswerDraft = string.Empty;
            IsAnswerRevealed = false;
        }
        _mainVm.RequestUiRefresh();
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

    public void OpenQuestionEvidenceInPdfReader(QuizQuestionRecord question)
    {
        if (string.IsNullOrWhiteSpace(question.SourcePdfPath) || !File.Exists(question.SourcePdfPath))
        {
            StatusMessage = $"Source PDF not found: {question.SourcePdfPath}";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        // Re-compute highlight rects on-the-fly if empty
        if (question.HighlightRects.Count == 0 && !string.IsNullOrWhiteSpace(question.VerbatimQuote))
        {
            PdfQuizGrounder.GroundQuestionsToPdf(question.SourcePdfPath, [question]);
        }

        _mainVm.OpenPdfWithQuizEvidence(
            question.SourcePdfPath,
            question.SourcePage,
            question.HighlightRects,
            question.VerbatimQuote,
            question.Prompt);
    }

    private List<PdfPageRange> GetSelectedPageRanges()
    {
        if (SelectedSectionKeys.Count == 0)
            return [];

        return OutlineSections
            .Where(s => SelectedSectionKeys.Contains(GetSectionKey(s)))
            .Select(s => new PdfPageRange(s.StartPage, s.EndPage))
            .ToList();
    }

    private QuizGenerationRequest BuildCurrentRequest()
    {
        var course = SelectedCourse ?? _mainVm.Courses.FirstOrDefault(c => !c.IsArchived);
        var settings = _mainVm.Settings;

        return new QuizGenerationRequest
        {
            BasinRoot = _mainVm.BasinRoot,
            CourseFolder = course?.FolderName ?? string.Empty,
            CourseTitle = course?.Title ?? "Course",
            Term = SelectedTerm,
            SourcePdfPath = SelectedPdfPath ?? string.Empty,
            StartPage = StartPage,
            EndPage = EndPage,
            SelectedRanges = GetSelectedPageRanges(),
            QuestionCount = QuestionCount,
            Preset = SelectedPreset,
            ProviderBaseUrl = settings.AiBaseUrl,
            ApiKey = settings.AiApiKey,
            ModelName = settings.AiModelName,
            UseOfflineHeuristicFallback = string.Equals(settings.AiProviderPreset, "OfflineSmart", StringComparison.OrdinalIgnoreCase)
        };
    }

    public Bitmap? GetCurrentPreviewBitmap()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath) || !File.Exists(SelectedPdfPath) || PdfTotalPages <= 0)
            return null;

        int pageOneBased = Math.Clamp(CurrentPreviewPdfPage, 1, PdfTotalPages);
        var key = (SelectedPdfPath, pageOneBased);
        if (_previewBitmapCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            if (!string.Equals(_cachedBytesPath, SelectedPdfPath, StringComparison.OrdinalIgnoreCase) || _cachedPdfBytes is null)
            {
                _cachedPdfBytes = File.ReadAllBytes(SelectedPdfPath);
                _cachedBytesPath = SelectedPdfPath;
            }

            using var ms = new MemoryStream();
#pragma warning disable CA1416
            var renderOpts = new PDFtoImage.RenderOptions(
                Dpi: 135,
                BackgroundColor: SkiaSharp.SKColors.White);
            PDFtoImage.Conversion.SavePng(ms, _cachedPdfBytes, page: (Index)(pageOneBased - 1), options: renderOpts);
#pragma warning restore CA1416
            ms.Position = 0;
            var bmp = new Bitmap(ms);

            if (_previewBitmapCache.Count >= 18)
            {
                var firstKey = _previewBitmapCache.Keys.First();
                _previewBitmapCache[firstKey].Dispose();
                _previewBitmapCache.Remove(firstKey);
            }

            _previewBitmapCache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void RebuildAvailablePdfsForCourse(string? preferredPdfPath)
    {
        var list = new List<(string Label, string FullPath, bool IsMaster)>();
        if (SelectedCourse is not null && !string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
        {
            var courseDir = Path.Combine(_mainVm.BasinRoot, SelectedCourse.FolderName);
            var masterPdf = MasterPdfService.FindExistingMasterPdfPath(_mainVm.BasinRoot, SelectedCourse.FolderName);
            if (masterPdf is not null && File.Exists(masterPdf))
            {
                list.Add(($"Master Study Packet ({Path.GetFileName(masterPdf)})", masterPdf, true));
            }

            if (Directory.Exists(courseDir))
            {
                foreach (var dir in Directory.GetDirectories(courseDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith('.') ||
                        dirName.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("ongoing", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var pdf in Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var fileName = Path.GetFileName(pdf);
                        if (fileName.EndsWith("_.pdf", StringComparison.OrdinalIgnoreCase))
                            continue;
                        list.Add((dirName.Replace('_', ' '), pdf, false));
                    }
                }
            }
        }

        AvailablePdfs = list;
        if (!string.IsNullOrWhiteSpace(preferredPdfPath) && File.Exists(preferredPdfPath))
        {
            if (!AvailablePdfs.Any(p => p.FullPath.Equals(preferredPdfPath, StringComparison.OrdinalIgnoreCase)))
            {
                list.Insert(0, (Path.GetFileName(preferredPdfPath), preferredPdfPath, false));
                AvailablePdfs = list;
            }
            SelectedPdfPath = preferredPdfPath;
        }
        else if (SelectedPdfPath is null || !AvailablePdfs.Any(p => p.FullPath.Equals(SelectedPdfPath, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPdfPath = AvailablePdfs.FirstOrDefault(p => p.IsMaster).FullPath
                              ?? AvailablePdfs.FirstOrDefault().FullPath;
        }

        InspectSelectedPdfOutlineAndPreview();
    }

    private void InspectSelectedPdfOutlineAndPreview()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath) || !File.Exists(SelectedPdfPath))
        {
            PdfTotalPages = 0;
            OutlineSections = [];
            MatchedScopePages = [];
            PreviewScopeIndex = 0;
            return;
        }

        if (_outlineCache.TryGetValue(SelectedPdfPath, out var cached))
        {
            PdfTotalPages = cached.TotalPages;
            OutlineSections = cached.Sections;
        }
        else
        {
            OutlineSections = PdfQuizGrounder.InspectOutlineSections(SelectedPdfPath, out int totalPages);
            PdfTotalPages = totalPages;
            _outlineCache[SelectedPdfPath] = (totalPages, OutlineSections);
        }

        RecomputeScopePreview();
    }

    private void RecomputeScopePreview()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath) || !File.Exists(SelectedPdfPath) || PdfTotalPages <= 0)
        {
            MatchedScopePages = [];
            PreviewScopeIndex = 0;
            return;
        }

        MatchedScopePages = PdfQuizGrounder.ResolveScopePagesFast(
            PdfTotalPages,
            OutlineSections,
            SelectedTerm,
            StartPage,
            EndPage,
            GetSelectedPageRanges());

        if (PreviewScopeIndex >= MatchedScopePages.Count)
            PreviewScopeIndex = 0;
    }
}
