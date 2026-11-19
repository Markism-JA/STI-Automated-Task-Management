using ScholarFlow.Core.Database;
using ScholarFlow.Core.Models;
using ScholarFlow.Core.Quiz;
using ScholarFlow.Core.Tasks;

namespace ScholarFlow.Core.Tests;

public class QuizStudioV2Tests
{
    [Fact]
    public void ResolveExactKindCounts_PercentagesMode_AllocatesExactCountsSummingToTarget()
    {
        var req = new QuizGenerationRequest
        {
            QuestionCount = 20,
            AllocationMode = QuestionAllocationMode.Percentages,
            KindAllocations = new Dictionary<QuizQuestionKind, int>
            {
                [QuizQuestionKind.MultipleChoice] = 25,
                [QuizQuestionKind.Situational] = 0,
                [QuizQuestionKind.Identification] = 25,
                [QuizQuestionKind.TrueFalse] = 0,
                [QuizQuestionKind.FillInTheBlank] = 35,
                [QuizQuestionKind.Ordering] = 15
            }
        };

        var counts = req.ResolveExactKindCounts();
        Assert.Equal(20, counts.Values.Sum());
        Assert.Equal(5, counts[QuizQuestionKind.MultipleChoice]);
        Assert.Equal(0, counts[QuizQuestionKind.Situational]);
        Assert.Equal(5, counts[QuizQuestionKind.Identification]);
        Assert.Equal(0, counts[QuizQuestionKind.TrueFalse]);
        Assert.Equal(7, counts[QuizQuestionKind.FillInTheBlank]);
        Assert.Equal(3, counts[QuizQuestionKind.Ordering]);
    }

    [Fact]
    public void ResolveExactKindCounts_ExactCountsMode_PreservesUserCountsAndExcludesZeroKinds()
    {
        var req = new QuizGenerationRequest
        {
            AllocationMode = QuestionAllocationMode.ExactCounts,
            KindAllocations = new Dictionary<QuizQuestionKind, int>
            {
                [QuizQuestionKind.MultipleChoice] = 0,
                [QuizQuestionKind.Situational] = 2,
                [QuizQuestionKind.Identification] = 3,
                [QuizQuestionKind.TrueFalse] = 0,
                [QuizQuestionKind.FillInTheBlank] = 4,
                [QuizQuestionKind.Ordering] = 2
            }
        };

        var counts = req.ResolveExactKindCounts();
        Assert.Equal(11, counts.Values.Sum());
        Assert.Equal(0, counts[QuizQuestionKind.MultipleChoice]);
        Assert.Equal(2, counts[QuizQuestionKind.Situational]);
        Assert.Equal(3, counts[QuizQuestionKind.Identification]);
        Assert.Equal(4, counts[QuizQuestionKind.FillInTheBlank]);
        Assert.Equal(2, counts[QuizQuestionKind.Ordering]);
    }

    [Fact]
    public void EvaluateAnswer_OrderingQuestion_ChecksStepSequenceAccurately()
    {
        var q = new QuizQuestionRecord
        {
            Kind = QuizQuestionKind.Ordering,
            Prompt = "Arrange the software testing levels in order:",
            Options = ["Unit Testing", "Integration Testing", "System Testing", "Acceptance Testing"],
            CorrectAnswer = "Unit Testing -> Integration Testing -> System Testing -> Acceptance Testing"
        };

        Assert.True(q.EvaluateAnswer("Unit Testing -> Integration Testing -> System Testing -> Acceptance Testing"));
        Assert.True(q.EvaluateAnswer("unit testing | integration testing | system testing | acceptance testing"));
        Assert.False(q.EvaluateAnswer("Integration Testing -> Unit Testing -> System Testing -> Acceptance Testing"));
    }

