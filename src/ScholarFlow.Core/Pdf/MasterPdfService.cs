using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using ScholarFlow.Core.Models;
using UglyToad.PdfPig;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfSharpDocument = PdfSharpCore.Pdf.PdfDocument;

namespace ScholarFlow.Core.Pdf;

public sealed record MasterPdfCompileResult(
    string CourseFolder,
    string OutputPdfPath,
    bool WasRegenerated,
    int PageCount);

/// <summary>
/// Pure C# (.NET 10) port of Compile_School using PDFsharp + PdfPig.
/// Crops whitespace margins via PdfPig text/image bounding boxes, splits landscape slides
/// into 2 portrait A4 pages (or rotates syllabi), builds Cover/Term/TOC pages + PDF bookmarks,
/// and caches source fingerprints so unchanged courses are skipped automatically.
/// </summary>
public sealed class MasterPdfService
{
    private const double TargetWidth = 595.0;
    private const double TargetHeight = 842.0;
    private const double SafetyPadding = 30.0;

    private static int _fontResolverInitialized;

    public static string GetPdfStatePath(string basinRoot)
        => Path.Combine(basinRoot, ".scholarflow", "pdf_state.json");

    public async Task<MasterPdfCompileResult> CompileIfStaleAsync(
        string basinRoot,
        string courseFolder,
        string? displayTitle = null,
        bool force = false,
        CancellationToken ct = default)
    {
        EnsureFontResolver();

        var coursePath = Path.Combine(basinRoot, courseFolder);
        var title = displayTitle ?? courseFolder.Replace('_', ' ').Trim();
        var outPath = Path.Combine(coursePath, $"{courseFolder}_Master.pdf");

        var fingerprint = ComputeCourseFingerprint(coursePath);
        var statePath = GetPdfStatePath(basinRoot);
        var state = await LoadStateAsync(statePath, ct);

        if (!force &&
            File.Exists(outPath) &&
            state.TryGetValue(courseFolder, out var prevHash) &&
            string.Equals(prevHash, fingerprint, StringComparison.Ordinal))
        {
            return new MasterPdfCompileResult(courseFolder, outPath, WasRegenerated: false, PageCount: 0);
        }

        int pageCount = await Task.Run(() => CompileCourseSync(coursePath, title, outPath, ct), ct);

        state[courseFolder] = fingerprint;
        await SaveStateAsync(statePath, state, ct);

        return new MasterPdfCompileResult(courseFolder, outPath, WasRegenerated: true, PageCount: pageCount);
    }

