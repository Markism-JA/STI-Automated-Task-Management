using System.Text;
using System.Text.Json;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using ScholarFlow.Core.Pdf;
using UglyToad.PdfPig;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Handles portable JSON export/import, printable PDF generation (via PdfSharpCore),
/// and point-to-point single-question AI repair prompts for quiz decks.
/// </summary>
public static class QuizExportService
{
    private static readonly JsonSerializerOptions PrettyJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string ExportDeckToJson(QuizSessionRecord quiz) =>
        JsonSerializer.Serialize(quiz, PrettyJsonOpts);

    public static QuizSessionRecord ImportDeckFromJson(
        string rawJson,
        string targetCourseFolder,
        string targetCourseTitle,
        string? sourcePdfPathOverride = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ArgumentException("JSON content is empty.", nameof(rawJson));

        var cleaned = ExtractJsonPayload(rawJson);

        // 1. Try full QuizSessionRecord format first
        try
        {
            var imported = JsonSerializer.Deserialize<QuizSessionRecord>(cleaned, PrettyJsonOpts);
            if (imported is not null && imported.Questions.Count > 0)
            {
                var newQuizId = Guid.NewGuid().ToString("N")[..10];
                imported.Id = newQuizId;
                imported.CourseFolder = targetCourseFolder;
                imported.CourseTitle = targetCourseTitle;
                if (!string.IsNullOrWhiteSpace(sourcePdfPathOverride))
                    imported.SourcePdfPath = sourcePdfPathOverride;
                imported.InProgressQuestionIndex = null;
                imported.InProgressAnswers.Clear();

                for (int i = 0; i < imported.Questions.Count; i++)
                {
                    var q = imported.Questions[i];
                    q.Id = Guid.NewGuid().ToString("N")[..10];
                    q.QuizId = newQuizId;
                    q.OrderIndex = i;
                    if (!string.IsNullOrWhiteSpace(sourcePdfPathOverride))
                        q.SourcePdfPath = sourcePdfPathOverride;
                }

                if (!string.IsNullOrWhiteSpace(imported.SourcePdfPath) && File.Exists(imported.SourcePdfPath))
                    PdfQuizGrounder.GroundQuestionsToPdf(imported.SourcePdfPath, imported.Questions);

                return imported;
            }
        }
        catch
        {
            // Fall through to QuizGenerationResponseDto
        }

        // 2. Fallback to AI QuizGenerationResponseDto format
        var dto = JsonSerializer.Deserialize<QuizGenerationResponseDto>(cleaned, PrettyJsonOpts)
                  ?? throw new InvalidOperationException("Could not parse quiz JSON as a QuizSessionRecord or QuizGenerationResponseDto.");

        if (dto.Questions.Count == 0)
            throw new InvalidOperationException("Imported JSON contains 0 questions.");

        var quizId = Guid.NewGuid().ToString("N")[..10];
        var session = new QuizSessionRecord
        {
            Id = quizId,
            CourseFolder = targetCourseFolder,
            CourseTitle = targetCourseTitle,
            Term = string.IsNullOrWhiteSpace(dto.CourseTerm) ? null : dto.CourseTerm.Trim(),
            Title = string.IsNullOrWhiteSpace(dto.QuizTitle) ? $"{targetCourseTitle} — Imported Deck" : dto.QuizTitle.Trim(),
            SourcePdfPath = sourcePdfPathOverride ?? string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        for (int i = 0; i < dto.Questions.Count; i++)
        {
            var qDto = dto.Questions[i];
            if (!Enum.TryParse<QuizQuestionKind>(qDto.Kind, ignoreCase: true, out var kind))
                kind = QuizQuestionKind.MultipleChoice;
            if (!Enum.TryParse<CognitiveLevel>(qDto.CognitiveLevel, ignoreCase: true, out var cog))
                cog = CognitiveLevel.Comprehension;

            session.Questions.Add(new QuizQuestionRecord
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                QuizId = quizId,
                OrderIndex = i,
                Kind = kind,
                CognitiveLevel = cog,
                Difficulty = Math.Clamp(qDto.Difficulty, 1, 5),
                TopicTag = string.IsNullOrWhiteSpace(qDto.TopicTag) ? "general" : qDto.TopicTag.Trim(),
                Prompt = qDto.Prompt.Trim(),
                Options = qDto.Options?.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList() ?? [],
                CorrectAnswer = qDto.CorrectAnswer.Trim(),
                AcceptedAliases = qDto.AcceptedAliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? [],
                Explanation = (qDto.Explanation ?? string.Empty).Trim(),
                CodeSnippet = string.IsNullOrWhiteSpace(qDto.CodeSnippet) ? null : qDto.CodeSnippet,
                CodeLanguage = string.IsNullOrWhiteSpace(qDto.CodeLanguage) ? null : qDto.CodeLanguage,
                MathLatex = string.IsNullOrWhiteSpace(qDto.MathLatex) ? null : qDto.MathLatex,
                Diagram = string.IsNullOrWhiteSpace(qDto.Diagram) ? null : qDto.Diagram,
                SourcePdfPath = sourcePdfPathOverride ?? string.Empty,
                SourcePage = qDto.Evidence?.PageNumber > 0 ? qDto.Evidence.PageNumber : 1,
                SectionTitle = qDto.Evidence?.SectionTitle ?? string.Empty,
                VerbatimQuote = qDto.Evidence?.VerbatimQuote ?? string.Empty,
                SrsDueDate = DateOnly.FromDateTime(DateTime.Today),
            });
        }

        if (!string.IsNullOrWhiteSpace(session.SourcePdfPath) && File.Exists(session.SourcePdfPath))
            PdfQuizGrounder.GroundQuestionsToPdf(session.SourcePdfPath, session.Questions);

        return session;
    }

