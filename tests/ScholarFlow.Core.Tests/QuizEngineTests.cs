using ScholarFlow.Core.Database;
using ScholarFlow.Core.Quiz;
using UglyToad.PdfPig;

namespace ScholarFlow.Core.Tests;

public sealed class QuizEngineTests
{
    [Fact]
    public async Task PdfQuizGrounder_ExtractsGroundedPagesAndComputesNormalizedHighlightBoundingBoxes()
    {
        const string sqaMasterPdf = "/home/marky/Documents/School-Files/YR4_01/Software_Quality_Assurance/Software_Quality_Assurance_Master.pdf";
        if (!File.Exists(sqaMasterPdf))
            return;

        var pages = PdfQuizGrounder.ExtractGroundedPages(sqaMasterPdf, termFilter: "Prelims", maxPages: 10);
        Assert.NotEmpty(pages);

        var samplePage = pages[0];
        var words = samplePage.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(words.Length >= 20);

        var sampleQuote = string.Join(' ', words.Skip(Math.Min(30, words.Length / 3)).Take(12));

        using var doc = PdfDocument.Open(sqaMasterPdf);
        // Pass an intentionally off-by-1 hintPage to verify automatic page snapping
        var rects = PdfQuizGrounder.ComputeHighlightRects(
            doc,
            hintPage: Math.Max(1, samplePage.PageNumber - 1),
            verbatimQuote: sampleQuote,
            out int resolvedPage);

        Assert.Equal(samplePage.PageNumber, resolvedPage);
        Assert.NotEmpty(rects);
        Assert.All(rects, r =>
        {
            Assert.InRange(r.X, 0.0, 1.0);
            Assert.InRange(r.Y, 0.0, 1.0);
            Assert.InRange(r.Width, 0.01, 1.0);
            Assert.InRange(r.Height, 0.005, 1.0);
        });

        // Also verify offline smart quiz generation & JSON import round-trip into SQLite
        var tempBasin = Path.Combine(Path.GetTempPath(), $"sf-quiz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBasin);
        try
        {
            var llm = new QuizLlmService();
            var req = new QuizGenerationRequest
            {
                BasinRoot = tempBasin,
                CourseFolder = "Software_Quality_Assurance",
                CourseTitle = "Software Quality Assurance",
                Term = "Prelims",
                SourcePdfPath = sqaMasterPdf,
                QuestionCount = 6,
                UseOfflineHeuristicFallback = true
            };

            var session = await llm.GenerateQuizAsync(req);
            Assert.NotEmpty(session.Questions);
            Assert.Contains(session.Questions, q => q.HighlightRects.Count > 0);

            var loadedSessions = await ScholarFlowDatabase.LoadQuizSessionsAsync(tempBasin);
            Assert.Single(loadedSessions);
            Assert.Equal(session.Questions.Count, loadedSessions[0].Questions.Count);

            // Verify SM-2 / FSRS attempt recording
            var firstQ = loadedSessions[0].Questions[0];
            await ScholarFlowDatabase.RecordQuestionAttemptAsync(tempBasin, firstQ, firstQ.CorrectAnswer, isCorrect: true, qualityRating: 5);
            Assert.True(firstQ.SrsIntervalDays >= 1.0);
            Assert.Equal(1, firstQ.SrsReps);
            Assert.True(firstQ.LastAnsweredCorrect);

            // Verify Manual Gemini JSON Import with markdown code fences
            var fencedJson = $$"""
                ```json
                {
                  "quizTitle": "SQA Prelims Imported Test",
                  "courseTerm": "Prelims",
                  "keyConceptsCovered": ["Software Quality"],
                  "questions": [
                    {
                      "kind": "MultipleChoice",
                      "cognitiveLevel": "Application",
                      "difficulty": 4,
                      "topicTag": "sqa-fundamentals",
                      "prompt": "Which statement is grounded on Page {{samplePage.PageNumber}}?",
                      "options": ["Option A", "Option B", "Option C", "Option D"],
                      "correctAnswer": "Option A",
                      "acceptedAliases": [],
                      "explanation": "Verified directly from the Master PDF.",
                      "evidence": {
                        "pageNumber": {{samplePage.PageNumber}},
                        "sectionTitle": "{{samplePage.SectionBreadcrumb}}",
                        "verbatimQuote": "{{sampleQuote.Replace("\"", "\\\"")}}"
                      }
                    }
                  ]
                }
                ```
                """;

            var imported = await llm.ImportQuizFromJsonAsync(req, fencedJson);
            Assert.Single(imported.Questions);
            Assert.NotEmpty(imported.Questions[0].HighlightRects);
            Assert.True(imported.Questions[0].EvaluateAnswer("Option A"));

            // Verify Outline Section inspection, Scope Preview, and explicit page range extraction
            var sections = PdfQuizGrounder.InspectOutlineSections(sqaMasterPdf, out int totalPages);
            Assert.True(totalPages > 10);
            Assert.NotEmpty(sections);

            var preview = PdfQuizGrounder.BuildScopePreview(
                sqaMasterPdf,
                startPage: sections[0].StartPage,
                endPage: Math.Min(totalPages, sections[0].StartPage + 6));
            Assert.True(preview.PageCount > 0);
            Assert.True(preview.CharCount > 0);
        }
        finally
        {
            if (Directory.Exists(tempBasin))
                Directory.Delete(tempBasin, recursive: true);
        }
    }
}
