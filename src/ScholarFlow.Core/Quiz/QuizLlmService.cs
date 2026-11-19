using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;

namespace ScholarFlow.Core.Quiz;

/// <summary>
/// Unified OpenAI-compatible AI Quiz Generator (supports Google AI Studio Gemini, Local Ollama, OpenRouter/OpenAI,
/// Manual Gemini Web JSON Import, and Offline Smart PDF Heuristic Generation) with deterministic PdfPig highlight grounding.
/// </summary>
public sealed partial class QuizLlmService
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string BuildPromptForManualCopy(
        QuizGenerationRequest request,
        out IReadOnlyList<GroundedPdfPage> extractedPages)
    {
        extractedPages = PdfQuizGrounder.ExtractGroundedPages(
            request.SourcePdfPath,
            request.Term,
            request.SectionFilter,
            request.StartPage,
            request.EndPage,
            request.SelectedRanges,
            maxPages: 120);

        var groundedContent = PdfQuizGrounder.FormatPagesForPrompt(extractedPages, maxChars: 48_000);
        var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(request.BasinRoot, request.CourseFolder);
        var modality = DetectContentModalities(extractedPages, request.CourseTitle, request.ModalityMode);
        int count = Math.Clamp(request.QuestionCount, 1, 150);
        return BuildSystemInstructions(request, weakTopics, modality) + "\n\n" + BuildUserPrompt(request, groundedContent, count, modality: modality);
    }

    public async Task<QuizSessionRecord> GenerateQuizAsync(
        QuizGenerationRequest request,
        CancellationToken ct = default)
    {
        var pages = await Task.Run(() => PdfQuizGrounder.ExtractGroundedPages(
            request.SourcePdfPath,
            request.Term,
            request.SectionFilter,
            request.StartPage,
            request.EndPage,
            request.SelectedRanges,
            maxPages: 150), ct);

        if (pages.Count == 0)
            throw new InvalidOperationException("No readable text pages were found in the selected PDF scope.");

        int desiredCount = Math.Clamp(request.QuestionCount, 1, 150);

        var effectiveKey = !string.IsNullOrWhiteSpace(request.ApiKey)
            ? request.ApiKey.Trim()
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        bool isLocalEndpoint = request.ProviderBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                               request.ProviderBaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        QuizGenerationResponseDto dto;
        string modelLabel;

        if (request.UseOfflineHeuristicFallback || (!isLocalEndpoint && string.IsNullOrWhiteSpace(effectiveKey)))
        {
            dto = await Task.Run(() => GenerateOfflineGroundedDto(request, pages, desiredCount), ct);
            modelLabel = "ScholarFlow Smart PDF Analyzer (Offline)";
        }
        else
        {
            var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(request.BasinRoot, request.CourseFolder);
            var modality = DetectContentModalities(pages, request.CourseTitle, request.ModalityMode);
            var sysPrompt = BuildSystemInstructions(request, weakTopics, modality);

            const int batchSize = 20;
            int batchCount = Math.Max(1, (int)Math.Ceiling(desiredCount / (double)batchSize));
            var allQuestions = new List<GeneratedQuestionDto>();
            var allConcepts = new List<string>();
            string? firstTitle = null;
            string? firstTerm = null;

            for (int b = 0; b < batchCount; b++)
            {
                int remaining = desiredCount - allQuestions.Count;
                if (remaining <= 0)
                    break;

                int thisBatchCount = Math.Min(batchSize, remaining);
                var batchPages = SlicePagesForBatch(pages, b, batchCount);
                var groundedContent = PdfQuizGrounder.FormatPagesForPrompt(batchPages, maxChars: 38_000);
                var usrPrompt = BuildUserPrompt(request, groundedContent, thisBatchCount, b + 1, batchCount, modality);

                var rawResponse = await CallOpenAiCompatibleChatAsync(
                    request.ProviderBaseUrl,
                    effectiveKey,
                    request.ModelName,
                    sysPrompt,
                    usrPrompt,
                    ct);

                var batchDto = ParseQuizResponseJson(rawResponse);
                firstTitle ??= batchDto.QuizTitle;
                firstTerm ??= batchDto.CourseTerm;
                allConcepts.AddRange(batchDto.KeyConceptsCovered);
                allQuestions.AddRange(batchDto.Questions);
            }

            dto = new QuizGenerationResponseDto
            {
                QuizTitle = firstTitle ?? string.Empty,
                CourseTerm = firstTerm ?? string.Empty,
                KeyConceptsCovered = allConcepts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Questions = allQuestions.Take(desiredCount).ToList()
            };
            modelLabel = request.ModelName;
        }

        return await BuildGroundAndPersistSessionAsync(request, dto, modelLabel, pages, ct);
    }

    private static IReadOnlyList<GroundedPdfPage> SlicePagesForBatch(
        IReadOnlyList<GroundedPdfPage> allPages,
        int batchIndex,
        int totalBatches)
    {
        if (totalBatches <= 1 || allPages.Count <= 4)
            return allPages;

        int sliceSize = Math.Max(1, (int)Math.Ceiling(allPages.Count / (double)totalBatches));
        int start = Math.Min(allPages.Count - 1, batchIndex * sliceSize);
        int count = Math.Min(sliceSize, allPages.Count - start);
        var slice = allPages.Skip(start).Take(count).ToList();
        return slice.Count > 0 ? slice : allPages;
    }

    public async Task<QuizSessionRecord> ImportQuizFromJsonAsync(
        QuizGenerationRequest request,
        string rawJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new ArgumentException("JSON input cannot be empty.", nameof(rawJson));

        var (pages, dto) = await Task.Run(() =>
        {
            var p = PdfQuizGrounder.ExtractGroundedPages(
                request.SourcePdfPath,
                request.Term,
                request.SectionFilter,
                request.StartPage,
                request.EndPage,
                request.SelectedRanges,
                maxPages: 120);
            var parsed = ParseQuizResponseJson(rawJson);
            return (p, parsed);
        }, ct);

        return await BuildGroundAndPersistSessionAsync(request, dto, "Imported Web AI JSON", pages, ct);
    }

    private static async Task<QuizSessionRecord> BuildGroundAndPersistSessionAsync(
        QuizGenerationRequest request,
        QuizGenerationResponseDto dto,
        string modelLabel,
        IReadOnlyList<GroundedPdfPage> pages,
        CancellationToken ct)
    {
        var inferredTerm = TaskItem.NormalizeTerm(dto.CourseTerm)
                           ?? TaskItem.NormalizeTerm(request.Term)
                           ?? pages.FirstOrDefault(p => p.Term is not null)?.Term;

        var defaultTitle = !string.IsNullOrWhiteSpace(dto.QuizTitle)
            ? dto.QuizTitle.Trim()
            : $"{request.CourseTitle} — {inferredTerm ?? "Comprehensive"} ({request.Preset})";

        var session = new QuizSessionRecord
        {
            CourseFolder = request.CourseFolder,
            CourseTitle = request.CourseTitle,
            Term = inferredTerm,
            Title = defaultTitle,
            SourcePdfPath = request.SourcePdfPath,
            SourcePdfMtimeTicks = !string.IsNullOrWhiteSpace(request.SourcePdfPath) && File.Exists(request.SourcePdfPath)
                ? File.GetLastWriteTimeUtc(request.SourcePdfPath).Ticks
                : null,
            ModelUsed = modelLabel,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var today = DateOnly.FromDateTime(DateTime.Today);
        for (int i = 0; i < dto.Questions.Count; i++)
        {
            var qDto = dto.Questions[i];
            if (string.IsNullOrWhiteSpace(qDto.Prompt))
                continue;

            var kind = Enum.TryParse<QuizQuestionKind>(qDto.Kind, ignoreCase: true, out var parsedKind)
                ? parsedKind
                : QuizQuestionKind.MultipleChoice;

            var cog = Enum.TryParse<CognitiveLevel>(qDto.CognitiveLevel, ignoreCase: true, out var parsedCog)
                ? parsedCog
                : CognitiveLevel.Comprehension;

            var options = qDto.Options?.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList() ?? [];
            if (kind == QuizQuestionKind.TrueFalse && options.Count == 0)
                options = ["True", "False"];

            var correctAnswer = (qDto.CorrectAnswer ?? string.Empty).Trim();
            if (kind == QuizQuestionKind.Ordering && string.IsNullOrWhiteSpace(correctAnswer) && options.Count >= 2)
                correctAnswer = string.Join(" -> ", options);

            var mathLatex = string.IsNullOrWhiteSpace(qDto.MathLatex) ? null : qDto.MathLatex.Trim();
            var codeSnippet = string.IsNullOrWhiteSpace(qDto.CodeSnippet) ? null : qDto.CodeSnippet.Trim();
            var codeLanguage = string.IsNullOrWhiteSpace(qDto.CodeLanguage) ? null : qDto.CodeLanguage.Trim();
            var diagram = string.IsNullOrWhiteSpace(qDto.Diagram) ? null : qDto.Diagram.Trim();

            // Strict Math Guardrail: symbolic/LaTeX math questions must use MultipleChoice/Situational
            // when options are available so students never have to type raw LaTeX into an Identification box.
            if (!string.IsNullOrWhiteSpace(mathLatex) &&
                kind is QuizQuestionKind.Identification or QuizQuestionKind.FillInTheBlank &&
                options.Count >= 2)
            {
                kind = QuizQuestionKind.MultipleChoice;
            }

            var qRecord = new QuizQuestionRecord
            {
                QuizId = session.Id,
                OrderIndex = i,
                Kind = kind,
                CognitiveLevel = cog,
                Difficulty = Math.Clamp(qDto.Difficulty, 1, 5),
                TopicTag = string.IsNullOrWhiteSpace(qDto.TopicTag) ? "general" : qDto.TopicTag.Trim().ToLowerInvariant().TrimStart('@', '#'),
                Prompt = qDto.Prompt.Trim(),
                Options = options,
                CorrectAnswer = correctAnswer,
                AcceptedAliases = qDto.AcceptedAliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? [],
                Explanation = (qDto.Explanation ?? string.Empty).Trim(),
                CodeSnippet = codeSnippet,
                CodeLanguage = codeLanguage,
                MathLatex = mathLatex,
                Diagram = diagram,
                SourcePdfPath = request.SourcePdfPath,
                SourcePage = qDto.Evidence?.PageNumber > 0 ? qDto.Evidence.PageNumber : (pages.FirstOrDefault()?.PageNumber ?? 1),
                SectionTitle = qDto.Evidence?.SectionTitle ?? string.Empty,
                VerbatimQuote = qDto.Evidence?.VerbatimQuote ?? string.Empty,
                SrsDueDate = today
            };

            session.Questions.Add(qRecord);
        }

        if (session.Questions.Count == 0)
            throw new InvalidOperationException("No valid questions were produced from the response.");

        // Deterministically ground every question's verbatim quote into normalized [0..1] PDF highlight boxes off the UI thread
        await Task.Run(() => PdfQuizGrounder.GroundQuestionsToPdf(request.SourcePdfPath, session.Questions), ct);

        if (!string.IsNullOrWhiteSpace(request.BasinRoot))
        {
            if (!string.IsNullOrWhiteSpace(request.AppendToQuizId))
            {
                var existingList = await ScholarFlowDatabase.LoadQuizSessionsAsync(request.BasinRoot, ct: ct);
                var existing = existingList.FirstOrDefault(x => string.Equals(x.Id, request.AppendToQuizId, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    foreach (var nq in session.Questions)
                    {
                        nq.QuizId = existing.Id;
                        nq.OrderIndex = existing.Questions.Count;
                        existing.Questions.Add(nq);
                    }
                    existing.SourcePdfMtimeTicks ??= session.SourcePdfMtimeTicks;
                    await ScholarFlowDatabase.SaveQuizSessionAsync(request.BasinRoot, existing, ct);
                    return existing;
                }
            }

            await ScholarFlowDatabase.SaveQuizSessionAsync(request.BasinRoot, session, ct);
        }

        return session;
    }

}
