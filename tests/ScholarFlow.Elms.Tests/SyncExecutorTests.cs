using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ScholarFlow.Elms.Models;
using ScholarFlow.Elms.Sync;

namespace ScholarFlow.Elms.Tests;

public sealed class SyncExecutorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"elms-exec-{Guid.NewGuid():N}");

    public SyncExecutorTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task ExecuteAsync_DownloadsAllowedFilesComputesHashAndFiltersDisallowed()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("handout.pdf"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 sample"))
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                resp.Headers.ETag = new EntityTagHeaderValue("\"etag-123\"");
                return resp;
            }

            var exeResp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            exeResp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return exeResp;
        });

        using var http = new HttpClient(handler);
        var executor = new SyncExecutor(http);

        var course = new ElmsCourse("1", "OS", "https://elms.sti.edu/student_class/show/1", "OS");
        var pdf = new ElmsResource("https://elms.sti.edu/files/handout.pdf", "Handout", "handout.pdf", "01_Handout", ElmsResourceKind.LessonAttachment, "1");
        var exe = new ElmsResource("https://elms.sti.edu/files/tool.exe", "Tool", "tool.exe", "01_Handout", ElmsResourceKind.LessonAttachment, "1");

        var plan = new SyncPlan(course, [
            new SyncAction(pdf, SyncActionKind.New, "OS/01_Handout/handout.pdf"),
            new SyncAction(exe, SyncActionKind.New, "OS/01_Handout/tool.exe")
        ], DateTimeOffset.UtcNow);

        var state = new LocalSyncState();
        var options = new SyncOptions { OutputRootDirectory = _tempRoot };

        var result = await executor.ExecuteAsync(plan, options, state);

        Assert.Equal(1, result.Downloaded);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "OS", "01_Handout", "handout.pdf")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "OS", "01_Handout", "tool.exe")));
        Assert.True(state.ResourcesByUrl.TryGetValue(pdf.Url, out var record));
        Assert.Equal("\"etag-123\"", record.ETag);
        Assert.False(string.IsNullOrEmpty(record.Sha256));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
