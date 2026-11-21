using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;
using ScholarFlow.Desktop.Services;
using ScholarFlow.Desktop.Theme;
using ScholarFlow.Desktop.UI;
using ScholarFlow.Desktop.ViewModels;

namespace ScholarFlow.Desktop.Views;

/// <summary>
/// Builds the responsive Course Workspace view with Master PDF action strip, Tasks vs Materials sub-tabs, and clean module/handout browser.
/// </summary>
public static class CourseWorkspaceViewBuilder
{
    public static Control Build(
        MainWindowViewModel vm,
        BasinCourseEntry course,
        Func<MainWindowViewModel, Control> buildTaskList
    )
    {
        var coursePath = Path.Combine(vm.BasinRoot, course.FolderName);
        var masterPdfPath =
            Core.Pdf.MasterPdfService.FindExistingMasterPdfPath(vm.BasinRoot, course.FolderName)
            ?? Core.Pdf.MasterPdfService.GetExpectedMasterPdfPath(vm.BasinRoot, course.FolderName);
        bool hasMasterPdf = File.Exists(masterPdfPath);

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(24, 16, 24, 12),
        };

        // 1. Course Header & Responsive Action Strip
        var headerStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };

        var titleWrap = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };

        var titleLeft = new StackPanel { Spacing = 3 };
        var titleRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(
            new TextBlock
            {
                Text = course.Title,
                FontSize = 20,
                FontWeight = FontWeight.Bold,
                Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                Margin = new Thickness(0, 0, 8, 0),
            }
        );
        titleRow.Children.Add(
            UiBuilder.Pill(
                $"#{course.Alias}",
                ThemeService.Brush(ThemeService.TextSecondary),
                ThemeService.Brush(ThemeService.BgElevated)
            )
        );
        titleLeft.Children.Add(titleRow);

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(course.CourseCode))
            metaParts.Add(course.CourseCode);
        if (!string.IsNullOrWhiteSpace(course.SectionCode))
            metaParts.Add(course.SectionCode);
        if (hasMasterPdf)
        {
            var info = new FileInfo(masterPdfPath);
            metaParts.Add(
                $"Master PDF · {info.Length / 1024.0 / 1024.0:F1} MB · Updated {info.LastWriteTime:MMM d}"
            );
        }
        else
        {
            metaParts.Add("Master PDF not yet compiled");
        }

        titleLeft.Children.Add(
            new TextBlock
            {
                Text = string.Join(" · ", metaParts),
                FontSize = 12,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
            }
        );
        titleWrap.Children.Add(titleLeft);
        headerStack.Children.Add(titleWrap);

        // Responsive WrapPanel for Course Actions
        var actionWrap = new WrapPanel { Orientation = Orientation.Horizontal };

        var addBtn = UiBuilder.ActionButton(
            "Add Task",
            MaterialIconKind.Plus,
            vm.OpenQuickAddModal,
            isPrimary: true
        );
        addBtn.Margin = new Thickness(0, 0, 6, 6);
        actionWrap.Children.Add(addBtn);

        if (vm.SupportsActivePerCourseSync && course.IsSyncableCourse)
        {
            var syncCourseBtn = UiBuilder.GhostButton(
                $"Sync from {vm.ActiveProviderName}",
                MaterialIconKind.CloudSyncOutline,
                async () => await vm.RunCourseSyncAsync(course)
            );
            syncCourseBtn.Margin = new Thickness(0, 0, 6, 6);
            actionWrap.Children.Add(syncCourseBtn);
        }

        var importPdfBtn = UiBuilder.GhostButton(
            "Import PDFs",
            MaterialIconKind.FilePlusOutline,
            async () =>
            {
                var top = TopLevel.GetTopLevel(actionWrap);
                if (top?.StorageProvider is null)
                    return;
                var files = await top.StorageProvider.OpenFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = $"Import Study PDFs into {course.Title}",
                        AllowMultiple = true,
                        FileTypeFilter =
                        [
                            new Avalonia.Platform.Storage.FilePickerFileType("PDF Documents")
                            {
                                Patterns = ["*.pdf"],
                            },
                        ],
                    }
                );
                var paths = files
                    .Select(f =>
                        Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f)
                    )
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Cast<string>()
                    .ToList();
                if (paths.Count > 0)
                    await vm.ImportPdfsToCourseAsync(course, paths);
            }
        );
        importPdfBtn.Margin = new Thickness(0, 0, 6, 6);
        actionWrap.Children.Add(importPdfBtn);

        if (hasMasterPdf)
        {
            var viewPdfBtn = UiBuilder.GhostButton(
                "View Master PDF",
                MaterialIconKind.FilePdfBox,
                () => vm.OpenPdfInReader(masterPdfPath),
                isActive: true
            );
            viewPdfBtn.Margin = new Thickness(0, 0, 6, 6);
            actionWrap.Children.Add(viewPdfBtn);

            var quizBtn = UiBuilder.GhostButton(
                "Generate Quiz",
                MaterialIconKind.HeadLightbulbOutline,
                async () => await vm.SelectQuizStudioViewAsync(course, masterPdfPath)
            );
            quizBtn.Margin = new Thickness(0, 0, 6, 6);
            actionWrap.Children.Add(quizBtn);
        }

        var compileBtn = UiBuilder.GhostButton(
            hasMasterPdf ? "Recompile PDF" : "Compile Master PDF",
            MaterialIconKind.HammerWrench,
            async () => await vm.CompileCourseMasterPdfAsync(course, force: true)
        );
        compileBtn.Margin = new Thickness(0, 0, 6, 6);
        actionWrap.Children.Add(compileBtn);

        var organizeBtn = UiBuilder.GhostButton(
            "Organize Packet",
            MaterialIconKind.FileDocumentEditOutline,
            () => vm.OpenPacketOrganizerModal(course)
        );
        organizeBtn.Margin = new Thickness(0, 0, 6, 6);
        actionWrap.Children.Add(organizeBtn);

        if (!string.IsNullOrWhiteSpace(course.CourseUrl))
        {
            var elmsBtn = UiBuilder.GhostButton(
                $"Open in {vm.ActiveSyncProvider.DisplayName}",
                MaterialIconKind.OpenInApp,
                async () => await vm.OpenUrlInProviderAppAsync(course.CourseUrl)
            );
            elmsBtn.Margin = new Thickness(0, 0, 6, 6);
            actionWrap.Children.Add(elmsBtn);
        }

        var deleteCourseBtn = UiBuilder.GhostButton(
            "Delete Course",
            MaterialIconKind.TrashCanOutline,
            () => vm.BeginConfirmDeleteCourse(course)
        );
        deleteCourseBtn.Margin = new Thickness(0, 0, 6, 6);
        actionWrap.Children.Add(deleteCourseBtn);

        headerStack.Children.Add(actionWrap);

        if (
            string.Equals(
                vm.ConfirmingDeleteCourseFolder,
                course.FolderName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            var confirmWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            confirmWrap.Children.Add(
                new TextBlock
                {
                    Text = $"Remove '{course.Title}'?",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 4),
                }
            );

            var unlinkBtn = UiBuilder.GhostButton(
                "Remove Only (Keep Folder)",
                MaterialIconKind.LinkOff,
                async () => await vm.DeleteCourseAsync(course, deleteFolderFromDisk: false)
            );
            unlinkBtn.Margin = new Thickness(0, 0, 6, 4);
            confirmWrap.Children.Add(unlinkBtn);

            var delDiskBtn = UiBuilder.GhostButton(
                "Delete Course & Folder",
                MaterialIconKind.DeleteOutline,
                async () => await vm.DeleteCourseAsync(course, deleteFolderFromDisk: true)
            );
            delDiskBtn.Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg);
            delDiskBtn.Margin = new Thickness(0, 0, 6, 4);
            confirmWrap.Children.Add(delDiskBtn);

            var cancelDelBtn = UiBuilder.GhostButton("Cancel", null, vm.CancelConfirmDeleteCourse);
            cancelDelBtn.Margin = new Thickness(0, 0, 0, 4);
            confirmWrap.Children.Add(cancelDelBtn);

            headerStack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgElevated),
                    BorderBrush = ThemeService.Brush(ThemeService.PriorityP1Fg),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7),
                    Child = confirmWrap,
                }
            );
        }

        root.Children.Add(headerStack);

        // 2. Clean Sub-Tab Switcher (Tasks vs Modules & Handouts)
        var subTabsPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        var subTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        subTabs.Children.Add(
            UiBuilder.GhostButton(
                $"Tasks ({vm.VisibleTasks.Count})",
                MaterialIconKind.CheckboxMarkedCircleOutline,
                () => vm.SetCourseSubTab(showMaterials: false),
                isActive: !vm.ShowCourseMaterialsSubTab
            )
        );

        subTabs.Children.Add(
            UiBuilder.GhostButton(
                "Files",
                MaterialIconKind.FolderOpenOutline,
                () => vm.SetCourseSubTab(showMaterials: true),
                isActive: vm.ShowCourseMaterialsSubTab
            )
        );

        subTabsPanel.Children.Add(subTabs);
        subTabsPanel.Children.Add(UiBuilder.HairlineDivider());
        Grid.SetRow(subTabsPanel, 1);
        root.Children.Add(subTabsPanel);

        // 3. Body
        var bodyContent = vm.ShowCourseMaterialsSubTab
            ? BuildMaterialsTree(vm, course, coursePath)
            : buildTaskList(vm);

        Grid.SetRow(bodyContent, 2);
        root.Children.Add(bodyContent);

        return root;
    }

    private static readonly HashSet<string> InternalCourseManifestFiles = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "packet.json",
        "term.json",
        "course.json",
        "syllabus.json",
    };

    private static Control BuildMaterialsTree(
        MainWindowViewModel vm,
        BasinCourseEntry course,
        string coursePath
    )
    {
        var outerGrid = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            RowSpacing = 8,
        };

        Directory.CreateDirectory(coursePath);

        // Enumerate subdirectories and loose root files
        var allDirs = Directory
            .GetDirectories(coursePath)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d =>
                string.Equals(Path.GetFileName(d), "Master_PDF", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0
            )
            .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var editableDirs = allDirs
            .Where(d =>
                !string.Equals(
                    Path.GetFileName(d),
                    "Master_PDF",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        var looseRootFiles = Directory
            .GetFiles(coursePath)
            .Where(f =>
            {
                var fn = Path.GetFileName(f);
                return !fn.StartsWith('.') && !InternalCourseManifestFiles.Contains(fn);
            })
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Compute file counts across all folders for type filter pills
        var dirFilesMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int totalFiles = looseRootFiles.Count;
        int totalPdfs = looseRootFiles.Count(IsPdfFile);
        foreach (var d in allDirs)
        {
            var files = Directory
                .GetFiles(d)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            dirFilesMap[d] = files;
            totalFiles += files.Count;
            totalPdfs += files.Count(IsPdfFile);
        }
        int totalOther = totalFiles - totalPdfs;

        // 1. Top Organizer Toolbar
        var toolbar = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8,
        };

        var leftControls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 2, 0, 20) };
        Action? refreshMaterialsList = null;

        var searchBox = new TextBox
        {
            Text = vm.MaterialsSearchQuery,
            Watermark = "Search files & folders...",
            FontSize = 12,
            Width = 220,
            Margin = new Thickness(0, 0, 6, 4),
        };
        var clearSearchBtn = UiBuilder.IconButton(
            MaterialIconKind.Close,
            () =>
            {
                searchBox.Text = string.Empty;
                vm.SetMaterialsSearchQueryQuiet(string.Empty);
                refreshMaterialsList?.Invoke();
                searchBox.Focus();
            },
            13
        );
        clearSearchBtn.Margin = new Thickness(0, 0, 6, 4);
        clearSearchBtn.IsVisible = !string.IsNullOrWhiteSpace(vm.MaterialsSearchQuery);

        searchBox.TextChanged += (_, _) =>
        {
            var next = searchBox.Text ?? string.Empty;
            vm.SetMaterialsSearchQueryQuiet(next);
            clearSearchBtn.IsVisible = !string.IsNullOrWhiteSpace(next);
            refreshMaterialsList?.Invoke();
        };
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(searchBox.Text))
            {
                e.Handled = true;
                searchBox.Text = string.Empty;
            }
        };
        leftControls.Children.Add(searchBox);
        leftControls.Children.Add(clearSearchBtn);

        void AddFilterPill(string key, string label)
        {
            var pill = UiBuilder.GhostButton(
                label,
                null,
                () => vm.SetMaterialsTypeFilter(key),
                isActive: string.Equals(
                    vm.MaterialsTypeFilter,
                    key,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            pill.Margin = new Thickness(0, 0, 4, 4);
            leftControls.Children.Add(pill);
        }

        AddFilterPill("All", $"All ({totalFiles})");
        AddFilterPill("PDFs", $"PDFs ({totalPdfs})");
        AddFilterPill("Other", $"Other ({totalOther})");

        toolbar.Children.Add(leftControls);

        var rightControls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var newFolderBtn = UiBuilder.GhostButton(
            "New Folder",
            MaterialIconKind.FolderPlusOutline,
            vm.ToggleNewMaterialFolderBar,
            isActive: vm.IsNewMaterialFolderBarOpen
        );
        newFolderBtn.Margin = new Thickness(0, 0, 4, 4);
        rightControls.Children.Add(newFolderBtn);

        var importAnyBtn = UiBuilder.GhostButton(
            "Import Files",
            MaterialIconKind.FileUploadOutline,
            async () => await PickAndImportFilesAsync(outerGrid, vm, course, coursePath)
        );
        importAnyBtn.Margin = new Thickness(0, 0, 4, 4);
        rightControls.Children.Add(importAnyBtn);

        var allCollapsibleKeys = allDirs.ToList();
        if (looseRootFiles.Count > 0)
            allCollapsibleKeys.Add(coursePath);
        bool allCollapsed =
            allCollapsibleKeys.Count > 0
            && allCollapsibleKeys.All(k => vm.CollapsedMaterialFolders.Contains(k));
        if (allCollapsibleKeys.Count > 0)
        {
            var collapseBtn = UiBuilder.GhostButton(
                allCollapsed ? "Expand All" : "Collapse All",
                allCollapsed
                    ? MaterialIconKind.UnfoldMoreHorizontal
                    : MaterialIconKind.UnfoldLessHorizontal,
                () => vm.SetAllMaterialFoldersCollapsed(allCollapsibleKeys, !allCollapsed)
            );
            collapseBtn.Margin = new Thickness(0, 0, 4, 4);
            rightControls.Children.Add(collapseBtn);
        }

        var openFolderBtn = UiBuilder.IconButton(
            MaterialIconKind.FolderOpenOutline,
            () => ExternalFileLauncher.OpenLocalFile(coursePath),
            15
        );
        openFolderBtn.Margin = new Thickness(0, 0, 0, 4);
        rightControls.Children.Add(openFolderBtn);

        Grid.SetColumn(rightControls, 1);
        toolbar.Children.Add(rightControls);
        outerGrid.Children.Add(toolbar);

        // 2. Inline "New Folder" Strip (when toggled open)
        if (vm.IsNewMaterialFolderBarOpen)
        {
            var newFolderGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 6,
            };
            var folderNameBox = new TextBox
            {
                Watermark = "New module/folder name (e.g. 07_Midterm_Review, Lab_Worksheets)...",
                FontSize = 12,
            };
            void SubmitNewFolder()
            {
                if (!string.IsNullOrWhiteSpace(folderNameBox.Text))
                    vm.CreateMaterialFolder(course, folderNameBox.Text);
            }
            folderNameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    SubmitNewFolder();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    vm.ToggleNewMaterialFolderBar();
                }
            };
            newFolderGrid.Children.Add(folderNameBox);

            var createFolderBtn = UiBuilder.PrimaryButton(
                "Create",
                MaterialIconKind.Check,
                SubmitNewFolder
            );
            Grid.SetColumn(createFolderBtn, 1);
            newFolderGrid.Children.Add(createFolderBtn);

            var cancelFolderBtn = UiBuilder.GhostButton(
                "Cancel",
                null,
                vm.ToggleNewMaterialFolderBar
            );
            Grid.SetColumn(cancelFolderBtn, 2);
            newFolderGrid.Children.Add(cancelFolderBtn);

            var stripBorder = new Border
            {
                Background = ThemeService.Brush(ThemeService.BgElevated),
                BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8),
                Child = newFolderGrid,
            };
            Grid.SetRow(stripBorder, 1);
            outerGrid.Children.Add(stripBorder);
        }

        // 3. Scrollable Folders & Files Tree (live-filtered as you type via QuickSearchMatcher)
        bool MatchesTypeFilter(string filePath) =>
            vm.MaterialsTypeFilter switch
            {
                "PDFs" => IsPdfFile(filePath),
                "Other" => !IsPdfFile(filePath),
                _ => true,
            };

        refreshMaterialsList = () =>
        {
            stack.Children.Clear();
            var query = vm.MaterialsSearchQuery;
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            int renderedGroups = 0;

            // Render Loose Files (Course Root) if any exist
            if (looseRootFiles.Count > 0)
            {
                var filteredRoot = looseRootFiles
                    .Where(f =>
                        MatchesTypeFilter(f)
                        && (
                            !hasQuery
                            || QuickSearchMatcher.Matches(
                                query,
                                Path.GetFileName(f),
                                Path.GetFileName(f).Replace('_', ' '),
                                "root"
                            )
                        )
                    )
                    .ToList();

                if (filteredRoot.Count > 0 || (!hasQuery && vm.MaterialsTypeFilter == "All"))
                {
                    stack.Children.Add(
                        BuildMaterialGroupCard(
                            outerGrid,
                            vm,
                            course,
                            coursePath,
                            folderPath: coursePath,
                            displayTitle: "Loose Files (Course Root)",
                            files: filteredRoot,
                            totalUnfilteredCount: looseRootFiles.Count,
                            editableDirs: editableDirs,
                            isRootGroup: true,
                            isCompiledOutput: false,
                            editableIndex: -1,
                            editableCount: editableDirs.Count
                        )
                    );
                    renderedGroups++;
                }
            }

            // Render Folders (including empty folders when no search/type filter hides them)
            for (int i = 0; i < allDirs.Count; i++)
            {
                var dir = allDirs[i];
                var dirName = Path.GetFileName(dir);
                var displayDirName = dirName.Replace('_', ' ');
                bool isMasterDir = string.Equals(
                    dirName,
                    "Master_PDF",
                    StringComparison.OrdinalIgnoreCase
                );
                var rawFiles = dirFilesMap[dir];
                bool folderNameMatches =
                    hasQuery && QuickSearchMatcher.Matches(query, dirName, displayDirName);

                var filteredFiles = rawFiles
                    .Where(f =>
                        MatchesTypeFilter(f)
                        && (
                            !hasQuery
                            || folderNameMatches
                            || QuickSearchMatcher.Matches(
                                query,
                                Path.GetFileName(f),
                                Path.GetFileName(f).Replace('_', ' '),
                                dirName,
                                displayDirName
                            )
                        )
                    )
                    .ToList();

                // Hide empty/non-matching folders only when actively filtering
                if (
                    (hasQuery || vm.MaterialsTypeFilter != "All")
                    && filteredFiles.Count == 0
                    && !folderNameMatches
                )
                    continue;

                int editIdx = isMasterDir
                    ? -1
                    : editableDirs.FindIndex(d =>
                        string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)
                    );

                stack.Children.Add(
                    BuildMaterialGroupCard(
                        outerGrid,
                        vm,
                        course,
                        coursePath,
                        folderPath: dir,
                        displayTitle: displayDirName,
                        files: filteredFiles,
                        totalUnfilteredCount: rawFiles.Count,
                        editableDirs: editableDirs,
                        isRootGroup: false,
                        isCompiledOutput: isMasterDir,
                        editableIndex: editIdx,
                        editableCount: editableDirs.Count
                    )
                );
                renderedGroups++;
            }

            if (renderedGroups == 0)
            {
                stack.Children.Add(
                    new Border
                    {
                        Background = ThemeService.Brush(ThemeService.BgElevated),
                        BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(16, 18),
                        Child = new StackPanel
                        {
                            Spacing = 6,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = hasQuery || vm.MaterialsTypeFilter != "All"
                                        ? "No files or folders match the current search."
                                        : "No files in this course yet.",
                                    FontSize = 13,
                                    FontWeight = FontWeight.SemiBold,
                                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                },
                                new TextBlock
                                {
                                    Text = hasQuery || vm.MaterialsTypeFilter != "All"
                                        ? "Try clearing the search query or switching back to All."
                                        : "Use New Folder or Import Files above to organize course files.",
                                    FontSize = 11.5,
                                    Foreground = ThemeService.Brush(ThemeService.TextMuted),
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                },
                            },
                        },
                    }
                );
            }
        };

        refreshMaterialsList();

        var scroller = UiBuilder.PersistentScrollViewer(
            $"course-workspace:{course.FolderName}",
            stack
        );
        Grid.SetRow(scroller, 2);
        outerGrid.Children.Add(scroller);
        return outerGrid;
    }

    private static Control BuildMaterialGroupCard(
        Control anchorControl,
        MainWindowViewModel vm,
        BasinCourseEntry course,
        string coursePath,
        string folderPath,
        string displayTitle,
        List<string> files,
        int totalUnfilteredCount,
        List<string> editableDirs,
        bool isRootGroup,
        bool isCompiledOutput,
        int editableIndex,
        int editableCount
    )
    {
        bool isCollapsed = vm.CollapsedMaterialFolders.Contains(folderPath);
        bool isRenaming = string.Equals(
            vm.RenamingMaterialPath,
            folderPath,
            StringComparison.OrdinalIgnoreCase
        );
        bool isConfirmingDelete = string.Equals(
            vm.ConfirmingDeleteMaterialPath,
            folderPath,
            StringComparison.OrdinalIgnoreCase
        );

        var cardStack = new StackPanel { Spacing = 6 };

        // Folder Header Row
        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto,Auto"),
            ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var collapseBtn = UiBuilder.IconButton(
            isCollapsed ? MaterialIconKind.ChevronRight : MaterialIconKind.ChevronDown,
            () => vm.ToggleMaterialFolderCollapsed(folderPath),
            15
        );
        headerGrid.Children.Add(collapseBtn);

        var folderIcon = UiBuilder.Icon(
            isCompiledOutput
                ? MaterialIconKind.BookCheckOutline
                : isRootGroup
                    ? MaterialIconKind.FolderHomeOutline
                    : isCollapsed
                        ? MaterialIconKind.FolderOutline
                        : MaterialIconKind.FolderOpenOutline,
            16,
            isCompiledOutput
                ? ThemeService.Brush(ThemeService.SuccessFg)
                : ThemeService.Brush(ThemeService.AccentPrimary)
        );
        folderIcon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(folderIcon, 1);
        headerGrid.Children.Add(folderIcon);

        if (isRenaming && !isRootGroup && !isCompiledOutput)
        {
            var renameRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 4,
            };
            var renameBox = new TextBox
            {
                Text = Path.GetFileName(folderPath),
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
            };
            void SubmitRename()
            {
                if (!string.IsNullOrWhiteSpace(renameBox.Text))
                    vm.RenameMaterialFolder(course, folderPath, renameBox.Text);
                else
                    vm.CancelRenameMaterial();
            }
            renameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    SubmitRename();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    vm.CancelRenameMaterial();
                }
            };
            renameRow.Children.Add(renameBox);

            var saveBtn = UiBuilder.IconButton(
                MaterialIconKind.Check,
                SubmitRename,
                14,
                ThemeService.Brush(ThemeService.SuccessFg)
            );
            Grid.SetColumn(saveBtn, 1);
            renameRow.Children.Add(saveBtn);

            var cancelBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                vm.CancelRenameMaterial,
                14
            );
            Grid.SetColumn(cancelBtn, 2);
            renameRow.Children.Add(cancelBtn);

            Grid.SetColumn(renameRow, 2);
            headerGrid.Children.Add(renameRow);
        }
        else
        {
            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            titlePanel.Children.Add(
                new TextBlock
                {
                    Text = displayTitle,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            if (isCompiledOutput)
            {
                titlePanel.Children.Add(
                    UiBuilder.Pill(
                        "Compiled Output",
                        ThemeService.Brush(ThemeService.SuccessFg),
                        ThemeService.Brush(ThemeService.BgSurface),
                        fontSize: 10
                    )
                );
            }
            Grid.SetColumn(titlePanel, 2);
            headerGrid.Children.Add(titlePanel);
        }

        var countLabel =
            files.Count == totalUnfilteredCount
                ? $"{totalUnfilteredCount} file{(totalUnfilteredCount == 1 ? "" : "s")}"
                : $"{files.Count}/{totalUnfilteredCount}";
        var countPill = UiBuilder.Pill(
            countLabel,
            ThemeService.Brush(ThemeService.TextSecondary),
            ThemeService.Brush(ThemeService.BgSurface),
            fontSize: 10
        );
        countPill.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(countPill, 3);
        headerGrid.Children.Add(countPill);

        // Folder action buttons
        var folderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!isCompiledOutput)
        {
            folderActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.FilePlusOutline,
                    async () => await PickAndImportFilesAsync(anchorControl, vm, course, folderPath),
                    14
                )
            );
        }

        if (!isRootGroup && !isCompiledOutput)
        {
            var upBtn = UiBuilder.IconButton(
                MaterialIconKind.ArrowUp,
                () => vm.ReorderMaterialFolder(course, folderPath, -1),
                14
            );
            upBtn.IsEnabled = editableIndex > 0;
            folderActions.Children.Add(upBtn);

            var downBtn = UiBuilder.IconButton(
                MaterialIconKind.ArrowDown,
                () => vm.ReorderMaterialFolder(course, folderPath, +1),
                14
            );
            downBtn.IsEnabled = editableIndex >= 0 && editableIndex < editableCount - 1;
            folderActions.Children.Add(downBtn);

            folderActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.PencilOutline,
                    () => vm.BeginRenameMaterial(folderPath),
                    14
                )
            );

            folderActions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.DeleteOutline,
                    () =>
                    {
                        if (totalUnfilteredCount == 0)
                            vm.DeleteMaterialFolder(course, folderPath, moveFilesToRootFirst: false);
                        else
                            vm.BeginConfirmDeleteMaterial(folderPath);
                    },
                    14
                )
            );
        }

        Grid.SetColumn(folderActions, 4);
        headerGrid.Children.Add(folderActions);
        cardStack.Children.Add(headerGrid);

        // Inline Folder Delete Confirmation Strip (for non-empty folders)
        if (isConfirmingDelete && !isRootGroup && !isCompiledOutput)
        {
            var confirmWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            confirmWrap.Children.Add(
                new TextBlock
                {
                    Text =
                        $"Remove '{displayTitle}' ({totalUnfilteredCount} file{(totalUnfilteredCount == 1 ? "" : "s")})?",
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 4),
                }
            );

            var moveAndDelBtn = UiBuilder.GhostButton(
                "Move Files to Root & Remove",
                MaterialIconKind.FolderMoveOutline,
                () => vm.DeleteMaterialFolder(course, folderPath, moveFilesToRootFirst: true)
            );
            moveAndDelBtn.Margin = new Thickness(0, 0, 6, 4);
            confirmWrap.Children.Add(moveAndDelBtn);

            var delAllBtn = UiBuilder.GhostButton(
                "Delete Folder & Files",
                MaterialIconKind.DeleteOutline,
                () => vm.DeleteMaterialFolder(course, folderPath, moveFilesToRootFirst: false)
            );
            delAllBtn.Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg);
            delAllBtn.Margin = new Thickness(0, 0, 6, 4);
            confirmWrap.Children.Add(delAllBtn);

            var cancelDelBtn = UiBuilder.GhostButton("Cancel", null, vm.CancelConfirmDeleteMaterial);
            cancelDelBtn.Margin = new Thickness(0, 0, 0, 4);
            confirmWrap.Children.Add(cancelDelBtn);

            cardStack.Children.Add(
                new Border
                {
                    Background = ThemeService.Brush(ThemeService.BgSurface),
                    BorderBrush = ThemeService.Brush(ThemeService.PriorityP1Fg),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6),
                    Child = confirmWrap,
                }
            );
        }

        // File Rows
        if (!isCollapsed)
        {
            if (files.Count == 0)
            {
                cardStack.Children.Add(
                    new TextBlock
                    {
                        Text = "Empty folder — click + above to import files or move files here.",
                        FontSize = 11.5,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                        Margin = new Thickness(26, 2, 0, 2),
                    }
                );
            }
            else
            {
                var filesPanel = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2, 0, 0) };
                foreach (var file in files)
                {
                    filesPanel.Children.Add(
                        BuildMaterialFileRow(
                            vm,
                            course,
                            coursePath,
                            file,
                            editableDirs,
                            isCompiledOutput
                        )
                    );
                }
                cardStack.Children.Add(filesPanel);
            }
        }

        return new Border
        {
            Background = ThemeService.Brush(ThemeService.BgElevated),
            BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = cardStack,
        };
    }

    private static Control BuildMaterialFileRow(
        MainWindowViewModel vm,
        BasinCourseEntry course,
        string coursePath,
        string file,
        List<string> editableDirs,
        bool isCompiledOutput
    )
    {
        var fileName = Path.GetFileName(file);
        var isPdf = IsPdfFile(file);
        bool isRenaming = string.Equals(
            vm.RenamingMaterialPath,
            file,
            StringComparison.OrdinalIgnoreCase
        );
        bool isConfirmingDelete = string.Equals(
            vm.ConfirmingDeleteMaterialPath,
            file,
            StringComparison.OrdinalIgnoreCase
        );

        var fileRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("22,*,Auto"),
            ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var fileIcon = UiBuilder.Icon(
            GetFileIconKind(file),
            15,
            isPdf
                ? ThemeService.Brush(ThemeService.AccentPrimary)
                : ThemeService.Brush(ThemeService.TextMuted)
        );
        fileIcon.VerticalAlignment = VerticalAlignment.Center;
        fileRow.Children.Add(fileIcon);

        if (isRenaming)
        {
            var renameRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
                ColumnSpacing = 4,
            };
            var renameBox = new TextBox
            {
                Text = fileName,
                FontSize = 12.5,
            };
            void SubmitFileRename()
            {
                if (!string.IsNullOrWhiteSpace(renameBox.Text))
                    vm.RenameMaterialFile(course, file, renameBox.Text);
                else
                    vm.CancelRenameMaterial();
            }
            renameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    SubmitFileRename();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    vm.CancelRenameMaterial();
                }
            };
            renameRow.Children.Add(renameBox);

            var saveBtn = UiBuilder.IconButton(
                MaterialIconKind.Check,
                SubmitFileRename,
                14,
                ThemeService.Brush(ThemeService.SuccessFg)
            );
            Grid.SetColumn(saveBtn, 1);
            renameRow.Children.Add(saveBtn);

            var cancelBtn = UiBuilder.IconButton(
                MaterialIconKind.Close,
                vm.CancelRenameMaterial,
                14
            );
            Grid.SetColumn(cancelBtn, 2);
            renameRow.Children.Add(cancelBtn);

            Grid.SetColumn(renameRow, 1);
            fileRow.Children.Add(renameRow);
        }
        else
        {
            var metaText = FormatFileMetadata(file);
            var infoStack = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = fileName,
                        FontSize = 12.5,
                        Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    },
                    new TextBlock
                    {
                        Text = metaText,
                        FontSize = 10,
                        Foreground = ThemeService.Brush(ThemeService.TextMuted),
                    },
                },
            };
            Grid.SetColumn(infoStack, 1);
            fileRow.Children.Add(infoStack);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (isConfirmingDelete)
        {
            var confirmDelBtn = UiBuilder.GhostButton(
                "Delete",
                MaterialIconKind.DeleteOutline,
                () => vm.DeleteMaterialFile(course, file)
            );
            confirmDelBtn.Foreground = ThemeService.Brush(ThemeService.PriorityP1Fg);
            actions.Children.Add(confirmDelBtn);
            actions.Children.Add(
                UiBuilder.GhostButton("Cancel", null, vm.CancelConfirmDeleteMaterial)
            );
        }
        else if (!isRenaming)
        {
            if (isPdf)
            {
                actions.Children.Add(
                    UiBuilder.GhostButton(
                        "Read",
                        MaterialIconKind.BookOpenOutline,
                        () => vm.OpenPdfInReader(file)
                    )
                );
                actions.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.HeadLightbulbOutline,
                        async () => await vm.SelectQuizStudioViewAsync(course, file),
                        14
                    )
                );
            }

            if (!isCompiledOutput)
            {
                var currentParent = Path.GetDirectoryName(file) ?? coursePath;
                var moveTargets = new List<(string Label, string DirPath)>();
                if (
                    !string.Equals(
                        Path.GetFullPath(currentParent),
                        Path.GetFullPath(coursePath),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    moveTargets.Add(("Course Root", coursePath));
                }
                foreach (var d in editableDirs)
                {
                    if (
                        !string.Equals(
                            Path.GetFullPath(d),
                            Path.GetFullPath(currentParent),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        moveTargets.Add((Path.GetFileName(d).Replace('_', ' '), d));
                    }
                }

                if (moveTargets.Count > 0)
                {
                    Button? moveBtn = null;
                    moveBtn = UiBuilder.IconButton(
                        MaterialIconKind.FolderMoveOutline,
                        () =>
                        {
                            if (moveBtn is null)
                                return;

                            var flyout = new MenuFlyout();
                            foreach (var (label, targetPath) in moveTargets)
                            {
                                var capturedTarget = targetPath;
                                var mi = new MenuItem { Header = $"Move to {label}" };
                                mi.Click += (_, _) =>
                                    vm.MoveMaterialFile(course, file, capturedTarget);
                                flyout.Items.Add(mi);
                            }
                            flyout.ShowAt(moveBtn);
                        },
                        14
                    );
                    actions.Children.Add(moveBtn);
                }

                actions.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.PencilOutline,
                        () => vm.BeginRenameMaterial(file),
                        14
                    )
                );
            }

            actions.Children.Add(
                UiBuilder.IconButton(
                    MaterialIconKind.OpenInNew,
                    () => ExternalFileLauncher.OpenLocalFile(file),
                    14
                )
            );

            if (!isCompiledOutput)
            {
                actions.Children.Add(
                    UiBuilder.IconButton(
                        MaterialIconKind.DeleteOutline,
                        () => vm.BeginConfirmDeleteMaterial(file),
                        14
                    )
                );
            }
        }

        Grid.SetColumn(actions, 2);
        fileRow.Children.Add(actions);

        var rowBorder = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 5),
            Child = fileRow,
        };
        rowBorder.PointerEntered += (_, _) =>
            rowBorder.Background = ThemeService.Brush(ThemeService.BgHover);
        rowBorder.PointerExited += (_, _) => rowBorder.Background = Brushes.Transparent;

        return rowBorder;
    }

    private static async Task PickAndImportFilesAsync(
        Control anchorControl,
        MainWindowViewModel vm,
        BasinCourseEntry course,
        string targetDir
    )
    {
        var top = TopLevel.GetTopLevel(anchorControl);
        if (top?.StorageProvider is null)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = $"Import Course Files into {course.Title}",
                AllowMultiple = true,
            }
        );
        var paths = files
            .Select(f =>
                Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f)
            )
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();
        if (paths.Count > 0)
            await vm.ImportFilesToFolderAsync(course, targetDir, paths);
    }

    private static bool IsPdfFile(string path) =>
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static MaterialIconKind GetFileIconKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => MaterialIconKind.FilePdfBox,
            ".md" or ".txt" => MaterialIconKind.FileDocumentOutline,
            ".doc" or ".docx" => MaterialIconKind.FileWordOutline,
            ".ppt" or ".pptx" => MaterialIconKind.FilePowerpointOutline,
            ".xls" or ".xlsx" or ".csv" => MaterialIconKind.FileExcelOutline,
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg" =>
                MaterialIconKind.FileImageOutline,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => MaterialIconKind.FolderZipOutline,
            _ => MaterialIconKind.FileOutline,
        };
    }

    private static string FormatFileMetadata(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(ext))
                ext = "FILE";
            string size = fi.Length switch
            {
                < 1024 => $"{fi.Length} B",
                < 1024 * 1024 => $"{fi.Length / 1024.0:0.#} KB",
                _ => $"{fi.Length / (1024.0 * 1024.0):0.#} MB",
            };
            return $"{ext} · {size}";
        }
        catch
        {
            return Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        }
    }
}
