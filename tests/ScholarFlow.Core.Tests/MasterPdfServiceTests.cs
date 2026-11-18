using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Core.Tests;

public sealed class MasterPdfServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"sf-pdf-test-{Guid.NewGuid():N}");

    public MasterPdfServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task CompileIfStaleAsync_GeneratesMasterPdfAndSkipsUnchangedOnSecondRun()
    {
        var courseDir = Path.Combine(_tempRoot, "Software_Quality_Assurance");
        var modDir = Path.Combine(courseDir, "01_Handout_1");
        Directory.CreateDirectory(modDir);

        await File.WriteAllTextAsync(Path.Combine(courseDir, "term.json"), """
            { "Prelims": ["01"] }
            """);

        // Create a sample 2-page PDF (page 1 portrait, page 2 landscape slide)
        var samplePdf = Path.Combine(modDir, "01_Handout_1.pdf");
        var duplicateUnderscorePdf = Path.Combine(modDir, "01_Handout_1_.pdf");
        CreateSamplePdf(samplePdf);
        File.Copy(samplePdf, duplicateUnderscorePdf);

        var service = new MasterPdfService();
        var first = await service.CompileIfStaleAsync(_tempRoot, "Software_Quality_Assurance", "Software Quality Assurance");

        Assert.True(first.WasRegenerated);
        Assert.True(File.Exists(first.OutputPdfPath));
        // Cover (1) + TOC (1) + Prelims divider (1) + Portrait (1) + Split Landscape (2) = 6 pages (duplicate _.pdf skipped!)
        Assert.Equal(6, first.PageCount);

        // Verify PDF Outline Bookmarks form a nested tree via bookmarks.Roots (no duplicate top-level child entries)
        using (var pdfDoc = UglyToad.PdfPig.PdfDocument.Open(first.OutputPdfPath))
        {
            Assert.True(pdfDoc.TryGetBookmarks(out var bookmarks));
            var rootTitles = bookmarks.Roots.Select(r => r.Title).ToList();
            Assert.Contains("Prelims", rootTitles);
            Assert.DoesNotContain("01 Handout 1", rootTitles);

            var prelimRoot = bookmarks.Roots.First(r => r.Title == "Prelims");
            var childTitles = prelimRoot.Children.Select(c => c.Title).ToList();
            Assert.Contains("01 Handout 1", childTitles);
        }

        var second = await service.CompileIfStaleAsync(_tempRoot, "Software_Quality_Assurance", "Software Quality Assurance");
        Assert.False(second.WasRegenerated);
    }

    [Fact]
    public void NormalizeUnsignedPdfPermissionsInPlace_RewritesUnsigned32BitPermissionWithoutChangingFileLength()
    {
        var dummyPath = Path.Combine(_tempRoot, "encrypted_sample.pdf");
        var original = "%PDF-1.7\n762 0 obj\n<</Filter/Standard/V 4/R 4/P 4294965444/Length 128>>\nendobj\n";
        File.WriteAllBytes(dummyPath, Encoding.Latin1.GetBytes(original));

        var changed = MasterPdfService.NormalizeUnsignedPdfPermissionsInPlace(dummyPath);
        Assert.True(changed);

        var updated = Encoding.Latin1.GetString(File.ReadAllBytes(dummyPath));
        Assert.Equal(original.Length, updated.Length);
        Assert.Contains("/P -1852     ", updated);
    }

    [Fact]
    public async Task BasinService_DeduplicateAndMergeCoursesAsync_MergesFuzzyDuplicateCourseFolders()
    {
        var basin = Path.Combine(_tempRoot, "BasinDedup");
        var svc = new BasinService();
        await svc.InitializeOrOpenAsync(basin);

        var origDir = Path.Combine(basin, "Platform_Technology_(Operating_System)", "01_Handout_1");
        var dupDir = Path.Combine(basin, "Platform_Technology_(Operating_Systems)", "05_Handout_1");
        Directory.CreateDirectory(origDir);
        Directory.CreateDirectory(dupDir);
        await File.WriteAllTextAsync(Path.Combine(origDir, "01.txt"), "h1");
        await File.WriteAllTextAsync(Path.Combine(dupDir, "05.txt"), "h5");

        await svc.SaveCoursesAsync(basin,
        [
            new BasinCourseEntry
            {
                CourseId = null,
                Title = "Platform Technology (Operating System)",
                FolderName = "Platform_Technology_(Operating_System)",
                Alias = "ptos"
            },
            new BasinCourseEntry
            {
                CourseId = "5725166",
                Title = "Platform Technology (Operating Systems)",
                FolderName = "Platform_Technology_(Operating_Systems)",
                Alias = "ptos2",
                CourseCode = "COSC1008"
            }
        ]);

        var deduped = await svc.DeduplicateAndMergeCoursesAsync(basin);
        Assert.Single(deduped);
        Assert.Equal("Platform_Technology_(Operating_System)", deduped[0].FolderName);
        Assert.Equal("5725166", deduped[0].CourseId);
        Assert.True(File.Exists(Path.Combine(basin, "Platform_Technology_(Operating_System)", "05_Handout_1", "05.txt")));
        Assert.False(Directory.Exists(Path.Combine(basin, "Platform_Technology_(Operating_Systems)")));
    }

    private static void CreateSamplePdf(string path)
    {
        using var doc = new PdfDocument();

        var p1 = doc.AddPage();
        p1.Width = XUnit.FromPoint(595);
        p1.Height = XUnit.FromPoint(842);
        using (var g1 = XGraphics.FromPdfPage(p1))
        {
            g1.DrawRectangle(XBrushes.DarkBlue, new XRect(60, 60, 200, 100));
        }

        var p2 = doc.AddPage();
        p2.Width = XUnit.FromPoint(960);
        p2.Height = XUnit.FromPoint(540);
        using (var g2 = XGraphics.FromPdfPage(p2))
        {
            g2.DrawRectangle(XBrushes.DarkGreen, new XRect(40, 40, 400, 300));
            g2.DrawRectangle(XBrushes.DarkRed, new XRect(520, 40, 400, 300));
        }

        doc.Save(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
