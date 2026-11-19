using Avalonia.Threading;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;
using ScholarFlow.Core.Sync;
using ScholarFlow.Core.Tasks;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Elms.Client;
using ScholarFlow.Elms.Models;

namespace ScholarFlow.Desktop.ViewModels;

public enum WorkspaceTabMode
{
    TaskList,
    Calendar,
    CourseWorkspace,
    PdfReader,
    QuizStudio
}

public enum TaskGroupingMode
{
    Default,
    Term,
    DueDate,
    Priority,
    Course
}

public enum UiChangeKind
{
    FullLayout,
    SelectionOnly,
    QuickAddOnly,
    SearchOrTaskList,
    StatusOnly,
    NotificationsOnly,
    OverlayOnly
}

public enum SearchResultKind
{
    Course,
    Task,
    PdfDocument,
    Quiz
}

public sealed record SearchResultItem(
    SearchResultKind Kind,
    string Title,
    string Subtitle,
    string Badge,
    TaskItem? Task = null,
    BasinCourseEntry? Course = null,
    string? PdfPath = null,
    Core.Quiz.QuizSessionRecord? Quiz = null);

/// <summary>
/// Central application ViewModel coordinating Basin state, Todoist smart views, Calendar,
/// Built-in PDF Reader, two-tier eLMS polling, and notifications.
/// </summary>
public sealed class MainWindowViewModel : IAsyncDisposable
{
    private readonly BasinService _basinService = new();
    private readonly TaskLifecycleService _lifecycleService = new();
    private readonly LegacyImporter _legacyImporter;
    private readonly MasterPdfService _pdfService = new();
    private readonly StiElmsClient _elmsClient = new();
    private readonly ElmsBasinSyncService _syncService;
    private readonly ElmsPollingService _pollingService;
    private readonly DesktopNotificationService _notificationService = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private BasinWatcher? _watcher;

    public AppSettings Settings { get; private set; } = new();
    public string BasinRoot { get; private set; } = string.Empty;
    public BasinConfig BasinConfig { get; private set; } = new();

    public WorkspaceTabMode ActiveMode { get; private set; } = WorkspaceTabMode.TaskList;
    public SmartViewKind ActiveSmartView { get; private set; } = SmartViewKind.Today;
    public BasinCourseEntry? ActiveCourse { get; private set; }
    public bool ShowCourseMaterialsSubTab { get; private set; }

    public string? ActiveTermFilter { get; private set; }
    public string? ActiveLabelFilter { get; private set; }
    public string? CompletedCourseFilter { get; private set; }
    public TaskGroupingMode ActiveGrouping { get; private set; } = TaskGroupingMode.Default;

    public IReadOnlyList<BasinCourseEntry> Courses { get; private set; } = [];
    public IReadOnlyList<TaskItem> AllTasks { get; private set; } = [];
    public IReadOnlyList<TaskItem> VisibleTasks { get; private set; } = [];
    public IReadOnlyList<string> AllKnownLabels { get; private set; } = [];
    public IReadOnlyList<UnregisteredOngoingFile> UnregisteredFiles { get; private set; } = [];

    public TaskItem? SelectedTask { get; private set; }
    public string? SelectedTaskNoteContent { get; private set; }
    public TaskItem? PdfReturnTask { get; private set; }
    public bool PdfOpenedFromQuiz { get; private set; }
    public WorkspaceTabMode PdfReturnMode { get; private set; } = WorkspaceTabMode.TaskList;

    public string QuickAddText { get; private set; } = string.Empty;
    public IReadOnlyList<QuickAddChip> QuickAddChips { get; private set; } = [];
    public string SearchQuery { get; private set; } = string.Empty;
    public IReadOnlyList<SearchResultItem> SearchResults { get; private set; } = [];
    public int SelectedSearchResultIndex { get; private set; }

    public bool IsSidebarCollapsed { get; private set; }
    public bool IsInspectorOpen { get; private set; }
    public bool IsQuickAddModalOpen { get; private set; }
    public bool IsSearchModalOpen { get; private set; }
    public bool IsShortcutsModalOpen { get; private set; }

    public bool IsSyncing { get; private set; }
    public string StatusText { get; private set; } = "Ready";

    public List<AppNotification> Notifications { get; } = [];
    public int UnreadNotificationsCount { get; private set; }
    public bool IsNotificationDrawerOpen { get; private set; }

    public CalendarViewModel Calendar { get; } = new();
    public PdfReaderViewModel PdfReader { get; } = new();
    public QuizStudioViewModel QuizStudio { get; }

    public event EventHandler? StateChanged;
    public event EventHandler<UiChangeKind>? UiChanged;

    public MainWindowViewModel()
    {
        _legacyImporter = new LegacyImporter(_basinService);
        _syncService = new ElmsBasinSyncService(_elmsClient, _basinService, pdfService: _pdfService);
        _pollingService = new ElmsPollingService(_syncService);
        _pollingService.NotificationEmitted += OnPollingNotification;
        QuizStudio = new QuizStudioViewModel(this);
    }

    public async Task InitializeAsync()
    {
        Settings = await AppSettingsStore.LoadAsync();
        ThemeService.ApplyTheme(Settings.Theme);

        var defaultSchoolFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "School-Files",
            "YR4_01");

