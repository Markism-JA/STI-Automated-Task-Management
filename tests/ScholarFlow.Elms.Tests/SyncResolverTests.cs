using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Elms.Tests;

public sealed class SyncResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"elms-resolver-{Guid.NewGuid():N}");

    public SyncResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Resolve_DetectsNewUpdatedUnchangedAndMovedLocallyEvenWhenLmsAuthRotates()
    {
        var course = new ElmsCourse("1", "SQA", "https://elms.sti.edu/student_class/show/1", "SQA");
        var resNew = new ElmsResource("https://elms.sti.edu/files/1.pdf?lmsauth=newtoken", "New", "new.pdf", "01_Handout", ElmsResourceKind.LessonAttachment, "1");
        var resUnchanged = new ElmsResource("https://elms.sti.edu/files/2.pdf?lmsauth=tokenB", "Same", "same.pdf", "01_Handout", ElmsResourceKind.LessonAttachment, "1", ETag: "\"v1\"");
        var resUpdated = new ElmsResource("https://elms.sti.edu/files/3.pdf?lmsauth=tokenB", "Updated", "updated.pdf", "01_Handout", ElmsResourceKind.LessonAttachment, "1", ETag: "\"v2\"");
        var resMoved = new ElmsResource("https://elms.sti.edu/files/4.pdf?lmsauth=tokenB", "Moved", "lab1.pdf", "ongoing", ElmsResourceKind.AssignmentAttachment, "1", ETag: "\"m1\"");
        var resScorm = new ElmsResource("https://elms.sti.edu/scorm", "Scorm", "scorm", "", ElmsResourceKind.ScormPackage, "1");

        var unchangedPath = Path.Combine(_tempRoot, "SQA", "01_Handout", "same.pdf");
        var updatedPath = Path.Combine(_tempRoot, "SQA", "01_Handout", "updated.pdf");
        var movedCompletedPath = Path.Combine(_tempRoot, "SQA", "completed", "lab1.pdf");

        Directory.CreateDirectory(Path.GetDirectoryName(unchangedPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(movedCompletedPath)!);
        File.WriteAllText(unchangedPath, "same");
        File.WriteAllText(updatedPath, "old");
        File.WriteAllText(movedCompletedPath, "lab");

        var localState = new LocalSyncState
        {
            ResourcesByUrl =
            {
                [resUnchanged.CanonicalUrl] = new SyncedResourceRecord { Url = resUnchanged.CanonicalUrl, RelativePath = "SQA/01_Handout/same.pdf", FileName = "same.pdf", ETag = "\"v1\"" },
                [resUpdated.CanonicalUrl] = new SyncedResourceRecord { Url = resUpdated.CanonicalUrl, RelativePath = "SQA/01_Handout/updated.pdf", FileName = "updated.pdf", ETag = "\"v1\"" },
                [resMoved.CanonicalUrl] = new SyncedResourceRecord { Url = resMoved.CanonicalUrl, RelativePath = "SQA/ongoing/lab1.pdf", FileName = "lab1.pdf", ETag = "\"m1\"" }
            }
        };

        var snapshot = new ElmsCourseSnapshot(
            course,
            GeneralResources: [resNew, resUnchanged, resUpdated, resMoved, resScorm],
            Modules: [],
            Lessons: [],
            Assignments: [],
            Warnings: [],
            CapturedAt: DateTimeOffset.UtcNow);

        var plan = new SyncResolver().Resolve(snapshot, localState, _tempRoot);

        Assert.Equal(SyncActionKind.New, plan.Actions.Single(a => a.Resource.CanonicalUrl == resNew.CanonicalUrl).Kind);
        Assert.Equal(SyncActionKind.Unchanged, plan.Actions.Single(a => a.Resource.CanonicalUrl == resUnchanged.CanonicalUrl).Kind);
        Assert.Equal(SyncActionKind.Updated, plan.Actions.Single(a => a.Resource.CanonicalUrl == resUpdated.CanonicalUrl).Kind);

        var movedAction = plan.Actions.Single(a => a.Resource.CanonicalUrl == resMoved.CanonicalUrl);
        Assert.Equal(SyncActionKind.MovedLocally, movedAction.Kind);
        Assert.Equal("SQA/completed/lab1.pdf", movedAction.ExistingRelocatedPath);

        Assert.Equal(SyncActionKind.SkippedExternal, plan.Actions.Single(a => a.Resource.CanonicalUrl == resScorm.CanonicalUrl).Kind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
