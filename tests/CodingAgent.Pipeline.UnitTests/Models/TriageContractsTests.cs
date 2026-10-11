using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for the triage contracts: labels, constants, the status resolver, list facts and the JSON shape.
/// </summary>
public sealed class TriageContractsTests
{
    // ── Labels ───────────────────────────────────────────────────────────────

    [Fact]
    public void TriageLabels_AreDefined_SoTheyAreCreatedWithTheOthers()
    {
        AgentLabels.All.Should().Contain([AgentLabels.Triage, AgentLabels.TriageReview]);
        AgentLabels.SwapTargets.Should().Contain([AgentLabels.Triage, AgentLabels.TriageReview]);
    }

    [Fact]
    public void TriageReview_IsTerminalAndDispatchIneligible_LikeEpicReview()
    {
        AgentLabels.TerminalLabels.Should().Contain(AgentLabels.TriageReview);
        AgentLabels.DispatchIneligibleLabels.Should().Contain(AgentLabels.TriageReview);
    }

    [Fact]
    public void TriageLabels_AreNotGatedAndNotHousekeepingActive()
    {
        // Drafts are approved in the app, so no triage label is a human-approval label; triage has no branches.
        AgentLabels.DispatchGatedLabels.Should().NotContain([AgentLabels.Triage, AgentLabels.TriageReview]);
        AgentLabels.HousekeepingActiveLabels.Should().NotContain([AgentLabels.Triage, AgentLabels.TriageReview]);
    }

    [Fact]
    public void DualLabelPrecedence_ListsTriageLabels_ReviewAheadOfStart()
    {
        var precedence = AgentLabels.DualLabelResolutionPrecedence.ToList();

        precedence.Should().Contain([AgentLabels.Triage, AgentLabels.TriageReview]);
        precedence.IndexOf(AgentLabels.TriageReview).Should().BeLessThan(precedence.IndexOf(AgentLabels.InProgress));
        precedence.IndexOf(AgentLabels.Triage).Should().BeGreaterThan(precedence.IndexOf(AgentLabels.InProgress));
    }

    [Theory]
    [InlineData(AgentLabels.Triage, AgentLabels.InProgress)]
    [InlineData(AgentLabels.InProgress, AgentLabels.TriageReview)]
    [InlineData(AgentLabels.InProgress, AgentLabels.Triage)]
    [InlineData(AgentLabels.TriageReview, AgentLabels.Triage)]
    [InlineData(AgentLabels.TriageReview, AgentLabels.Done)]
    [InlineData(AgentLabels.TriageReview, AgentLabels.WontDo)]
    [InlineData(AgentLabels.Error, AgentLabels.Triage)]
    public void LabelStateMachine_AllowsTheTriageTransitions(string from, string to)
    {
        LabelStateMachine.IsValidTransition(from, to).Should().BeTrue();
    }

    [Fact]
    public void LabelStateMachine_DoesNotLetATriagedIssueJumpToImplementation()
    {
        LabelStateMachine.IsValidTransition(AgentLabels.TriageReview, AgentLabels.Next).Should().BeFalse();
    }

    // ── Constants ────────────────────────────────────────────────────────────

    [Fact]
    public void OperatorTriageIdentifier_RoundTrips()
    {
        var id = Guid.NewGuid();

        var identifier = TriageConstants.IssueIdentifierFor(id);

        identifier.Should().Be($"triage:{id:D}");
        TriageConstants.TryParseTriageId(identifier).Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("triage:")]
    [InlineData("triage:not-a-guid")]
    [InlineData("RefactoringDetection:00000000-0000-0000-0000-000000000000")]
    public void TryParseTriageId_OtherIdentifiers_ReturnNull(string? identifier)
    {
        TriageConstants.TryParseTriageId(identifier).Should().BeNull();
    }

