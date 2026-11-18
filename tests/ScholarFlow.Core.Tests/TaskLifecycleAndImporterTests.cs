using ScholarFlow.Core.Markdown;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Tests;

public sealed class TaskLifecycleAndImporterTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"sf-core-test-{Guid.NewGuid():N}");

    public TaskLifecycleAndImporterTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task SetCompletedAsync_MovesLinkedFileFromOngoingToCompletedAndUpdatesMarkdown()
    {
        var basin = new BasinService();
        await basin.InitializeOrOpenAsync(_tempRoot);
        await basin.SaveCoursesAsync(_tempRoot, [
            new BasinCourseEntry { Title = "Software Quality Assurance", FolderName = "Software_Quality_Assurance", Alias = "sqa" }
        ]);

        var ongoingDir = Path.Combine(_tempRoot, "Software_Quality_Assurance", "ongoing");
        Directory.CreateDirectory(ongoingDir);
        var labPdf = Path.Combine(ongoingDir, "Lab_1.pdf");
        await File.WriteAllTextAsync(labPdf, "pdf");

        var tasksPath = BasinService.GetCourseTasksPath(_tempRoot, "Software_Quality_Assurance");
        var task = new TaskItem
        {
            Title = "Lab 1",
            CourseFolder = "Software_Quality_Assurance",
            CourseAlias = "sqa",
            LinkedFiles = ["ongoing/Lab_1.pdf"],
            SourceMarkdownPath = tasksPath
        };
        await MarkdownTaskCodec.SaveDocumentAsync(tasksPath, [task], "Software Quality Assurance");

        var lifecycle = new TaskLifecycleService();
        await lifecycle.SetCompletedAsync(_tempRoot, task, isCompleted: true, new DateOnly(2026, 11, 19));

        Assert.False(File.Exists(labPdf));
        Assert.True(File.Exists(Path.Combine(_tempRoot, "Software_Quality_Assurance", "completed", "Lab_1.pdf")));

        var reloaded = await MarkdownTaskCodec.LoadFileAsync(tasksPath, "Software_Quality_Assurance");
        Assert.Single(reloaded);
        Assert.True(reloaded[0].IsCompleted);
        Assert.Equal(new DateOnly(2026, 11, 19), reloaded[0].CompletedDate);
        Assert.Equal(["completed/Lab_1.pdf"], reloaded[0].LinkedFiles);
    }

    [Fact]
    public async Task LegacyImporter_ImportsOngoingFilesAndMetadataYaml()
    {
        var ongoingDir = Path.Combine(_tempRoot, "Network_Technology", "ongoing");
        Directory.CreateDirectory(ongoingDir);
        await File.WriteAllTextAsync(Path.Combine(ongoingDir, "01_Lab_1.pdf"), "lab");

        var yamlPath = Path.Combine(_tempRoot, "metadata.yaml");
        await File.WriteAllTextAsync(yamlPath, """
            Network_Technology/ongoing/01_Lab_1.pdf:
              due_date: '2026-11-28'
              difficulty: 4
            """);

        var importer = new LegacyImporter();
        var summary = await importer.ImportAsync(_tempRoot, yamlPath);

        Assert.Equal(1, summary.CoursesImported);
        Assert.Equal(1, summary.OngoingTasksImported);
        Assert.Equal(1, summary.MetadataEntriesMatched);

        var tasks = await new BasinService().LoadAllTasksAsync(_tempRoot);
        Assert.Single(tasks);
        Assert.Equal(new DateOnly(2026, 11, 28), tasks[0].DueDate);
        Assert.Equal(4, tasks[0].Difficulty);
        Assert.Equal(TaskPriority.P1, tasks[0].Priority);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
