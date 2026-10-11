using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>Tests for <see cref="TriagePromptBuilder"/> and <see cref="TriageResultParser"/>.</summary>
public sealed class TriagePromptAndParserTests
{
    private static readonly DecompositionProjectContext Project = new()
    {
        ProjectName = "Shop",
        Repositories = [new RepositoryTarget { TemplateName = "checkout-api", Description = "" }],
    };

    // ── Prompts ──────────────────────────────────────────────────────────────

    [Fact]
    public void InvestigationPrompt_NamesTheInputsAndTheOutput()
    {
        var prompt = TriagePromptBuilder.BuildInvestigationPrompt(reportToTracker: false, Project);

        prompt.Should().Contain(AgentWorkspacePaths.IssueContextFilePath)
            .And.Contain(AgentWorkspacePaths.TriageContextFilePath)
            .And.Contain(AgentWorkspacePaths.OpenIssuesDirectory)
            .And.Contain(AgentWorkspacePaths.ProjectContextFilePath)
            .And.Contain(AgentWorkspacePaths.TriageResultFilePath);
    }

    [Fact]
    public void InvestigationPrompt_AsksForHypothesesEvidenceAndTheInvestigatedList()
    {
        var prompt = TriagePromptBuilder.BuildInvestigationPrompt(reportToTracker: false, Project);

        prompt.Should().Contain("at least three different hypotheses")
            .And.Contain("Record every check")
            .And.Contain("Every claim needs evidence")
            .And.Contain("`investigated` is required")
            .And.Contain("inconclusive");
    }

    [Fact]
    public void InvestigationPrompt_ForbidsWritesAndPushes()
    {
        var prompt = TriagePromptBuilder.BuildInvestigationPrompt(reportToTracker: false, Project);

        prompt.Should().Contain("Never write to external systems")
            .And.Contain("Never create branches, commits, pushes or pull requests");
    }

    [Fact]
    public void InvestigationPrompt_CapsTheDrafts()
    {
        TriagePromptBuilder.BuildInvestigationPrompt(false, Project)
            .Should().Contain($"at most **{TriageConstants.MaxDrafts}** drafts");
    }

    [Fact]
    public void InvestigationPrompt_ForATracker_ForbidsPastedLogsAndQuotesOfOtherTriages()
    {
        var tracker = TriagePromptBuilder.BuildInvestigationPrompt(reportToTracker: true, Project);
        var app = TriagePromptBuilder.BuildInvestigationPrompt(reportToTracker: false, Project);

        tracker.Should().Contain("may be public").And.Contain("Do not quote other triages");
        app.Should().NotContain("may be public");
    }

    [Fact]
    public void InvestigationPrompt_WithoutProject_DoesNotMentionTheProjectContext()
    {
        var prompt = TriagePromptBuilder.BuildInvestigationPrompt(false, projectContext: null);

        prompt.Should().NotContain(AgentWorkspacePaths.ProjectContextFilePath).And.NotContain("targetRepository` to the repository");
    }

    [Fact]
    public void ReviewPrompt_ChecksEvidenceCausationAndSensitiveData_AndUsesSeverityMarkers()
    {
        var prompt = TriagePromptBuilder.BuildReviewPrompt(reportToTracker: true);

        prompt.Should().Contain(AgentWorkspacePaths.TriageReviewFilePath)
            .And.Contain("[CRITICAL]").And.Contain("[WARNING]")
            .And.Contain("coincidence in time")
            .And.Contain("Sensitive data")
            .And.Contain("Public tracker");
        TriagePromptBuilder.BuildReviewPrompt(reportToTracker: false).Should().NotContain("Public tracker");
    }

    [Fact]
    public void RefinementPrompt_RewritesTheResultFile()
    {
        TriagePromptBuilder.BuildRefinementPrompt()
            .Should().Contain(AgentWorkspacePaths.TriageReviewFilePath).And.Contain(AgentWorkspacePaths.TriageResultFilePath);
    }