    [Fact]
    public void IsOperatorTriage_MatchesOnlyTheSentinel()
    {
        TriageConstants.IsOperatorTriage("triage").Should().BeTrue();
        TriageConstants.IsOperatorTriage("Triage").Should().BeFalse();
        TriageConstants.IsOperatorTriage(ConsolidationConstants.ProviderConfigId).Should().BeFalse();
        TriageConstants.IsOperatorTriage(null).Should().BeFalse();
    }

    // ── Status resolver ──────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ActiveAttempt_IsInvestigating_WhateverTheState()
    {
        var record = Record(TriageState.IssuesCreated, Attempt(result: Result(TriageVerdict.CauseFound)));

        TriageStatusResolver.Resolve(record, isActive: true).Should().Be(TriageStatus.Investigating);
    }

    [Fact]
    public void Resolve_NoAttempts_IsNew()
    {
        TriageStatusResolver.Resolve(Record(TriageState.New), isActive: false).Should().Be(TriageStatus.New);
    }

    [Theory]
    [InlineData(TriageVerdict.CauseFound, TriageStatus.NeedsReview)]
    [InlineData(TriageVerdict.Inconclusive, TriageStatus.NeedsInput)]
    [InlineData(TriageVerdict.NotABug, TriageStatus.NotABug)]
    [InlineData(TriageVerdict.Duplicate, TriageStatus.Duplicate)]
    public void Resolve_LastAttemptWithResult_FollowsTheVerdict(TriageVerdict verdict, TriageStatus expected)
    {
        var record = Record(TriageStatusResolver.StateFor(verdict), Attempt(result: Result(verdict)));

        TriageStatusResolver.Resolve(record, isActive: false).Should().Be(expected);
    }

    [Fact]
    public void Resolve_LastAttemptEndedWithoutResult_IsFailed_EvenAfterAnEarlierResult()
    {
        var record = Record(TriageState.NeedsReview,
            Attempt(result: Result(TriageVerdict.CauseFound)),
            Attempt());

        TriageStatusResolver.Resolve(record, isActive: false).Should().Be(TriageStatus.Failed);
    }

    [Fact]
    public void Resolve_LastAttemptCancelled_IsCancelled()
    {
        var record = Record(TriageState.New, Attempt(outcome: TriageAttemptOutcome.Cancelled));

        TriageStatusResolver.Resolve(record, isActive: false).Should().Be(TriageStatus.Cancelled);
    }

    [Theory]
    [InlineData(TriageState.IssuesCreated, TriageStatus.IssuesCreated)]
    [InlineData(TriageState.Dismissed, TriageStatus.Dismissed)]
    public void Resolve_PersonsDecision_WinsOverAFailedLaterAttempt(TriageState state, TriageStatus expected)
    {
        var record = Record(state, Attempt(result: Result(TriageVerdict.CauseFound)), Attempt(outcome: TriageAttemptOutcome.Failed));

        TriageStatusResolver.Resolve(record, isActive: false).Should().Be(expected);
    }

    [Theory]
    [InlineData(TriageStatus.NeedsReview, TriageListTab.NeedYou, true)]
    [InlineData(TriageStatus.NeedsInput, TriageListTab.NeedYou, true)]
    [InlineData(TriageStatus.Investigating, TriageListTab.NeedYou, false)]
    [InlineData(TriageStatus.Investigating, TriageListTab.Investigating, true)]
    [InlineData(TriageStatus.Failed, TriageListTab.Done, true)]
    [InlineData(TriageStatus.IssuesCreated, TriageListTab.Done, true)]
    [InlineData(TriageStatus.NeedsReview, TriageListTab.Done, false)]
    [InlineData(TriageStatus.NeedsReview, TriageListTab.All, true)]
    public void IsInTab_SortsStatusesIntoTabs(TriageStatus status, TriageListTab tab, bool expected)
    {
        TriageStatusResolver.IsInTab(status, tab).Should().Be(expected);
    }

    // ── List facts ───────────────────────────────────────────────────────────

