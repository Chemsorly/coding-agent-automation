using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class FindingsParserTests
{
    private const string AgentName = "TestAgent";

    [Fact]
    public void Parse_NullInput_ReturnsEmptyList()
    {
        var result = FindingsParser.Parse(null, AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyList()
    {
        var result = FindingsParser.Parse("", AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NullAgentName_ThrowsArgumentNullException()
    {
        var act = () => FindingsParser.Parse("test", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_NoSeverityMarkers_ReturnsEmptyList()
    {
        var input = "All looks good.\nNo issues found.";
        var result = FindingsParser.Parse(input, AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SingleFinding_ColonFormat()
    {
        var input = "[CRITICAL] src/Service.cs:42 — Null reference possible";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(FindingSeverity.Critical);
        result[0].FilePath.Should().Be("src/Service.cs");
        result[0].LineNumber.Should().Be(42);
        result[0].Message.Should().Be("Null reference possible");
        result[0].AgentName.Should().Be(AgentName);
    }

    [Fact]
    public void Parse_SingleFinding_HashFormat()
    {
        var input = "[WARNING] src/Controller.cs#L15 — Missing validation";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(FindingSeverity.Warning);
        result[0].FilePath.Should().Be("src/Controller.cs");
        result[0].LineNumber.Should().Be(15);
        result[0].Message.Should().Be("Missing validation");
    }

    [Fact]
    public void Parse_SingleFinding_ParenFormat()
    {
        var input = "[SUGGESTION] src/Utils.cs (line 7) — Consider renaming";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(FindingSeverity.Suggestion);
        result[0].FilePath.Should().Be("src/Utils.cs");
        result[0].LineNumber.Should().Be(7);
        result[0].Message.Should().Be("Consider renaming");
    }

    [Fact]
    public void Parse_SingleFinding_CommaFormat()
    {
        var input = "[WARNING] src/Data.cs, line 99 — Potential leak";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(FindingSeverity.Warning);
        result[0].FilePath.Should().Be("src/Data.cs");
        result[0].LineNumber.Should().Be(99);
        result[0].Message.Should().Be("Potential leak");
    }

    [Fact]
    public void Parse_CaseInsensitiveSeverity()
    {
        var input = "[critical] src/A.cs:1 — msg1\n[Warning] src/B.cs:2 — msg2\n[SUGGESTION] src/C.cs:3 — msg3";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(3);
        result[0].Severity.Should().Be(FindingSeverity.Critical);
        result[1].Severity.Should().Be(FindingSeverity.Warning);
        result[2].Severity.Should().Be(FindingSeverity.Suggestion);
    }

    [Fact]
    public void Parse_NoFileReference_ProducesNullPathAndZeroLine()
    {
        var input = "[WARNING] — General observation about architecture";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().BeNull();
        result[0].LineNumber.Should().Be(0);
        result[0].Message.Should().Be("General observation about architecture");
    }

    [Fact]
    public void Parse_MultipleMarkersOnSameLine_FirstWins()
    {
        var input = "[CRITICAL] src/A.cs:1 — issue [WARNING] src/B.cs:2 — other";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(FindingSeverity.Critical);
        result[0].FilePath.Should().Be("src/A.cs");
        result[0].LineNumber.Should().Be(1);
    }

    [Fact]
    public void Parse_CodeBlockFencesStripped()
    {
        var input = "```\n[CRITICAL] src/Service.cs:42 — Null ref\n```";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Service.cs");
        result[0].LineNumber.Should().Be(42);
    }

    [Fact]
    public void Parse_CodeBlockWithLanguageTag_FencesStripped()
    {
        var input = "```text\n[WARNING] src/File.cs:10 — Issue\n```";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Parse_UrlsNotMatchedAsFilePaths()
    {
        var input = "[WARNING] See https://example.com/docs:80 for details — Bad pattern";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().BeNull();
        result[0].LineNumber.Should().Be(0);
    }

    [Fact]
    public void Parse_EmailsNotMatchedAsFilePaths()
    {
        var input = "[WARNING] Contact user@example.com:25 for help — Issue found";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().BeNull();
        result[0].LineNumber.Should().Be(0);
    }

    [Fact]
    public void Parse_BackslashPathsNormalized()
    {
        var input = @"[CRITICAL] src\Controllers\UserController.cs:15 — Missing validation";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Controllers/UserController.cs");
        result[0].LineNumber.Should().Be(15);
    }

    [Fact]
    public void Parse_SeparatorDash_Stripped()
    {
        var input = "[WARNING] src/File.cs:10 - Issue description";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Message.Should().Be("Issue description");
    }

    [Fact]
    public void Parse_SeparatorColon_Stripped()
    {
        var input = "[WARNING] src/File.cs:10: Issue description";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Message.Should().Be("Issue description");
    }

    [Fact]
    public void Parse_MessageTrimmed()
    {
        var input = "[WARNING] src/File.cs:10 —   spaces around message   ";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Message.Should().Be("spaces around message");
    }

    [Fact]
    public void Parse_MultipleLines_OneFindingPerLine()
    {
        var input = "[CRITICAL] src/A.cs:1 — issue1\nSome text without markers\n[WARNING] src/B.cs:2 — issue2";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(2);
        result[0].Severity.Should().Be(FindingSeverity.Critical);
        result[1].Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Parse_FirstFileLineReferenceWins()
    {
        var input = "[WARNING] src/First.cs:10 — see also src/Second.cs:20";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/First.cs");
        result[0].LineNumber.Should().Be(10);
    }
}
