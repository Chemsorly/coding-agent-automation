using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Api.Triage;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Api.IntegrationTests.Triage;

/// <summary>
/// The enricher's triage branch: the project-wide repository list, an operator triage's report in place of a
/// tracker issue, the newest comments, and the triage context.
/// </summary>
public sealed class AssignmentEnricherTriageTests
{
    private sealed class StubInfrastructure : DispatchInfrastructure
    {
        public DispatchCoreRequest? Captured { get; private set; }
        public DecompositionProjectContext? ProjectContext { get; set; } = new()
        {
            ProjectName = "Shop",
            Repositories =
            [
                new RepositoryTarget { TemplateName = "checkout-api", Description = "", RepoProviderId = "rp-api", IssueProviderId = "t-api" },
                new RepositoryTarget { TemplateName = "storefront-web", Description = "", RepoProviderId = "rp-web", IssueProviderId = "t-web" },
            ],
        };

        internal override Task<(IReadOnlyList<QualityGateConfiguration> QualityGates,
            IReadOnlyList<ReviewerConfiguration> Reviewers,
            IssueContextResult IssueContext,
            IReadOnlyList<ProviderConfig> ProviderConfigs,
            PipelineConfiguration Config,
            bool ForceRefresh,
            string? StalenessSignal,
            int RefreshCount)?> PrepareDispatchCoreAsync(DispatchCoreRequest request, CancellationToken ct)
        {
            Captured = request;
            var subject = request.PullRequest ?? new IssueDetail { Identifier = "431", Title = "From tracker", Description = "d", Labels = [] };
            var context = new IssueContextResult(subject, new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" }, [], null, false, null, 0);
            return Task.FromResult<(IReadOnlyList<QualityGateConfiguration>, IReadOnlyList<ReviewerConfiguration>, IssueContextResult,
                IReadOnlyList<ProviderConfig>, PipelineConfiguration, bool, string?, int)?>(
                ([], [], context, [], new PipelineConfiguration(), false, null, 0));
        }

        internal override Task<DecompositionProjectContext?> BuildProjectEpicContextAsync(
            PipelineProject project, Serilog.ILogger logger, CancellationToken ct) => Task.FromResult(ProjectContext);
    }

    private static readonly PipelineProject Project = new() { Id = Guid.NewGuid().ToString(), Name = "Shop" };

    private readonly StubInfrastructure _infra = new();
    private readonly Mock<ITriageStore> _store = new();

    private AssignmentEnricher Enricher(ITriageStore? store)
    {
        var profiles = new Mock<IAgentProfileStore>();
        profiles.Setup(p => p.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentProfile { Id = "p1", DisplayName = "p", AgentProviderConfigId = "a1", MatchLabels = ["dotnet"], Enabled = true }]);
        var projects = new Mock<IProjectStore>();
        return new AssignmentEnricher(
            _infra, profiles.Object, Mock.Of<IConsolidationJobPreparationService>(), projects.Object,
            new ConsolidationTemplateResolver(projects.Object), Serilog.Log.Logger, triageStore: store);
    }

    private static JobDistributionRequest Identity(string tracker, string identifier, string runId = "run-2") => new()
    {
        IssueIdentifier = identifier,
        IssueProviderConfigId = tracker,
        RepoProviderConfigId = "rp-api",
        InitiatedBy = InitiatedByConstants.Manual,
        TaskType = WorkItemTaskType.Triage,
        RunType = PipelineRunType.Triage,
        AgentSelector = "dotnet",
        TimeoutSeconds = 1800,
        RunId = runId,
    };

