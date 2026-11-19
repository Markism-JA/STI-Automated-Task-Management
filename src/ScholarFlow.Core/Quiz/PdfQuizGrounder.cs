using System.Collections.Concurrent;
using System.Text;
using ScholarFlow.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace ScholarFlow.Core.Quiz;

public sealed record GroundedPdfPage(
    int PageNumber,
    string? Term,
    string SectionBreadcrumb,
    string Text);

/// <summary>
/// Extracts bookmark-aware grounded page chunks from Master/Handout PDFs and resolves
/// verbatim quote evidence anchors into exact normalized [0..1] highlight bounding boxes via PdfPig.
/// </summary>
public static class PdfQuizGrounder
{
    private sealed record CachedPdfTextData(
        long LastWriteTicks,
        int TotalPages,
        (string? Term, string SectionBreadcrumb)[] PageSections,
        string?[] PageTexts);

    private static readonly ConcurrentDictionary<string, CachedPdfTextData> PdfTextCache =
        new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<PdfOutlineSectionInfo> InspectOutlineSections(string pdfPath, out int totalPages)
    {
        totalPages = 0;
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return [];

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            int pageCount = doc.NumberOfPages;
            totalPages = pageCount;
            if (pageCount == 0 || !doc.TryGetBookmarks(out var bookmarks) || bookmarks.Roots.Count == 0)
                return [];

            var flat = new List<(string Title, string? Term, int Level, int StartPage)>();

            void Walk(IEnumerable<BookmarkNode> nodes, string? inheritedTerm, int level)
            {
                foreach (var node in nodes)
                {
                    var title = (node.Title ?? string.Empty).Trim();
                    var term = TaskItem.NormalizeTerm(title) ?? inheritedTerm;
                    if (node is DocumentBookmarkNode docNode &&
                        docNode.PageNumber >= 1 &&
                        docNode.PageNumber <= pageCount &&
                        !title.Equals("Table of Contents", StringComparison.OrdinalIgnoreCase))
                    {
                        flat.Add((title, term, level, docNode.PageNumber));
                    }

                    if (node.Children.Count > 0)
                        Walk(node.Children, term, level + 1);
                }
            }

            Walk(bookmarks.Roots, null, 1);

            var sections = new List<PdfOutlineSectionInfo>();
            for (int i = 0; i < flat.Count; i++)
            {
                var cur = flat[i];
                int endPage = totalPages;
                for (int j = i + 1; j < flat.Count; j++)
                {
                    if (flat[j].Level <= cur.Level && flat[j].StartPage > cur.StartPage)
                    {
                        endPage = flat[j].StartPage - 1;
                        break;
                    }
                    if (flat[j].Level > cur.Level && flat[j].StartPage > cur.StartPage && cur.Level == 1)
                        continue;
                }

                // For leaf/handout nodes, also cap at next sibling or parent boundary
                if (cur.Level >= 2)
                {
                    for (int j = i + 1; j < flat.Count; j++)
                    {
                        if (flat[j].StartPage > cur.StartPage)
                        {
                            endPage = Math.Min(endPage, flat[j].StartPage - 1);
                            break;
                        }
                    }
                }

                endPage = Math.Clamp(endPage, cur.StartPage, totalPages);
                sections.Add(new PdfOutlineSectionInfo(cur.Title, cur.Term, cur.Level, cur.StartPage, endPage));
            }

            return sections;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Fast (< 0.1ms) metadata-only resolution of which PDF pages and section titles are included in the active scope,
    /// without re-opening or re-parsing the PDF on the UI thread.
    /// </summary>
    public static IReadOnlyList<(int PageNumber, string SectionTitle, string? Term)> ResolveScopePagesFast(
        int totalPages,
        IReadOnlyList<PdfOutlineSectionInfo> outlineSections,
        string? termFilter = null,
        int? startPage = null,
        int? endPage = null,
        IReadOnlyList<PdfPageRange>? explicitRanges = null)
    {
        if (totalPages <= 0)
            return [];

        var normalizedTerm = TaskItem.NormalizeTerm(termFilter);
        bool hasExplicitRanges = explicitRanges is { Count: > 0 };
        var leafSections = outlineSections.Where(s => s.Level >= 2).ToList();
        if (leafSections.Count == 0)
            leafSections = outlineSections.ToList();

        bool hasNonSyllabusSections = leafSections.Any(s => !s.Title.Contains("Syllabus", StringComparison.OrdinalIgnoreCase));
        int minP = Math.Clamp(startPage ?? 1, 1, totalPages);
        int maxP = Math.Clamp(endPage ?? totalPages, minP, totalPages);

        var matched = new List<(int PageNumber, string SectionTitle, string? Term)>();
        for (int p = minP; p <= maxP; p++)
        {
            if (hasExplicitRanges && !explicitRanges!.Any(r => r.Contains(p)))
                continue;

            var sec = leafSections.LastOrDefault(s => p >= s.StartPage && p <= s.EndPage)
                      ?? outlineSections.LastOrDefault(s => p >= s.StartPage && p <= s.EndPage);

            // Skip cover / TOC / single-page term divider headers when outline sections exist and no explicit page range was typed
            if (!hasExplicitRanges && !startPage.HasValue && !endPage.HasValue && outlineSections.Count > 0)
            {
                if (sec is null)
                    continue;

                // Skip the 1-page Term Divider sheet itself (Level 1 start page before Level 2 handout starts)
                if (outlineSections.Any(s => s.Level == 1 && s.StartPage == p && TaskItem.NormalizeTerm(s.Title) is not null) &&
                    outlineSections.Any(s => s.Level >= 2))
                {
                    continue;
                }

                // Skip Course Syllabus boilerplate by default unless explicitly checked
                if (hasNonSyllabusSections && sec.Title.Contains("Syllabus", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (!hasExplicitRanges && !string.IsNullOrWhiteSpace(normalizedTerm))
            {
                if (sec is null || !string.Equals(sec.Term, normalizedTerm, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var label = sec is not null
                ? (!string.IsNullOrWhiteSpace(sec.Term) && !sec.Title.Contains(sec.Term, StringComparison.OrdinalIgnoreCase)
                    ? $"{sec.Term} / {sec.Title}"
                    : sec.Title)
                : $"Page {p}";

            matched.Add((p, label, sec?.Term ?? normalizedTerm));
        }

        // Fallback if individual handout PDF has no bookmarks
        if (matched.Count == 0 && outlineSections.Count == 0)
        {
            for (int p = minP; p <= maxP; p++)
            {
                if (hasExplicitRanges && !explicitRanges!.Any(r => r.Contains(p)))
                    continue;
                matched.Add((p, $"Page {p}", normalizedTerm));
            }
        }

        return matched;
    }

    public static (int PageCount, int CharCount, string SampleExcerpt) BuildScopePreview(
        string pdfPath,
        string? termFilter = null,
        string? sectionFilter = null,
        int? startPage = null,
        int? endPage = null,
        IReadOnlyList<PdfPageRange>? explicitRanges = null)
    {
        var pages = ExtractGroundedPages(pdfPath, termFilter, sectionFilter, startPage, endPage, explicitRanges, maxPages: 30);
        if (pages.Count == 0)
            return (0, 0, "No readable content pages match the current filter.");

        int totalChars = pages.Sum(p => p.Text.Length);
        var first = pages[0];
        var snippet = first.Text.Length > 260 ? first.Text[..260].Trim() + "..." : first.Text;
        return (pages.Count, totalChars, $"Page {first.PageNumber} ({first.SectionBreadcrumb}): {snippet}");
    }

    public static IReadOnlyList<GroundedPdfPage> ExtractGroundedPages(
        string pdfPath,
        string? termFilter = null,
        string? sectionFilter = null,
        int? startPage = null,
        int? endPage = null,
        IReadOnlyList<PdfPageRange>? explicitRanges = null,
        int maxPages = 150)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return [];

        long currentTicks = File.GetLastWriteTimeUtc(pdfPath).Ticks;
        if (!PdfTextCache.TryGetValue(pdfPath, out var cached) || cached.LastWriteTicks != currentTicks)
        {
            using var doc = PdfDocument.Open(pdfPath);
            int docPages = doc.NumberOfPages;
            if (docPages == 0)
                return [];

            var sections = BuildPageSectionMap(doc);
            var texts = new string?[docPages + 1];
            for (int p = 1; p <= docPages; p++)
            {
                texts[p] = ExtractCleanPageText(doc.GetPage(p));
            }

            if (PdfTextCache.Count >= 8)
                PdfTextCache.Clear();

            cached = new CachedPdfTextData(currentTicks, docPages, sections, texts);
            PdfTextCache[pdfPath] = cached;
        }

        int totalPages = cached.TotalPages;
        if (totalPages == 0)
            return [];

        var pageSections = cached.PageSections;
        var normalizedTerm = TaskItem.NormalizeTerm(termFilter);
        bool hasAnyTermBookmarks = pageSections.Any(ps => !string.IsNullOrWhiteSpace(ps.Term));
        bool hasNonSyllabusPages = pageSections.Any(ps =>
            !string.IsNullOrWhiteSpace(ps.SectionBreadcrumb) &&
            !ps.SectionBreadcrumb.Contains("Syllabus", StringComparison.OrdinalIgnoreCase) &&
            !ps.SectionBreadcrumb.Equals("Table of Contents", StringComparison.OrdinalIgnoreCase));
        bool hasExplicitRanges = explicitRanges is { Count: > 0 };

        int minP = Math.Clamp(startPage ?? 1, 1, totalPages);
        int maxP = Math.Clamp(endPage ?? totalPages, minP, totalPages);

        var results = new List<GroundedPdfPage>();
        for (int p = minP; p <= maxP; p++)
        {
            if (hasExplicitRanges && !explicitRanges!.Any(r => r.Contains(p)))
                continue;

            var (pageTerm, breadcrumb) = pageSections[p];

            if (!hasExplicitRanges &&
                !startPage.HasValue &&
                !endPage.HasValue &&
                string.IsNullOrWhiteSpace(sectionFilter) &&
                hasNonSyllabusPages &&
                breadcrumb.Contains("Syllabus", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!hasExplicitRanges &&
                !string.IsNullOrWhiteSpace(normalizedTerm) && hasAnyTermBookmarks &&
                !string.Equals(pageTerm, normalizedTerm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sectionFilter) &&
                !breadcrumb.Contains(sectionFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = cached.PageTexts[p] ?? string.Empty;

            // Skip cover pages, term divider pages, table of contents, or nearly empty pages
            if (text.Length < 180 ||
                text.StartsWith("TABLE OF CONTENTS", StringComparison.OrdinalIgnoreCase) ||
                (text.Contains("Includes:", StringComparison.OrdinalIgnoreCase) && text.Length < 320))
            {
                continue;
            }

            results.Add(new GroundedPdfPage(
                PageNumber: p,
                Term: pageTerm ?? normalizedTerm,
                SectionBreadcrumb: string.IsNullOrWhiteSpace(breadcrumb) ? Path.GetFileNameWithoutExtension(pdfPath) : breadcrumb,
                Text: text));

            if (results.Count >= maxPages)
                break;
        }

        // Fallback if term filter excluded everything (e.g. individual handout PDF without term bookmarks)
        if (results.Count == 0 && (!string.IsNullOrWhiteSpace(normalizedTerm) || !string.IsNullOrWhiteSpace(sectionFilter)))
        {
            for (int p = minP; p <= maxP && results.Count < maxPages; p++)
            {
                var text = cached.PageTexts[p] ?? string.Empty;
                if (text.Length < 55)
                    continue;

                var (_, breadcrumb) = pageSections[p];
                results.Add(new GroundedPdfPage(
                    PageNumber: p,
                    Term: normalizedTerm,
                    SectionBreadcrumb: string.IsNullOrWhiteSpace(breadcrumb) ? Path.GetFileNameWithoutExtension(pdfPath) : breadcrumb,
                    Text: text));
            }
        }

        return results;
    }

    public static string FormatPagesForPrompt(IReadOnlyList<GroundedPdfPage> pages, int maxChars = 42_000)
    {
        var sb = new StringBuilder();
        foreach (var p in pages)
        {
            var header = $"=== [PAGE {p.PageNumber}] | Section: {p.SectionBreadcrumb} ===";
            if (sb.Length + header.Length + p.Text.Length + 8 > maxChars && sb.Length > 0)
                break;

            sb.AppendLine(header);
            sb.AppendLine(p.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Grounds every question in the list against the source PDF, snapping SourcePage if off by 1-2 pages
    /// and computing exact normalized [0..1] highlight bounding boxes for the VerbatimQuote.
    /// </summary>
    public static void GroundQuestionsToPdf(string pdfPath, IEnumerable<QuizQuestionRecord> questions, bool forceUpdateSection = false)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return;

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            if (doc.NumberOfPages == 0)
                return;

            var pageSections = BuildPageSectionMap(doc);

            foreach (var q in questions)
            {
                q.SourcePdfPath = pdfPath;
                var rects = ComputeHighlightRects(doc, q.SourcePage, q.VerbatimQuote, out int resolvedPage);
                q.SourcePage = Math.Clamp(resolvedPage, 1, doc.NumberOfPages);
                q.HighlightRects = rects;

                if (forceUpdateSection || string.IsNullOrWhiteSpace(q.SectionTitle))
                {
                    var sec = pageSections[q.SourcePage].SectionBreadcrumb;
                    if (!string.IsNullOrWhiteSpace(sec))
                        q.SectionTitle = sec;
                }
            }
        }
        catch
        {
            // Keep questions usable even if PDF grounding encounters an unexpected structure
        }
    }

    /// <summary>
    /// Re-grounds every question in an existing quiz session against its current SourcePdfPath
    /// (for example after a Master PDF recompiles and page numbers shift) and updates SourcePdfMtimeTicks.
    /// </summary>
    public static int RegroundSessionToPdf(QuizSessionRecord session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.SourcePdfPath) || !File.Exists(session.SourcePdfPath))
            return 0;

        GroundQuestionsToPdf(session.SourcePdfPath, session.Questions, forceUpdateSection: true);
        session.SourcePdfMtimeTicks = File.GetLastWriteTimeUtc(session.SourcePdfPath).Ticks;
        return session.Questions.Count(q => q.HighlightRects.Count > 0);
    }

    public static List<NormalizedHighlightRect> ComputeHighlightRects(
        PdfDocument doc,
        int hintPage,
        string? verbatimQuote,
        out int resolvedPage)
    {
        int totalPages = doc.NumberOfPages;
        resolvedPage = Math.Clamp(hintPage <= 0 ? 1 : hintPage, 1, Math.Max(1, totalPages));
        if (totalPages == 0 || string.IsNullOrWhiteSpace(verbatimQuote))
            return [];

        var quoteTokens = TokenizeWords(verbatimQuote);
        if (quoteTokens.Count == 0)
            return [];

        // Search hintPage first, then nearby pages (+/- 1..3), then full document if needed
        var candidatePages = new List<int> { resolvedPage };
        for (int delta = 1; delta <= 3; delta++)
        {
            if (resolvedPage - delta >= 1) candidatePages.Add(resolvedPage - delta);
            if (resolvedPage + delta <= totalPages) candidatePages.Add(resolvedPage + delta);
        }
        for (int p = 1; p <= totalPages; p++)
        {
            if (!candidatePages.Contains(p))
                candidatePages.Add(p);
        }

        double bestScore = 0;
        int bestPage = resolvedPage;
        List<Word>? bestSpan = null;
        double bestPageWidth = 1;
        double bestPageHeight = 1;

        foreach (var pageNum in candidatePages)
        {
            var page = doc.GetPage(pageNum);
            var words = GetVisibleWordsInReadingOrder(page);
            if (words.Count == 0)
                continue;

            var (score, span) = FindBestMatchingWordSpan(words, quoteTokens);
            if (score > bestScore && span.Count > 0)
            {
                bestScore = score;
                bestPage = pageNum;
                bestSpan = span;
                bestPageWidth = Math.Max(1.0, page.Width);
                bestPageHeight = Math.Max(1.0, page.Height);

                if (bestScore >= 0.92)
                    break;
            }
        }

        if (bestSpan is null || bestSpan.Count == 0 || bestScore < 0.35)
            return [];

        resolvedPage = bestPage;
        return BuildLineRectangles(bestSpan, bestPageWidth, bestPageHeight);
    }

    private static (double Score, List<Word> Span) FindBestMatchingWordSpan(
        IReadOnlyList<Word> pageWords,
        IReadOnlyList<string> quoteTokens)
    {
        var tokenWords = pageWords
            .Select(w => (Word: w, Token: NormalizeToken(w.Text)))
            .Where(x => x.Token.Length > 0)
            .ToList();

        if (tokenWords.Count == 0 || quoteTokens.Count == 0)
            return (0, []);

        int qLen = quoteTokens.Count;
        int windowLen = Math.Clamp(qLen, 2, Math.Min(tokenWords.Count, qLen + 4));

        double bestScore = 0;
        int bestStart = -1;
        int bestCount = 0;

        var quoteSet = new HashSet<string>(quoteTokens.Where(t => t.Length >= 2), StringComparer.OrdinalIgnoreCase);
        if (quoteSet.Count == 0)
            return (0, []);

        var consumed = new bool[qLen];

        for (int i = 0; i < tokenWords.Count; i++)
        {
            var startToken = tokenWords[i].Token;

            bool matchesStart = false;
            for (int k = 0; k < Math.Min(3, qLen); k++)
            {
                if (TokensMatch(startToken, quoteTokens[k]))
                {
                    matchesStart = true;
                    break;
                }
            }
            if (!matchesStart && !quoteSet.Contains(startToken))
                continue;

            Array.Clear(consumed, 0, qLen);
            int maxEnd = Math.Min(tokenWords.Count - 1, i + windowLen + 3);
            int hits = 0;
            int orderedIdx = 0;

            for (int j = i; j <= maxEnd; j++)
            {
                var pw = tokenWords[j].Token;

                while (orderedIdx < qLen && consumed[orderedIdx])
                    orderedIdx++;

                if (orderedIdx < qLen && TokensMatch(pw, quoteTokens[orderedIdx]))
                {
                    consumed[orderedIdx] = true;
                    hits += 2;
                    orderedIdx++;
                }
                else
                {
                    for (int k = 0; k < qLen; k++)
                    {
                        if (!consumed[k] && quoteTokens[k].Length >= 3 && TokensMatch(pw, quoteTokens[k]))
                        {
                            consumed[k] = true;
                            hits += 1;
                            break;
                        }
                    }
                }

                int spanLen = j - i + 1;
                if (spanLen >= Math.Max(2, qLen - 2))
                {
                    double score = (double)hits / (2.0 * qLen + Math.Max(0, spanLen - qLen) * 0.35);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestStart = i;
                        bestCount = spanLen;
                    }
                }
            }
        }

        if (bestStart < 0 || bestCount <= 0)
            return (0, []);

        return (bestScore, tokenWords.Skip(bestStart).Take(bestCount).Select(x => x.Word).ToList());
    }

    private static List<NormalizedHighlightRect> BuildLineRectangles(
        List<Word> spanWords,
        double pageWidth,
        double pageHeight)
    {
        var rects = new List<NormalizedHighlightRect>();
        var lines = new List<List<Word>>();

        foreach (var word in spanWords)
        {
            if (lines.Count == 0)
            {
                lines.Add([word]);
                continue;
            }

            var currentLine = lines[^1];
            double avgBottom = currentLine.Average(w => w.BoundingBox.Bottom);
            if (Math.Abs(word.BoundingBox.Bottom - avgBottom) <= 5.0)
            {
                currentLine.Add(word);
            }
            else
            {
                lines.Add([word]);
            }
        }

        const double padPt = 1.8;
        foreach (var line in lines)
        {
            double minLeft = line.Min(w => w.BoundingBox.Left) - padPt;
            double maxRight = line.Max(w => w.BoundingBox.Right) + padPt;
            double minBottom = line.Min(w => w.BoundingBox.Bottom) - padPt;
            double maxTop = line.Max(w => w.BoundingBox.Top) + padPt;

            double x = Math.Clamp(minLeft / pageWidth, 0.0, 0.98);
            double y = Math.Clamp((pageHeight - maxTop) / pageHeight, 0.0, 0.98);
            double w = Math.Clamp((maxRight - minLeft) / pageWidth, 0.015, 1.0 - x);
            double h = Math.Clamp((maxTop - minBottom) / pageHeight, 0.012, 1.0 - y);

            rects.Add(new NormalizedHighlightRect(x, y, w, h));
        }

        return rects;
    }

    private static (string? Term, string SectionBreadcrumb)[] BuildPageSectionMap(PdfDocument doc)
    {
        int n = doc.NumberOfPages;
        var map = new (string? Term, string SectionBreadcrumb)[n + 1];
        for (int i = 1; i <= n; i++)
            map[i] = (null, string.Empty);

        if (!doc.TryGetBookmarks(out var bookmarks) || bookmarks.Roots.Count == 0)
            return map;

        var anchors = new List<(int Page, string? Term, string Breadcrumb)>();

        void Walk(IEnumerable<BookmarkNode> nodes, string? inheritedTerm, string parentPath)
        {
            foreach (var node in nodes)
            {
                var title = (node.Title ?? string.Empty).Trim();
                var term = TaskItem.NormalizeTerm(title) ?? inheritedTerm;
                var path = string.IsNullOrEmpty(parentPath) ? title : $"{parentPath} / {title}";

                if (node is DocumentBookmarkNode docNode && docNode.PageNumber >= 1 && docNode.PageNumber <= n)
                {
                    anchors.Add((docNode.PageNumber, term, path));
                }

                if (node.Children.Count > 0)
                    Walk(node.Children, term, path);
            }
        }

        Walk(bookmarks.Roots, null, string.Empty);

        if (anchors.Count == 0)
            return map;

        var ordered = anchors.OrderBy(a => a.Page).ToList();
        int aIdx = 0;
        string? currentTerm = ordered[0].Term;
        string currentCrumb = ordered[0].Breadcrumb;

        for (int p = 1; p <= n; p++)
        {
            while (aIdx < ordered.Count && ordered[aIdx].Page <= p)
            {
                if (ordered[aIdx].Term is not null)
                    currentTerm = ordered[aIdx].Term;
                currentCrumb = ordered[aIdx].Breadcrumb;
                aIdx++;
            }
            map[p] = (currentTerm, currentCrumb);
        }

        return map;
    }

    private static string ExtractCleanPageText(Page page)
    {
        var words = GetVisibleWordsInReadingOrder(page);
        if (words.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        double? lastBottom = null;

        foreach (var w in words)
        {
            var token = w.Text.Trim();
            if (string.IsNullOrEmpty(token))
                continue;

            if (lastBottom.HasValue && Math.Abs(w.BoundingBox.Bottom - lastBottom.Value) > 5.0)
                sb.AppendLine();
            else if (sb.Length > 0 && sb[^1] != '\n')
                sb.Append(' ');

            sb.Append(token);
            lastBottom = w.BoundingBox.Bottom;
        }

        return sb.ToString().Trim();
    }

    private static List<Word> GetVisibleWordsInReadingOrder(Page page)
    {
        double pw = Math.Max(1.0, page.Width);
        double ph = Math.Max(1.0, page.Height);

        var visible = page.GetWords()
            .Where(w =>
            {
                double cx = (w.BoundingBox.Left + w.BoundingBox.Right) * 0.5;
                double cy = (w.BoundingBox.Bottom + w.BoundingBox.Top) * 0.5;
                return cx >= 0 && cx <= pw && cy >= 0 && cy <= ph;
            })
            .ToList();

        return SortWordsReadingOrder(visible);
    }

    private static List<Word> SortWordsReadingOrder(List<Word> words)
    {
        // PDF coordinates have Y increasing upwards: group into approximate horizontal bands (6pt tolerance), top-to-bottom, left-to-right
        return words
            .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom / 5.0) * 5.0)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();
    }

    private static List<string> TokenizeWords(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken)
            .Where(t => t.Length > 0)
            .ToList();
    }

    private static string NormalizeToken(string raw)
    {
        var chars = raw.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    private static bool TokensMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        if (a.Length >= 5 && b.Length >= 5 &&
            (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return false;
    }
}