    public static void ExportDeckToPrintablePdf(QuizSessionRecord quiz, string outputPdfPath)
    {
        MasterPdfService.EnsureFontResolver();

        using var doc = new PdfSharpCore.Pdf.PdfDocument();
        doc.Info.Title = quiz.Title;

        const double margin = 44;
        double y = margin;
        PdfPage page = doc.AddPage();
        XGraphics gfx = XGraphics.FromPdfPage(page);

        var titleFont = new XFont("Arial", 16, XFontStyle.Bold);
        var subFont = new XFont("Arial", 10, XFontStyle.Regular);
        var headingFont = new XFont("Arial", 12, XFontStyle.Bold);
        var bodyFont = new XFont("Arial", 10.5, XFontStyle.Regular);
        var boldBodyFont = new XFont("Arial", 10.5, XFontStyle.Bold);
        var monoFont = new XFont("Courier New", 9.5, XFontStyle.Regular);
        var mutedFont = new XFont("Arial", 9, XFontStyle.Italic);

        void NewPage()
        {
            gfx.Dispose();
            page = doc.AddPage();
            gfx = XGraphics.FromPdfPage(page);
            y = margin;
        }

        void EnsureSpace(double neededHeight)
        {
            if (y + neededHeight > page.Height.Point - margin)
                NewPage();
        }

        void DrawWrapped(string text, XFont font, XBrush brush, double indent = 0, double lineHeight = 14)
        {
            double maxWidth = page.Width.Point - (margin * 2) - indent;
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (string.IsNullOrEmpty(line))
                {
                    y += lineHeight * 0.5;
                    continue;
                }

                var words = line.Split(' ');
                var current = new StringBuilder();
                foreach (var word in words)
                {
                    var candidate = current.Length == 0 ? word : $"{current} {word}";
                    if (gfx.MeasureString(candidate, font).Width > maxWidth && current.Length > 0)
                    {
                        EnsureSpace(lineHeight);
                        gfx.DrawString(current.ToString(), font, brush, new XPoint(margin + indent, y));
                        y += lineHeight;
                        current.Clear();
                        current.Append(word);
                    }
                    else
                    {
                        current.Clear();
                        current.Append(candidate);
                    }
                }

                if (current.Length > 0)
                {
                    EnsureSpace(lineHeight);
                    gfx.DrawString(current.ToString(), font, brush, new XPoint(margin + indent, y));
                    y += lineHeight;
                }
            }
        }

