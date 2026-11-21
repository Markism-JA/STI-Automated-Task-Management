using ScholarFlow.Core.Services;

namespace ScholarFlow.Elms.Tests;

public sealed class FilenameSanitizerTests
{
    [Fact]
    public void SanitizeFolderName_NormalizesSpacesAndSpecialChars()
    {
        var result = FilenameSanitizer.SanitizeFolderName("Platform Technology (Operating System)!");
        Assert.Equal("Platform_Technology_(Operating_System)", result);
    }

    [Fact]
    public void SanitizeFileName_DoesNotCorruptNamesEndingInPdf()
    {
        var result = FilenameSanitizer.SanitizeFileName("01 Handout pdf", ".pdf");
        Assert.Equal("01_Handout_pdf.pdf", result);
    }

    [Fact]
    public void SanitizeFileName_PreservesExistingExtension()
    {
        var result = FilenameSanitizer.SanitizeFileName("Lab 1 Instructions.docx", ".pdf");
        Assert.Equal("Lab_1_Instructions.docx", result);
    }

    [Fact]
    public void SanitizeFileName_HandlesNumericDotPrefixWithoutTreatingAsExtension()
    {
        var result = FilenameSanitizer.SanitizeFileName("01. Introduction to SQA", ".pdf");
        Assert.Equal("01_Introduction_to_SQA.pdf", result);
    }
}
