using ScholarFlow.Core.Database;
using ScholarFlow.Core.Quiz;

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
}