        // Cover / Quiz Sheet Header
        DrawWrapped(quiz.Title, titleFont, XBrushes.Black, lineHeight: 20);
        var meta = $"{quiz.CourseTitle}  |  {quiz.Term ?? "General"}  |  {quiz.Questions.Count} Questions  |  Printed {DateTime.Now:MMM d, yyyy}";
        DrawWrapped(meta, subFont, XBrushes.DimGray, lineHeight: 14);
        y += 8;
        gfx.DrawLine(XPens.LightGray, margin, y, page.Width.Point - margin, y);
        y += 16;

        // Questions Section
        for (int i = 0; i < quiz.Questions.Count; i++)
        {
            var q = quiz.Questions[i];
            EnsureSpace(48);
            DrawWrapped($"{i + 1}. [{q.Kind}] {q.Prompt}", boldBodyFont, XBrushes.Black, lineHeight: 15);

            if (!string.IsNullOrWhiteSpace(q.CodeSnippet))
            {
                y += 2;
                DrawWrapped(q.CodeSnippet, monoFont, XBrushes.DarkSlateGray, indent: 14, lineHeight: 13);
            }
            if (!string.IsNullOrWhiteSpace(q.MathLatex))
                DrawWrapped($"Formula: {q.MathLatex}", monoFont, XBrushes.DarkBlue, indent: 14, lineHeight: 13);
            if (!string.IsNullOrWhiteSpace(q.Diagram))
                DrawWrapped($"Flow: {q.Diagram}", monoFont, XBrushes.DarkGreen, indent: 14, lineHeight: 13);

            if (q.Kind is QuizQuestionKind.MultipleChoice or QuizQuestionKind.Situational or QuizQuestionKind.TrueFalse)
            {
                for (int oi = 0; oi < q.Options.Count; oi++)
                {
                    char letter = (char)('A' + oi);
                    DrawWrapped($"{letter}) {q.Options[oi]}", bodyFont, XBrushes.Black, indent: 16, lineHeight: 14);
                }
            }
            else if (q.Kind == QuizQuestionKind.Ordering && q.Options.Count > 0)
            {
                DrawWrapped("Order the steps:", mutedFont, XBrushes.DimGray, indent: 16, lineHeight: 13);
                foreach (var step in q.Options.OrderBy(_ => q.Id, StringComparer.Ordinal))
                    DrawWrapped($"[  ] {step}", bodyFont, XBrushes.Black, indent: 20, lineHeight: 14);
            }
            else
            {
                DrawWrapped("Answer: ________________________________________", bodyFont, XBrushes.DimGray, indent: 16, lineHeight: 16);
            }

            y += 10;
        }

        // Answer Key & Evidence Section
        NewPage();
        DrawWrapped($"Answer Key — {quiz.Title}", headingFont, XBrushes.Black, lineHeight: 18);
        y += 6;
        gfx.DrawLine(XPens.LightGray, margin, y, page.Width.Point - margin, y);
        y += 14;

        for (int i = 0; i < quiz.Questions.Count; i++)
        {
            var q = quiz.Questions[i];
            EnsureSpace(42);
            DrawWrapped($"{i + 1}. Answer: {q.CorrectAnswer}", boldBodyFont, XBrushes.DarkGreen, lineHeight: 14);
            if (!string.IsNullOrWhiteSpace(q.Explanation))
                DrawWrapped($"Explanation: {q.Explanation}", bodyFont, XBrushes.Black, indent: 14, lineHeight: 13);
            if (!string.IsNullOrWhiteSpace(q.VerbatimQuote))
                DrawWrapped($"Evidence ({q.SectionTitle}, p. {q.SourcePage}): \"{q.VerbatimQuote}\"", mutedFont, XBrushes.DimGray, indent: 14, lineHeight: 12);
            y += 8;
        }

        gfx.Dispose();
        var dir = Path.GetDirectoryName(outputPdfPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        doc.Save(outputPdfPath);
    }

