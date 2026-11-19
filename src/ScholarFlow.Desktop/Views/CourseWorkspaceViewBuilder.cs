using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using ScholarFlow.Core.Models;
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
    public static Control Build(MainWindowViewModel vm, BasinCourseEntry course, Func<MainWindowViewModel, Control> buildTaskList)
    {
        var coursePath = Path.Combine(vm.BasinRoot, course.FolderName);
        var masterPdfPath = ScholarFlow.Core.Pdf.MasterPdfService.FindExistingMasterPdfPath(vm.BasinRoot, course.FolderName)
                            ?? ScholarFlow.Core.Pdf.MasterPdfService.GetExpectedMasterPdfPath(vm.BasinRoot, course.FolderName);
        bool hasMasterPdf = File.Exists(masterPdfPath);

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(24, 16, 24, 12)
        };

        // 1. Course Header & Responsive Action Strip
        var headerStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };

        var titleWrap = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto")
        };

        var titleLeft = new StackPanel { Spacing = 3 };
        var titleRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock
        {
            Text = course.Title,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeService.Brush(ThemeService.TextPrimary),
            Margin = new Thickness(0, 0, 8, 0)
        });
        titleRow.Children.Add(UiBuilder.Pill(
            $"#{course.Alias}",
            ThemeService.Brush(ThemeService.TextSecondary),
            ThemeService.Brush(ThemeService.BgElevated)));
        titleLeft.Children.Add(titleRow);

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(course.CourseCode)) metaParts.Add(course.CourseCode);
        if (!string.IsNullOrWhiteSpace(course.SectionCode)) metaParts.Add(course.SectionCode);
        if (hasMasterPdf)
        {
            var info = new FileInfo(masterPdfPath);
            metaParts.Add($"Master PDF · {info.Length / 1024.0 / 1024.0:F1} MB · Updated {info.LastWriteTime:MMM d}");
        }
        else
        {
            metaParts.Add("Master PDF not yet compiled");
        }

        titleLeft.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", metaParts),
            FontSize = 12,
            Foreground = ThemeService.Brush(ThemeService.TextSecondary)
        });
        titleWrap.Children.Add(titleLeft);
        headerStack.Children.Add(titleWrap);

        // Responsive WrapPanel for Course Actions
        var actionWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };

        var addBtn = UiBuilder.ActionButton(
            "Add Task",
            MaterialIconKind.Plus,
            () => vm.OpenQuickAddModal(),
            isPrimary: true);
        addBtn.Margin = new Thickness(0, 0, 6, 6);
        actionWrap.Children.Add(addBtn);

        if (hasMasterPdf)
        {
            var viewPdfBtn = UiBuilder.GhostButton(
                "View Master PDF",
                MaterialIconKind.FilePdfBox,
                () => vm.OpenPdfInReader(masterPdfPath),
                isActive: true);
            viewPdfBtn.Margin = new Thickness(0, 0, 6, 6);
            actionWrap.Children.Add(viewPdfBtn);

            var quizBtn = UiBuilder.GhostButton(
                "Generate Quiz",
                MaterialIconKind.HeadLightbulbOutline,
                async () => await vm.SelectQuizStudioViewAsync(course, masterPdfPath));
            quizBtn.Margin = new Thickness(0, 0, 6, 6);
            actionWrap.Children.Add(quizBtn);
        }

        var compileBtn = UiBuilder.GhostButton(
            hasMasterPdf ? "Recompile PDF" : "Compile Master PDF",
            MaterialIconKind.HammerWrench,
            async () => await vm.CompileCourseMasterPdfAsync(course, force: true));
        compileBtn.Margin = new Thickness(0, 0, 6, 6);
        actionWrap.Children.Add(compileBtn);

        if (!string.IsNullOrWhiteSpace(course.CourseUrl))
        {
            var elmsBtn = UiBuilder.GhostButton(
                "Open in eLMS",
                MaterialIconKind.OpenInApp,
                async () => await ExternalFileLauncher.OpenInElmsAppAndWatchAsync(course.CourseUrl, () => vm.RunFastPollAsync()));
            elmsBtn.Margin = new Thickness(0, 0, 6, 6);
            actionWrap.Children.Add(elmsBtn);
        }

        headerStack.Children.Add(actionWrap);
        root.Children.Add(headerStack);

        // 2. Clean Sub-Tab Switcher (Tasks vs Modules & Handouts)
        var subTabsPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        var subTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        subTabs.Children.Add(UiBuilder.GhostButton(
            $"Tasks ({vm.VisibleTasks.Count})",
            MaterialIconKind.CheckboxMarkedCircleOutline,
            () => vm.SetCourseSubTab(showMaterials: false),
            isActive: !vm.ShowCourseMaterialsSubTab));

        subTabs.Children.Add(UiBuilder.GhostButton(
            "Modules & Handouts",
            MaterialIconKind.FolderOpenOutline,
            () => vm.SetCourseSubTab(showMaterials: true),
            isActive: vm.ShowCourseMaterialsSubTab));

        subTabsPanel.Children.Add(subTabs);
        subTabsPanel.Children.Add(UiBuilder.HairlineDivider());
        Grid.SetRow(subTabsPanel, 1);
        root.Children.Add(subTabsPanel);

        // 3. Body
        var bodyContent = vm.ShowCourseMaterialsSubTab
            ? BuildMaterialsTree(vm, coursePath)
            : buildTaskList(vm);

        Grid.SetRow(bodyContent, 2);
        root.Children.Add(bodyContent);

        return root;
    }

    private static Control BuildMaterialsTree(MainWindowViewModel vm, string coursePath)
    {
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 4, 0, 20) };
        if (!Directory.Exists(coursePath))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Course directory does not exist on disk yet. Run Sync eLMS to download course materials.",
                Foreground = ThemeService.Brush(ThemeService.TextSecondary)
            });
            return stack;
        }

        var dirs = Directory.GetDirectories(coursePath)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in dirs)
        {
            var dirName = Path.GetFileName(dir);
            var files = Directory.GetFiles(dir)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0)
                continue;

            var sectionStack = new StackPanel { Spacing = 2 };
            sectionStack.Children.Add(new TextBlock
            {
                Text = dirName.Replace('_', ' '),
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeService.Brush(ThemeService.TextSecondary),
                Margin = new Thickness(2, 4, 0, 4)
            });

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

                var fileRow = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("22,*,Auto")
                };
                fileRow.Children.Add(UiBuilder.Icon(
                    isPdf ? MaterialIconKind.FilePdfBox : MaterialIconKind.FileDocumentOutline,
                    15,
                    isPdf ? ThemeService.Brush(ThemeService.AccentPrimary) : ThemeService.Brush(ThemeService.TextMuted)));

                fileRow.Children.Add(new TextBlock
                {
                    Text = fileName,
                    FontSize = 13,
                    Foreground = ThemeService.Brush(ThemeService.TextPrimary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [Grid.ColumnProperty] = 1
                });

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                if (isPdf)
                {
                    actions.Children.Add(UiBuilder.GhostButton("Read", MaterialIconKind.BookOpenOutline, () => vm.OpenPdfInReader(file)));
                }
                actions.Children.Add(UiBuilder.IconButton(MaterialIconKind.OpenInNew, () => ExternalFileLauncher.OpenLocalFile(file), 14));
                Grid.SetColumn(actions, 2);
                fileRow.Children.Add(actions);

                var rowBorder = new Border
                {
                    Background = Brushes.Transparent,
                    BorderBrush = ThemeService.Brush(ThemeService.BorderSubtle),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(6, 6),
                    Child = fileRow
                };
                rowBorder.PointerEntered += (_, _) => rowBorder.Background = ThemeService.Brush(ThemeService.BgHover);
                rowBorder.PointerExited += (_, _) => rowBorder.Background = Brushes.Transparent;

                sectionStack.Children.Add(rowBorder);
            }

            stack.Children.Add(sectionStack);
        }

        return new ScrollViewer { Content = stack };
    }
}
