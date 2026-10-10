using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>Triage records and results for the triage page tests.</summary>
internal static class TriageTestData
{
    public const string ShopProjectId = "6f1c2a9e-0000-0000-0000-00000000000a";
    public const string OtherProjectId = "6f1c2a9e-0000-0000-0000-00000000000b";
    public static readonly Guid TriageId = Guid.Parse("0f8e7d6c-0000-0000-0000-000000000001");

    public static TriageDraft Draft(string id, string title, string repository = "payments-worker") => new()
    {
        Id = id,
        Kind = TriageDraftKind.RootFix,
        TargetRepository = repository,
        Title = title,
        Body = "Move the ack after MarkPaidAsync.",
    };

    public static TriageResult CauseFound(params TriageDraft[] drafts) => new()
    {
        Verdict = TriageVerdict.CauseFound,
        Confidence = TriageConfidence.High,
        Summary = "The worker acknowledges the message before it marks the order as paid.",
        Impact = "37 orders since Oct 8.",
        Reproduced = true,
        CausalChain =
        [
            new TriageChainStep { Text = "Orders stay Pending", Kind = TriageChainKind.Symptom, EvidenceIds = ["E1"] },
            new TriageChainStep { Text = "Ack runs before the update", Kind = TriageChainKind.RootCause, EvidenceIds = ["E2"] },
        ],
        Hypotheses =
        [
            new TriageHypothesis { Id = "H1", Text = "Ack before update", State = TriageHypothesisState.Confirmed },
            new TriageHypothesis { Id = "H2", Text = "Lost webhook", State = TriageHypothesisState.RuledOut },
        ],
        Evidence =
        [
            new TriageEvidence { Id = "E1", Claim = "37 orders Pending", Source = "grafana · Loki", Query = "{app=\"payments-worker\"}", Link = "https://grafana.example/explore" },
            new TriageEvidence { Id = "E2", Claim = "Ack at line 58", Source = "code", Link = "javascript:alert(1)" },
        ],
        Investigated =
        [
            new TriageCheck { Check = "Captured payments without a paid line", Where = "grafana · Loki", For = "symptom", Result = "37 orders", EvidenceIds = ["E1"] },
            new TriageCheck { Check = "Pod restarts", Where = "grafana · Prometheus", For = "H1", Result = "9 OOM kills" },
            new TriageCheck { Check = "Message handling order", Where = "code · payments-worker", For = "H1", Result = "Ack before update", EvidenceIds = ["E2"] },
        ],
        NotChecked = [new TriageGap { What = "Traces", Why = "no trace source connected" }],
        Drafts = drafts,
    };

    public static TriageResult Inconclusive() => new()
    {
        Verdict = TriageVerdict.Inconclusive,
        Summary = "Logins are slow in the morning, but no single cause was found.",
        Investigated = [new TriageCheck { Check = "Login latency", Where = "grafana", Result = "slow 07:00-08:30" }],
        Questions =
        [
            new TriageQuestion { Question = "Is the login page slow, or the redirect back?", Why = "The handler stays under 200 ms." },
            new TriageQuestion { Question = "When are nightly deployments rolled out?" },
        ],
    };

    public static TriageAttempt Attempt(string workItemId, TriageResult? result = null, TriageAttemptOutcome? outcome = null, string? failure = null) => new()
    {
        WorkItemId = workItemId,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        CompletedAt = result is null && outcome is null ? null : DateTimeOffset.UtcNow.AddMinutes(-5),
        Result = result,
        Outcome = outcome,
        FailureReason = failure,
    };

    public static TriageRecord OperatorRecord(string projectId = ShopProjectId, params TriageAttempt[] attempts)
    {
        var latest = attempts.LastOrDefault(a => a.Result is not null);
        return new TriageRecord
        {
            Id = TriageId,
            ProjectId = projectId,
            Source = TriageSource.Operator,
            Title = "Orders stuck in Pending after payment",
            RequestedBy = "ben",
            Request = new TriageRequest
            {
                Title = "Orders stuck in Pending after payment",
                WhatHappened = "Customers paid but the order stays Pending.",
                Expected = "Paid within a minute.",
                Environment = "production",
            },
            Attempts = attempts,
            Drafts = latest?.Result?.Drafts.Select(d => new TriageEditableDraft { Current = d, Original = d }).ToList() ?? [],
            DraftsFromWorkItemId = latest?.WorkItemId,
            State = latest?.Result is { } r ? TriageStatusResolver.StateFor(r.Verdict) : TriageState.New,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
    }

    public static TriageDetail Detail(TriageRecord record, string? activeWorkItemId = null) => new()
    {
        Record = record,
        Status = TriageStatusResolver.Resolve(record, activeWorkItemId is not null),
        ActiveWorkItemId = activeWorkItemId,
    };

    public static TriageListItem ListItem(TriageStatus status, string title = "Checkout returns 502", TriageSource source = TriageSource.Operator,
        TriageVerdict? verdict = null, int drafts = 0, int created = 0, string? issueUrl = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = ShopProjectId,
        Source = source,
        Title = title,
        IssueIdentifier = source == TriageSource.Issue ? "431" : null,
        Status = status,
        Facts = new TriageListFacts
        {
            Verdict = verdict,
            Confidence = verdict is null ? null : TriageConfidence.High,
            DraftCount = drafts,
            CreatedIssueCount = created,
            AttemptCount = 1,
            RequestedBy = source == TriageSource.Operator ? "anna" : null,
            IssueUrl = issueUrl,
        },
        LatestTokens = 188_000,
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
    };
}
