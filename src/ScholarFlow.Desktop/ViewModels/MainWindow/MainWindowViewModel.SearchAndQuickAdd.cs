using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Tasks;

namespace ScholarFlow.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
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
            current = System
                .Text.RegularExpressions.Regex.Replace(
                    current,
                    replacePattern,
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                )
                .Trim();
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
        else if (
            ActiveMode == WorkspaceTabMode.Calendar
            && !QuickAddParser.Parse(input, Courses).Task.DueDate.HasValue
        )
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

        SelectedSearchResultIndex = Math.Clamp(
            SelectedSearchResultIndex + delta,
            0,
            SearchResults.Count - 1
        );
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
                var qCourse = Courses.FirstOrDefault(c =>
                    c.FolderName.Equals(item.Quiz.CourseFolder, StringComparison.OrdinalIgnoreCase)
                );
                await SelectQuizStudioViewAsync(qCourse, item.Quiz.SourcePdfPath);
                QuizStudio.StartQuizSession(
                    item.Quiz,
                    resumeSavedProgress: item.Quiz.HasInProgressAttempt
                );
                break;

            case SearchResultKind.Task when item.Task is not null:
                var task = item.Task;
                if (!string.IsNullOrWhiteSpace(task.CourseFolder))
                {
                    var course = Courses.FirstOrDefault(c =>
                        c.FolderName.Equals(task.CourseFolder, StringComparison.OrdinalIgnoreCase)
                    );
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
                        ActiveSmartView = task.IsCompleted
                            ? SmartViewKind.Completed
                            : SmartViewKind.All;
                    }
                }
                else
                {
                    ActiveMode = WorkspaceTabMode.TaskList;
                    ActiveSmartView = task.IsCompleted
                        ? SmartViewKind.Completed
                        : SmartViewKind.Inbox;
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
            if (
                string.IsNullOrEmpty(q)
                || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Alias.Contains(q.TrimStart('#'), StringComparison.OrdinalIgnoreCase)
                || (c.CourseCode?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
            )
            {
                results.Add(
                    new SearchResultItem(
                        SearchResultKind.Course,
                        c.Title,
                        c.CourseCode ?? "Course Workspace",
                        $"#{c.Alias}",
                        Course: c
                    )
                );
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
            var seenIds = new HashSet<string>(
                matchedTasks.Select(t => t.Id),
                StringComparer.OrdinalIgnoreCase
            );
            var candidateTasks = allNodes
                .OrderBy(t => t.IsCompleted ? 1 : 0)
                .ThenByDescending(t => UrgencyCalculator.Explain(t).Score)
                .ToList();

            foreach (var t in candidateTasks)
            {
                if (seenIds.Contains(t.Id))
                    continue;

                if (
                    string.IsNullOrEmpty(q)
                    || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (
                        t.CourseAlias?.Contains(
                            q.TrimStart('#'),
                            StringComparison.OrdinalIgnoreCase
                        ) == true
                    )
                    || (t.Term?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                    || t.Labels.Any(l =>
                        l.Contains(q.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    matchedTasks.Add(t);
                    if (matchedTasks.Count >= 18)
                        break;
                }
            }
        }

        foreach (var t in matchedTasks)
        {
            var sub = t.DueDate.HasValue
                ? $"Due {t.DueDate.Value:MMM d}"
                : (t.IsCompleted ? "Completed" : "No due date");
            if (!string.IsNullOrWhiteSpace(t.ParentTaskId))
                sub = $"Subtask · {sub}";
            var badge = !string.IsNullOrWhiteSpace(t.CourseAlias)
                ? $"#{t.CourseAlias}"
                : $"P{(int)t.Priority}";
            results.Add(new SearchResultItem(SearchResultKind.Task, t.Title, sub, badge, Task: t));

            if (results.Count >= 18)
                break;
        }

        // 3. Matching Master PDFs & Handouts
        if (!string.IsNullOrWhiteSpace(BasinRoot) && Directory.Exists(BasinRoot))
        {
            foreach (var c in Courses.Where(c => !c.IsArchived))
            {
                var masterPdf = MasterPdfService.FindExistingMasterPdfPath(BasinRoot, c.FolderName);
                if (
                    masterPdf is not null
                    && (
                        string.IsNullOrEmpty(q)
                        || q.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                        || q.Contains("master", StringComparison.OrdinalIgnoreCase)
                        || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || c.Alias.Contains(q.TrimStart('#'), StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    results.Add(
                        new SearchResultItem(
                            SearchResultKind.PdfDocument,
                            $"{c.Title} — Master Study Packet",
                            Path.GetFileName(masterPdf),
                            "Master PDF",
                            Course: c,
                            PdfPath: masterPdf
                        )
                    );
                }
            }
        }

        // 4. Matching Test & Recall Quizzes (by title, term, course, or question prompt/tag)
        foreach (var qz in QuizStudio.SavedQuizzes)
        {
            bool matchesQuiz =
                string.IsNullOrEmpty(q)
                || qz.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || qz.CourseTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (qz.Term?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || q.Contains("quiz", StringComparison.OrdinalIgnoreCase)
                || q.Contains("test", StringComparison.OrdinalIgnoreCase)
                || q.Contains("recall", StringComparison.OrdinalIgnoreCase)
                || qz.Questions.Any(qq =>
                    qq.Prompt.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || qq.TopicTag.Contains(q, StringComparison.OrdinalIgnoreCase)
                );

            if (matchesQuiz)
            {
                var sub =
                    $"{qz.CourseTitle} · {qz.Questions.Count} items"
                    + (
                        qz.HasInProgressAttempt
                            ? $" · In progress (Q{(qz.InProgressQuestionIndex ?? 0) + 1})"
                            : ""
                    );
                results.Add(
                    new SearchResultItem(
                        SearchResultKind.Quiz,
                        qz.Title,
                        sub,
                        qz.Term ?? "Quiz",
                        Quiz: qz
                    )
                );
            }
        }

        SearchResults = [.. results.Take(16)];
        SelectedSearchResultIndex = 0;
    }
}