    [Fact]
    public void ListFacts_TakeTheLatestResultAndTheLastAttempt()
    {
        var record = Record(TriageState.NeedsReview,
            Attempt("w1", Result(TriageVerdict.CauseFound, TriageConfidence.High))) with
        {
            Drafts = [Editable("d1"), Editable("d2")],
            CreatedIssues = [Created("d1")],
            RequestedBy = "anna",
        };

        var facts = TriageListFacts.From(record);

        facts.Verdict.Should().Be(TriageVerdict.CauseFound);
        facts.Confidence.Should().Be(TriageConfidence.High);
        facts.DraftCount.Should().Be(2);
        facts.CreatedIssueCount.Should().Be(1);
        facts.AttemptCount.Should().Be(1);
        facts.LastAttemptHasResult.Should().BeTrue();
        facts.LastWorkItemId.Should().Be("w1");
        facts.RequestedBy.Should().Be("anna");
    }

    // ── JSON ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Result_ReadsTheAgentsSnakeCaseEnums()
    {
        const string json = """
            {
              "verdict": "cause_found",
              "confidence": "medium",
              "summary": "Acked before the update.",
              "causalChain": [ { "text": "Ack first", "kind": "root_cause", "evidenceIds": ["E1"] } ],
              "hypotheses": [ { "id": "H1", "text": "Ack order", "state": "ruled_out" } ],
              "drafts": [ { "id": "d1", "kind": "root_fix", "targetRepository": "api", "title": "Fix", "body": "Body" } ]
            }
            """;

        var result = JsonSerializer.Deserialize<TriageResult>(json, PipelineJsonOptions.Lenient)!;

        result.Verdict.Should().Be(TriageVerdict.CauseFound);
        result.Confidence.Should().Be(TriageConfidence.Medium);
        result.CausalChain.Single().Kind.Should().Be(TriageChainKind.RootCause);
        result.Hypotheses.Single().State.Should().Be(TriageHypothesisState.RuledOut);
        result.Drafts.Single().Kind.Should().Be(TriageDraftKind.RootFix);
    }

    [Fact]
    public void Record_RoundTripsThroughTheStorageOptions()
    {
        var record = Record(TriageState.NeedsReview, Attempt("w1", Result(TriageVerdict.CauseFound))) with
        {
            Drafts = [Editable("d1") with { EditedBy = "ben", EditedAt = DateTimeOffset.UnixEpoch }],
            Request = new TriageRequest { Title = "t", WhatHappened = "w", Expected = "e" },
        };

        var json = JsonSerializer.Serialize(record, PipelineJsonOptions.Default);
        var back = JsonSerializer.Deserialize<TriageRecord>(json, PipelineJsonOptions.Lenient)!;

        json.Should().Contain("\"needs_review\"").And.Contain("\"cause_found\"");
        back.Should().BeEquivalentTo(record);
        back.Drafts.Single().Edited.Should().BeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TriageRecord Record(TriageState state, params TriageAttempt[] attempts) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = "p1",
        Source = TriageSource.Operator,
        Title = "Orders stuck",
        State = state,
        Attempts = attempts,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static TriageAttempt Attempt(
        string workItemId = "w", TriageResult? result = null, TriageAttemptOutcome? outcome = null) => new()
    {
        WorkItemId = workItemId,
        StartedAt = DateTimeOffset.UnixEpoch,
        Result = result,
        Outcome = outcome,
    };

    private static TriageResult Result(TriageVerdict verdict, TriageConfidence? confidence = null) => new()
    {
        Verdict = verdict,
        Confidence = confidence,
        Summary = "summary",
    };

    private static TriageEditableDraft Editable(string id)
    {
        var draft = new TriageDraft { Id = id, TargetRepository = "api", Title = "t", Body = "b" };
        return new TriageEditableDraft { Current = draft, Original = draft };
    }

    private static TriageCreatedIssue Created(string draftId) => new()
    {
        DraftId = draftId,
        Repository = "api",
        IssueProviderConfigId = "tracker",
        Identifier = "12",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}
