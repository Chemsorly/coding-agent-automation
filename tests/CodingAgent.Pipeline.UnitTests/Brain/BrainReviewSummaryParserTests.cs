using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using AwesomeAssertions;

namespace CodingAgent.Pipeline.UnitTests.Brain;

public class BrainReviewSummaryParserTests
{
    private static readonly string ValidSummaryJson = """
        {
            "promoted":   [{ "scope": "repository", "file": "projects/repo/SKILL.md", "text": "- [2+] For a situation, do something (#1)." }],
            "appended":   [{ "scope": "technology:dotnet", "file": "technology/dotnet/SKILL.md", "text": "- [2+] For b, do c (#2)." }],
            "retired":    [{ "scope": "general", "file": "general/SKILL.md", "text": "- [user] For d, do e (#3).", "reason": "code removed" }],
            "missedAgain":[{ "scope": "repository", "file": "projects/repo/SKILL.md", "text": "- [2+] For f, do g (#4)." }],
            "checks":     [{ "cause": "coverage below 80%", "repositories": ["owner/repo"], "suggestedHome": "quality_gate" }],
            "flakyTests": [{ "name": "MyTest.Flaky", "repositories": ["owner/repo"], "count": 3 }],
            "infrastructureFailures": 2, "ciStatusErrors": 1, "timeouts": 0,
            "unknownField": "ignored"
        }
        """;

    // ── Parse ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Null_ReturnsSummaryMissing()
    {
        var r = BrainReviewSummaryParser.Parse(null);
        r.Error.Should().Be("summary_missing");
        r.Summary.Should().BeNull();
    }

    [Fact]
    public void Parse_BrokenJson_ReturnsSummaryInvalid()
    {
        var r = BrainReviewSummaryParser.Parse("not json {");
        r.Error.Should().Be("summary_invalid");
    }

    [Fact]
    public void Parse_WrongRootType_ReturnsSummaryInvalid()
    {
        var r = BrainReviewSummaryParser.Parse("[1,2,3]");
        r.Error.Should().Be("summary_invalid");
    }

    [Fact]
    public void Parse_ValidSummary_FillsAllFields()
    {
        var r = BrainReviewSummaryParser.Parse(ValidSummaryJson);
        r.Error.Should().BeNull();
        r.Summary.Should().NotBeNull();
        r.Summary!.Promoted.Should().HaveCount(1);
        r.Summary.Appended.Should().HaveCount(1);
        r.Summary.Retired.Should().HaveCount(1);
        r.Summary.Retired[0].Reason.Should().Be("code removed");
        r.Summary.MissedAgain.Should().HaveCount(1);
        r.Summary.Checks.Should().HaveCount(1);
        r.Summary.Checks[0].Cause.Should().Be("coverage below 80%");
        r.Summary.Checks[0].SuggestedHome.Should().Be("quality_gate");
        r.Summary.FlakyTests.Should().HaveCount(1);
        r.Summary.FlakyTests[0].Count.Should().Be(3);
        r.Summary.InfrastructureFailures.Should().Be(2);
        r.Summary.CiStatusErrors.Should().Be(1);
        r.Summary.Timeouts.Should().Be(0);
    }

    [Fact]
    public void Parse_UnknownFieldsIgnored()
    {
        var r = BrainReviewSummaryParser.Parse(ValidSummaryJson);
        r.Error.Should().BeNull(); // unknown "unknownField" does not fail
    }

    // ── ToOutcome ─────────────────────────────────────────────────────

    [Fact]
    public void ToOutcome_MapsAllActions()
    {
        var r = BrainReviewSummaryParser.Parse(ValidSummaryJson);
        var outcome = BrainReviewSummaryParser.ToOutcome(r.Summary!);
        outcome.Promoted.Should().Be(1);
        outcome.Appended.Should().Be(1);
        outcome.Retired.Should().Be(1);
        outcome.MissedAgain.Should().Be(1);
        outcome.Lines.Should().HaveCount(4); // 1+1+1+1
        var actions = outcome.Lines.Select(l => l.Action).ToList();
        actions.Should().Contain("promoted");
        actions.Should().Contain("appended");
        actions.Should().Contain("retired");
        actions.Should().Contain("missed_again");
    }