    public static string BuildSingleQuestionFixPrompt(
        QuizSessionRecord quiz,
        QuizQuestionRecord question,
        string problemDescription)
    {
        var dto = new GeneratedQuestionDto
        {
            Kind = question.Kind.ToString(),
            CognitiveLevel = question.CognitiveLevel.ToString(),
            Difficulty = question.Difficulty,
            TopicTag = question.TopicTag,
            Prompt = question.Prompt,
            Options = [.. question.Options],
            CorrectAnswer = question.CorrectAnswer,
            AcceptedAliases = [.. question.AcceptedAliases],
            Explanation = question.Explanation,
            CodeSnippet = question.CodeSnippet,
            CodeLanguage = question.CodeLanguage,
            MathLatex = question.MathLatex,
            Diagram = question.Diagram,
            Evidence = new EvidenceAnchorDto
            {
                PageNumber = question.SourcePage,
                SectionTitle = question.SectionTitle,
                VerbatimQuote = question.VerbatimQuote
            }
        };

        IReadOnlyList<GroundedPdfPage> contextPages = [];
        var pdfPath = !string.IsNullOrWhiteSpace(question.SourcePdfPath)
            ? question.SourcePdfPath
            : quiz.SourcePdfPath;
        if (!string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath) && question.SourcePage > 0)
        {
            try
            {
                contextPages = PdfQuizGrounder.ExtractGroundedPages(
                    pdfPath,
                    startPage: Math.Max(1, question.SourcePage - 1),
                    endPage: question.SourcePage + 1,
                    maxPages: 3
                );
            }
            catch
            {
                // Ignore if PDF cannot be read
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"You are repairing ONE question from the quiz \"{quiz.Title}\" ({quiz.CourseTitle}).");
        sb.AppendLine("Return ONLY a single valid JSON object matching the exact question schema below (no markdown fences or commentary).");
        sb.AppendLine();
        sb.AppendLine("REPORTED ISSUE WITH THIS QUESTION:");
        sb.AppendLine(string.IsNullOrWhiteSpace(problemDescription) ? "Fix clarity, answer accuracy, and PDF evidence grounding." : problemDescription.Trim());
        sb.AppendLine();
        sb.AppendLine("CURRENT QUESTION JSON:");
        sb.AppendLine(JsonSerializer.Serialize(dto, PrettyJsonOpts));

        if (contextPages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("GROUNDED SOURCE PDF CONTEXT:");
            sb.AppendLine(PdfQuizGrounder.FormatPagesForPrompt(contextPages, maxChars: 14_000, out _));
        }

        return sb.ToString();
    }