    [Fact]
    public void GenerateOfflineGroundedDto_RespectsCustomFillInTheBlankAndOrderingAllocations()
    {
        var pages = new List<GroundedPdfPage>
        {
            new(1, "Prelims", "01 Handout 1",
                "Encapsulation is defined as the bundling of data and methods operating on that data within a single unit. " +
                "Polymorphism refers to the ability of different objects to respond to the same method call in their own way. " +
                "Inheritance is the process of deriving a new class from an existing base class to reuse code and behavior. " +
                "Abstraction provides a simplified interface by hiding internal implementation details from the caller.")
        };

        var req = new QuizGenerationRequest
        {
            CourseTitle = "Object-Oriented Programming",
            Term = "Prelims",
            QuestionCount = 4,
            AllocationMode = QuestionAllocationMode.ExactCounts,
            KindAllocations = new Dictionary<QuizQuestionKind, int>
            {
                [QuizQuestionKind.MultipleChoice] = 0,
                [QuizQuestionKind.Situational] = 0,
                [QuizQuestionKind.Identification] = 0,
                [QuizQuestionKind.TrueFalse] = 0,
                [QuizQuestionKind.FillInTheBlank] = 2,
                [QuizQuestionKind.Ordering] = 2
            }
        };

        var dto = QuizLlmService.GenerateOfflineGroundedDto(req, pages);
        Assert.Equal(4, dto.Questions.Count);
        Assert.Equal(2, dto.Questions.Count(q => q.Kind == nameof(QuizQuestionKind.FillInTheBlank)));
        Assert.Equal(2, dto.Questions.Count(q => q.Kind == nameof(QuizQuestionKind.Ordering)));
        Assert.All(dto.Questions.Where(q => q.Kind == nameof(QuizQuestionKind.FillInTheBlank)),
            q => Assert.Contains("_____", q.Prompt));
    }

    [Fact]
    public async Task Database_PersistsDeckReorganizationAndSourcePdfMtimeTicks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-quiz-v2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var session = new QuizSessionRecord
            {
                CourseFolder = "Software_Quality_Assurance",
                CourseTitle = "Software Quality Assurance",
                Term = "Prelims",
                Title = "Original Deck",
                SourcePdfPath = "/tmp/fake.pdf",
                SourcePdfMtimeTicks = 123456789L,
                ModelUsed = "UnitTest",
                Questions =
                [
                    new QuizQuestionRecord
                    {
                        Id = "q-1",
                        Kind = QuizQuestionKind.FillInTheBlank,
                        Prompt = "_____ is the process of evaluating a system.",
                        CorrectAnswer = "Testing"
                    },
                    new QuizQuestionRecord
                    {
                        Id = "q-2",
                        Kind = QuizQuestionKind.Ordering,
                        Prompt = "Order the phases:",
                        Options = ["Plan", "Build", "Verify"],
                        CorrectAnswer = "Plan -> Build -> Verify"
                    }
                ]
            };

            await ScholarFlowDatabase.SaveQuizSessionAsync(tempRoot, session);

            // Reorder, rename, change term, and update SourcePdfMtimeTicks
            session.Title = "Reorganized Prelims Deck";
            session.Term = "Midterms";
            session.SourcePdfMtimeTicks = 987654321L;
            session.Questions.Reverse();

            await ScholarFlowDatabase.SaveQuizSessionAsync(tempRoot, session);

            var loaded = await ScholarFlowDatabase.LoadQuizSessionsAsync(tempRoot);
            Assert.Single(loaded);
            Assert.Equal("Reorganized Prelims Deck", loaded[0].Title);
            Assert.Equal("Midterms", loaded[0].Term);
            Assert.Equal(987654321L, loaded[0].SourcePdfMtimeTicks);
            Assert.Equal("q-2", loaded[0].Questions[0].Id);
            Assert.Equal(QuizQuestionKind.Ordering, loaded[0].Questions[0].Kind);
            Assert.Equal("q-1", loaded[0].Questions[1].Id);
            Assert.Equal(QuizQuestionKind.FillInTheBlank, loaded[0].Questions[1].Kind);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Database_PersistsAndClearsQuizInProgressState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-quiz-prog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var session = new QuizSessionRecord
            {
                CourseFolder = "Network_Technology",
                CourseTitle = "Network Technology",
                Title = "Midterms Quiz",
                SourcePdfPath = "/tmp/net.pdf",
                ModelUsed = "UnitTest",
                Questions =
                [
                    new QuizQuestionRecord { Id = "q1", Prompt = "Q1", CorrectAnswer = "A" },
                    new QuizQuestionRecord { Id = "q2", Prompt = "Q2", CorrectAnswer = "B" }
                ]
            };

