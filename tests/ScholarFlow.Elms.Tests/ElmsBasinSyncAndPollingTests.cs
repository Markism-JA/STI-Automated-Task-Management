using ScholarFlow.Core.Models;
using ScholarFlow.Core.Services;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Elms.Tests;

public sealed class ElmsBasinSyncAndPollingTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"sf-poll-test-{Guid.NewGuid():N}"
    );

    public ElmsBasinSyncAndPollingTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task UpsertCourseAssignments_EmitsNewAssignmentAndGradePostedNotificationsOnDiff()
    {
        var basin = new BasinService();
        await basin.InitializeOrOpenAsync(_tempRoot);
        var course = new BasinCourseEntry
        {
            CourseId = "5725165",
            Title = "Software Quality Assurance",
            FolderName = "Software_Quality_Assurance",
            Alias = "sqa",
        };
        await basin.SaveCoursesAsync(_tempRoot, [course]);

        var pollState = new PollState();
        var notifications1 = new List<AppNotification>();

        var pending = new ElmsAssignment(
            "9001",
            "5725165",
            "05 Task Performance 1",
            "https://elms.sti.edu/student_freeform_assignment/show/9001",
            DateTimeOffset.Now.AddDays(3),
            AssignmentStatus.Pending,
            null,
            null,
            []
        );

        await ElmsBasinSyncService.UpsertCourseAssignmentsAsync(
            _tempRoot,
            course,
            [pending],
            pollState,
            notifications1,
            CancellationToken.None
        );

        Assert.Contains(notifications1, n => n.Kind == NotificationKind.NewAssignment);

        // Second poll: assignment gets graded
        var notifications2 = new List<AppNotification>();
        var graded = pending with { Status = AssignmentStatus.Graded, Score = "48/50" };
        await ElmsBasinSyncService.UpsertCourseAssignmentsAsync(
            _tempRoot,
            course,
            [graded],
            pollState,
            notifications2,
            CancellationToken.None
        );

        Assert.Contains(
            notifications2,
            n => n.Kind == NotificationKind.GradePosted && n.Message.Contains("48/50")
        );

        var tasks = await basin.LoadAllTasksAsync(_tempRoot);
        Assert.Single(tasks);
        Assert.True(tasks[0].IsCompleted);
        Assert.Equal("48/50", tasks[0].LmsScore);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
