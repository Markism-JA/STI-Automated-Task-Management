using ScholarFlow.Core.Pdf;

namespace ScholarFlow.Core.Tests;

public class SyllabusTermParserTests
{
    private const string ActiveBasin = "/home/marky/Documents/School-Files/YR4_01";

    [Fact]
    public void ParseTermSchedule_InformationAssurance_MatchesExpectedBuckets()
    {
        var courseDir = Path.Combine(ActiveBasin, "Information_Assurance_And_Security");
        if (!Directory.Exists(courseDir)) return;

        var syllabus = SyllabusTermParser.FindSyllabusPdfPath(courseDir);
        Assert.NotNull(syllabus);

        var schedule = SyllabusTermParser.ParseTermScheduleFromSyllabus(syllabus!, courseDir);
        Assert.Equal(["01", "02"], schedule.TermPrefixes["Prelims"]);
        Assert.Equal(["03", "04"], schedule.TermPrefixes["Midterms"]);
        Assert.Equal(["05"], schedule.TermPrefixes["Prefinals"]);
        Assert.Equal(["06"], schedule.TermPrefixes["Finals"]);
    }

    [Fact]
    public void ParseTermSchedule_PlatformTechnology_HandlesDirectoryTypoAndMatchesBuckets()
    {
        var courseDir = Path.Combine(ActiveBasin, "Platform_Technology_(Operating_System)");
        if (!Directory.Exists(courseDir)) return;

        var syllabus = SyllabusTermParser.FindSyllabusPdfPath(courseDir);
        Assert.NotNull(syllabus);

        var schedule = SyllabusTermParser.ParseTermScheduleFromSyllabus(syllabus!, courseDir);
        Assert.Equal(["01", "02"], schedule.TermPrefixes["Prelims"]);
        Assert.Equal(["03", "04"], schedule.TermPrefixes["Midterms"]);
        Assert.Equal(["05"], schedule.TermPrefixes["Prefinals"]);
        Assert.Equal(["06"], schedule.TermPrefixes["Finals"]);
    }

    [Fact]
    public void ParseTermSchedule_SoftwareQualityAssurance_MatchesExpectedBuckets()
    {
        var courseDir = Path.Combine(ActiveBasin, "Software_Quality_Assurance");
        if (!Directory.Exists(courseDir)) return;

        var syllabus = SyllabusTermParser.FindSyllabusPdfPath(courseDir);
        Assert.NotNull(syllabus);

        var schedule = SyllabusTermParser.ParseTermScheduleFromSyllabus(syllabus!, courseDir);
        Assert.Equal(["01", "02", "03"], schedule.TermPrefixes["Prelims"]);
        Assert.Equal(["04", "05"], schedule.TermPrefixes["Midterms"]);
        Assert.Equal(["06", "07"], schedule.TermPrefixes["Prefinals"]);
        Assert.Equal(["08", "09"], schedule.TermPrefixes["Finals"]);
    }

    [Fact]
    public void ParseTermSchedule_Technopreneurship_MapsUnprefixedModulesViaTopics()
    {
        var courseDir = Path.Combine(ActiveBasin, "Technopreneurship");
        if (!Directory.Exists(courseDir)) return;

        var syllabus = SyllabusTermParser.FindSyllabusPdfPath(courseDir);
        Assert.NotNull(syllabus);

        var schedule = SyllabusTermParser.ParseTermScheduleFromSyllabus(syllabus!, courseDir);
        var allMapped = SyllabusTermParser.TermOrder
            .SelectMany(t => schedule.TermPrefixes[t])
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("01", allMapped);
        Assert.Contains("09", allMapped);
        Assert.NotEmpty(schedule.TermPrefixes["Prelims"]);
        Assert.NotEmpty(schedule.TermPrefixes["Midterms"]);
        Assert.NotEmpty(schedule.TermPrefixes["Prefinals"]);
        Assert.Contains("08", schedule.TermPrefixes["Prefinals"]);
        Assert.Contains("09", schedule.TermPrefixes["Prefinals"]);
    }
}
