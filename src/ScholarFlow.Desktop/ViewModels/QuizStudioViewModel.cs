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
    Catalog,
    Organizer
}

public enum QuizBuilderOutputMode
{
    AiApi,
    WebJson,
    OfflineSmart
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
    public QuestionAllocationMode AllocationMode { get; private set; } = QuestionAllocationMode.Percentages;
    public Dictionary<QuizQuestionKind, int> KindAllocations { get; private set; } =
        QuizGenerationRequest.GetDefaultAllocationsForPreset(QuizPresetMode.ExamSimulator, QuestionAllocationMode.Percentages);
    public QuizBuilderOutputMode BuilderOutputMode { get; private set; } = QuizBuilderOutputMode.AiApi;
    public string? AppendTargetQuizId { get; private set; }
    public string? AppendTargetQuizTitle { get; private set; }

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
    public string? CatalogTermFilter { get; private set; }
    public HashSet<string> SelectedQuizIdsForMerge { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShuffleQuestionsOnStart { get; private set; }
    public bool ShuffleOptionsOnStart { get; private set; } = true;

    // Deck Organizer State (Single & Multi-Quiz Reorganization)
    public QuizSessionRecord? OrganizerQuiz { get; private set; }
    public QuizQuestionKind? OrganizerKindFilter { get; private set; }
    public string? EditingQuestionId { get; private set; }

    public bool IsAiSettingsOpen { get; private set; }
    public bool IsManualJsonModalOpen { get; private set; }
    public string ManualJsonInput { get; set; } = string.Empty;
    public string GeneratedPromptPreview { get; private set; } = string.Empty;

    public bool IsGenerating { get; private set; }
    public bool IsInspectingPdf { get; private set; }
    public bool IsBuildingPrompt { get; private set; }
    public bool IsLoadingPreviewPage { get; private set; }
    public bool IsBusy => IsGenerating || IsInspectingPdf || IsBuildingPrompt;
    private CancellationTokenSource? _promptBuildCts;
    private readonly HashSet<(string Path, int Page)> _pendingPreviewRenders = [];
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
    public Dictionary<string, List<string>> OrderingDrafts { get; } = new(StringComparer.OrdinalIgnoreCase);

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
        if (AllocationMode == QuestionAllocationMode.ExactCounts)
        {
            var scaled = BuildCurrentRequest().ResolveExactKindCounts(QuestionCount);
            KindAllocations = scaled;
        }
        RefreshLivePromptPreviewIfActive();
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void SetAllocationMode(QuestionAllocationMode mode)
    {
        if (AllocationMode == mode)
            return;

        if (mode == QuestionAllocationMode.ExactCounts)
        {
            var resolved = BuildCurrentRequest().ResolveExactKindCounts(QuestionCount);
            AllocationMode = QuestionAllocationMode.ExactCounts;
            KindAllocations = resolved;
            QuestionCount = Math.Clamp(KindAllocations.Values.Sum(), 1, 150);
        }
        else
        {
            int sum = Math.Max(1, KindAllocations.Values.Sum());
            var pcts = new Dictionary<QuizQuestionKind, int>();
            foreach (var k in Enum.GetValues<QuizQuestionKind>())
            {
                pcts[k] = (int)Math.Round(100.0 * KindAllocations.GetValueOrDefault(k, 0) / sum);
            }
            AllocationMode = QuestionAllocationMode.Percentages;
            KindAllocations = pcts;
        }

        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void SetKindAllocation(QuizQuestionKind kind, int value, bool notify = true)
    {
        int clamped = Math.Clamp(value, 0, 150);
        KindAllocations[kind] = clamped;
        SelectedPreset = QuizPresetMode.Custom;

        if (AllocationMode == QuestionAllocationMode.ExactCounts)
        {
            int sum = KindAllocations.Values.Sum();
            if (sum > 0)
                QuestionCount = Math.Clamp(sum, 1, 150);
        }

        RefreshLivePromptPreviewIfActive();
        if (notify)
            _mainVm.RequestUiRefresh();
    }

    public void StepKindAllocation(QuizQuestionKind kind, int delta)
    {
        int step = AllocationMode == QuestionAllocationMode.Percentages ? (delta * 5) : delta;
        int current = KindAllocations.GetValueOrDefault(kind, 0);
        SetKindAllocation(kind, Math.Max(0, current + step), notify: true);
    }

    public Dictionary<QuizQuestionKind, int> GetResolvedExactKindCounts()
    {
        return BuildCurrentRequest().ResolveExactKindCounts(QuestionCount);
    }

    public void SetBuilderOutputMode(QuizBuilderOutputMode mode)
    {
        BuilderOutputMode = mode;
        if (mode == QuizBuilderOutputMode.WebJson)
            RefreshLivePromptPreviewIfActive(force: true);
        _mainVm.RequestUiRefresh();
    }

    public void RefreshLivePromptPreviewIfActive(bool force = false)
    {
        if (!force && !IsManualJsonModalOpen && BuilderOutputMode != QuizBuilderOutputMode.WebJson)
            return;

        var req = BuildCurrentRequest();
        if (string.IsNullOrWhiteSpace(req.SourcePdfPath) || !File.Exists(req.SourcePdfPath))
        {
            GeneratedPromptPreview = string.Empty;
            return;
        }

        _ = RebuildPromptPreviewAsync(req, updateStatus: force);
    }

    private async Task RebuildPromptPreviewAsync(QuizGenerationRequest req, bool updateStatus)
    {
        _promptBuildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _promptBuildCts = cts;
        var token = cts.Token;

        IsBuildingPrompt = true;
        try
        {
            var (prompt, pageCount) = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var text = _llmService.BuildPromptForManualCopy(req, out var pages);
                return (text, pages.Count);
            }, token);

            if (token.IsCancellationRequested)
                return;

            GeneratedPromptPreview = prompt;
            if (updateStatus)
            {
                StatusMessage = $"Live grounded prompt ready across {pageCount} PDF page(s) ({req.QuestionCount} items).";
                IsStatusError = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scope/allocation change
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusMessage = $"Prompt preview error: {ex.Message}";
                IsStatusError = true;
            }
        }
        finally
        {
            if (_promptBuildCts == cts)
            {
                IsBuildingPrompt = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _mainVm.RequestUiRefresh());
            }
        }
    }

    public void StartAppendToQuiz(QuizSessionRecord quiz)
    {
        AppendTargetQuizId = quiz.Id;
        AppendTargetQuizTitle = quiz.Title;
        var course = _mainVm.Courses.FirstOrDefault(c => string.Equals(c.FolderName, quiz.CourseFolder, StringComparison.OrdinalIgnoreCase));
        if (course is not null)
            SelectedCourse = course;
        RebuildAvailablePdfsForCourse(quiz.SourcePdfPath);
        if (!string.IsNullOrWhiteSpace(quiz.Term))
            SelectedTerm = quiz.Term;
        RecomputeScopePreview();
        ActiveSubTab = QuizStudioSubTab.Builder;
        StatusMessage = $"Appending new non-duplicate questions to '{quiz.Title}'. Configure count/types and generate.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void ClearAppendTarget()
    {
        AppendTargetQuizId = null;
        AppendTargetQuizTitle = null;
        RefreshLivePromptPreviewIfActive();
        _mainVm.RequestUiRefresh();
    }

    public void ToggleCatalogShowAllCourses()
    {
        CatalogShowAllCourses = !CatalogShowAllCourses;
        _mainVm.RequestUiRefresh();
    }

    public void SetCatalogTermFilter(string? term)
    {
        CatalogTermFilter = string.Equals(CatalogTermFilter, term, StringComparison.OrdinalIgnoreCase) ? null : term;
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
        if (preset != QuizPresetMode.Custom)
        {
            KindAllocations = QuizGenerationRequest.GetDefaultAllocationsForPreset(preset, AllocationMode);
            if (AllocationMode == QuestionAllocationMode.ExactCounts)
                QuestionCount = Math.Clamp(KindAllocations.Values.Sum(), 1, 150);
        }
        RefreshLivePromptPreviewIfActive();
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

        IsManualJsonModalOpen = !IsManualJsonModalOpen;
        if (IsManualJsonModalOpen)
        {
            StatusMessage = "Building grounded prompt from selected PDF scope...";
            IsStatusError = false;
            _ = RebuildPromptPreviewAsync(req, updateStatus: true);
        }
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
        await Task.Yield();

        try
        {
            var session = await _llmService.GenerateQuizAsync(req);
            AppendTargetQuizId = null;
            AppendTargetQuizTitle = null;
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
        IsGenerating = true;
        IsStatusError = false;
        StatusMessage = "Parsing JSON and grounding quotes to PDF pages...";
        _mainVm.RequestUiRefresh();
        await Task.Yield();

        try
        {
            var session = await _llmService.ImportQuizFromJsonAsync(req, rawJson);
            AppendTargetQuizId = null;
            AppendTargetQuizTitle = null;
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
        finally
        {
            IsGenerating = false;
        }

        _mainVm.RequestUiRefresh();
    }

    public void StartQuizSession(
        QuizSessionRecord session,
        bool? shuffleQuestionsOverride = null,
        bool? shuffleOptionsOverride = null,
        bool missedOnly = false)
    {
        bool doShuffleQ = shuffleQuestionsOverride ?? ShuffleQuestionsOnStart;
        bool doShuffleOpt = shuffleOptionsOverride ?? ShuffleOptionsOnStart;
        var rng = new Random();

        var sourceQuestions = missedOnly
            ? session.Questions.Where(q => q.LastAnsweredCorrect == false).ToList()
            : [.. session.Questions];
        if (sourceQuestions.Count == 0)
            sourceQuestions = [.. session.Questions];

        if (doShuffleQ && sourceQuestions.Count > 1)
            sourceQuestions = sourceQuestions.OrderBy(_ => rng.Next()).ToList();

        OrderingDrafts.Clear();
        var playbackQuestions = new List<QuizQuestionRecord>(sourceQuestions.Count);
        foreach (var orig in sourceQuestions)
        {
            var opts = new List<string>(orig.Options);
            if (doShuffleOpt && orig.Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational && opts.Count > 1)
            {
                opts = opts.OrderBy(_ => rng.Next()).ToList();
            }

            if (orig.Kind == QuizQuestionKind.Ordering && orig.Options.Count >= 2)
            {
                var shuffledSteps = orig.Options.OrderBy(_ => rng.Next()).ToList();
                // Ensure ordering steps don't start already in exact canonical order when possible
                if (shuffledSteps.SequenceEqual(orig.Options, StringComparer.OrdinalIgnoreCase) && shuffledSteps.Count >= 2)
                    (shuffledSteps[0], shuffledSteps[^1]) = (shuffledSteps[^1], shuffledSteps[0]);
                OrderingDrafts[orig.Id] = shuffledSteps;
            }

            playbackQuestions.Add(new QuizQuestionRecord
            {
                Id = orig.Id,
                QuizId = orig.QuizId,
                OrderIndex = orig.OrderIndex,
                Kind = orig.Kind,
                CognitiveLevel = orig.CognitiveLevel,
                Difficulty = orig.Difficulty,
                TopicTag = orig.TopicTag,
                Prompt = orig.Prompt,
                Options = opts,
                CorrectAnswer = orig.CorrectAnswer,
                AcceptedAliases = [.. orig.AcceptedAliases],
                Explanation = orig.Explanation,
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
                LastAnsweredCorrect = orig.LastAnsweredCorrect
            });
        }

        ActiveSession = new QuizSessionRecord
        {
            Id = session.Id,
            CourseFolder = session.CourseFolder,
            CourseTitle = session.CourseTitle,
            Term = session.Term,
            Title = session.Title,
            SourcePdfPath = session.SourcePdfPath,
            SourcePdfMtimeTicks = session.SourcePdfMtimeTicks,
            ModelUsed = session.ModelUsed,
            CreatedAtUtc = session.CreatedAtUtc,
            AttemptCount = session.AttemptCount,
            LastScorePercent = session.LastScorePercent,
            Questions = playbackQuestions
        };
        CurrentQuestionIndex = 0;
        CurrentAnswerDraft = playbackQuestions.FirstOrDefault() is { Kind: QuizQuestionKind.Ordering } firstOrd &&
                             OrderingDrafts.TryGetValue(firstOrd.Id, out var initSteps)
            ? string.Join(" -> ", initSteps)
            : string.Empty;
        IsAnswerRevealed = false;
        IsCurrentAnswerCorrect = false;
        IsShowingResultsSummary = false;
        SessionResults.Clear();
        _mainVm.RequestUiRefresh();
    }

    public IReadOnlyList<string> GetOrderingItemsForQuestion(QuizQuestionRecord q)
    {
        if (OrderingDrafts.TryGetValue(q.Id, out var steps) && steps.Count > 0)
            return steps;

        var rng = new Random();
        var list = q.Options.Count >= 2
            ? q.Options.OrderBy(_ => rng.Next()).ToList()
            : q.CorrectAnswer.Split("->", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (list.SequenceEqual(q.Options, StringComparer.OrdinalIgnoreCase) && list.Count >= 2)
            (list[0], list[^1]) = (list[^1], list[0]);

        OrderingDrafts[q.Id] = list;
        return list;
    }

    public void MoveOrderingStep(QuizQuestionRecord q, int index, int delta)
    {
        if (IsAnswerRevealed)
            return;

        var list = GetOrderingItemsForQuestion(q).ToList();
        int target = index + delta;
        if (index < 0 || index >= list.Count || target < 0 || target >= list.Count)
            return;

        (list[index], list[target]) = (list[target], list[index]);
        OrderingDrafts[q.Id] = list;
        CurrentAnswerDraft = string.Join(" -> ", list);
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
                CurrentAnswerDraft = nextQ is { Kind: QuizQuestionKind.Ordering } &&
                                     OrderingDrafts.TryGetValue(nextQ.Id, out var steps)
                    ? string.Join(" -> ", steps)
                    : string.Empty;
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

    public void JumpToQuestion(int index)
    {
        if (ActiveSession is null || index < 0 || index >= ActiveSession.Questions.Count)
            return;

        CurrentQuestionIndex = index;
        var q = CurrentQuestion;
        if (q is not null && SessionResults.TryGetValue(q.Id, out var prev))
        {
            CurrentAnswerDraft = prev.Answer;
            IsCurrentAnswerCorrect = prev.IsCorrect;
            IsAnswerRevealed = true;
        }
        else
        {
            CurrentAnswerDraft = q is { Kind: QuizQuestionKind.Ordering } &&
                                 OrderingDrafts.TryGetValue(q.Id, out var steps)
                ? string.Join(" -> ", steps)
                : string.Empty;
            IsCurrentAnswerCorrect = false;
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
        StatusMessage = $"Re-grounding '{quiz.Title}' against {Path.GetFileName(quiz.SourcePdfPath)}...";
        _mainVm.RequestUiRefresh();
        await Task.Yield();

        try
        {
            int matched = await Task.Run(() => PdfQuizGrounder.RegroundSessionToPdf(quiz));
            await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, quiz);
            SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
            if (OrganizerQuiz?.Id == quiz.Id)
                OrganizerQuiz = SavedQuizzes.FirstOrDefault(x => x.Id == quiz.Id) ?? quiz;

            StatusMessage = $"Re-grounded '{quiz.Title}' against updated PDF ({matched}/{quiz.Questions.Count} statements anchored).";
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
            Questions = quiz.Questions.Select(CloneQuestionForEdit).ToList()
        };
        OrganizerKindFilter = null;
        EditingQuestionId = null;
        ActiveSubTab = QuizStudioSubTab.Organizer;
        StatusMessage = $"Organizing '{quiz.Title}' ({quiz.Questions.Count} items). Reorder, filter by kind, edit, or shuffle.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    public void OpenSelectedQuizzesInOrganizer(string? customTitle = null)
    {
        var selected = SavedQuizzes.Where(q => SelectedQuizIdsForMerge.Contains(q.Id)).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Select 1 or more quizzes in the catalog to open in the Deck Organizer.";
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
        var distinctTerms = selected.Select(q => q.Term).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            CourseFolder = selected.All(q => q.CourseFolder == first.CourseFolder) ? first.CourseFolder : (SelectedCourse?.FolderName ?? first.CourseFolder),
            CourseTitle = selected.All(q => q.CourseTitle == first.CourseTitle) ? first.CourseTitle : "Combined Multi-Subject Assessment",
            Term = distinctTerms.Count == 1 ? distinctTerms[0] : "Combined",
            Title = !string.IsNullOrWhiteSpace(customTitle)
                ? customTitle.Trim()
                : $"Combined Deck ({string.Join(" + ", selected.Select(q => q.Term ?? q.Title).Take(3))})",
            SourcePdfPath = first.SourcePdfPath,
            SourcePdfMtimeTicks = first.SourcePdfMtimeTicks,
            ModelUsed = "Curated Multi-Deck",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Questions = mergedQuestions
        };
        OrganizerKindFilter = null;
        EditingQuestionId = null;
        ActiveSubTab = QuizStudioSubTab.Organizer;
        StatusMessage = $"Curating combined deck from {selected.Count} quizzes ({mergedQuestions.Count} items). Customize and click Save Deck.";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    private static QuizQuestionRecord CloneQuestionForEdit(QuizQuestionRecord orig) => new()
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
        LastAnsweredCorrect = orig.LastAnsweredCorrect
    };

    public void SetOrganizerKindFilter(QuizQuestionKind? kind)
    {
        OrganizerKindFilter = OrganizerKindFilter == kind ? null : kind;
        _mainVm.RequestUiRefresh();
    }

    public void ToggleEditQuestion(string questionId)
    {
        EditingQuestionId = string.Equals(EditingQuestionId, questionId, StringComparison.OrdinalIgnoreCase)
            ? null
            : questionId;
        _mainVm.RequestUiRefresh();
    }

    public void MoveOrganizerQuestion(QuizQuestionRecord q, int delta)
    {
        if (OrganizerQuiz is null) return;
        int idx = OrganizerQuiz.Questions.IndexOf(q);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= OrganizerQuiz.Questions.Count) return;

        (OrganizerQuiz.Questions[idx], OrganizerQuiz.Questions[target]) =
            (OrganizerQuiz.Questions[target], OrganizerQuiz.Questions[idx]);
        ReindexOrganizerQuestions();
        _mainVm.RequestUiRefresh();
    }

    public void ShuffleOrganizerQuestions(QuizQuestionKind? onlyKind = null)
    {
        if (OrganizerQuiz is null || OrganizerQuiz.Questions.Count <= 1) return;
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
            OrganizerQuiz.Questions = OrganizerQuiz.Questions.OrderBy(_ => rng.Next()).ToList();
            StatusMessage = $"Shuffled all {OrganizerQuiz.Questions.Count} questions in deck.";
        }

        ReindexOrganizerQuestions();
        _mainVm.RequestUiRefresh();
    }

    public void SortOrganizerQuestionsByKind()
    {
        if (OrganizerQuiz is null) return;
        OrganizerQuiz.Questions = OrganizerQuiz.Questions
            .OrderBy(q => (int)q.Kind)
            .ThenBy(q => q.SourcePage)
            .ToList();
        ReindexOrganizerQuestions();
        StatusMessage = "Grouped and ordered deck questions by Question Kind.";
        _mainVm.RequestUiRefresh();
    }

    public void SortOrganizerQuestionsByPage()
    {
        if (OrganizerQuiz is null) return;
        OrganizerQuiz.Questions = OrganizerQuiz.Questions
            .OrderBy(q => q.SourcePage)
            .ThenBy(q => q.OrderIndex)
            .ToList();
        ReindexOrganizerQuestions();
        StatusMessage = "Ordered deck questions chronologically by PDF Page.";
        _mainVm.RequestUiRefresh();
    }

    public void DeleteOrganizerQuestion(QuizQuestionRecord q)
    {
        if (OrganizerQuiz is null) return;
        OrganizerQuiz.Questions.Remove(q);
        ReindexOrganizerQuestions();
        if (EditingQuestionId == q.Id)
            EditingQuestionId = null;
        _mainVm.RequestUiRefresh();
    }

    public void AddBlankOrganizerQuestion(QuizQuestionKind kind)
    {
        if (OrganizerQuiz is null) return;
        var q = new QuizQuestionRecord
        {
            QuizId = OrganizerQuiz.Id,
            OrderIndex = OrganizerQuiz.Questions.Count,
            Kind = kind,
            Prompt = kind == QuizQuestionKind.FillInTheBlank
                ? "Enter statement with _____ for the missing concept..."
                : "Enter question prompt...",
            Options = kind switch
            {
                QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational => ["Option A", "Option B", "Option C", "Option D"],
                QuizQuestionKind.TrueFalse => ["True", "False"],
                QuizQuestionKind.Ordering => ["Step 1", "Step 2", "Step 3"],
                _ => []
            },
            CorrectAnswer = kind switch
            {
                QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational => "Option A",
                QuizQuestionKind.TrueFalse => "True",
                QuizQuestionKind.Ordering => "Step 1 -> Step 2 -> Step 3",
                _ => "Answer"
            },
            SourcePdfPath = OrganizerQuiz.SourcePdfPath,
            SourcePage = 1,
            SrsDueDate = DateOnly.FromDateTime(DateTime.Today)
        };
        OrganizerQuiz.Questions.Add(q);
        EditingQuestionId = q.Id;
        _mainVm.RequestUiRefresh();
    }

    public async Task SaveOrganizerDeckAsync()
    {
        if (OrganizerQuiz is null) return;
        ReindexOrganizerQuestions();
        await ScholarFlowDatabase.SaveQuizSessionAsync(_mainVm.BasinRoot, OrganizerQuiz);
        SelectedQuizIdsForMerge.Clear();
        SavedQuizzes = await ScholarFlowDatabase.LoadQuizSessionsAsync(_mainVm.BasinRoot);
        StatusMessage = $"Saved deck '{OrganizerQuiz.Title}' ({OrganizerQuiz.Questions.Count} questions).";
        IsStatusError = false;
        _mainVm.RequestUiRefresh();
    }

    private void ReindexOrganizerQuestions()
    {
        if (OrganizerQuiz is null) return;
        for (int i = 0; i < OrganizerQuiz.Questions.Count; i++)
        {
            OrganizerQuiz.Questions[i].QuizId = OrganizerQuiz.Id;
            OrganizerQuiz.Questions[i].OrderIndex = i;
        }
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

        var parentQuiz = SavedQuizzes.FirstOrDefault(qz => string.Equals(qz.Id, question.QuizId, StringComparison.OrdinalIgnoreCase));
        bool isStale = parentQuiz?.IsSourcePdfStale() == true;

        // Re-compute highlight rects on-the-fly if empty or if the source Master PDF was recompiled
        if ((isStale || question.HighlightRects.Count == 0) && !string.IsNullOrWhiteSpace(question.VerbatimQuote))
        {
            PdfQuizGrounder.GroundQuestionsToPdf(question.SourcePdfPath, [question], forceUpdateSection: true);
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

        var excludes = new List<string>();
        if (!string.IsNullOrWhiteSpace(AppendTargetQuizId))
        {
            var targetQuiz = SavedQuizzes.FirstOrDefault(q => string.Equals(q.Id, AppendTargetQuizId, StringComparison.OrdinalIgnoreCase));
            if (targetQuiz is not null)
                excludes.AddRange(targetQuiz.Questions.Select(q => q.Prompt));
        }

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
            AllocationMode = AllocationMode,
            KindAllocations = new Dictionary<QuizQuestionKind, int>(KindAllocations),
            ExcludePromptsOrTopics = excludes,
            AppendToQuizId = AppendTargetQuizId,
            ProviderBaseUrl = settings.AiBaseUrl,
            ApiKey = settings.AiApiKey,
            ModelName = settings.AiModelName,
            UseOfflineHeuristicFallback = BuilderOutputMode == QuizBuilderOutputMode.OfflineSmart ||
                                          string.Equals(settings.AiProviderPreset, "OfflineSmart", StringComparison.OrdinalIgnoreCase)
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

        _ = LoadPreviewBitmapInBackgroundAsync(SelectedPdfPath, pageOneBased);
        return null;
    }

    private async Task LoadPreviewBitmapInBackgroundAsync(string pdfPath, int pageOneBased)
    {
        var key = (pdfPath, pageOneBased);
        if (!_pendingPreviewRenders.Add(key))
            return;

        IsLoadingPreviewPage = true;
        try
        {
            var pngBytes = await Task.Run(() =>
            {
                byte[] pdfBytes;
                lock (_pendingPreviewRenders)
                {
                    if (!string.Equals(_cachedBytesPath, pdfPath, StringComparison.OrdinalIgnoreCase) || _cachedPdfBytes is null)
                    {
                        _cachedPdfBytes = File.ReadAllBytes(pdfPath);
                        _cachedBytesPath = pdfPath;
                    }
                    pdfBytes = _cachedPdfBytes;
                }

                using var ms = new MemoryStream();
#pragma warning disable CA1416
                var renderOpts = new PDFtoImage.RenderOptions(
                    Dpi: 135,
                    BackgroundColor: SkiaSharp.SKColors.White);
                PDFtoImage.Conversion.SavePng(ms, pdfBytes, page: (Index)(pageOneBased - 1), options: renderOpts);
#pragma warning restore CA1416
                return ms.ToArray();
            });

            using var bmpStream = new MemoryStream(pngBytes);
            var bmp = new Bitmap(bmpStream);

            if (_previewBitmapCache.Count >= 18)
            {
                var firstKey = _previewBitmapCache.Keys.First();
                _previewBitmapCache[firstKey].Dispose();
                _previewBitmapCache.Remove(firstKey);
            }

            _previewBitmapCache[key] = bmp;
        }
        catch
        {
            // Non-fatal preview error
        }
        finally
        {
            _pendingPreviewRenders.Remove(key);
            IsLoadingPreviewPage = _pendingPreviewRenders.Count > 0;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _mainVm.RequestUiRefresh());
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
            RecomputeScopePreview();
        }
        else
        {
            _ = InspectOutlineInBackgroundAsync(SelectedPdfPath);
        }
    }

    private async Task InspectOutlineInBackgroundAsync(string pdfPath)
    {
        IsInspectingPdf = true;
        try
        {
            var (sections, totalPages) = await Task.Run(() =>
            {
                var secs = PdfQuizGrounder.InspectOutlineSections(pdfPath, out int pages);
                return (secs, pages);
            });

            _outlineCache[pdfPath] = (totalPages, sections);
            if (string.Equals(SelectedPdfPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                PdfTotalPages = totalPages;
                OutlineSections = sections;
                RecomputeScopePreview();
            }
        }
        finally
        {
            IsInspectingPdf = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _mainVm.RequestUiRefresh());
        }
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

        RefreshLivePromptPreviewIfActive();
    }
}
