using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>Tests for <see cref="TriageResultValidator"/> and <see cref="TriageCommentRenderer"/>.</summary>
public sealed class TriageValidatorAndRendererTests
{
    private static readonly string[] Repositories = ["checkout-api", "storefront-web"];

    // ── Validator ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_CauseFoundWithoutInvestigatedChecks_Fails()
    {
        var outcome = TriageResultValidator.Validate(Result(TriageVerdict.CauseFound) with { Investigated = [] }, Repositories, "checkout-api");

        outcome.Result.Should().BeNull();
        outcome.Error.Should().Contain("investigated");
    }

    [Fact]
    public void Validate_NoSummary_Fails()
    {
        TriageResultValidator.Validate(Result(TriageVerdict.NotABug) with { Summary = " " }, Repositories, "checkout-api")
            .Error.Should().Contain("summary");
    }

    [Fact]
    public void Validate_MoreThanTheMaximumDrafts_KeepsTheFirst()
    {
        var drafts = Enumerable.Range(1, TriageConstants.MaxDrafts + 2).Select(i => Draft($"Fix {i}")).ToList();

        var outcome = TriageResultValidator.Validate(Result(TriageVerdict.CauseFound) with { Drafts = drafts }, Repositories, "checkout-api");

        outcome.Result!.Drafts.Should().HaveCount(TriageConstants.MaxDrafts);
        outcome.Result.Drafts[0].Title.Should().Be("Fix 1");
        outcome.Warnings.Should().Contain(w => w.Contains("beyond the first"));
    }