            await ScholarFlowDatabase.SaveQuizSessionAsync(tempRoot, session);
            await ScholarFlowDatabase.SaveQuizInProgressAsync(
                tempRoot,
                session.Id,
                1,
                new Dictionary<string, QuizInProgressAnswer>
                {
                    ["q1"] = new("A", true)
                });

            var loaded = await ScholarFlowDatabase.LoadQuizSessionsAsync(tempRoot);
            Assert.Single(loaded);
            Assert.True(loaded[0].HasInProgressAttempt);
            Assert.Equal(1, loaded[0].InProgressQuestionIndex);
            Assert.True(loaded[0].InProgressAnswers["q1"].IsCorrect);

            await ScholarFlowDatabase.ClearQuizInProgressAsync(tempRoot, session.Id);
            var cleared = await ScholarFlowDatabase.LoadQuizSessionsAsync(tempRoot);
            Assert.False(cleared[0].HasInProgressAttempt);
            Assert.Null(cleared[0].InProgressQuestionIndex);
            Assert.Empty(cleared[0].InProgressAnswers);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TaskQueryService_IncludesSubtasksInUpcomingAndFiltersCompletedByCourse()
    {
        var today = new DateOnly(2026, 11, 19);
        var sub = new TaskItem
        {
            Id = "sub-1",
            ParentTaskId = "parent-1",
            Title = "Finish UML Diagram",
            CourseFolder = "Software_Quality_Assurance",
            DueDate = today.AddDays(2),
            IsCompleted = false
        };
        var parent = new TaskItem
        {
            Id = "parent-1",
            Title = "Capstone Deliverable",
            CourseFolder = "Software_Quality_Assurance",
            IsCompleted = false,
            ChildTasks = [sub]
        };
        var doneSqa = new TaskItem
        {
            Id = "done-1",
            Title = "SQA Quiz 1",
            CourseFolder = "Software_Quality_Assurance",
            IsCompleted = true
        };
        var doneIas = new TaskItem
        {
            Id = "done-2",
            Title = "IAS Quiz 1",
            CourseFolder = "Information_Assurance_And_Security",
            IsCompleted = true
        };

        var flattened = new[] { parent, sub, doneSqa, doneIas };
        var upcoming = TaskQueryService.Filter(flattened, SmartViewKind.Upcoming, todayOverride: today);
        Assert.Contains(upcoming, t => t.Id == "sub-1");

        var completedSqa = TaskQueryService.Filter(
            [parent, doneSqa, doneIas],
            SmartViewKind.Completed,
            targetValue: "Software_Quality_Assurance",
            todayOverride: today);
        Assert.Single(completedSqa);
        Assert.Equal("done-1", completedSqa[0].Id);
    }

    [Fact]
    public void BuildGuardedCustomInstructionsBlock_SanitizesAndPreservesGuardrails()
    {
        var raw = "Focus on \"\"\"edge cases\"\"\" and troubleshooting.";
        var block = QuizLlmService.BuildGuardedCustomInstructionsBlock(raw);

        Assert.Contains("<user_directives>", block);
        Assert.Contains("NEVER override the JSON-only output", block);
        Assert.DoesNotContain("\"\"\"", block);
        Assert.Contains("Focus on 'edge cases' and troubleshooting.", block);
        Assert.Equal(string.Empty, QuizLlmService.BuildGuardedCustomInstructionsBlock("   "));
    }