    private static TriageRecord OperatorTriage(params TriageAttempt[] attempts) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Project.Id,
        Source = TriageSource.Operator,
        Title = "Orders stuck in Pending",
        Request = new TriageRequest { Title = "Orders stuck in Pending", WhatHappened = "Paid but Pending", Expected = "Paid", Environment = "production" },
        RequestedBy = "ben",
        Attempts = attempts,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task OperatorTriage_GetsItsFormAsTheIssue_TheProjectRepositories_AndTheContext()
    {
        var triage = OperatorTriage(
            new TriageAttempt
            {
                WorkItemId = "run-1", StartedAt = DateTimeOffset.UtcNow,
                Result = new TriageResult { Verdict = TriageVerdict.Inconclusive, Summary = "No order id in logs" },
            },
            new TriageAttempt
            {
                WorkItemId = "run-2", StartedAt = DateTimeOffset.UtcNow,
                Feedback = new TriageFeedback { Text = "The worker logs ref=", Author = "ben", CreatedAt = DateTimeOffset.UtcNow },
            });
        _store.Setup(s => s.GetAsync(triage.Id, It.IsAny<CancellationToken>())).ReturnsAsync(triage);
        _store.Setup(s => s.ListRecentAsync(Project.Id, It.IsAny<DateTimeOffset>(), TriageConstants.MaxHistoryContext, triage.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await Enricher(_store.Object).EnrichAsync(
            Identity(TriageConstants.ProviderConfigId, TriageConstants.IssueIdentifierFor(triage.Id)), Project, CancellationToken.None);

        result.Should().NotBeNull();
        _infra.Captured!.PullRequest!.Description.Should().Contain("Paid but Pending").And.Contain("production");
        _infra.Captured.AdditionalRepoProviderIds.Should().BeEquivalentTo("rp-api", "rp-web");
        _infra.Captured.NewestComments.Should().BeTrue();
        result!.ProjectContext!.Repositories.Should().HaveCount(2);
        result.TriageContextMarkdown.Should().Contain("No order id in logs").And.Contain("The worker logs ref=");
        result.ProjectReviewers.Should().BeEmpty();
    }

    [Fact]
    public async Task TrackerTriage_ReadsTheTrackerIssue_AndFindsItsTriageByTheIssue()
    {
        _store.Setup(s => s.GetByIssueAsync("t-api", "431", It.IsAny<CancellationToken>())).ReturnsAsync((TriageRecord?)null);
        _store.Setup(s => s.ListRecentAsync(Project.Id, It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await Enricher(_store.Object).EnrichAsync(Identity("t-api", "431"), Project, CancellationToken.None);

        result.Should().NotBeNull();
        _infra.Captured!.PullRequest.Should().BeNull("a tracker triage's report comes from the tracker");
        result!.TriageContextMarkdown.Should().Contain("None: this is the first attempt.");
    }

    [Fact]
    public async Task OperatorTriageThatNoLongerExists_CannotBeEnriched()
    {
        _store.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((TriageRecord?)null);

        var result = await Enricher(_store.Object).EnrichAsync(
            Identity(TriageConstants.ProviderConfigId, TriageConstants.IssueIdentifierFor(Guid.NewGuid())), Project, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task WithoutRepositoryListOrStore_CannotBeEnriched()
    {
        (await Enricher(store: null).EnrichAsync(Identity("t-api", "431"), Project, CancellationToken.None)).Should().BeNull();

        _infra.ProjectContext = null;
        (await Enricher(_store.Object).EnrichAsync(Identity("t-api", "431"), Project, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ImplementationRuns_StillKeepTheOldestComments()
    {
        await Enricher(_store.Object).EnrichAsync(
            Identity("t-api", "431") with { TaskType = WorkItemTaskType.Implementation, RunType = PipelineRunType.Implementation },
            Project, CancellationToken.None);

        _infra.Captured!.NewestComments.Should().BeFalse();
    }

    // ── TriageContextRenderer ────────────────────────────────────────────────

    [Fact]
    public void Context_ForATracker_NamesOtherTriagesWithoutTheirSummaries()
    {
        var other = OperatorTriage(new TriageAttempt
        {
            WorkItemId = "x", StartedAt = DateTimeOffset.UtcNow,
            Result = new TriageResult { Verdict = TriageVerdict.CauseFound, Summary = "SECRET internal detail" },
        }) with { Title = "Login slow" };

        var tracker = TriageContextRenderer.RenderContext(null, "run-1", [other], reportToTracker: true);
        var app = TriageContextRenderer.RenderContext(null, "run-1", [other], reportToTracker: false);

        tracker.Should().Contain("Login slow").And.NotContain("SECRET internal detail").And.Contain("Do not quote them");
        app.Should().Contain("SECRET internal detail");
    }

    [Fact]
    public void ToIssueDetail_RendersTheFormAsTheIssueBody()
    {
        var triage = OperatorTriage() with
        {
            Request = new TriageRequest
            {
                Title = "t", WhatHappened = "502 on pay", Expected = "200", Where = "POST /pay", Version = "2.14.0",
                Links = "trace 4bf9", AlreadyTried = "restart", From = DateTimeOffset.Parse("2026-10-10T13:45:00Z"),
            },
        };

        var issue = TriageContextRenderer.ToIssueDetail(triage);

        issue.Identifier.Should().Be(TriageConstants.IssueIdentifierFor(triage.Id));
        issue.Description.Should().Contain("502 on pay").And.Contain("POST /pay").And.Contain("2.14.0")
            .And.Contain("trace 4bf9").And.Contain("restart").And.Contain("2026-10-10 13:45 UTC").And.Contain("still happening")
            .And.Contain("Reported by ben");
    }
}
