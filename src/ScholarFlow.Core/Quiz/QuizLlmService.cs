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
public sealed class QuizLlmService
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
        int count = Math.Clamp(request.QuestionCount, 1, 150);
        return BuildSystemInstructions(request, weakTopics) + "\n\n" + BuildUserPrompt(request, groundedContent, count);
    }

    public async Task<QuizSessionRecord> GenerateQuizAsync(
        QuizGenerationRequest request,
        CancellationToken ct = default)
    {
        var pages = PdfQuizGrounder.ExtractGroundedPages(
            request.SourcePdfPath,
            request.Term,
            request.SectionFilter,
            request.StartPage,
            request.EndPage,
            request.SelectedRanges,
            maxPages: 150);

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
            dto = GenerateOfflineGroundedDto(request, pages, desiredCount);
            modelLabel = "ScholarFlow Smart PDF Analyzer (Offline)";
        }
        else
        {
            var weakTopics = ScholarFlowDatabase.GetWeakTopicTags(request.BasinRoot, request.CourseFolder);
            var sysPrompt = BuildSystemInstructions(request, weakTopics);

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
                var usrPrompt = BuildUserPrompt(request, groundedContent, thisBatchCount, b + 1, batchCount);

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

        var pages = PdfQuizGrounder.ExtractGroundedPages(
            request.SourcePdfPath,
            request.Term,
            request.SectionFilter,
            request.StartPage,
            request.EndPage,
            maxPages: 45);

        var dto = ParseQuizResponseJson(rawJson);
        return await BuildGroundAndPersistSessionAsync(request, dto, "Imported JSON (Gemini Pro)", pages, ct);
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
                CorrectAnswer = (qDto.CorrectAnswer ?? string.Empty).Trim(),
                AcceptedAliases = qDto.AcceptedAliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? [],
                Explanation = (qDto.Explanation ?? string.Empty).Trim(),
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

        // Deterministically ground every question's verbatim quote into normalized [0..1] PDF highlight boxes
        PdfQuizGrounder.GroundQuestionsToPdf(request.SourcePdfPath, session.Questions);

        if (!string.IsNullOrWhiteSpace(request.BasinRoot))
        {
            await ScholarFlowDatabase.SaveQuizSessionAsync(request.BasinRoot, session, ct);
        }

        return session;
    }

    private static async Task<string> CallOpenAiCompatibleChatAsync(
        string endpointUrl,
        string? apiKey,
        string modelName,
        string systemPrompt,
        string userPrompt,
        CancellationToken ct)
    {
        var normalizedUrl = NormalizeChatCompletionsEndpoint(endpointUrl);

        // Try with response_format: json_object first, fallback without response_format if provider rejects it
        foreach (bool includeJsonResponseFormat in new[] { true, false })
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, normalizedUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
                reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            object payload = includeJsonResponseFormat
                ? new
                {
                    model = modelName,
                    temperature = 0.25,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                }
                : new
                {
                    model = modelName,
                    temperature = 0.25,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                };

            var jsonBody = JsonSerializer.Serialize(payload);
            reqMsg.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(reqMsg, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                if (includeJsonResponseFormat && (int)resp.StatusCode == 400)
                    continue;

                throw new InvalidOperationException($"AI endpoint returned HTTP {(int)resp.StatusCode}: {respText}");
            }

            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var contentEl))
            {
                return contentEl.GetString() ?? string.Empty;
            }

            return respText;
        }

        throw new InvalidOperationException("Failed to receive a valid chat completion response.");
    }

    private static string NormalizeChatCompletionsEndpoint(string rawUrl)
    {
        var u = (rawUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(u))
            return "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";

        if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return u;

        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
            u.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            return $"{u}/chat/completions";
        }

        return $"{u}/v1/chat/completions";
    }

    public static QuizGenerationResponseDto ParseQuizResponseJson(string rawText)
    {
        var cleaned = ExtractJsonObjectSubstring(rawText);
        var dto = JsonSerializer.Deserialize<QuizGenerationResponseDto>(cleaned, JsonOpts);
        if (dto is null || dto.Questions.Count == 0)
            throw new InvalidOperationException("Could not parse any questions from the JSON payload.");
        return dto;
    }

    private static string ExtractJsonObjectSubstring(string raw)
    {
        var text = (raw ?? string.Empty).Trim();

        // Strip markdown ```json ... ``` fences if present
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline > 0)
                text = text[(firstNewline + 1)..];
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                text = text[..lastFence];
            text = text.Trim();
        }

        int startBrace = text.IndexOf('{');
        int endBrace = text.LastIndexOf('}');
        if (startBrace >= 0 && endBrace > startBrace)
            return text[startBrace..(endBrace + 1)];

        return text;
    }

    private static string BuildSystemInstructions(QuizGenerationRequest req, IReadOnlyList<string> weakTopics)
    {
        var mixRule = req.Preset switch
        {
            QuizPresetMode.QuickReview =>
                "Pedagogical Mix: 50% MultipleChoice, 30% Identification, 20% TrueFalse. Focus on core definitions, classifications, and key distinctions.",
            QuizPresetMode.WeakTopicDrill when weakTopics.Count > 0 =>
                $"Pedagogical Mix: Focus heavily on previously missed/weak topics ({string.Join(", ", weakTopics)}). Use 45% Situational/Application MultipleChoice, 35% Identification, 20% FillInTheBlank.",
            _ =>
                "Pedagogical Mix (STI College Exam Simulator): 40% Situational/Application MultipleChoice, 30% Identification, 15% TrueFalse, 15% FillInTheBlank. Test deep understanding, troubleshooting, and real-world application rather than shallow trivia."
        };

        return $$"""
            You are an expert university computer science examiner creating an evidence-grounded academic assessment from a course Master PDF.
            {{mixRule}}

            CRITICAL RULES:
            1. Return ONLY a valid JSON object matching the exact schema below. Do not include commentary outside the JSON.
            2. Every single question MUST include an "evidence" object with:
               - "pageNumber": the exact integer page number from the "=== [PAGE N] | Section: ... ===" header where the fact appears.
               - "sectionTitle": the exact section title from that page header.
               - "verbatimQuote": an exact 8 to 28 word substring copied WORD-FOR-WORD from that page's text so our PDF highlighter can locate its pixel bounding box.
            3. For MultipleChoice and Situational questions:
               - Provide exactly 4 options in "options" (plain text without "A)" prefixes).
               - "correctAnswer" MUST exactly match one of the 4 strings in "options".
               - Distractors must be plausible sibling concepts from the same handout.
            4. For Identification and FillInTheBlank:
               - "options" must be an empty array [].
               - "correctAnswer" must be the concise canonical term (1-4 words).
               - "acceptedAliases" should list valid synonyms, acronyms, or alternate spellings.
            5. Ignore syllabus boilerplate, headers/footers ("STI College", copyright lines, table of contents).

            REQUIRED JSON SCHEMA:
            {
              "quizTitle": "Course — Term Topic Assessment",
              "courseTerm": "Prelims | Midterms | Prefinals | Finals",
              "keyConceptsCovered": ["concept 1", "concept 2"],
              "questions": [
                {
                  "kind": "MultipleChoice | Situational | Identification | TrueFalse | FillInTheBlank",
                  "cognitiveLevel": "Recall | Comprehension | Application | Analysis",
                  "difficulty": 3,
                  "topicTag": "short-kebab-tag",
                  "prompt": "Clear question stem or scenario...",
                  "options": ["Option 1", "Option 2", "Option 3", "Option 4"],
                  "correctAnswer": "Option 1",
                  "acceptedAliases": [],
                  "explanation": "Concise explanation of why the answer is correct and why distractors are wrong.",
                  "evidence": {
                    "pageNumber": 12,
                    "sectionTitle": "Midterms / 04 Handout 1",
                    "verbatimQuote": "Exact 8-28 word sentence copied directly from [PAGE 12]"
                  }
                }
              ]
            }
            """;
    }

    private static string BuildUserPrompt(
        QuizGenerationRequest req,
        string groundedContent,
        int count,
        int batchNumber = 1,
        int totalBatches = 1)
    {
        var batchNote = totalBatches > 1 ? $" (Batch {batchNumber} of {totalBatches} — ensure unique questions covering these pages)" : string.Empty;
        return $"""
            Course: {req.CourseTitle}
            Target Term: {req.Term ?? "All Covered Terms"}
            Section Scope: {req.SectionFilter ?? "All Sections in Scope"}
            Generate exactly {Math.Clamp(count, 1, 150)} high-quality, evidence-grounded questions{batchNote} from the following grounded PDF pages:

            {groundedContent}
            """;
    }

    /// <summary>
    /// Smart deterministic offline PDF analyzer that extracts definition & claim statements directly from GroundedPdfPages,
    /// producing evidence-grounded Identification, MultipleChoice, FillInTheBlank, and TrueFalse questions with exact PDF highlight quotes.
    /// </summary>
    public static QuizGenerationResponseDto GenerateOfflineGroundedDto(
        QuizGenerationRequest req,
        IReadOnlyList<GroundedPdfPage> pages,
        int? desiredCountOverride = null)
    {
        var candidates = new List<(GroundedPdfPage Page, string TermName, string Predicate, string FullSentence)>();
        var defRegex = new Regex(
            @"(?<term>[A-Z][A-Za-z0-9\-\s/()]{2,38}?)\s+(?:is\s+defined\s+as|refers\s+to|is\s+a\s+type\s+of|is\s+the\s+process\s+of|is\s+an?\s+[a-z]+\s+that|provides\s+a|ensures\s+that|is\s+used\s+to)\s+(?<pred>[^.?!:\n]{18,160}[.])",
            RegexOptions.Compiled);

        foreach (var page in pages)
        {
            var normalizedText = Regex.Replace(page.Text, @"\s+", " ");
            foreach (Match m in defRegex.Matches(normalizedText))
            {
                var term = m.Groups["term"].Value.Trim();
                var pred = m.Groups["pred"].Value.Trim();
                var full = m.Value.Trim();

                if (term.Split(' ').Length > 5 ||
                    term.Contains("STI", StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith("This ", StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith("It ", StringComparison.OrdinalIgnoreCase) ||
                    term.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && term.Split(' ').Length < 2)
                {
                    continue;
                }

                candidates.Add((page, term, pred, full));
            }

            // Also extract clean factual sentences if definition matches are sparse
            var sentences = Regex.Split(normalizedText, @"(?<=[.?!])\s+")
                .Where(s => s.Length is >= 45 and <= 170 && char.IsUpper(s[0]) && !s.Contains("Property of STI", StringComparison.OrdinalIgnoreCase))
                .Take(6);

            foreach (var s in sentences)
            {
                var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 8)
                {
                    var keyPhrase = string.Join(' ', words.Take(Math.Min(3, words.Length))).Trim(',', ':', ';');
                    var rest = string.Join(' ', words.Skip(Math.Min(3, words.Length)));
                    if (keyPhrase.Length >= 4 && rest.Length >= 20)
                        candidates.Add((page, keyPhrase, rest, s));
                }
            }
        }

        var distinctCandidates = candidates
            .GroupBy(c => c.TermName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        int desired = Math.Clamp(desiredCountOverride ?? req.QuestionCount, 1, 150);
        int targetCount = Math.Min(desired, Math.Max(1, distinctCandidates.Count));
        var allTermNames = distinctCandidates.Select(c => c.TermName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var questions = new List<GeneratedQuestionDto>();
        for (int i = 0; i < Math.Min(targetCount, distinctCandidates.Count); i++)
        {
            var c = distinctCandidates[i];
            var topicTag = Regex.Replace(c.TermName.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (string.IsNullOrEmpty(topicTag))
                topicTag = "concept";

            int mode = i % 3;
            if (mode == 0 && allTermNames.Count >= 4)
            {
                // Multiple Choice
                var distractors = allTermNames
                    .Where(t => !t.Equals(c.TermName, StringComparison.OrdinalIgnoreCase))
                    .Skip(i % Math.Max(1, allTermNames.Count - 3))
                    .Take(3)
                    .ToList();
                while (distractors.Count < 3)
                    distractors.Add($"Alternative Concept {distractors.Count + 1}");

                var opts = new List<string>(distractors) { c.TermName };
                // Deterministic shuffle by index
                opts = opts.OrderBy(o => (o.GetHashCode() ^ i) & 0x7FFFFFFF).ToList();

                questions.Add(new GeneratedQuestionDto
                {
                    Kind = nameof(QuizQuestionKind.MultipleChoice),
                    CognitiveLevel = nameof(CognitiveLevel.Comprehension),
                    Difficulty = 3,
                    TopicTag = topicTag,
                    Prompt = $"According to {c.Page.SectionBreadcrumb} (Page {c.Page.PageNumber}), which concept best matches the following statement?\n\n\"...{c.Predicate}\"",
                    Options = opts,
                    CorrectAnswer = c.TermName,
                    Explanation = $"Grounded on Page {c.Page.PageNumber} ({c.Page.SectionBreadcrumb}): \"{c.FullSentence}\"",
                    Evidence = new EvidenceAnchorDto
                    {
                        PageNumber = c.Page.PageNumber,
                        SectionTitle = c.Page.SectionBreadcrumb,
                        VerbatimQuote = c.FullSentence
                    }
                });
            }
            else if (mode == 1)
            {
                // Fill-in-the-blank / Identification
                var blanked = c.FullSentence.Replace(c.TermName, "________", StringComparison.OrdinalIgnoreCase);
                if (blanked == c.FullSentence)
                    blanked = $"________ {c.Predicate}";

                questions.Add(new GeneratedQuestionDto
                {
                    Kind = nameof(QuizQuestionKind.Identification),
                    CognitiveLevel = nameof(CognitiveLevel.Recall),
                    Difficulty = 3,
                    TopicTag = topicTag,
                    Prompt = $"Identify the missing term or concept from {c.Page.SectionBreadcrumb}:\n\n\"{blanked}\"",
                    Options = [],
                    CorrectAnswer = c.TermName,
                    Explanation = $"Full statement on Page {c.Page.PageNumber}: \"{c.FullSentence}\"",
                    Evidence = new EvidenceAnchorDto
                    {
                        PageNumber = c.Page.PageNumber,
                        SectionTitle = c.Page.SectionBreadcrumb,
                        VerbatimQuote = c.FullSentence
                    }
                });
            }
            else
            {
                // True / False
                questions.Add(new GeneratedQuestionDto
                {
                    Kind = nameof(QuizQuestionKind.TrueFalse),
                    CognitiveLevel = nameof(CognitiveLevel.Application),
                    Difficulty = 2,
                    TopicTag = topicTag,
                    Prompt = $"True or False: In the context of {c.Page.SectionBreadcrumb}, {c.FullSentence}",
                    Options = ["True", "False"],
                    CorrectAnswer = "True",
                    Explanation = $"Verified directly on Page {c.Page.PageNumber} ({c.Page.SectionBreadcrumb}): \"{c.FullSentence}\"",
                    Evidence = new EvidenceAnchorDto
                    {
                        PageNumber = c.Page.PageNumber,
                        SectionTitle = c.Page.SectionBreadcrumb,
                        VerbatimQuote = c.FullSentence
                    }
                });
            }
        }

        var termLabel = req.Term ?? pages.FirstOrDefault(p => p.Term is not null)?.Term ?? "All Terms";
        return new QuizGenerationResponseDto
        {
            QuizTitle = $"{req.CourseTitle} — {termLabel} Grounded Assessment",
            CourseTerm = termLabel,
            KeyConceptsCovered = allTermNames.Take(10).ToList(),
            Questions = questions
        };
    }
}
