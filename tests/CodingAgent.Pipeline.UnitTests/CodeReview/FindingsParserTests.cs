using AwesomeAssertions;
using CodingAgent.Pipeline.CodeReview;
using CodingAgent.Pipeline.CodeReview.Models;

namespace CodingAgent.Pipeline.UnitTests.CodeReview;

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

    // TODO: Add a test for the leading '/' stripping branch in NormalizePath (FindingsParser.cs:226,
    // `normalized.StartsWith('/')` → `normalized = normalized[1..]`). Currently only the "./" prefix
    // is covered by Parse_LeadingDotSlash_IsStripped. A path like "[WARNING] /src/Foo.cs:5 — msg"
    // exercises the '/' branch; without a test for it, a regression in that branch would go undetected.

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

    [Fact]
    public void Parse_FindingMarkedResolvedByStatusWord_IsSkipped()
    {
        var result = FindingsParser.Parse("[WARNING] src/Foo.cs:42 — RESOLVED: the guard was added", AgentName);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_FindingWithLowercaseResolvedInDescription_IsParsed()
    {
        // A real finding from production: the word "resolved" in the description used to drop the whole line.
        var input =
            "[WARNING] tests/CodingAgent.Web.E2ETests/Tests/RunDetailCancelAndRedispatchTests.cs:468 — " +
            "IDispatchOrchestrationService and IWorkDistributor are resolved directly from the root IServiceProvider.";

        var result = FindingsParser.Parse(input, AgentName);

        result.Should().ContainSingle();
        result[0].Severity.Should().Be(FindingSeverity.Warning);
        result[0].LineNumber.Should().Be(468);
    }

    // ── Only finding lines are findings ───────────────────────────────────

    [Fact]
    public void Parse_HardWrappedFindingThatQuotesMarkers_ReturnsOnlyTheFinding()
    {
        // The Correctness findings of the PR #3363 review. The quoted `[CRITICAL]` on a wrapped line
        // became a CRITICAL finding without a location, which no reader could find in the review.
        var input =
            "[SUGGESTION] tests/CodingAgent.Pipeline.UnitTests/CodeReview/SeverityParserTests.cs:74 — The removed\n" +
            "test `Parse_CaseInsensitive_MatchesAllVariants` exercised case-insensitive marker matching for all\n" +
            "three severities (Critical/Warning/Suggestion). The claimed survivor `Parse_CriticalCaseInsensitive_CountsOne`\n" +
            "only covers case variants of `[CRITICAL]`. Lowercase `[warning]`/`[suggestion]` matching is no\n" +
            "longer directly asserted (only `[WARNING]` appears via the RESOLVED-exclusion test). This is a minor";

        var result = FindingsParser.Parse(input, AgentName);

        result.Should().ContainSingle();
        result[0].Severity.Should().Be(FindingSeverity.Suggestion);
        result[0].FilePath.Should().Be("tests/CodingAgent.Pipeline.UnitTests/CodeReview/SeverityParserTests.cs");
        result[0].LineNumber.Should().Be(74);
    }

    [Theory]
    [InlineData("No issues were found in the changed code that rise to [CRITICAL] or [WARNING] severity")]
    [InlineData("stated checklist. One [SUGGESTION] is noted below.")]
    [InlineData("        // TODO [WARNING]: This PVC availability snapshot is taken OUTSIDE _pvcSelectLock.")]
    [InlineData("| [CRITICAL] | 1 |")]
    [InlineData("> [CRITICAL] src/Auth.cs:10 — quoted from the previous review")]
    public void Parse_MarkerThatDoesNotStartTheLine_ProducesNoFinding(string line)
    {
        var result = FindingsParser.Parse(line, AgentName);
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("   [WARNING] src/Foo.cs:12 — message")]
    [InlineData("1. [WARNING] src/Foo.cs:12 — message")]
    [InlineData("- [WARNING] src/Foo.cs:12 — message")]
    [InlineData("**[WARNING]** src/Foo.cs:12 — message")]
    [InlineData("### [WARNING] src/Foo.cs:12 — message")]
    public void Parse_FindingLineFormats_ExtractTheFinding(string line)
    {
        var result = FindingsParser.Parse(line, AgentName);

        result.Should().ContainSingle();
        result[0].Severity.Should().Be(FindingSeverity.Warning);
        result[0].FilePath.Should().Be("src/Foo.cs");
        result[0].LineNumber.Should().Be(12);
        result[0].Message.Should().Be("message");
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

    // ── Finding formats and edge cases ──────────────────────────────

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
