using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Elms.Tests;

public sealed class CourseFilterAndSessionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"elms-cf-{Guid.NewGuid():N}");

    public CourseFilterAndSessionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CourseFilter_FiltersByIdAndTitlePattern()
    {
        var courses = new[]
        {
            new ElmsCourse("101", "Software Quality Assurance", "https://elms.sti.edu/student_class/show/101", "SQA"),
            new ElmsCourse("102", "Student Handbook & Orientation", "https://elms.sti.edu/student_class/show/102", "Handbook"),
            new ElmsCourse("103", "Registrar Announcement Shell", "https://elms.sti.edu/student_class/show/103", "Registrar"),
            new ElmsCourse("104", "Network Technology", "https://elms.sti.edu/student_class/show/104", "Network_Technology")
        };

        var filter = new CourseFilter
        {
            IgnoredCourseIds = ["103"],
            IgnoredTitlePatterns = ["*Handbook*", "Orientation"]
        };

        var visible = filter.Apply(courses);

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, c => c.CourseId == "101");
        Assert.Contains(visible, c => c.CourseId == "104");
    }

    [Fact]
    public async Task ElmsSessionStore_RoundTripsSessionJson()
    {
        var path = Path.Combine(_tempDir, "session.json");
        var session = new ElmsSession(
            "https://elms.sti.edu",
            "TestAgent/1.0",
            [new ElmsCookie("sid", "abc123", "elms.sti.edu", "/", true, true, null)],
            DateTimeOffset.UtcNow);

        await ElmsSessionStore.SaveAsync(session, path);
        var loaded = await ElmsSessionStore.LoadAsync(path);

        Assert.NotNull(loaded);
        Assert.Equal("TestAgent/1.0", loaded.UserAgent);
        Assert.Single(loaded.Cookies);
        Assert.Equal("abc123", loaded.Cookies[0].Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
