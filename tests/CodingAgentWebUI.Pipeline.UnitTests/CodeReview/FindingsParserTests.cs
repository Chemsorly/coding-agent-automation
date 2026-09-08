using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.CodeReview;

/// <summary>
/// Unit tests for <see cref="FindingsParser.Parse"/>.
/// FindingsParser is pure static with no I/O — all tests are in-memory.
/// </summary>
public sealed class FindingsParserTests
{
    private const string AgentName = "TestReviewer";

    // ── Null / empty input ────────────────────────────────────────────────

    [Fact]
    public void Parse_NullInput_ReturnsEmptyList()
    {
        var result = FindingsParser.Parse(null, AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyList()
    {
        var result = FindingsParser.Parse(string.Empty, AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NullAgentName_ThrowsArgumentNullException()
    {
        var act = () => FindingsParser.Parse("[WARNING] some issue", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_WhitespaceOnlyInput_ReturnsEmptyList()
    {
        var result = FindingsParser.Parse("   \n\t  \n", AgentName);
        result.Should().BeEmpty();
    }

    // ── Severity markers ─────────────────────────────────────────────────

    [Theory]
    [InlineData("[CRITICAL]", FindingSeverity.Critical)]
    [InlineData("[WARNING]", FindingSeverity.Warning)]
    [InlineData("[SUGGESTION]", FindingSeverity.Suggestion)]
    [InlineData("[critical]", FindingSeverity.Critical)]
    [InlineData("[Warning]", FindingSeverity.Warning)]
    [InlineData("[Suggestion]", FindingSeverity.Suggestion)]
    public void Parse_SeverityMarkers_AreCaseInsensitive(string marker, FindingSeverity expectedSeverity)
    {
        var result = FindingsParser.Parse($"{marker} src/Foo.cs:10 — message", AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(expectedSeverity);
    }

    [Fact]
    public void Parse_LineWithNoSeverityMarker_IsSkipped()
    {
        var input = "This line has no severity marker\n[WARNING] src/Foo.cs:5 — real finding";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Message.Should().Be("real finding");
    }

    // ── File:line patterns ────────────────────────────────────────────────

    [Fact]
    public void Parse_ColonPattern_ExtractsFileAndLine()
    {
        var result = FindingsParser.Parse("[WARNING] src/Services/Foo.cs:42 — null reference", AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Services/Foo.cs");
        result[0].LineNumber.Should().Be(42);
        result[0].Message.Should().Be("null reference");
    }

    [Fact]
    public void Parse_HashLPattern_ExtractsFileAndLine()
    {
        var result = FindingsParser.Parse("[CRITICAL] src/Services/Foo.cs#L77 — injection risk", AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Services/Foo.cs");
        result[0].LineNumber.Should().Be(77);
        result[0].Message.Should().Be("injection risk");
    }

    [Fact]
    public void Parse_ParenPattern_ExtractsFileAndLine()
    {
        var result = FindingsParser.Parse("[SUGGESTION] src/Services/Bar.cs (line 15) — rename variable", AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Services/Bar.cs");
        result[0].LineNumber.Should().Be(15);
        result[0].Message.Should().Be("rename variable");
    }

    [Fact]
    public void Parse_CommaLinePattern_ExtractsFileAndLine()
    {
        var result = FindingsParser.Parse("[WARNING] src/Services/Baz.cs, line 99 — unused variable", AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Services/Baz.cs");
        result[0].LineNumber.Should().Be(99);
        result[0].Message.Should().Be("unused variable");
    }

    [Fact]
    public void Parse_NoFileReference_MessageCoversFullContent()
    {
        var result = FindingsParser.Parse("[WARNING] general architecture concern", AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().BeNull();
        result[0].LineNumber.Should().Be(0);
        result[0].Message.Should().Be("general architecture concern");
    }

    [Fact]
    public void Parse_SetsAgentNameOnAllFindings()
    {
        var input = "[WARNING] src/Foo.cs:1 — issue1\n[CRITICAL] src/Bar.cs:2 — issue2";
        var result = FindingsParser.Parse(input, "MyAgent");

        result.Should().AllSatisfy(f => f.AgentName.Should().Be("MyAgent"));
    }

    // ── Path normalisation ────────────────────────────────────────────────

    [Fact]
    public void Parse_BackslashInPath_IsNormalisedToForwardSlash()
    {
        var result = FindingsParser.Parse(@"[WARNING] src\Services\Foo.cs:42 — message", AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Services/Foo.cs");
    }

    [Fact]
    public void Parse_LeadingDotSlash_IsStripped()
    {
        var result = FindingsParser.Parse("[WARNING] ./src/Foo.cs:5 — message", AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Foo.cs");
    }

    // ── RESOLVED skipping ─────────────────────────────────────────────────

    [Fact]
    public void Parse_LineContainsResolved_IsSkipped()
    {
        var input = "RESOLVED [WARNING] src/Foo.cs:42 — old finding";
        var result = FindingsParser.Parse(input, AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ResolvedCaseInsensitive_IsSkipped()
    {
        var input = "resolved [WARNING] src/Foo.cs:42 — old finding";
        var result = FindingsParser.Parse(input, AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_OnlyNonResolvedLinesReturned()
    {
        var input =
            "RESOLVED [WARNING] src/Old.cs:1 — already fixed\n" +
            "[CRITICAL] src/New.cs:5 — real issue\n" +
            "RESOLVED [SUGGESTION] src/Other.cs:10 — also fixed";

        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/New.cs");
    }

    // ── Code fence stripping ──────────────────────────────────────────────

    [Fact]
    public void Parse_CodeFenceLines_AreStrippedBeforeProcessing()
    {
        var input = "```\n[WARNING] src/Foo.cs:1 — inside fence\n```";
        var result = FindingsParser.Parse(input, AgentName);

        // Fence lines removed; the finding line is still parsed
        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/Foo.cs");
    }

    [Fact]
    public void Parse_LanguageTaggedFence_IsStripped()
    {
        var input = "```csharp\n[CRITICAL] src/Bar.cs:10 — critical bug\n```";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(FindingSeverity.Critical);
    }

    // ── Multi-finding output ──────────────────────────────────────────────

    [Fact]
    public void Parse_MultipleFindings_AllExtracted()
    {
        var input =
            "[CRITICAL] src/Auth.cs:10 — SQL injection\n" +
            "[WARNING] src/Cache.cs:55 — race condition\n" +
            "[SUGGESTION] src/Utils.cs:3 — rename method";

        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(3);
        result[0].Severity.Should().Be(FindingSeverity.Critical);
        result[1].Severity.Should().Be(FindingSeverity.Warning);
        result[2].Severity.Should().Be(FindingSeverity.Suggestion);
    }

    [Fact]
    public void Parse_WindowsLineEndings_AreHandled()
    {
        var input = "[WARNING] src/Foo.cs:1 — message1\r\n[CRITICAL] src/Bar.cs:2 — message2";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(2);
        result[0].FilePath.Should().Be("src/Foo.cs");
        result[1].FilePath.Should().Be("src/Bar.cs");
    }

    // ── Message truncation ────────────────────────────────────────────────

    [Fact]
    public void Parse_MessageExceeding65536Chars_IsTruncated()
    {
        var longMessage = new string('x', 70000);
        var input = $"[WARNING] {longMessage}";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Message.Length.Should().Be(65536);
    }

    // ── URL exclusion ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_HttpUrlInContent_IsNotTreatedAsFilePath()
    {
        var input = "[WARNING] see https://example.com/docs:80 for details";
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        // URL should not be extracted as a file path
        result[0].FilePath.Should().BeNull();
    }

    // ── Separator stripping ───────────────────────────────────────────────

    [Theory]
    [InlineData("[WARNING] src/Foo.cs:1 — em-dash message", "em-dash message")]
    [InlineData("[WARNING] src/Foo.cs:1 - hyphen message", "hyphen message")]
    [InlineData("[WARNING] src/Foo.cs:1: colon message", "colon message")]
    public void Parse_LeadingSeparators_AreStrippedFromMessage(string input, string expectedMessage)
    {
        var result = FindingsParser.Parse(input, AgentName);

        result.Should().HaveCount(1);
        result[0].Message.Should().Be(expectedMessage);
    }

    // ── Crash-freedom (property-style) ───────────────────────────────────

    [Theory]
    [InlineData("[WARNING]")]
    [InlineData("[CRITICAL] ")]
    [InlineData("[SUGGESTION] no file ref just text")]
    [InlineData("[WARNING] src/Foo.cs:0 — zero line number")]
    [InlineData("[WARNING] src/Foo.cs:-1 — negative line number")]
    public void Parse_VariousEdgeCaseInputs_NeverThrows(string input)
    {
        // Property: Parse() never throws for any string input
        var act = () => FindingsParser.Parse(input, AgentName);
        act.Should().NotThrow();
    }
}