        var initialBasin = !string.IsNullOrWhiteSpace(Settings.LastOpenedBasinPath) && Directory.Exists(Settings.LastOpenedBasinPath)
            ? Settings.LastOpenedBasinPath
            : Directory.Exists(defaultSchoolFiles)
                ? defaultSchoolFiles
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "ScholarFlow-Basin");

        await OpenBasinAsync(initialBasin);
    }

    public async Task OpenBasinAsync(string basinPath)
    {
        _watcher?.Dispose();
        _pollingService.Stop();

        BasinRoot = Path.GetFullPath(basinPath);
        BasinConfig = await _basinService.InitializeOrOpenAsync(BasinRoot);

        // If courses.json is empty and legacy subdirectories exist, run non-destructive legacy import automatically
        var existingCourses = await _basinService.LoadCoursesAsync(BasinRoot);
        if (existingCourses.Count == 0 && Directory.GetDirectories(BasinRoot).Any(d => !Path.GetFileName(d).StartsWith('.')))
        {
            var legacyYaml = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "Ongoing",
                "metadata.yaml");
            var summary = await _legacyImporter.ImportAsync(BasinRoot, legacyYaml);
            StatusText = $"Imported {summary.CoursesImported} courses & {summary.OngoingTasksImported} tasks from Basin";
        }

        Settings.LastOpenedBasinPath = BasinRoot;
        if (!Settings.KnownBasins.Any(b => b.FilePath.Equals(BasinRoot, StringComparison.OrdinalIgnoreCase)))
        {
            Settings.KnownBasins.Insert(0, new KnownBasinEntry { Name = BasinConfig.Name, FilePath = BasinRoot });
        }
        await AppSettingsStore.SaveAsync(Settings);

        await ReloadBasinDataAsync();

        _watcher = new BasinWatcher(BasinRoot);
        _watcher.BasinChanged += (_, _) =>
            Dispatcher.UIThread.Post(async () => await ReloadBasinDataAsync());

        _pollingService.Start(BasinRoot, BasinConfig);
    }

    public async Task ReloadBasinDataAsync()
    {
        if (string.IsNullOrWhiteSpace(BasinRoot))
            return;

        await _reloadLock.WaitAsync();
        try
        {
            Courses = await _basinService.LoadCoursesAsync(BasinRoot);
            AllTasks = await _basinService.LoadAllTasksAsync(BasinRoot);
            AllKnownLabels = EnumerateAllTaskNodes(AllTasks)
                .SelectMany(t => t.Labels)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();
            UnregisteredFiles = TaskQueryService.FindUnregisteredOngoingFiles(BasinRoot, Courses, AllTasks);
            Calendar.UpdateTasks(EnumerateAllTaskNodes(AllTasks).ToList());
            await QuizStudio.RefreshAsync();

            RecomputeVisibleTasks();
        }
        finally
        {
            _reloadLock.Release();
        }

        NotifyStateChanged();
    }

    public static IEnumerable<TaskItem> EnumerateAllTaskNodes(IEnumerable<TaskItem> roots)
    {
        foreach (var t in roots)
        {
            yield return t;
            foreach (var c in EnumerateAllTaskNodes(t.ChildTasks))
                yield return c;
        }
    }

    public void SelectSmartView(SmartViewKind view)
    {
        ActiveMode = WorkspaceTabMode.TaskList;
        ActiveSmartView = view;
        ActiveCourse = null;
        ActiveLabelFilter = null;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SelectTermView(string? term)
    {
        var normalized = TaskItem.NormalizeTerm(term);
        if (ActiveMode == WorkspaceTabMode.CourseWorkspace)
        {
            ActiveTermFilter = string.Equals(ActiveTermFilter, normalized, StringComparison.OrdinalIgnoreCase) ? null : normalized;
        }
        else
        {
            ActiveMode = WorkspaceTabMode.TaskList;
            if (normalized is null || string.Equals(ActiveTermFilter, normalized, StringComparison.OrdinalIgnoreCase))
            {
                ActiveTermFilter = null;
                if (ActiveSmartView == SmartViewKind.Term)
                    ActiveSmartView = SmartViewKind.All;
            }
            else
            {
                ActiveTermFilter = normalized;
                if (ActiveSmartView is SmartViewKind.Completed)
                    ActiveSmartView = SmartViewKind.All;
            }
        }

        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SelectLabelView(string label)
    {
        ActiveMode = WorkspaceTabMode.TaskList;
        ActiveSmartView = SmartViewKind.Label;
        ActiveCourse = null;
        ActiveLabelFilter = label;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SetGroupingMode(TaskGroupingMode mode)
    {
        ActiveGrouping = mode;
        NotifyStateChanged();
    }

    public void SelectCalendarView()
    {
        ActiveMode = WorkspaceTabMode.Calendar;
        ActiveCourse = null;
        Calendar.UpdateTasks(EnumerateAllTaskNodes(AllTasks).ToList());
        NotifyStateChanged();
    }

    public void SetCompletedCourseFilter(string? courseFolder)
    {
        CompletedCourseFilter = string.Equals(CompletedCourseFilter, courseFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : courseFolder;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SelectCourse(BasinCourseEntry course, bool showMaterials = false)
    {
        ActiveMode = WorkspaceTabMode.CourseWorkspace;
        ActiveSmartView = SmartViewKind.Course;
        ActiveCourse = course;
        ActiveLabelFilter = null;
        ShowCourseMaterialsSubTab = showMaterials;
        RecomputeVisibleTasks();
        NotifyStateChanged();
    }

    public void SetCourseSubTab(bool showMaterials)
    {
        ShowCourseMaterialsSubTab = showMaterials;
        NotifyStateChanged();
    }

    public void RequestUiRefresh() => NotifyStateChanged(UiChangeKind.FullLayout);

    public async Task SelectQuizStudioViewAsync(
        BasinCourseEntry? course = null,
        string? pdfPath = null,
        int? focusPage = null)
    {
        ActiveMode = WorkspaceTabMode.QuizStudio;
        if (course is not null)
            ActiveCourse = course;
        await QuizStudio.RefreshAsync(course ?? ActiveCourse, pdfPath, focusPage);
        NotifyStateChanged();
    }

    public void OpenQuizStudioForCurrentPdf()
    {
        var currentPdf = PdfReader.CurrentFilePath;
        var currentPage = PdfReader.CurrentPage;
        var matchedCourse = Courses.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(currentPdf) &&
            currentPdf.Contains(c.FolderName, StringComparison.OrdinalIgnoreCase)) ?? ActiveCourse;

        _ = SelectQuizStudioViewAsync(matchedCourse, currentPdf, currentPage);
    }

    public void OpenPdfWithQuizEvidence(
        string pdfFullPath,
        int pageNumber,
        IReadOnlyList<ScholarFlow.Core.Quiz.NormalizedHighlightRect> highlights,
        string? verbatimQuote,
        string? questionPrompt)
    {
        PdfReturnMode = WorkspaceTabMode.QuizStudio;
        PdfReturnTask = null;
        PdfOpenedFromQuiz = true;
        if (!string.Equals(PdfReader.CurrentFilePath, pdfFullPath, StringComparison.OrdinalIgnoreCase))
            PdfReader.LoadPdf(pdfFullPath);
        PdfReader.SetEvidenceHighlight(pageNumber, highlights, verbatimQuote, questionPrompt);
        ActiveMode = WorkspaceTabMode.PdfReader;
        NotifyStateChanged();
    }

    public void OpenPdfInReader(string pdfFullPath, TaskItem? associatedTask = null)
    {
        if (ActiveMode != WorkspaceTabMode.PdfReader)
            PdfReturnMode = ActiveMode;
        PdfReturnTask = associatedTask;
        PdfOpenedFromQuiz = ActiveMode == WorkspaceTabMode.QuizStudio;
        PdfReader.LoadPdf(pdfFullPath);
        ActiveMode = WorkspaceTabMode.PdfReader;
        NotifyStateChanged();
    }

    public void ReturnFromPdfReader()
    {
        if (PdfOpenedFromQuiz || PdfReturnMode == WorkspaceTabMode.QuizStudio)
        {
            PdfOpenedFromQuiz = false;
            ActiveMode = WorkspaceTabMode.QuizStudio;
            NotifyStateChanged();
            return;
        }

        var returnTask = PdfReturnTask;
        PdfReturnTask = null;
        if (returnTask is not null)
        {
            ActiveMode = PdfReturnMode == WorkspaceTabMode.PdfReader ? WorkspaceTabMode.TaskList : PdfReturnMode;
            SelectTask(returnTask, openInspector: true);
            return;
        }

        if (ActiveCourse is not null)
            SelectCourse(ActiveCourse, showMaterials: ShowCourseMaterialsSubTab);
        else if (PdfReturnMode == WorkspaceTabMode.Calendar)
            SelectCalendarView();
        else
            SelectSmartView(ActiveSmartView);
    }

    public void SelectTask(TaskItem? task, bool openInspector = false)
    {
        SelectedTask = task;
        SelectedTaskNoteContent = LoadTaskNotePreview(task);
        if (openInspector && task is not null)
        {
            IsInspectorOpen = true;
            NotifyStateChanged(UiChangeKind.FullLayout);
            return;
        }
        NotifyStateChanged(UiChangeKind.SelectionOnly);
    }

    public void OpenInspector(TaskItem? task = null)
    {
        if (task is not null)
        {
            SelectedTask = task;
            SelectedTaskNoteContent = LoadTaskNotePreview(task);
        }
        if (SelectedTask is not null)
        {
            IsInspectorOpen = true;
            NotifyStateChanged(UiChangeKind.FullLayout);
        }
    }

    public void CloseInspector()
    {
        if (!IsInspectorOpen)
            return;

        IsInspectorOpen = false;
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public void ToggleInspector()
    {
        if (IsInspectorOpen)
            CloseInspector();
        else
            OpenInspector();
    }

    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        NotifyStateChanged(UiChangeKind.FullLayout);
    }

    public void ToggleQuickAddModal()
    {
        IsQuickAddModalOpen = !IsQuickAddModalOpen;
        if (IsQuickAddModalOpen)
        {
            IsSearchModalOpen = false;
            IsShortcutsModalOpen = false;
            IsNotificationDrawerOpen = false;
        }
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void OpenQuickAddModal()
    {
        IsQuickAddModalOpen = true;
        IsSearchModalOpen = false;
        IsShortcutsModalOpen = false;
        IsNotificationDrawerOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void CloseQuickAddModal()
    {
        if (!IsQuickAddModalOpen)
            return;

        IsQuickAddModalOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void ToggleSearchModal()
    {
        IsSearchModalOpen = !IsSearchModalOpen;
        if (IsSearchModalOpen)
        {
            IsQuickAddModalOpen = false;
            IsShortcutsModalOpen = false;
            IsNotificationDrawerOpen = false;
            RecomputeSearchResults();
        }
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void OpenSearchModal()
    {
        IsSearchModalOpen = true;
        IsQuickAddModalOpen = false;
        IsShortcutsModalOpen = false;
        IsNotificationDrawerOpen = false;
        RecomputeSearchResults();
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void CloseSearchModal()
    {
        if (!IsSearchModalOpen)
            return;

        IsSearchModalOpen = false;
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public void ToggleShortcutsModal()
    {
        IsShortcutsModalOpen = !IsShortcutsModalOpen;
        if (IsShortcutsModalOpen)
        {
            IsQuickAddModalOpen = false;
            IsSearchModalOpen = false;
            IsNotificationDrawerOpen = false;
        }
        NotifyStateChanged(UiChangeKind.OverlayOnly);
    }

    public bool CloseTopOverlayOrDrawer()
    {
        if (IsQuickAddModalOpen)
        {
            IsQuickAddModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsSearchModalOpen)
        {
            IsSearchModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsShortcutsModalOpen)
        {
            IsShortcutsModalOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsNotificationDrawerOpen)
        {
            IsNotificationDrawerOpen = false;
            NotifyStateChanged(UiChangeKind.OverlayOnly);
            return true;
        }
        if (IsInspectorOpen)
        {
            IsInspectorOpen = false;
            NotifyStateChanged(UiChangeKind.FullLayout);
            return true;
        }
        return false;
    }

    public void MoveSelection(int delta)
    {
        if (VisibleTasks.Count == 0)
            return;

        if (SelectedTask is null)
        {
            SelectTask(VisibleTasks[0]);
            return;
        }

        var idx = VisibleTasks.ToList().FindIndex(t => t.Id == SelectedTask.Id);
        int next = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, VisibleTasks.Count - 1);
        SelectTask(VisibleTasks[next]);
    }

    public void JumpSelection(bool toEnd)
    {
        if (VisibleTasks.Count == 0)
            return;

        SelectTask(toEnd ? VisibleTasks[^1] : VisibleTasks[0]);
    }

    public void UpdateQuickAddText(string text)
    {
        QuickAddText = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            QuickAddChips = [];
        }
        else
        {
            var parsed = QuickAddParser.Parse(text, Courses);
            QuickAddChips = parsed.Chips;
        }
        NotifyStateChanged(UiChangeKind.QuickAddOnly);
    }

    public void InsertQuickAddToken(string token, string? replacePattern = null)
    {
        var current = QuickAddText?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(replacePattern))
        {
            current = System.Text.RegularExpressions.Regex.Replace(
                current,
                replacePattern,
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        var next = string.IsNullOrEmpty(current) ? token : $"{current} {token}";
        UpdateQuickAddText(next);
    }

    public async Task SubmitQuickAddAsync()
    {
        if (string.IsNullOrWhiteSpace(QuickAddText))
            return;

        var input = QuickAddText;
        if (ActiveCourse is not null && !input.Contains('#'))
            input = $"{input} #{ActiveCourse.Alias}";
        else if (ActiveMode == WorkspaceTabMode.Calendar && !QuickAddParser.Parse(input, Courses).Task.DueDate.HasValue)
            input = $"{input} {Calendar.SelectedDate:yyyy-MM-dd}";

        var created = await _basinService.AddQuickTaskAsync(BasinRoot, input);
        QuickAddText = string.Empty;
        QuickAddChips = [];
        IsQuickAddModalOpen = false;
        StatusText = $"Added task: {created.Title}";
        await ReloadBasinDataAsync();
        SelectTask(VisibleTasks.FirstOrDefault(t => t.Title == created.Title));
    }

    public void UpdateSearchQuery(string query)
    {
        SearchQuery = query;
        RecomputeSearchResults();
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void MoveSearchSelection(int delta)
    {
        if (SearchResults.Count == 0)
            return;

        SelectedSearchResultIndex = Math.Clamp(SelectedSearchResultIndex + delta, 0, SearchResults.Count - 1);
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void ExecuteSelectedSearchResult()
    {
        if (SelectedSearchResultIndex >= 0 && SelectedSearchResultIndex < SearchResults.Count)
        {
            ExecuteSearchResult(SearchResults[SelectedSearchResultIndex]);
        }
    }

    public async void ExecuteSearchResult(SearchResultItem item)
    {
        IsSearchModalOpen = false;
        SearchQuery = string.Empty;

        switch (item.Kind)
        {
            case SearchResultKind.Course when item.Course is not null:
                SelectCourse(item.Course);
                break;

            case SearchResultKind.PdfDocument when !string.IsNullOrWhiteSpace(item.PdfPath):
                OpenPdfInReader(item.PdfPath);
                break;

            case SearchResultKind.Quiz when item.Quiz is not null:
                var qCourse = Courses.FirstOrDefault(c => c.FolderName.Equals(item.Quiz.CourseFolder, StringComparison.OrdinalIgnoreCase));
                await SelectQuizStudioViewAsync(qCourse, item.Quiz.SourcePdfPath);
                QuizStudio.StartQuizSession(item.Quiz, resumeSavedProgress: item.Quiz.HasInProgressAttempt);
                break;

            case SearchResultKind.Task when item.Task is not null:
                var task = item.Task;
                if (!string.IsNullOrWhiteSpace(task.CourseFolder))
                {
                    var course = Courses.FirstOrDefault(c => c.FolderName.Equals(task.CourseFolder, StringComparison.OrdinalIgnoreCase));
                    if (course is not null)
                    {
                        ActiveMode = WorkspaceTabMode.CourseWorkspace;
                        ActiveSmartView = SmartViewKind.Course;
                        ActiveCourse = course;
                        ShowCourseMaterialsSubTab = false;
                    }
                    else
                    {
                        ActiveMode = WorkspaceTabMode.TaskList;
                        ActiveSmartView = task.IsCompleted ? SmartViewKind.Completed : SmartViewKind.All;
                    }
                }
                else
                {
                    ActiveMode = WorkspaceTabMode.TaskList;
                    ActiveSmartView = task.IsCompleted ? SmartViewKind.Completed : SmartViewKind.Inbox;
                }
                RecomputeVisibleTasks();
                SelectTask(task, openInspector: true);
                break;
        }
    }

    private void RecomputeSearchResults()
    {
        var q = SearchQuery?.Trim() ?? string.Empty;
        var results = new List<SearchResultItem>();

        // 1. Matching Courses
        foreach (var c in Courses.Where(c => !c.IsArchived))
        {
            if (string.IsNullOrEmpty(q) ||
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Alias.Contains(q.TrimStart('#'), StringComparison.OrdinalIgnoreCase) ||
                (c.CourseCode?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))
            {
                results.Add(new SearchResultItem(
                    SearchResultKind.Course,
                    c.Title,
                    c.CourseCode ?? "Course Workspace",
                    $"#{c.Alias}",
                    Course: c));
            }
        }

        // 2. Matching Tasks (via SQLite FTS5 index across titles, subtasks, tags, terms, and note bodies, with in-memory fallback)
        var allNodes = EnumerateAllTaskNodes(AllTasks).ToList();
        var byId = allNodes.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var matchedTasks = new List<TaskItem>();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var ftsIds = ScholarFlowDatabase.SearchTaskIdsFts(BasinRoot, q, limit: 18);
            foreach (var ftsId in ftsIds)
            {
                if (byId.TryGetValue(ftsId, out var matched))
                    matchedTasks.Add(matched);
            }
        }

        if (matchedTasks.Count < 16)
        {
            var seenIds = new HashSet<string>(matchedTasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            var candidateTasks = allNodes
                .OrderBy(t => t.IsCompleted ? 1 : 0)
                .ThenByDescending(t => UrgencyCalculator.Explain(t).Score)
                .ToList();

            foreach (var t in candidateTasks)
            {
                if (seenIds.Contains(t.Id))
                    continue;

                if (string.IsNullOrEmpty(q) ||
                    t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (t.CourseAlias?.Contains(q.TrimStart('#'), StringComparison.OrdinalIgnoreCase) == true) ||
                    (t.Term?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                    t.Labels.Any(l => l.Contains(q.TrimStart('@'), StringComparison.OrdinalIgnoreCase)))
                {
                    matchedTasks.Add(t);
                    if (matchedTasks.Count >= 18)
                        break;
                }
            }
        }

        foreach (var t in matchedTasks)
        {
            var sub = t.DueDate.HasValue ? $"Due {t.DueDate.Value:MMM d}" : (t.IsCompleted ? "Completed" : "No due date");
            if (!string.IsNullOrWhiteSpace(t.ParentTaskId))
                sub = $"Subtask · {sub}";
            var badge = !string.IsNullOrWhiteSpace(t.CourseAlias) ? $"#{t.CourseAlias}" : $"P{(int)t.Priority}";
            results.Add(new SearchResultItem(
                SearchResultKind.Task,
                t.Title,
                sub,
                badge,
                Task: t));

            if (results.Count >= 18)
                break;
        }

        // 3. Matching Master PDFs & Handouts
        if (!string.IsNullOrWhiteSpace(BasinRoot) && Directory.Exists(BasinRoot))
        {
            foreach (var c in Courses.Where(c => !c.IsArchived))
            {
                var masterPdf = MasterPdfService.FindExistingMasterPdfPath(BasinRoot, c.FolderName);
                if (masterPdf is not null &&
                    (string.IsNullOrEmpty(q) ||
                     q.Contains("pdf", StringComparison.OrdinalIgnoreCase) ||
                     q.Contains("master", StringComparison.OrdinalIgnoreCase) ||
                     c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     c.Alias.Contains(q.TrimStart('#'), StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new SearchResultItem(
                        SearchResultKind.PdfDocument,
                        $"{c.Title} — Master Study Packet",
                        Path.GetFileName(masterPdf),
                        "Master PDF",
                        Course: c,
                        PdfPath: masterPdf));
                }
            }
        }

        // 4. Matching Test & Recall Quizzes (by title, term, course, or question prompt/tag)
        foreach (var qz in QuizStudio.SavedQuizzes)
        {
            bool matchesQuiz = string.IsNullOrEmpty(q) ||
                qz.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                qz.CourseTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (qz.Term?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                q.Contains("quiz", StringComparison.OrdinalIgnoreCase) ||
                q.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                q.Contains("recall", StringComparison.OrdinalIgnoreCase) ||
                qz.Questions.Any(qq => qq.Prompt.Contains(q, StringComparison.OrdinalIgnoreCase) || qq.TopicTag.Contains(q, StringComparison.OrdinalIgnoreCase));

            if (matchesQuiz)
            {
                var sub = $"{qz.CourseTitle} · {qz.Questions.Count} items" +
                          (qz.HasInProgressAttempt ? $" · In progress (Q{(qz.InProgressQuestionIndex ?? 0) + 1})" : "");
                results.Add(new SearchResultItem(
                    SearchResultKind.Quiz,
                    qz.Title,
                    sub,
                    qz.Term ?? "Quiz",
                    Quiz: qz));
            }
        }

        SearchResults = results.Take(16).ToList();
        SelectedSearchResultIndex = 0;
    }

    public async Task ToggleTaskCompletedAsync(TaskItem task)
    {
        await _lifecycleService.SetCompletedAsync(BasinRoot, task, !task.IsCompleted);
        StatusText = !task.IsCompleted ? $"Completed: {task.Title}" : $"Reopened: {task.Title}";
        await ReloadBasinDataAsync();
    }

    public async Task SaveInspectorEditsAsync(
        TaskItem task,
        string newTitle,
        string? newDueDateIso,
        TaskPriority newPriority,
        int newDifficulty)
    {
        if (!string.IsNullOrWhiteSpace(newTitle))
            task.Title = newTitle.Trim();

        if (string.IsNullOrWhiteSpace(newDueDateIso))
            task.DueDate = null;
        else if (DateOnly.TryParse(newDueDateIso.Trim(), out var parsedDue))
            task.DueDate = parsedDue;

        task.Priority = newPriority;
        task.Difficulty = Math.Clamp(newDifficulty, 1, 5);

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = $"Saved changes to '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public TaskItem? GetParentTask(TaskItem task) =>
        TaskItem.FindParentOf(AllTasks, task.Id)
        ?? (!string.IsNullOrWhiteSpace(task.ParentTaskId) ? TaskItem.FindById(AllTasks, task.ParentTaskId) : null);

    public async Task AddSubtaskAsync(TaskItem parentTask, string subtaskInput)
    {
        if (string.IsNullOrWhiteSpace(subtaskInput))
            return;

        parentTask.EnsureChildTasksSynced();

        var parsed = QuickAddParser.Parse(subtaskInput.Trim(), Courses);
        var child = parsed.Task;
        if (string.IsNullOrWhiteSpace(child.Title))
            child.Title = subtaskInput.Trim();

        child.Id = $"{parentTask.Id}:sub-{parentTask.ChildTasks.Count + 1}";
        child.ParentTaskId = parentTask.Id;
        child.CourseFolder ??= parentTask.CourseFolder;
        child.CourseAlias ??= parentTask.CourseAlias;
        child.Term ??= parentTask.Term;
        child.Section ??= parentTask.Section;
        child.SourceMarkdownPath = parentTask.SourceMarkdownPath;

        parentTask.ChildTasks.Add(child);
        parentTask.EnsureChildTasksSynced();

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, parentTask);
        StatusText = $"Added subtask '{child.Title}' under '{parentTask.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task ToggleSubtaskAsync(TaskItem parentTask, int subIndex)
    {
        parentTask.EnsureChildTasksSynced();
        if (subIndex < 0 || subIndex >= parentTask.ChildTasks.Count)
            return;

        var child = parentTask.ChildTasks[subIndex];
        child.IsCompleted = !child.IsCompleted;
        child.CompletedDate = child.IsCompleted ? DateOnly.FromDateTime(DateTime.Today) : null;
        parentTask.EnsureChildTasksSynced();

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, parentTask);
        StatusText = child.IsCompleted ? $"Completed subtask: {child.Title}" : $"Reopened subtask: {child.Title}";
        await ReloadBasinDataAsync();
    }

    public async Task ToggleChildTaskAsync(TaskItem parentTask, TaskItem childTask)
    {
        parentTask.EnsureChildTasksSynced();
        var idx = parentTask.ChildTasks.FindIndex(c =>
            string.Equals(c.Id, childTask.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Title, childTask.Title, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            await ToggleSubtaskAsync(parentTask, idx);
        }
        else
        {
            await ToggleTaskCompletedAsync(childTask);
        }
    }

    public async Task DeleteTaskAsync(TaskItem task)
    {
        var parent = GetParentTask(task);
        await MarkdownTaskCodec.DeleteTaskFromFileAsync(BasinRoot, task);
        if (SelectedTask?.Id == task.Id)
            SelectedTask = parent;
        StatusText = $"Deleted task '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task RegisterOngoingFileAsync(UnregisteredOngoingFile file)
    {
        var created = await _lifecycleService.RegisterOngoingFileAsync(BasinRoot, file);
        StatusText = $"Registered '{file.FileName}' as task";
        await ReloadBasinDataAsync();
        SelectTask(created);
    }

    public async Task SetTaskDueDateAsync(TaskItem task, DateOnly? dueDate)
    {
        if (task.DueDate == dueDate)
            return;

        task.DueDate = dueDate;
        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = dueDate.HasValue
            ? $"Due date set to {dueDate.Value:MMM d, yyyy} for '{task.Title}'"
            : $"Cleared due date for '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task SetTaskTermAsync(TaskItem task, string? term)
    {
        var normalized = TaskItem.NormalizeTerm(term);
        task.Term = normalized;

        // Remove old term tags and add the new term tag
        task.Labels.RemoveAll(l => TaskItem.NormalizeTerm(l) is not null);
        if (normalized is not null)
            task.Labels.Insert(0, normalized.ToLowerInvariant());

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = normalized is not null
            ? $"Assigned '{task.Title}' to {normalized}"
            : $"Cleared term for '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task SetTaskSectionAsync(TaskItem task, string? section)
    {
        task.Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim();
        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = $"Updated section for '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public async Task AddTaskLabelAsync(TaskItem task, string label)
    {
        var clean = label.Trim().TrimStart('@').Replace(' ', '-').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(clean))
            return;

        if (!task.Labels.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            task.Labels.Add(clean);
            var maybeTerm = TaskItem.NormalizeTerm(clean);
            if (maybeTerm is not null)
                task.Term = maybeTerm;

            await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
            StatusText = $"Added @{clean} to '{task.Title}'";
            await ReloadBasinDataAsync();
        }
    }

    public async Task RemoveTaskLabelAsync(TaskItem task, string label)
    {
        if (task.Labels.RemoveAll(l => l.Equals(label, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
            StatusText = $"Removed @{label} from '{task.Title}'";
            await ReloadBasinDataAsync();
        }
    }

    public async Task AttachFileToTaskAsync(TaskItem task, string fullFilePath, bool isSubmission)
    {
        if (!File.Exists(fullFilePath))
            return;

        var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder)
            ? BasinRoot
            : Path.Combine(BasinRoot, task.CourseFolder);

        string relPath;
        if (fullFilePath.StartsWith(courseDir, StringComparison.OrdinalIgnoreCase))
        {
            relPath = Path.GetRelativePath(courseDir, fullFilePath).Replace('\\', '/');
        }
        else
        {
            // Copy external file into ongoing/ or completed/ inside the course folder
            var targetSub = isSubmission ? (task.IsCompleted ? "completed" : "ongoing") : "ongoing";
            var destDir = Path.Combine(courseDir, targetSub);
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(fullFilePath));
            if (!File.Exists(destPath))
                File.Copy(fullFilePath, destPath);
            relPath = $"{targetSub}/{Path.GetFileName(fullFilePath)}";
        }

        if (isSubmission)
        {
            if (!task.SubmissionFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
                task.SubmissionFiles.Add(relPath);
        }
        else
        {
            if (!task.InstructionFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
                task.InstructionFiles.Add(relPath);
        }

        if (!task.LinkedFiles.Contains(relPath, StringComparer.OrdinalIgnoreCase))
            task.LinkedFiles.Add(relPath);

        await MarkdownTaskCodec.UpdateTaskInFileAsync(BasinRoot, task);
        StatusText = $"Linked '{Path.GetFileName(relPath)}' to '{task.Title}'";
        await ReloadBasinDataAsync();
    }

    public void ToggleTaskSubtasksExpanded(TaskItem task)
    {
        task.IsSubtasksExpanded = !task.IsSubtasksExpanded;
        NotifyStateChanged(UiChangeKind.SearchOrTaskList);
    }

    public void OpenTaskFile(TaskItem task, string relativeOrFullFilePath)
    {
        var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder) ? BasinRoot : Path.Combine(BasinRoot, task.CourseFolder);
        var fullPath = Path.IsPathRooted(relativeOrFullFilePath)
            ? relativeOrFullFilePath
            : Path.Combine(courseDir, relativeOrFullFilePath);

        if (fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
        {
            OpenPdfInReader(fullPath, associatedTask: task);
            return;
        }

        ExternalFileLauncher.OpenLocalFile(fullPath);
    }

    public void OpenPrimaryTaskFileOrNote(TaskItem? task)
    {
        if (task is null)
            return;

        var primaryPdf = task.PrimaryPdfFile;
        if (!string.IsNullOrWhiteSpace(primaryPdf))
        {
            OpenTaskFile(task, primaryPdf);
            return;
        }

        var firstFile = task.AllFiles.FirstOrDefault() ?? task.NotePath;
        if (!string.IsNullOrWhiteSpace(firstFile))
        {
            OpenTaskFile(task, firstFile);
            return;
        }

        if (!string.IsNullOrWhiteSpace(task.SourceMarkdownPath))
            ExternalFileLauncher.OpenLocalFile(task.SourceMarkdownPath);
    }

    public async Task OpenTaskInElmsAppAsync(TaskItem? task)
    {
        var url = task?.LmsUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            if (ActiveCourse?.CourseUrl is not null)
                url = ActiveCourse.CourseUrl;
            else
                url = "/user_dashboard";
        }

        StatusText = $"Opening eLMS app window...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        await ExternalFileLauncher.OpenInElmsAppAndWatchAsync(url, async () =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                StatusText = "eLMS window closed — refreshing assignments...";
                NotifyStateChanged(UiChangeKind.StatusOnly);
                await RunFastPollAsync();
            });
        });
    }

    public async Task RunFastPollAsync()
    {
        if (IsSyncing)
            return;

        IsSyncing = true;
        StatusText = "Polling eLMS assignments...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        try
        {
            var notifs = await _pollingService.TriggerFastPollAsync(BasinRoot);
            StatusText = $"Fast poll complete ({notifs.Count} update(s))";
            await ReloadBasinDataAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Poll error: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
    }

    public async Task RunFullSyncAsync()
    {
        if (IsSyncing)
            return;

        IsSyncing = true;
        StatusText = "Running full eLMS sync...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        var progress = new Progress<SyncProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"[{p.Stage}] {(p.CourseTitle is not null ? $"{p.CourseTitle}: " : "")}{p.Message}";
                NotifyStateChanged(UiChangeKind.StatusOnly);
            });
        });

        try
        {
            var report = await _syncService.SyncAllAsync(BasinRoot, progress: progress);
            foreach (var n in report.Notifications)
                AddNotification(n);

            StatusText = $"Synced {report.CoursesSynced} courses (+{report.FilesDownloaded} files, {report.MasterPdfsCompiled} PDFs compiled)";
            await ReloadBasinDataAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
    }

    public async Task CompileCourseMasterPdfAsync(BasinCourseEntry course, bool force = true)
    {
        IsSyncing = true;
        StatusText = $"Compiling {course.Title} Master PDF...";
        NotifyStateChanged(UiChangeKind.StatusOnly);

        try
        {
            var res = await _pdfService.CompileIfStaleAsync(BasinRoot, course.FolderName, course.Title, force);
            StatusText = res.WasRegenerated
                ? $"Compiled {Path.GetFileName(res.OutputPdfPath)} ({res.PageCount} pages)"
                : $"{Path.GetFileName(res.OutputPdfPath)} is already up to date";

            if (File.Exists(res.OutputPdfPath))
                OpenPdfInReader(res.OutputPdfPath);
        }
        catch (Exception ex)
        {
            StatusText = $"PDF compile error: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged(UiChangeKind.StatusOnly);
        }
    }

    public async Task ToggleThemeAsync()
    {
        var next = ThemeService.Toggle();
        Settings.Theme = next;
        await AppSettingsStore.SaveAsync(Settings);
        NotifyStateChanged();
    }

    public void ToggleNotificationDrawer()
    {
        IsNotificationDrawerOpen = !IsNotificationDrawerOpen;
        if (IsNotificationDrawerOpen)
            UnreadNotificationsCount = 0;
        NotifyStateChanged(UiChangeKind.NotificationsOnly);
    }

    private void OnPollingNotification(object? sender, AppNotification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AddNotification(notification);
            _ = ReloadBasinDataAsync();
        });
    }

    private void AddNotification(AppNotification notification)
    {
        Notifications.Insert(0, notification);
        if (!IsNotificationDrawerOpen)
            UnreadNotificationsCount++;
        _notificationService.SendSystemNotification(notification);
        NotifyStateChanged(UiChangeKind.NotificationsOnly);
    }

    private void RecomputeVisibleTasks()
    {
        var viewKind = ActiveMode == WorkspaceTabMode.CourseWorkspace && ActiveCourse is not null
            ? SmartViewKind.Course
            : ActiveSmartView;

        var targetVal = viewKind switch
        {
            SmartViewKind.Course => ActiveCourse?.FolderName,
            SmartViewKind.Label => ActiveLabelFilter,
            SmartViewKind.Term => ActiveTermFilter,
            SmartViewKind.Completed => CompletedCourseFilter,
            _ => null
        };

        // Include subtasks with due dates in Today & Upcoming views
        var querySource = viewKind is SmartViewKind.Today or SmartViewKind.Upcoming
            ? EnumerateAllTaskNodes(AllTasks)
            : AllTasks;

        VisibleTasks = TaskQueryService.Filter(
            querySource,
            viewKind,
            targetValue: targetVal,
            searchQuery: IsSearchModalOpen ? null : SearchQuery,
            termFilter: ActiveTermFilter);

        if (SelectedTask is not null)
        {
            var prev = SelectedTask;
            var allNodes = EnumerateAllTaskNodes(AllTasks).ToList();
            SelectedTask = TaskItem.FindById(AllTasks, prev.Id)
                           ?? allNodes.FirstOrDefault(t =>
                               (!string.IsNullOrWhiteSpace(prev.LmsId) && string.Equals(t.LmsId, prev.LmsId, StringComparison.OrdinalIgnoreCase)) ||
                               (string.Equals(t.CourseFolder, prev.CourseFolder, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(t.Title, prev.Title, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(t.ParentTaskId ?? "", prev.ParentTaskId ?? "", StringComparison.OrdinalIgnoreCase)))
                           ?? VisibleTasks.FirstOrDefault();
            SelectedTaskNoteContent = LoadTaskNotePreview(SelectedTask);
        }
        else if (VisibleTasks.Count > 0)
        {
            SelectedTask = VisibleTasks[0];
            SelectedTaskNoteContent = LoadTaskNotePreview(SelectedTask);
        }
    }

    private string? LoadTaskNotePreview(TaskItem? task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.NotePath))
            return null;

        var courseDir = string.IsNullOrWhiteSpace(task.CourseFolder) ? BasinRoot : Path.Combine(BasinRoot, task.CourseFolder);
        var full = Path.Combine(courseDir, task.NotePath);
        if (!File.Exists(full))
            return null;

        try
        {
            return File.ReadAllText(full);
        }
        catch
        {
            return null;
        }
    }

    private void NotifyStateChanged(UiChangeKind kind = UiChangeKind.FullLayout)
    {
        UiChanged?.Invoke(this, kind);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        PdfReader.Dispose();
        await _pollingService.DisposeAsync();
        await _elmsClient.DisposeAsync();
    }
}