    public static string BuildDeckComprehensiveFixPrompt(
        QuizSessionRecord deck,
        IReadOnlyList<QuizQuestionRecord> targetQuestions,
        string instructions)
    {
        var dtoList = targetQuestions.Select(q => new
        {
            targetId = q.Id,
            kind = q.Kind.ToString(),
            cognitiveLevel = q.CognitiveLevel.ToString(),
            difficulty = q.Difficulty,
            topicTag = q.TopicTag,
            prompt = q.Prompt,
            options = q.Options,
            correctAnswer = q.CorrectAnswer,
            acceptedAliases = q.AcceptedAliases,
            explanation = q.Explanation,
            codeSnippet = q.CodeSnippet,
            codeLanguage = q.CodeLanguage,
            mathLatex = q.MathLatex,
            diagram = q.Diagram,
            evidence = new
            {
                pageNumber = q.SourcePage,
                sectionTitle = q.SectionTitle,
                verbatimQuote = q.VerbatimQuote
            }
        }).ToList();

        var ranges = targetQuestions
            .Where(q => q.SourcePage > 0)
            .Select(q => new PdfPageRange(Math.Max(1, q.SourcePage - 1), q.SourcePage + 1))
            .OrderBy(r => r.StartPage)
            .ToList();

        IReadOnlyList<GroundedPdfPage> contextPages = [];
        if (!string.IsNullOrWhiteSpace(deck.SourcePdfPath) && File.Exists(deck.SourcePdfPath))
        {
            try
            {
                contextPages = ranges.Count > 0
                    ? PdfQuizGrounder.ExtractGroundedPages(deck.SourcePdfPath, explicitRanges: ranges, maxPages: 30)
                    : PdfQuizGrounder.ExtractGroundedPages(deck.SourcePdfPath, maxPages: 20);
            }
            catch
            {
                // Ignore if PDF cannot be read
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"You are performing a comprehensive AI repair & enhancement on the quiz deck \"{deck.Title}\" ({deck.CourseTitle}).");
        sb.AppendLine("Return ONLY a valid JSON object matching this exact schema (no markdown fences):");
        sb.AppendLine("{");
        sb.AppendLine("  \"updatedQuestions\": [ { \"targetId\": \"existing-id\", \"kind\": \"...\", \"cognitiveLevel\": \"...\", \"difficulty\": 3, \"topicTag\": \"...\", \"prompt\": \"...\", \"options\": [...], \"correctAnswer\": \"...\", \"acceptedAliases\": [], \"explanation\": \"...\", \"codeSnippet\": null, \"codeLanguage\": null, \"mathLatex\": null, \"diagram\": null, \"evidence\": { \"pageNumber\": 1, \"sectionTitle\": \"...\", \"verbatimQuote\": \"...\" } } ],");
        sb.AppendLine("  \"newQuestions\": [ { \"kind\": \"...\", ...same fields without targetId... } ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("USER INSTRUCTIONS / ISSUES TO FIX:");
        sb.AppendLine(string.IsNullOrWhiteSpace(instructions) ? "Fix inaccuracies, improve distractors, and ensure every question has accurate PDF evidence." : instructions.Trim());
        sb.AppendLine();
        sb.AppendLine($"TARGETED QUESTIONS ({dtoList.Count}):");
        sb.AppendLine(JsonSerializer.Serialize(dtoList, PrettyJsonOpts));

        if (contextPages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("GROUNDED SOURCE PDF CONTEXT:");
            sb.AppendLine(PdfQuizGrounder.FormatPagesForPrompt(contextPages, maxChars: 60_000, out _));
        }

        return sb.ToString();
    }

    private sealed class ComprehensiveFixResponseDto
    {
        public List<ComprehensiveUpdatedQuestionDto> UpdatedQuestions { get; set; } = [];
        public List<GeneratedQuestionDto> NewQuestions { get; set; } = [];
    }

    private sealed class ComprehensiveUpdatedQuestionDto
    {
        public string? TargetId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string CognitiveLevel { get; set; } = string.Empty;
        public int Difficulty { get; set; } = 3;
        public string TopicTag { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public List<string> Options { get; set; } = [];
        public string CorrectAnswer { get; set; } = string.Empty;
        public List<string> AcceptedAliases { get; set; } = [];
        public string Explanation { get; set; } = string.Empty;
        public string? CodeSnippet { get; set; }
        public string? CodeLanguage { get; set; }
        public string? MathLatex { get; set; }
        public string? Diagram { get; set; }
        public EvidenceAnchorDto? Evidence { get; set; }

        public GeneratedQuestionDto ToGeneratedDto() =>
            new()
            {
                Kind = Kind,
                CognitiveLevel = CognitiveLevel,
                Difficulty = Difficulty,
                TopicTag = TopicTag,
                Prompt = Prompt,
                Options = Options,
                CorrectAnswer = CorrectAnswer,
                AcceptedAliases = AcceptedAliases,
                Explanation = Explanation,
                CodeSnippet = CodeSnippet,
                CodeLanguage = CodeLanguage,
                MathLatex = MathLatex,
                Diagram = Diagram,
                Evidence = Evidence ?? new EvidenceAnchorDto(),
            };
    }

    public static (int UpdatedCount, int AddedCount) ApplyDeckComprehensiveFixJson(
        QuizSessionRecord deck,
        string rawJson)
    {
        var cleaned = ExtractJsonPayload(rawJson);
        var payload = JsonSerializer.Deserialize<ComprehensiveFixResponseDto>(cleaned, PrettyJsonOpts)
            ?? throw new InvalidOperationException("Could not parse comprehensive deck fix JSON.");

        int updated = 0;
        var touched = new List<QuizQuestionRecord>();

        foreach (var upd in payload.UpdatedQuestions)
        {
            var target = !string.IsNullOrWhiteSpace(upd.TargetId)
                ? deck.Questions.FirstOrDefault(q => string.Equals(q.Id, upd.TargetId, StringComparison.OrdinalIgnoreCase))
                : null;
            if (target is null)
                continue;

            ApplyDtoToQuestion(target, upd.ToGeneratedDto());
            touched.Add(target);
            updated++;
        }

        int added = 0;
        foreach (var addDto in payload.NewQuestions)
        {
            var newQ = new QuizQuestionRecord
            {
                QuizId = deck.Id,
                OrderIndex = deck.Questions.Count,
                SourcePdfPath = deck.SourcePdfPath,
                SrsDueDate = DateOnly.FromDateTime(DateTime.Today),
            };
            ApplyDtoToQuestion(newQ, addDto);
            deck.Questions.Add(newQ);
            touched.Add(newQ);
            added++;
        }

        if (touched.Count > 0 && !string.IsNullOrWhiteSpace(deck.SourcePdfPath) && File.Exists(deck.SourcePdfPath))
            PdfQuizGrounder.GroundQuestionsToPdf(deck.SourcePdfPath, touched);

        return (updated, added);
    }

    public static void ApplySingleQuestionFixJson(QuizQuestionRecord target, string rawJson)
    {
        var cleaned = ExtractJsonPayload(rawJson);
        GeneratedQuestionDto? dto = null;

        try
        {
            var wrapper = JsonSerializer.Deserialize<QuizGenerationResponseDto>(cleaned, PrettyJsonOpts);
            if (wrapper?.Questions is { Count: > 0 })
                dto = wrapper.Questions[0];
        }
        catch
        {
            // Try direct single-object deserialization
        }

        dto ??= JsonSerializer.Deserialize<GeneratedQuestionDto>(cleaned, PrettyJsonOpts)
            ?? throw new InvalidOperationException("Could not parse fixed question JSON.");

        ApplyDtoToQuestion(target, dto);

        if (!string.IsNullOrWhiteSpace(target.SourcePdfPath) && File.Exists(target.SourcePdfPath))
            PdfQuizGrounder.GroundQuestionsToPdf(target.SourcePdfPath, [target]);
    }

    private static void ApplyDtoToQuestion(QuizQuestionRecord target, GeneratedQuestionDto dto)
    {
        if (Enum.TryParse<QuizQuestionKind>(dto.Kind, ignoreCase: true, out var kind))
            target.Kind = kind;
        if (Enum.TryParse<CognitiveLevel>(dto.CognitiveLevel, ignoreCase: true, out var cog))
            target.CognitiveLevel = cog;

        target.Difficulty = Math.Clamp(dto.Difficulty, 1, 5);
        if (!string.IsNullOrWhiteSpace(dto.TopicTag))
            target.TopicTag = dto.TopicTag.Trim();
        if (!string.IsNullOrWhiteSpace(dto.Prompt))
            target.Prompt = dto.Prompt.Trim();
        target.Options = dto.Options?.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList() ?? [];
        target.CorrectAnswer = (dto.CorrectAnswer ?? string.Empty).Trim();
        target.AcceptedAliases = dto.AcceptedAliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? [];
        target.Explanation = (dto.Explanation ?? string.Empty).Trim();
        target.CodeSnippet = string.IsNullOrWhiteSpace(dto.CodeSnippet) ? null : dto.CodeSnippet;
        target.CodeLanguage = string.IsNullOrWhiteSpace(dto.CodeLanguage) ? null : dto.CodeLanguage;
        target.MathLatex = string.IsNullOrWhiteSpace(dto.MathLatex) ? null : dto.MathLatex;
        target.Diagram = string.IsNullOrWhiteSpace(dto.Diagram) ? null : dto.Diagram;

        if (dto.Evidence is not null)
        {
            if (dto.Evidence.PageNumber > 0)
                target.SourcePage = dto.Evidence.PageNumber;
            if (!string.IsNullOrWhiteSpace(dto.Evidence.SectionTitle))
                target.SectionTitle = dto.Evidence.SectionTitle;
            if (!string.IsNullOrWhiteSpace(dto.Evidence.VerbatimQuote))
                target.VerbatimQuote = dto.Evidence.VerbatimQuote;
        }
    }

    private static string ExtractJsonPayload(string raw)
    {
        var trimmed = raw.Trim();
        int firstBrace = trimmed.IndexOf('{');
        int lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];
        return trimmed;
    }
}
