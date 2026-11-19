using Avalonia.Media.Imaging;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Pdf;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Core.Services;

namespace ScholarFlow.Desktop.ViewModels;


public sealed partial class QuizStudioViewModel
{
    public void SelectCourseScope(BasinCourseEntry course)
    {
        SelectedCourse = course;
        SelectedPdfPath = null;
        StartPage = null;
        EndPage = null;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RebuildAvailablePdfsForCourse(null);
        _mainVm.RequestUiRefresh();
    }

    public void ToggleShowIndividualHandouts()
    {
        ShowIndividualHandouts = !ShowIndividualHandouts;
        _mainVm.RequestUiRefresh();
    }

    public void SelectPdfScope(string pdfPath)
    {
        SelectedPdfPath = pdfPath;
        StartPage = null;
        EndPage = null;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        InspectSelectedPdfOutlineAndPreview();
        _mainVm.RequestUiRefresh();
    }

    public void SelectTermScope(string? term)
    {
        SelectedTerm = string.Equals(SelectedTerm, term, StringComparison.OrdinalIgnoreCase) ? null : term;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public void SetPageRange(int? start, int? end)
    {
        StartPage = start is > 0 ? start : null;
        EndPage = end is > 0 ? end : null;
        if (StartPage.HasValue && EndPage.HasValue && EndPage.Value < StartPage.Value)
            EndPage = StartPage.Value;
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }

    public static string GetSectionKey(PdfOutlineSectionInfo s) => $"{s.StartPage}-{s.EndPage}:{s.Title}";

    public void ToggleOutlineSection(PdfOutlineSectionInfo sec)
    {
        var key = GetSectionKey(sec);
        bool added = SelectedSectionKeys.Add(key);
        if (!added)
            SelectedSectionKeys.Remove(key);
        RecomputeScopePreview();
        if (added)
        {
            var idx = MatchedScopePages
                .Select((p, i) => (p.PageNumber, Index: i))
                .FirstOrDefault(x => x.PageNumber >= sec.StartPage).Index;
            PreviewScopeIndex = idx;
        }
        _mainVm.RequestUiRefresh();
    }

    public void JumpPreviewToSection(PdfOutlineSectionInfo sec)
    {
        var matchIdx = -1;
        for (int i = 0; i < MatchedScopePages.Count; i++)
        {
            if (MatchedScopePages[i].PageNumber >= sec.StartPage)
            {
                matchIdx = i;
                break;
            }
        }

        if (matchIdx >= 0)
        {
            PreviewScopeIndex = matchIdx;
            _mainVm.RequestUiRefresh();
        }
    }

    public void PreviousPreviewPage()
    {
        if (PreviewScopeIndex > 0)
        {
            PreviewScopeIndex--;
            _mainVm.RequestUiRefresh();
        }
    }

    public void NextPreviewPage()
    {
        if (PreviewScopeIndex + 1 < MatchedScopePages.Count)
        {
            PreviewScopeIndex++;
            _mainVm.RequestUiRefresh();
        }
    }

    public void ClearScopeFilters()
    {
        SelectedTerm = null;
        StartPage = null;
        EndPage = null;
        SelectedSectionKeys.Clear();
        PreviewScopeIndex = 0;
        RecomputeScopePreview();
        _mainVm.RequestUiRefresh();
    }


    public void OpenQuestionEvidenceInPdfReader(QuizQuestionRecord question)
    {
        if (string.IsNullOrWhiteSpace(question.SourcePdfPath) || !File.Exists(question.SourcePdfPath))
        {
            StatusMessage = $"Source PDF not found: {question.SourcePdfPath}";
            IsStatusError = true;
            _mainVm.RequestUiRefresh();
            return;
        }

        var parentQuiz = SavedQuizzes.FirstOrDefault(qz => string.Equals(qz.Id, question.QuizId, StringComparison.OrdinalIgnoreCase));
        bool isStale = parentQuiz?.IsSourcePdfStale() == true;

        // Re-compute highlight rects on-the-fly if empty or if the source Master PDF was recompiled
        if ((isStale || question.HighlightRects.Count == 0) && !string.IsNullOrWhiteSpace(question.VerbatimQuote))
        {
            PdfQuizGrounder.GroundQuestionsToPdf(question.SourcePdfPath, [question], forceUpdateSection: true);
        }

        _mainVm.OpenPdfWithQuizEvidence(
            question.SourcePdfPath,
            question.SourcePage,
            question.HighlightRects,
            question.VerbatimQuote,
            question.Prompt);
    }

    private List<PdfPageRange> GetSelectedPageRanges()
    {
        if (SelectedSectionKeys.Count == 0)
            return [];

        return OutlineSections
            .Where(s => SelectedSectionKeys.Contains(GetSectionKey(s)))
            .Select(s => new PdfPageRange(s.StartPage, s.EndPage))
            .ToList();
    }

    public Bitmap? GetCurrentPreviewBitmap()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath) || !File.Exists(SelectedPdfPath) || PdfTotalPages <= 0)
            return null;

        int pageOneBased = Math.Clamp(CurrentPreviewPdfPage, 1, PdfTotalPages);
        var key = (SelectedPdfPath, pageOneBased);
        if (_previewBitmapCache.TryGetValue(key, out var cached))
            return cached;

        _ = LoadPreviewBitmapInBackgroundAsync(SelectedPdfPath, pageOneBased);
        return null;
    }

    private async Task LoadPreviewBitmapInBackgroundAsync(string pdfPath, int pageOneBased)
    {
        var key = (pdfPath, pageOneBased);
        if (!_pendingPreviewRenders.Add(key))
            return;

        IsLoadingPreviewPage = true;
        try
        {
            var pngBytes = await Task.Run(() =>
            {
                byte[] pdfBytes;
                lock (_pendingPreviewRenders)
                {
                    if (!string.Equals(_cachedBytesPath, pdfPath, StringComparison.OrdinalIgnoreCase) || _cachedPdfBytes is null)
                    {
                        _cachedPdfBytes = File.ReadAllBytes(pdfPath);
                        _cachedBytesPath = pdfPath;
                    }
                    pdfBytes = _cachedPdfBytes;
                }

                using var ms = new MemoryStream();
#pragma warning disable CA1416
                var renderOpts = new PDFtoImage.RenderOptions(
                    Dpi: 135,
                    BackgroundColor: SkiaSharp.SKColors.White);
                PDFtoImage.Conversion.SavePng(ms, pdfBytes, page: (Index)(pageOneBased - 1), options: renderOpts);
#pragma warning restore CA1416
                return ms.ToArray();
            });

            using var bmpStream = new MemoryStream(pngBytes);
            var bmp = new Bitmap(bmpStream);

            if (_previewBitmapCache.Count >= 18)
            {
                var firstKey = _previewBitmapCache.Keys.First();
                _previewBitmapCache[firstKey].Dispose();
                _previewBitmapCache.Remove(firstKey);
            }

            _previewBitmapCache[key] = bmp;
        }
        catch
        {
            // Non-fatal preview error
        }
        finally
        {
            _pendingPreviewRenders.Remove(key);
            IsLoadingPreviewPage = _pendingPreviewRenders.Count > 0;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _mainVm.RequestUiRefresh());
        }
    }

    private void RebuildAvailablePdfsForCourse(string? preferredPdfPath)
    {
        var list = new List<(string Label, string FullPath, bool IsMaster)>();
        if (SelectedCourse is not null && !string.IsNullOrWhiteSpace(_mainVm.BasinRoot))
        {
            var courseDir = Path.Combine(_mainVm.BasinRoot, SelectedCourse.FolderName);
            var masterPdf = MasterPdfService.FindExistingMasterPdfPath(_mainVm.BasinRoot, SelectedCourse.FolderName);
            if (masterPdf is not null && File.Exists(masterPdf))
            {
                list.Add(($"Master Study Packet ({Path.GetFileName(masterPdf)})", masterPdf, true));
            }

            if (Directory.Exists(courseDir))
            {
                foreach (var dir in Directory.GetDirectories(courseDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith('.') ||
                        dirName.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("ongoing", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var pdf in Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var fileName = Path.GetFileName(pdf);
                        if (fileName.EndsWith("_.pdf", StringComparison.OrdinalIgnoreCase))
                            continue;
                        list.Add((dirName.Replace('_', ' '), pdf, false));
                    }
                }
            }
        }

        AvailablePdfs = list;
        if (!string.IsNullOrWhiteSpace(preferredPdfPath) && File.Exists(preferredPdfPath))
        {
            if (!AvailablePdfs.Any(p => p.FullPath.Equals(preferredPdfPath, StringComparison.OrdinalIgnoreCase)))
            {
                list.Insert(0, (Path.GetFileName(preferredPdfPath), preferredPdfPath, false));
                AvailablePdfs = list;
            }
            SelectedPdfPath = preferredPdfPath;
        }
        else if (SelectedPdfPath is null || !AvailablePdfs.Any(p => p.FullPath.Equals(SelectedPdfPath, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPdfPath = AvailablePdfs.FirstOrDefault(p => p.IsMaster).FullPath
                              ?? AvailablePdfs.FirstOrDefault().FullPath;
        }

        InspectSelectedPdfOutlineAndPreview();
    }

    private void InspectSelectedPdfOutlineAndPreview()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath) || !File.Exists(SelectedPdfPath))
        {
            PdfTotalPages = 0;
            OutlineSections = [];
            MatchedScopePages = [];
            PreviewScopeIndex = 0;
            return;
        }

        if (_outlineCache.TryGetValue(SelectedPdfPath, out var cached))
        {
            PdfTotalPages = cached.TotalPages;
            OutlineSections = cached.Sections;
            RecomputeScopePreview();
        }
        else
        {
            _ = InspectOutlineInBackgroundAsync(SelectedPdfPath);
        }
    }

    private async Task InspectOutlineInBackgroundAsync(string pdfPath)
    {
        IsInspectingPdf = true;
        try
        {
            var (sections, totalPages) = await Task.Run(() =>
            {
                var secs = PdfQuizGrounder.InspectOutlineSections(pdfPath, out int pages);
                return (secs, pages);
            });

            _outlineCache[pdfPath] = (totalPages, sections);
            if (string.Equals(SelectedPdfPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                PdfTotalPages = totalPages;
                OutlineSections = sections;
                RecomputeScopePreview();
            }
        }
        finally
        {
            IsInspectingPdf = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _mainVm.RequestUiRefresh());
        }
    }

    private void RecomputeScopePreview()
    {
        if (string.IsNullOrWhiteSpace(SelectedPdfPath) || !File.Exists(SelectedPdfPath) || PdfTotalPages <= 0)
        {
            MatchedScopePages = [];
            PreviewScopeIndex = 0;
            return;
        }

        MatchedScopePages = PdfQuizGrounder.ResolveScopePagesFast(
            PdfTotalPages,
            OutlineSections,
            SelectedTerm,
            StartPage,
            EndPage,
            GetSelectedPageRanges());

        if (PreviewScopeIndex >= MatchedScopePages.Count)
            PreviewScopeIndex = 0;

        RefreshLivePromptPreviewIfActive();
    }
}
