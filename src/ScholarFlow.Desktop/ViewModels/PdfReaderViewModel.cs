using Avalonia.Media.Imaging;
using ScholarFlow.Desktop.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace ScholarFlow.Desktop.ViewModels;

/// <summary>
/// Hierarchical node in a PDF document's bookmark/outline tree.
/// </summary>
public sealed class PdfBookmarkNode
{
    public required string Title { get; init; }
    public required int PageNumber { get; init; }
    public required int Level { get; init; }
    public List<PdfBookmarkNode> Children { get; } = [];
    public bool IsExpanded { get; set; } = true;
}

/// <summary>
/// Built-in PDF reader ViewModel backed by PDFtoImage (PDFium + SkiaSharp) and PdfPig hierarchical outlines.
/// </summary>
public sealed class PdfReaderViewModel : IDisposable
{
    private readonly Dictionary<(int Page, int Dpi), Bitmap> _pageCache = [];
    private byte[]? _pdfBytes;

    public string? CurrentFilePath { get; private set; }
    public string Title => string.IsNullOrEmpty(CurrentFilePath) ? "No PDF Loaded" : Path.GetFileName(CurrentFilePath);
    public int PageCount { get; private set; }
    public int CurrentPage { get; private set; } = 1; // 1-based
    public int ZoomPercent { get; private set; } = 100;
    public bool FitToWidth { get; private set; } = true;
    public bool IsOutlineVisible { get; private set; } = true;
    public IReadOnlyList<PdfBookmarkNode> BookmarkRoots { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public event EventHandler? Changed;

    public void LoadPdf(string filePath)
    {
        ClearCache();
        CurrentFilePath = filePath;
        ErrorMessage = null;

        if (!File.Exists(filePath))
        {
            ErrorMessage = $"File not found: {filePath}";
            PageCount = 0;
            CurrentPage = 1;
            BookmarkRoots = [];
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            _pdfBytes = File.ReadAllBytes(filePath);
            using var doc = PdfDocument.Open(_pdfBytes);
            PageCount = doc.NumberOfPages;
            CurrentPage = 1;
            BookmarkRoots = ExtractBookmarkRoots(doc);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to open PDF: {ex.Message}";
            PageCount = 0;
            BookmarkRoots = [];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Bitmap? GetCurrentPageBitmap()
    {
        if (_pdfBytes is null || PageCount <= 0)
            return null;

        int zeroBased = Math.Clamp(CurrentPage - 1, 0, PageCount - 1);
        int dpi = FitToWidth
            ? 180
            : Math.Clamp((int)(160 * (ZoomPercent / 100.0)), 96, 300);

        if (_pageCache.TryGetValue((zeroBased, dpi), out var cached))
            return cached;

        try
        {
            using var ms = new MemoryStream();
#pragma warning disable CA1416
            var renderOpts = new PDFtoImage.RenderOptions(
                Dpi: dpi,
                BackgroundColor: SkiaSharp.SKColors.White);
            PDFtoImage.Conversion.SavePng(ms, _pdfBytes, page: (Index)zeroBased, options: renderOpts);
#pragma warning restore CA1416
            ms.Position = 0;
            var bmp = new Bitmap(ms);

            if (_pageCache.Count >= 16)
            {
                var firstKey = _pageCache.Keys.First();
                _pageCache[firstKey].Dispose();
                _pageCache.Remove(firstKey);
            }

            _pageCache[(zeroBased, dpi)] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Render error: {ex.Message}";
            return null;
        }
    }

    public void NextPage()
    {
        if (CurrentPage < PageCount)
        {
            CurrentPage++;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void JumpToPage(int oneBasedPage)
    {
        if (PageCount <= 0)
            return;

        CurrentPage = Math.Clamp(oneBasedPage, 1, PageCount);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn()
    {
        FitToWidth = false;
        ZoomPercent = Math.Min(250, ZoomPercent + 25);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomOut()
    {
        FitToWidth = false;
        ZoomPercent = Math.Max(50, ZoomPercent - 25);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetZoom()
    {
        FitToWidth = false;
        ZoomPercent = 100;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleFitToWidth()
    {
        FitToWidth = !FitToWidth;
        if (FitToWidth)
            ZoomPercent = 100;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleOutline()
    {
        IsOutlineVisible = !IsOutlineVisible;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleNodeExpanded(PdfBookmarkNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public PdfBookmarkNode? FindActiveBookmarkNode()
    {
        PdfBookmarkNode? best = null;

        void Walk(IEnumerable<PdfBookmarkNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.PageNumber <= CurrentPage)
                {
                    if (best is null || n.PageNumber >= best.PageNumber)
                        best = n;
                }
                if (n.Children.Count > 0)
                    Walk(n.Children);
            }
        }

        Walk(BookmarkRoots);
        return best;
    }

    public void OpenExternal()
    {
        if (!string.IsNullOrEmpty(CurrentFilePath))
            ExternalFileLauncher.OpenLocalFile(CurrentFilePath);
    }

    public static List<PdfBookmarkNode> ExtractBookmarkRoots(PdfDocument doc)
    {
        var roots = new List<PdfBookmarkNode>();
        try
        {
            if (doc.TryGetBookmarks(out var bookmarks))
            {
                // Use bookmarks.Roots (NOT bookmarks.GetNodes(), which already flattens the entire tree!)
                var seenSiblings = new HashSet<(string Title, int Page)>();
                foreach (var rootNode in bookmarks.Roots)
                {
                    var converted = BuildBookmarkTree(rootNode, level: 1);
                    if (converted is not null && seenSiblings.Add((converted.Title, converted.PageNumber)))
                    {
                        roots.Add(converted);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed PDF outline trees
        }
        return roots;
    }

    private static PdfBookmarkNode? BuildBookmarkTree(BookmarkNode node, int level)
    {
        int pageNumber = node is DocumentBookmarkNode docNode ? docNode.PageNumber : 1;
        var title = node.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
            return null;

        var vmNode = new PdfBookmarkNode
        {
            Title = title,
            PageNumber = pageNumber,
            Level = level,
            IsExpanded = level <= 2
        };

        var seenChildren = new HashSet<(string Title, int Page)>();
        foreach (var child in node.Children)
        {
            var childVm = BuildBookmarkTree(child, level + 1);
            if (childVm is not null && seenChildren.Add((childVm.Title, childVm.PageNumber)))
            {
                vmNode.Children.Add(childVm);
            }
        }

        return vmNode;
    }

    private void ClearCache()
    {
        foreach (var bmp in _pageCache.Values)
            bmp.Dispose();
        _pageCache.Clear();
    }

    public void Dispose() => ClearCache();
}