    [Fact]
    public void ToOutcome_CopiesChecksAndFlakyTests()
    {
        var r = BrainReviewSummaryParser.Parse(ValidSummaryJson);
        var outcome = BrainReviewSummaryParser.ToOutcome(r.Summary!);
        outcome.Checks.Should().HaveCount(1);
        outcome.FlakyTests.Should().HaveCount(1);
        outcome.InfrastructureFailures.Should().Be(2);
        outcome.CiStatusErrors.Should().Be(1);
    }

    // ── Cap ───────────────────────────────────────────────────────────

    [Fact]
    public void Cap_SmallOutcome_Unchanged()
    {
        var r = BrainReviewSummaryParser.Parse(ValidSummaryJson);
        var outcome = BrainReviewSummaryParser.ToOutcome(r.Summary!);
        var capped = BrainReviewSummaryParser.Cap(outcome);
        capped.LinesTruncated.Should().BeFalse();
        capped.Lines.Should().HaveCount(4);
    }

    [Fact]
    public void Cap_ManyLines_TruncatesAndFlags()
    {
        // Build an outcome with 3000 lines (each ~70 chars) → well over 64 KB
        var lines = Enumerable.Range(0, 3000)
            .Select(i => new BrainReviewLine { Action = "promoted", Scope = "repository", File = "f.md", Text = $"- [2+] For situation {i}, do something (#{i})." })
            .ToList();
        var outcome = new BrainConsolidationOutcome { Lines = lines };
        var capped = BrainReviewSummaryParser.Cap(outcome);
        capped.LinesTruncated.Should().BeTrue();
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(capped);
        json.Length.Should().BeLessThan(BrainLayout.MaxReviewSummaryBytes + 1);
    }

    [Fact]
    public void Cap_ManyLines_KeepsTheLongestPrefixThatFits()
    {
        var lines = Enumerable.Range(0, 3000)
            .Select(i => new BrainReviewLine { Action = "promoted", Scope = "repository", File = "f.md", Text = $"- [2+] For situation {i}, do something (#{i})." })
            .ToList();
        var outcome = new BrainConsolidationOutcome { Lines = lines };

        var capped = BrainReviewSummaryParser.Cap(outcome);

        capped.Lines.Should().Equal(lines.Take(capped.Lines.Count), "the cap drops lines from the end only");
        var oneLineMore = outcome with { Lines = lines.Take(capped.Lines.Count + 1).ToList(), LinesTruncated = true };
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(oneLineMore).Length.Should().BeGreaterThan(
            BrainLayout.MaxReviewSummaryBytes, "one more line would not have fitted");
    }

    [Fact]
    public void Cap_LinesThatFit_AreKeptAndNotFlagged()
    {
        var lines = new List<BrainReviewLine> { new() { Action = "promoted", Scope = "repository", File = "f.md", Text = "- [2+] Short (#1)." } };

        var capped = BrainReviewSummaryParser.Cap(new BrainConsolidationOutcome { Lines = lines });

        capped.Lines.Should().Equal(lines);
        capped.LinesTruncated.Should().BeFalse();
    }

    [Fact]
    public void Cap_SingleLineTooLarge_DropsItAndFlags()
    {
        var lines = new List<BrainReviewLine> { new() { Action = "promoted", Scope = "repository", File = "f.md", Text = new string('x', BrainLayout.MaxReviewSummaryBytes + 1) } };

        var capped = BrainReviewSummaryParser.Cap(new BrainConsolidationOutcome { Lines = lines });

        capped.Lines.Should().BeEmpty();
        capped.LinesTruncated.Should().BeTrue();
    }
}