    [Fact]
    public void Validate_DropsDraftsWithoutTitleOrBody_AndRenumbersTheRest()
    {
        var drafts = new[] { Draft("") , Draft("Keep") , Draft("No body") with { Body = "" } };

        var outcome = TriageResultValidator.Validate(Result(TriageVerdict.CauseFound) with { Drafts = drafts }, Repositories, "checkout-api");

        outcome.Result!.Drafts.Should().ContainSingle().Which.Should().Match<TriageDraft>(d => d.Title == "Keep" && d.Id == "d1");
        outcome.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public void Validate_UnknownRepository_GoesToTheExecutor_WithAWarningOnTheDraft()
    {
        var outcome = TriageResultValidator.Validate(
            Result(TriageVerdict.CauseFound) with { Drafts = [Draft("Fix") with { TargetRepository = "billing" }] },
            Repositories, "checkout-api");

        var draft = outcome.Result!.Drafts.Single();
        draft.TargetRepository.Should().Be("checkout-api");
        draft.Warning.Should().Contain("billing");
    }

    [Fact]
    public void Validate_RepositoryNameInOtherCase_IsCanonicalised()
    {
        var outcome = TriageResultValidator.Validate(
            Result(TriageVerdict.CauseFound) with { Drafts = [Draft("Fix") with { TargetRepository = "STOREFRONT-web" }] },
            Repositories, "checkout-api");

        outcome.Result!.Drafts.Single().Should().Match<TriageDraft>(d => d.TargetRepository == "storefront-web" && d.Warning == null);
    }

    [Fact]
    public void Validate_InconclusiveWithoutQuestions_AddsAGenericQuestion()
    {
        var outcome = TriageResultValidator.Validate(Result(TriageVerdict.Inconclusive), Repositories, "checkout-api");

        outcome.Result!.Questions.Should().ContainSingle().Which.Question.Should().Be(TriageResultValidator.GenericQuestion);
    }

    [Fact]
    public void Validate_CutsLongFieldsAndLongLists()
    {
        var result = Result(TriageVerdict.CauseFound) with
        {
            Summary = new string('s', TriageResultValidator.MaxSummaryChars + 50),
            Investigated = Enumerable.Range(1, TriageResultValidator.MaxListItems + 5)
                .Select(i => new TriageCheck { Check = $"c{i}", Where = "w", Result = "r" }).ToList(),
            Drafts = [Draft("Fix") with { Body = new string('b', TriageConstants.MaxDraftBodyChars + 10) }],
        };

        var validated = TriageResultValidator.Validate(result, Repositories, "checkout-api").Result!;

        validated.Summary.Length.Should().Be(TriageResultValidator.MaxSummaryChars + 1);
        validated.Investigated.Should().HaveCount(TriageResultValidator.MaxListItems);
        validated.Drafts.Single().Body.Should().EndWith("…(cut)");
    }

    // ── Renderer ─────────────────────────────────────────────────────────────

    [Fact]
    public void Render_StartsWithTheMarker_AndHasEverySection()
    {
        var comment = TriageCommentRenderer.Render(FullResult());

        comment.Should().StartWith(TriageConstants.CommentMarker);
        comment.Should().Contain("**Verdict:** cause found · **Confidence:** high · **Reproduced:** yes")
            .And.Contain("### From symptom to cause")
            .And.Contain("**Root cause:** Ack before update")
            .And.Contain("### Evidence")
            .And.Contain("### Hypotheses")
            .And.Contain("What I investigated (1 checks)")
            .And.Contain("### Not checked")
            .And.Contain("Proposed issues (1)")
            .And.Contain("Root fix → checkout-api: Ack after the update")
            .And.Contain("replace the status label with `agent:triage`");
    }

    [Fact]
    public void Render_SanitisesAgentValues_ButKeepsItsOwnMarkup()
    {
        var result = FullResult() with { Summary = "<script>alert(1)</script> ping @octocat" };

        var comment = TriageCommentRenderer.Render(result);

        comment.Should().NotContain("<script>");
        comment.Should().NotContain("@octocat");
        comment.Should().Contain("<details><summary>").And.Contain("</details>");
    }

    [Fact]
    public void Render_LinksOnlyHttpUrls()
    {
        var result = FullResult() with
        {
            Evidence =
            [
                new TriageEvidence { Id = "E1", Claim = "a", Source = "s", Link = "https://grafana.example/x" },
                new TriageEvidence { Id = "E2", Claim = "b", Source = "s", Link = "javascript:alert(1)" },
            ],
        };

        var comment = TriageCommentRenderer.Render(result);

        comment.Should().Contain("[link](https://grafana.example/x)").And.NotContain("javascript:");
    }

    [Fact]
    public void Render_EscapesPipesAndNewlinesInTableCells()
    {
        var result = FullResult() with
        {
            Evidence = [new TriageEvidence { Id = "E1", Claim = "a | b\nc", Source = "s", Query = "x | y" }],
        };

        var comment = TriageCommentRenderer.Render(result);

        comment.Should().Contain("a \\| b<br>c").And.Contain("`x \\| y`");
    }

    [Fact]
    public void Render_HugeResult_StaysWithinTheBudget_ByShorteningDraftsFirst()
    {
        var bigBody = new string('x', TriageConstants.MaxDraftBodyChars);
        var result = FullResult() with
        {
            Drafts = Enumerable.Range(1, TriageConstants.MaxDrafts)
                .Select(i => Draft($"Fix {i}") with { Body = bigBody }).ToList(),
            Investigated = Enumerable.Range(1, 60)
                .Select(i => new TriageCheck { Check = new string('c', 500), Where = "w", Result = new string('r', 500) }).ToList(),
        };

        var comment = TriageCommentRenderer.Render(result);

        comment.Length.Should().BeLessThanOrEqualTo(TriageConstants.MaxCommentChars);
        comment.Should().Contain("shortened — the full draft is in the app");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TriageResult Result(TriageVerdict verdict) => new()
    {
        Verdict = verdict,
        Summary = "summary",
        Investigated = [new TriageCheck { Check = "c", Where = "w", Result = "r" }],
    };

    private static TriageDraft Draft(string title) => new()
    {
        Id = "x",
        TargetRepository = "checkout-api",
        Title = title,
        Body = "## Problem\nbody",
    };

    private static TriageResult FullResult() => new()
    {
        Verdict = TriageVerdict.CauseFound,
        Confidence = TriageConfidence.High,
        Summary = "The worker acks before it updates the order.",
        Reproduced = true,
        CausalChain = [new TriageChainStep { Text = "Ack before update", Kind = TriageChainKind.RootCause, EvidenceIds = ["E1"] }],
        Hypotheses = [new TriageHypothesis { Id = "H1", Text = "Ack order", State = TriageHypothesisState.Confirmed }],
        Evidence = [new TriageEvidence { Id = "E1", Claim = "Ack first", Source = "code · api", Query = "Consumer.cs:58" }],
        Investigated = [new TriageCheck { Check = "Consumer", Where = "code · api", For = "H1", Result = "ack first", EvidenceIds = ["E1"] }],
        NotChecked = [new TriageGap { What = "Traces", Why = "no source" }],
        Drafts = [Draft("Ack after the update")],
    };
}