    [Fact]
    public void DetectContentModalities_IdentifiesProseCodeMathAndDiagramsAccurately()
    {
        var prosePages = new[]
        {
            new GroundedPdfPage(1, "Prelims", "Lesson 1", "Technopreneurship combines technology and entrepreneurship to solve customer pain points.")
        };
        var proseResult = QuizLlmService.DetectContentModalities(prosePages, "Technopreneurship");
        Assert.False(proseResult.HasCode);
        Assert.False(proseResult.HasMath);
        Assert.False(proseResult.HasDiagrams);

        var codePages = new[]
        {
            new GroundedPdfPage(1, "Midterms", "OOP", "public class OrderProcessor { private void Run() { Console.Write(\"ok\"); } }")
        };
        var codeResult = QuizLlmService.DetectContentModalities(codePages, "Object Oriented Programming");
        Assert.True(codeResult.HasCode);
        Assert.Equal("csharp", codeResult.DetectedLanguage);
        Assert.False(codeResult.HasMath);

        var mathPages = new[]
        {
            new GroundedPdfPage(2, "Finals", "Complexity", @"Using \frac{n(n+1)}{2} and \sum_{i=1}^{n} i, we bound f(x) = O(n log n) where x \leq y and a \geq b.")
        };
        var mathResult = QuizLlmService.DetectContentModalities(mathPages, "Discrete Math");
        Assert.True(mathResult.HasMath);

        var diagramPages = new[]
        {
            new GroundedPdfPage(3, "Prelims", "Architecture", "Figure 1 shows the Flowchart of Client -> API Gateway -> Auth Service -> Database.")
        };
        var diagramResult = QuizLlmService.DetectContentModalities(diagramPages, "Systems Integration");
        Assert.True(diagramResult.HasDiagrams);

        var forcedProse = QuizLlmService.DetectContentModalities(codePages, "OOP", QuizContentModalityMode.StandardProseOnly);
        Assert.False(forcedProse.HasCode);
        Assert.False(forcedProse.HasMath);
        Assert.False(forcedProse.HasDiagrams);
    }

    [Fact]
    public async Task SaveAndLoadQuizSession_PreservesSpecializedModalityFields()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"scholarflow_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var session = new QuizSessionRecord
            {
                Id = "quiz-modality-1",
                CourseFolder = "Algorithms",
                CourseTitle = "Algorithms",
                Term = "Midterms",
                Title = "Modality Roundtrip",
                Questions =
                [
                    new QuizQuestionRecord
                    {
                        Id = "q-1",
                        QuizId = "quiz-modality-1",
                        OrderIndex = 0,
                        Kind = QuizQuestionKind.MultipleChoice,
                        Prompt = "Evaluate the snippet and recurrence.",
                        CodeSnippet = "public int Sum(int n) { return n * (n + 1) / 2; }",
                        CodeLanguage = "csharp",
                        MathLatex = @"\frac{n(n+1)}{2}",
                        Diagram = "Input -> Sum() -> Output",
                        Options = ["O(1)", "O(n)", "O(n^2)", "O(log n)"],
                        CorrectAnswer = "O(1)"
                    }
                ]
            };

            await ScholarFlowDatabase.SaveQuizSessionAsync(tempRoot, session);
            var loaded = await ScholarFlowDatabase.LoadQuizSessionsAsync(tempRoot, "Algorithms");

            var loadedSession = Assert.Single(loaded);
            var loadedQ = Assert.Single(loadedSession.Questions);
            Assert.Equal("public int Sum(int n) { return n * (n + 1) / 2; }", loadedQ.CodeSnippet);
            Assert.Equal("csharp", loadedQ.CodeLanguage);
            Assert.Equal(@"\frac{n(n+1)}{2}", loadedQ.MathLatex);
            Assert.Equal("Input -> Sum() -> Output", loadedQ.Diagram);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