    // ── Parser ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_FullResult_MapsEveryPart()
    {
        const string json = """
            {
              "verdict": "cause_found", "confidence": "high", "summary": "Acked before update.",
              "impact": "37 orders", "reproduced": true, "reproduction": "PaymentConsumerTests",
              "causalChain": [ { "text": "Pending", "kind": "symptom", "evidenceIds": ["E1"] } ],
              "hypotheses": [ { "id": "H1", "text": "Ack order", "state": "confirmed" } ],
              "evidence": [ { "id": "E1", "claim": "37 orders", "source": "grafana", "query": "{app=\"w\"}", "link": "https://g/x" } ],
              "investigated": [ { "check": "Logs", "where": "grafana · Loki", "for": "H1", "result": "37", "evidenceIds": ["E1"] } ],
              "notChecked": [ { "what": "Traces", "why": "no source" } ],
              "questions": [],
              "drafts": [ { "id": "d1", "kind": "root_fix", "targetRepository": "api", "title": "Fix", "body": "Body", "size": "S" } ]
            }
            """;

        var outcome = TriageResultParser.Parse(json);

        outcome.Error.Should().BeNull();
        outcome.Warnings.Should().BeEmpty();
        var r = outcome.Result!;
        r.Verdict.Should().Be(TriageVerdict.CauseFound);
        r.Confidence.Should().Be(TriageConfidence.High);
        r.Reproduced.Should().BeTrue();
        r.CausalChain.Single().Kind.Should().Be(TriageChainKind.Symptom);
        r.Hypotheses.Single().State.Should().Be(TriageHypothesisState.Confirmed);
        r.Evidence.Single().Query.Should().Be("{app=\"w\"}");
        r.Investigated.Single().For.Should().Be("H1");
        r.NotChecked.Single().Why.Should().Be("no source");
        r.Drafts.Single().Kind.Should().Be(TriageDraftKind.RootFix);
    }

    [Theory]
    [InlineData("cause_found", TriageVerdict.CauseFound)]
    [InlineData("Cause Found", TriageVerdict.CauseFound)]
    [InlineData("cause-found", TriageVerdict.CauseFound)]
    [InlineData("INCONCLUSIVE", TriageVerdict.Inconclusive)]
    [InlineData("not a bug", TriageVerdict.NotABug)]
    [InlineData("duplicate", TriageVerdict.Duplicate)]
    public void Parse_AcceptsVerdictVariants(string verdict, TriageVerdict expected)
    {
        TriageResultParser.Parse($$"""{ "verdict": "{{verdict}}", "summary": "s" }""").Result!.Verdict.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{ "summary": "s" }""")]
    [InlineData("""{ "verdict": "maybe", "summary": "s" }""")]
    public void Parse_MissingOrUnknownVerdict_Fails(string json)
    {
        var outcome = TriageResultParser.Parse(json);

        outcome.Result.Should().BeNull();
        outcome.Error.Should().Contain("verdict");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void Parse_NotAJsonObject_Fails(string json)
    {
        TriageResultParser.Parse(json).Result.Should().BeNull();
    }

    [Fact]
    public void Parse_UnknownValues_DefaultWithWarnings_InsteadOfFailing()
    {
        const string json = """
            {
              "verdict": "cause_found", "summary": "s", "confidence": "very",
              "hypotheses": [ { "text": "x", "state": "maybe" } ],
              "drafts": [ { "kind": "rewrite", "title": "t", "body": "b" } ]
            }
            """;

        var outcome = TriageResultParser.Parse(json);

        outcome.Result!.Confidence.Should().BeNull();
        outcome.Result.Hypotheses.Single().State.Should().Be(TriageHypothesisState.Open);
        outcome.Result.Hypotheses.Single().Id.Should().Be("H1");
        outcome.Result.Drafts.Single().Kind.Should().Be(TriageDraftKind.RootFix);
        outcome.Result.Drafts.Single().Id.Should().Be("d1");
        outcome.Warnings.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_ToleratesCodeFencesCommentsTrailingCommasAndStringBooleans()
    {
        const string json = """
            ```json
            {
              // the agent's note
              "verdict": "inconclusive",
              "summary": "s",
              "reproduced": "yes",
              "questions": [ { "question": "When?" }, ],
            }
            ```
            """;

        var outcome = TriageResultParser.Parse(json);

        outcome.Result!.Reproduced.Should().BeTrue();
        outcome.Result.Questions.Single().Question.Should().Be("When?");
    }

    [Fact]
    public void Parse_SkipsEntriesWithoutTheirMainText()
    {
        const string json = """
            {
              "verdict": "inconclusive", "summary": "s",
              "investigated": [ { "where": "grafana" }, { "check": "c", "where": "w", "result": "r" } ],
              "evidence": [ { "source": "s" } ]
            }
            """;

        var r = TriageResultParser.Parse(json).Result!;

        r.Investigated.Should().ContainSingle();
        r.Evidence.Should().BeEmpty();
    }
}
