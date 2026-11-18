using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using ScholarFlow.Core.Pdf;

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
        CreateSamplePdf(samplePdf);

        var service = new MasterPdfService();
        var first = await service.CompileIfStaleAsync(_tempRoot, "Software_Quality_Assurance", "Software Quality Assurance");

        Assert.True(first.WasRegenerated);
        Assert.True(File.Exists(first.OutputPdfPath));
        // Cover (1) + TOC (1) + Prelims divider (1) + Portrait (1) + Split Landscape (2) = 6 pages
        Assert.Equal(6, first.PageCount);

        var second = await service.CompileIfStaleAsync(_tempRoot, "Software_Quality_Assurance", "Software Quality Assurance");
        Assert.False(second.WasRegenerated);
    }

    private static void CreateSamplePdf(string path)
    {
        // Ensure FontResolver is initialized via MasterPdfService before drawing
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
