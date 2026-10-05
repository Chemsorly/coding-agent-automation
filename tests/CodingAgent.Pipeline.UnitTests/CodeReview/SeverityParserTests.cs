using AwesomeAssertions;
using CodingAgent.Pipeline.CodeReview;

namespace CodingAgent.Pipeline.UnitTests.CodeReview;

/// <summary>
/// Unit tests for <see cref="SeverityParser.Parse"/>.
/// </summary>
public sealed class SeverityParserTests
{
    // ── Null guard ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullLines_ThrowsArgumentNullException()
    {
        var act = () => SeverityParser.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Empty input ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyList_ReturnsZeroCounts()
    {
        var result = SeverityParser.Parse([]);
        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    // ── Counting ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SingleCritical_CountsOne()
    {
        var result = SeverityParser.Parse(["[CRITICAL] serious bug in auth"]);
        result.Critical.Should().Be(1);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    [Fact]
    public void Parse_MultipleMarkersOnOneLine_CountsTheLeadingMarkerOnly()
    {
        // One line is one finding; FindingsParser also takes only the leading marker.
        var result = SeverityParser.Parse(["[CRITICAL] first issue, unlike the [WARNING] second"]);
        result.Critical.Should().Be(1);
        result.Warning.Should().Be(0);
    }

    [Fact]
    public void Parse_MixedSeverities_CountsEachCorrectly()
    {
        var lines = new[]
        {
            "[CRITICAL] auth bypass",
            "[WARNING] null ref",
            "[WARNING] race condition",
            "[SUGGESTION] rename method"
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(1);
        result.Warning.Should().Be(2);
        result.Suggestion.Should().Be(1);
    }

    // ── Case insensitivity ────────────────────────────────────────────────

    [Theory]
    [InlineData("[critical]")]
    [InlineData("[Critical]")]
    [InlineData("[CRITICAL]")]
    public void Parse_CriticalCaseInsensitive_CountsOne(string marker)
    {
        var result = SeverityParser.Parse([$"{marker} issue"]);
        result.Critical.Should().Be(1);
    }

    // ── RESOLVED lines are excluded ───────────────────────────────────────

    [Fact]
    public void Parse_ResolvedLine_IsExcludedFromCounts()
    {
        var result = SeverityParser.Parse(["RESOLVED [CRITICAL] old auth bypass"]);
        result.Critical.Should().Be(0);
    }

    [Fact]
    public void Parse_ResolvedCaseInsensitive_IsExcluded()
    {
        var result = SeverityParser.Parse(["resolved [WARNING] old issue"]);
        result.Warning.Should().Be(0);
    }

    [Fact]
    public void Parse_MixedResolvedAndActive_OnlyCountsActive()
    {
        var lines = new[]
        {
            "RESOLVED [CRITICAL] old issue",
            "[WARNING] new issue"
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(1);
    }

    [Fact]
    public void Parse_FindingMarkedResolvedByStatusWord_IsExcluded()
    {
        var result = SeverityParser.Parse(["[CRITICAL] src/Auth.cs:10 — RESOLVED: the null check was added"]);
        result.Critical.Should().Be(0);
    }

    [Fact]
    public void Parse_FindingWithLowercaseResolvedInDescription_IsCounted()
    {
        // A real finding from production: the word "resolved" in the description used to drop the whole line.
        var result = SeverityParser.Parse([
            "[WARNING] tests/CodingAgent.Web.E2ETests/Tests/RunDetailCancelAndRedispatchTests.cs:468 — " +
            "IDispatchOrchestrationService and IWorkDistributor are resolved directly from the root IServiceProvider."]);
        result.Warning.Should().Be(1);
    }

    [Fact]
    public void Parse_FindingWithResolvedInsideAnIdentifier_IsCounted()
    {
        var result = SeverityParser.Parse([
            "[SUGGESTION] src/CodingAgent.Api/WorkItemDispatchEndpoints.cs:195 — " +
            "The 503 pattern-match re-checks the raw result of DispatchResolvedWorkItemAsync."]);
        result.Suggestion.Should().Be(1);
    }

    // ── Only finding lines count ──────────────────────────────────────────

    [Fact]
    public void Parse_HardWrappedFindingThatQuotesMarkers_CountsOnlyTheFinding()
    {
        // The Correctness findings of the PR #3363 review: one suggestion, hard-wrapped, that quotes the
        // marker syntax in code spans. Counting every marker made it 1 critical, 1 warning and 2 suggestions.
        var lines = new[]
        {
            "[SUGGESTION] tests/CodingAgent.Pipeline.UnitTests/CodeReview/SeverityParserTests.cs:74 — The removed",
            "test `Parse_CaseInsensitive_MatchesAllVariants` exercised case-insensitive marker matching for all",
            "three severities (Critical/Warning/Suggestion). The claimed survivor `Parse_CriticalCaseInsensitive_CountsOne`",
            "only covers case variants of `[CRITICAL]`. Lowercase `[warning]`/`[suggestion]` matching is no",
            "longer directly asserted (only `[WARNING]` appears via the RESOLVED-exclusion test). This is a minor"
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(1);
    }

    [Fact]
    public void Parse_ProseThatNamesSeverities_CountsNothing()
    {
        // The DotNetSpecialist summary of an issue #3369 review, counted as 1 critical, 1 warning and
        // 1 suggestion, which sent a CRITICAL fix prompt.
        var lines = new[]
        {
            "No issues were found in the changed code that rise to [CRITICAL] or [WARNING] severity under the",
            "stated checklist. One [SUGGESTION] is noted below."
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    [Theory]
    [InlineData("No [CRITICAL] issues found.")]
    [InlineData("        // TODO [WARNING]: This PVC availability snapshot is taken OUTSIDE _pvcSelectLock.")]
    [InlineData("  # TODO: [WARNING] Stale class name — WorkItemDispatchPoller was renamed to WorkItemDispatchLoop.")]
    [InlineData("### TODO [WARNING] comment removal")]
    [InlineData("`TODO [WARNING]` comments. They were NOT fixed in this PR because the scope was to add")]
    [InlineData("      \"evidence\": \"The specific TODO [WARNING] text is gone from QualityGateValidator.cs.\",")]
    [InlineData("| [CRITICAL] | 1 |")]
    [InlineData("> [CRITICAL] src/Auth.cs:10 — quoted from the previous review")]
    public void Parse_MarkerThatDoesNotStartTheLine_IsNotCounted(string line)
    {
        var result = SeverityParser.Parse([line]);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    [Theory]
    [InlineData("[WARNING] src/Foo.cs:12 — plain")]
    [InlineData("   [WARNING] src/Foo.cs:12 — indented")]
    [InlineData("1. [WARNING] src/Foo.cs:12 — numbered")]
    [InlineData("2) [WARNING] src/Foo.cs:12 — numbered with a parenthesis")]
    [InlineData("- [WARNING] src/Foo.cs:12 — bullet")]
    [InlineData("* [WARNING] src/Foo.cs:12 — bullet")]
    [InlineData("- **[WARNING] DI lifetime mismatch** — the services are resolved from the root provider")]
    [InlineData("**[WARNING]** src/Foo.cs:12 — bold marker")]
    [InlineData("### [WARNING] src/Foo.cs:12 — heading")]
    [InlineData("- `[WARNING]` — the adversarial review prompts list the markers in code spans")]
    public void Parse_FindingLineFormats_CountTheFinding(string line)
    {
        var result = SeverityParser.Parse([line]);

        result.Warning.Should().Be(1);
    }

    // ── Non-marker lines ──────────────────────────────────────────────────

    [Fact]
    public void Parse_LinesWithoutMarkers_CountsZero()
    {
        var result = SeverityParser.Parse(["just some text", "no markers here"]);
        result.Critical.Should().Be(0);
        result.Warning.Should().Be(0);
        result.Suggestion.Should().Be(0);
    }

    [Fact]
    public void Parse_WithMarkersInNoise_CountsCorrectly()
    {
        var lines = new[]
        {
            "Starting code review...",
            "Checking file src/Foo.cs",
            "[WARNING] Unused import on line 3",
            "Checking file src/Bar.cs",
            "[SUGGESTION] Consider extracting method",
            "Review complete."
        };

        var result = SeverityParser.Parse(lines);

        result.Critical.Should().Be(0);
        result.Warning.Should().Be(1);
        result.Suggestion.Should().Be(1);
    }
}