    private static int CompileCourseSync(string subjectPath, string subjectName, string outPath, CancellationToken ct)
    {
        using var destDoc = new PdfSharpDocument();
        destDoc.Info.Title = $"{subjectName} - Master Study Packet";

        var visualToc = new List<(int Level, string Text, int LogicalPage)>();
        var sidebarToc = new List<(int Level, string Title, int PhysicalPageOneBased)>();
        var categorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Main Cover (page index 0)
        AddCenteredTitlePage(destDoc, subjectName, isMainCover: true);
        sidebarToc.Add((1, "Cover Page", 1));

        var allDirs = Directory.Exists(subjectPath)
            ? Directory.GetDirectories(subjectPath)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith('.') &&
                            !n.Equals("ongoing", StringComparison.OrdinalIgnoreCase) &&
                            !n.Equals("completed", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .ToList()
            : [];

        // 2. Syllabus
        var syllabusDir = allDirs.FirstOrDefault(d => d.Contains("syllabus", StringComparison.OrdinalIgnoreCase));
        if (syllabusDir is not null)
        {
            var startLogical = destDoc.PageCount;
            var dirPath = Path.Combine(subjectPath, syllabusDir);
            var files = Directory.GetFiles(dirPath, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList();
            if (files.Count > 0)
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    AppendScaledOrSplit(destDoc, f, forceRotate: true);
                }
                visualToc.Add((1, "Course Syllabus", startLogical));
                sidebarToc.Add((1, "Syllabus", startLogical + 1));
                categorized.Add(syllabusDir);
            }
        }

        // 3. Term Logic from term.json
        var termConfig = LoadTermConfig(Path.Combine(subjectPath, "term.json"));
        foreach (var (term, prefixes) in termConfig)
        {
            var handouts = new List<string>();
            var activities = new List<string>();

            foreach (var prefix in prefixes)
            {
                var matches = allDirs.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var m in matches)
                {
                    if (m.Contains("activity", StringComparison.OrdinalIgnoreCase))
                        activities.Add(m);
                    else
                        handouts.Add(m);
                    categorized.Add(m);
                }
            }

            if (handouts.Count == 0 && activities.Count == 0)
                continue;

            var termLogical = destDoc.PageCount;
            AddCenteredTitlePage(destDoc, term, isMainCover: false);
            visualToc.Add((1, term, termLogical));
            sidebarToc.Add((1, term, termLogical + 1));

            foreach (var mod in handouts.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                var mPath = Path.Combine(subjectPath, mod);
                var files = Directory.GetFiles(mPath, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList();
                if (files.Count == 0)
                    continue;

                var modP = destDoc.PageCount;
                var label = mod.Replace('_', ' ').Trim();
                visualToc.Add((2, label, modP));
                sidebarToc.Add((2, label, modP + 1));

                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    AppendScaledOrSplit(destDoc, f, forceRotate: false);
                }
            }

            if (activities.Count > 0)
            {
                var actHeaderP = destDoc.PageCount;
                visualToc.Add((2, $"{term} Activities", actHeaderP));
                sidebarToc.Add((2, "Activities", actHeaderP + 1));

                foreach (var act in activities.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                {
                    var mPath = Path.Combine(subjectPath, act);
                    var files = Directory.GetFiles(mPath, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList();
                    if (files.Count == 0)
                        continue;

                    var actP = destDoc.PageCount;
                    var label = act.Replace('_', ' ').Trim();
                    visualToc.Add((3, label, actP));
                    sidebarToc.Add((3, label, actP + 1));

                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        AppendScaledOrSplit(destDoc, f, forceRotate: false);
                    }
                }
            }
        }

        // 4. Appendices (uncategorized folders with PDFs)
        var appendixDirs = allDirs
            .Where(d => !categorized.Contains(d))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => (Name: d, Files: Directory.GetFiles(Path.Combine(subjectPath, d), "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList()))
            .Where(x => x.Files.Count > 0)
            .ToList();

        if (appendixDirs.Count > 0)
        {
            var appLogical = destDoc.PageCount;
            AddCenteredTitlePage(destDoc, "Appendices", isMainCover: false);
            visualToc.Add((1, "Appendices", appLogical));
            sidebarToc.Add((1, "Appendices", appLogical + 1));

            foreach (var (appName, files) in appendixDirs)
            {
                var appP = destDoc.PageCount;
                var label = $"Appendix: {appName.Replace('_', ' ').Trim()}";
                visualToc.Add((2, label, appP));
                sidebarToc.Add((2, label, appP + 1));

                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    AppendScaledOrSplit(destDoc, f, forceRotate: false);
                }
            }
        }

        // 5. TOC Injection at index 1
        int tocPagesAdded = InsertTocPages(destDoc, visualToc, insertionIndex: 1);

        // 6. Centered Page Footers (skip cover + TOC pages)
        var footerFont = new XFont("Sans", 10, XFontStyle.Regular);
        int contentStartIndex = 1 + tocPagesAdded;
        for (int i = contentStartIndex; i < destDoc.PageCount; i++)
        {
            using var gfx = XGraphics.FromPdfPage(destDoc.Pages[i]);
            var pageNumText = (i - tocPagesAdded).ToString();
            gfx.DrawString(
                pageNumText,
                footerFont,
                XBrushes.Black,
                new XRect(0, TargetHeight - 36, TargetWidth, 20),
                XStringFormats.TopCenter);
        }

        // 7. PDF Outline Bookmarks
        BuildOutlines(destDoc, sidebarToc, tocPagesAdded);

        var tempOut = $"{outPath}.tmp.{Guid.NewGuid():N}";
        destDoc.Save(tempOut);
        File.Move(tempOut, outPath, overwrite: true);

        return destDoc.PageCount;
    }

    private readonly record struct ContentBox(double X0, double Y0, double X1, double Y1, double W, double H);

    private static void AppendScaledOrSplit(PdfSharpDocument destDoc, string srcPath, bool forceRotate)
    {
        List<ContentBox>? pigBoxes = null;
        try
        {
            using var pig = PdfPigDocument.Open(srcPath);
            pigBoxes = [];
            foreach (var p in pig.GetPages())
            {
                pigBoxes.Add(ComputePdfPigContentBox(p));
            }
        }
        catch
        {
            pigBoxes = null;
        }

        using var form = XPdfForm.FromFile(srcPath);
        for (int idx = 0; idx < form.PageCount; idx++)
        {
            form.PageNumber = idx + 1;
            double w = form.PointWidth;
            double h = form.PointHeight;
            if (w <= 0 || h <= 0)
                continue;

            var contentBox = (pigBoxes is not null && idx < pigBoxes.Count)
                ? pigBoxes[idx]
                : new ContentBox(0.0, 0.0, w, h, w, h);

            if (contentBox.W < 50 || contentBox.H < 50)
                contentBox = new ContentBox(0, 0, w, h, w, h);

            if (w > h)
            {
                if (forceRotate)
                {
                    var page = AddA4Page(destDoc);
                    using var gfx = XGraphics.FromPdfPage(page);
                    var target = GetTargetRectForContent(contentBox.H, contentBox.W);

                    gfx.Save();
                    gfx.TranslateTransform(target.X + target.Width / 2, target.Y + target.Height / 2);
                    gfx.RotateTransform(90);
                    gfx.TranslateTransform(-(target.Height / 2), -(target.Width / 2));

                    var scale = Math.Min(target.Height / contentBox.W, target.Width / contentBox.H);
                    gfx.IntersectClip(new XRect(0, 0, contentBox.W * scale, contentBox.H * scale));
                    gfx.DrawImage(
                        form,
                        new XRect(-contentBox.X0 * scale, -contentBox.Y0 * scale, w * scale, h * scale));
                    gfx.Restore();
                }
                else
                {
                    double midW = w / 2.0;
                    DrawClippedRegion(destDoc, form, w, h, new XRect(0, 0, midW, h));
                    DrawClippedRegion(destDoc, form, w, h, new XRect(midW, 0, midW, h));
                }
            }
            else
            {
                DrawClippedRegion(destDoc, form, w, h, new XRect(contentBox.X0, contentBox.Y0, contentBox.W, contentBox.H));
            }
        }
    }

    private static void DrawClippedRegion(PdfSharpDocument destDoc, XPdfForm form, double fullW, double fullH, XRect srcClip)
    {
        var page = AddA4Page(destDoc);
        using var gfx = XGraphics.FromPdfPage(page);
        var target = GetTargetRectForContent(srcClip.Width, srcClip.Height);
        var scale = Math.Min(target.Width / srcClip.Width, target.Height / srcClip.Height);

        gfx.Save();
        gfx.IntersectClip(target);
        gfx.DrawImage(
            form,
            new XRect(
                target.X - srcClip.X * scale,
                target.Y - srcClip.Y * scale,
                fullW * scale,
                fullH * scale));
        gfx.Restore();
    }

    private static ContentBox ComputePdfPigContentBox(UglyToad.PdfPig.Content.Page page)
    {
        double pageW = page.Width;
        double pageH = page.Height;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool hasContent = false;

        foreach (var word in page.GetWords())
        {
            var b = word.BoundingBox;
            // PdfPig origin is bottom-left; convert to top-left coordinates
            double topY = pageH - b.Top;
            double bottomY = pageH - b.Bottom;
            minX = Math.Min(minX, b.Left);
            minY = Math.Min(minY, topY);
            maxX = Math.Max(maxX, b.Right);
            maxY = Math.Max(maxY, bottomY);
            hasContent = true;
        }

        foreach (var img in page.GetImages())
        {
            var b = img.BoundingBox;
            double topY = pageH - b.Top;
            double bottomY = pageH - b.Bottom;
            minX = Math.Min(minX, b.Left);
            minY = Math.Min(minY, topY);
            maxX = Math.Max(maxX, b.Right);
            maxY = Math.Max(maxY, bottomY);
            hasContent = true;
        }

        if (!hasContent)
            return new ContentBox(0, 0, pageW, pageH, pageW, pageH);

        minX = Math.Max(0, minX - 2);
        minY = Math.Max(0, minY - 2);
        maxX = Math.Min(pageW, maxX + 2);
        maxY = Math.Min(pageH, maxY + 2);

        return new ContentBox(minX, minY, maxX, maxY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private static XRect GetTargetRectForContent(double contentW, double contentH)
    {
        double maxW = TargetWidth - (SafetyPadding * 2);
        double maxH = TargetHeight - (SafetyPadding * 2);
        double scale = Math.Min(maxW / contentW, maxH / contentH);
        double newW = contentW * scale;
        double newH = contentH * scale;
        return new XRect((TargetWidth - newW) / 2, (TargetHeight - newH) / 2, newW, newH);
    }

    private static PdfPage AddA4Page(PdfSharpDocument doc)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(TargetWidth);
        page.Height = XUnit.FromPoint(TargetHeight);
        return page;
    }

    private static void AddCenteredTitlePage(PdfSharpDocument doc, string title, bool isMainCover)
    {
        var page = AddA4Page(doc);
        using var gfx = XGraphics.FromPdfPage(page);
        var font = new XFont("Sans", isMainCover ? 36 : 30, isMainCover ? XFontStyle.Bold : XFontStyle.Regular);

        double usableWidth = TargetWidth - (SafetyPadding * 2);
        var lines = WrapText(gfx, title, font, usableWidth);
        double lineHeight = font.Size * 1.25;
        double totalH = lines.Count * lineHeight;
        double y = (TargetHeight - totalH) / 2;

        foreach (var line in lines)
        {
            gfx.DrawString(line, font, XBrushes.Black, new XRect(SafetyPadding, y, usableWidth, lineHeight), XStringFormats.TopCenter);
            y += lineHeight;
        }
    }

    private static int InsertTocPages(
        PdfSharpDocument doc,
        List<(int Level, string Text, int LogicalPage)> entries,
        int insertionIndex)
    {
        int pagesCreated = 1;
        var tocPage = doc.Pages.Insert(insertionIndex);
        tocPage.Width = XUnit.FromPoint(TargetWidth);
        tocPage.Height = XUnit.FromPoint(TargetHeight);

        var gfx = XGraphics.FromPdfPage(tocPage);
        var titleFont = new XFont("Sans", 24, XFontStyle.Bold);
        var l1Font = new XFont("Sans", 13, XFontStyle.Bold);
        var l2Font = new XFont("Sans", 10.5, XFontStyle.Regular);
        var l3Font = new XFont("Sans", 9, XFontStyle.Regular);

        gfx.DrawString("Table of Contents", titleFont, XBrushes.Black, new XRect(0, 45, TargetWidth, 36), XStringFormats.TopCenter);

        double y = 110;
        double textMargin = SafetyPadding + 10;

        foreach (var (level, text, logicalPage) in entries)
        {
            if (y > TargetHeight - 60)
            {
                gfx.Dispose();
                tocPage = doc.Pages.Insert(insertionIndex + pagesCreated);
                tocPage.Width = XUnit.FromPoint(TargetWidth);
                tocPage.Height = XUnit.FromPoint(TargetHeight);
                gfx = XGraphics.FromPdfPage(tocPage);
                pagesCreated++;
                y = 60;
            }

            double indent = textMargin + (level == 1 ? 0 : level == 2 ? 20 : 40);
            var font = level == 1 ? l1Font : level == 2 ? l2Font : l3Font;

            gfx.DrawString(text, font, XBrushes.Black, new XPoint(indent, y));
            gfx.DrawString(logicalPage.ToString(), font, XBrushes.Black, new XPoint(TargetWidth - textMargin - 25, y));
            y += level == 1 ? 24 : 17;
        }

        gfx.Dispose();
        return pagesCreated;
    }

    private static void BuildOutlines(
        PdfSharpDocument doc,
        List<(int Level, string Title, int PhysicalPageOneBased)> sidebarToc,
        int tocPagesAdded)
    {
        doc.Outlines.Add("Table of Contents", doc.Pages[1], true);

        PdfOutline? lastL1 = null;
        PdfOutline? lastL2 = null;

        foreach (var (level, title, physicalP) in sidebarToc)
        {
            int adjZeroBased = (physicalP > 1 ? physicalP + tocPagesAdded : physicalP) - 1;
            adjZeroBased = Math.Clamp(adjZeroBased, 0, doc.PageCount - 1);
            var page = doc.Pages[adjZeroBased];

            if (level == 1)
            {
                lastL1 = doc.Outlines.Add(title, page, true);
                lastL2 = null;
            }
            else if (level == 2 && lastL1 is not null)
            {
                lastL2 = lastL1.Outlines.Add(title, page, true);
            }
            else if (level >= 3 && lastL2 is not null)
            {
                lastL2.Outlines.Add(title, page, true);
            }
            else
            {
                doc.Outlines.Add(title, page, true);
            }
        }
    }

    private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new List<string>();

        foreach (var word in words)
        {
            var test = string.Join(' ', current.Append(word));
            if (gfx.MeasureString(test, font).Width <= maxWidth || current.Count == 0)
            {
                current.Add(word);
            }
            else
            {
                lines.Add(string.Join(' ', current));
                current = [word];
            }
        }

        if (current.Count > 0)
            lines.Add(string.Join(' ', current));

        return lines;
    }

    private static Dictionary<string, List<string>> LoadTermConfig(string termJsonPath)
    {
        if (!File.Exists(termJsonPath))
            return [];

        try
        {
            var json = File.ReadAllText(termJsonPath);
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    internal static string ComputeCourseFingerprint(string coursePath)
    {
        if (!Directory.Exists(coursePath))
            return string.Empty;

        var entries = new List<string>();
        var termJson = Path.Combine(coursePath, "term.json");
        if (File.Exists(termJson))
        {
            var info = new FileInfo(termJson);
            entries.Add($"term.json|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }

        foreach (var dir in Directory.GetDirectories(coursePath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name.Equals("ongoing", StringComparison.OrdinalIgnoreCase) || name.Equals("completed", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var pdf in Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(pdf);
                entries.Add($"{name}/{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<Dictionary<string, string>> LoadStateAsync(string statePath, CancellationToken ct)
    {
        if (!File.Exists(statePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task SaveStateAsync(string statePath, Dictionary<string, string> state, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = $"{statePath}.tmp.{Guid.NewGuid():N}";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, state, new JsonSerializerOptions { WriteIndented = true }, ct);
        }
        File.Move(temp, statePath, overwrite: true);
    }

    private static void EnsureFontResolver()
    {
        if (Interlocked.Exchange(ref _fontResolverInitialized, 1) == 0)
        {
            GlobalFontSettings.FontResolver ??= new SystemFontResolver();
        }
    }

    private sealed class SystemFontResolver : IFontResolver
    {
        public string DefaultFontName => "Sans";

        private static readonly string[] RegularCandidates =
        [
            "/usr/share/fonts/noto/NotoSans-Regular.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        ];

        private static readonly string[] BoldCandidates =
        [
            "/usr/share/fonts/noto/NotoSans-Bold.ttf",
            "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/liberation/LiberationSans-Bold.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
        ];

        public byte[]? GetFont(string faceName)
        {
            var candidates = faceName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                ? BoldCandidates.Concat(RegularCandidates)
                : RegularCandidates;

            var found = candidates.FirstOrDefault(File.Exists);
            if (found is not null)
                return File.ReadAllBytes(found);

            // Fallback: scan /usr/share/fonts for any regular .ttf
            if (Directory.Exists("/usr/share/fonts"))
            {
                var anyTtf = Directory.EnumerateFiles("/usr/share/fonts", "*.ttf", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains("Regular", StringComparison.OrdinalIgnoreCase))
                    ?? Directory.EnumerateFiles("/usr/share/fonts", "*.ttf", SearchOption.AllDirectories).FirstOrDefault();

                if (anyTtf is not null)
                    return File.ReadAllBytes(anyTtf);
            }

            return null;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
            => new(isBold ? "Sans-Bold" : "Sans-Regular");
    }
}
