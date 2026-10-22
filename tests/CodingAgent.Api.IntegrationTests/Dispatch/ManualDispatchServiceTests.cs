using AwesomeAssertions;
using CodingAgent.Api.Browse;
using CodingAgent.Api.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Dispatch;

/// <summary>
/// Spec 051 2.3c: the drawers' manual dispatch moved from the web host's drawer services into the API.
/// These cover what the web's IssueDrawerServiceTests, PrReviewDrawerServiceTests and
/// EpicDrawerServiceTests covered at the provider level. The issue or pull request is now read from the
/// provider, not taken from the browser: the force-requeue of a blocking label, the epic-review and
/// dependency refusals, the epic phase and tracker, and the messages. The RunPage's re-dispatch, which
/// now queues the run, is covered at the end.
/// </summary>
public class ManualDispatchServiceTests
{
    private static readonly PipelineJobTemplate Template = new() { Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", BrainProviderId = "brain-1" };
    private static readonly ProviderConfig IssueTracker = new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Issues" };
    private static readonly ProviderConfig EpicTracker = new() { Id = "epic-ip", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Epics" };
    private static readonly ProviderConfig Repository = new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
    private static readonly PipelineProject Project = new() { Id = "8f0d5c1e-0000-0000-0000-000000000001", Name = "P", TemplateIds = ["t-1"] };

    private readonly Mock<IProjectStore> _projects = new();
    private readonly Mock<IProviderConfigStore> _configs = new();
    private readonly Mock<IProviderFactory> _factory = new();
    private readonly Mock<IDependencyChecker> _dependencies = new();
    private readonly Mock<IDispatchOrchestrationService> _orchestration = new();
    private readonly Mock<IIssueProvider> _issues = new();
    private readonly Mock<IIssueProvider> _epics = new();
    private readonly Mock<IRepositoryProvider> _repository = new();
    private readonly JobDistributionRequest _prepared = new()
    {
        IssueIdentifier = "42", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1", InitiatedBy = "manual",
        TaskType = WorkItemTaskType.Implementation, AgentSelector = "dotnet", TimeoutSeconds = 3600, ProviderConfigs = []
    };

    public ManualDispatchServiceTests()
    {
        _projects.Setup(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Project]);
        _configs.Setup(c => c.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>())).ReturnsAsync(IssueTracker);
        _configs.Setup(c => c.GetProviderConfigByIdAsync("epic-ip", ProviderKind.Issue, It.IsAny<CancellationToken>())).ReturnsAsync(EpicTracker);
        _configs.Setup(c => c.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>())).ReturnsAsync(Repository);
        _factory.Setup(f => f.CreateIssueProvider(IssueTracker)).Returns(_issues.Object);
        _factory.Setup(f => f.CreateIssueProvider(EpicTracker)).Returns(_epics.Object);
        _factory.Setup(f => f.CreateRepositoryProvider(Repository)).Returns(_repository.Object);
        _dependencies.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(), It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        _orchestration.Setup(o => o.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(_prepared);
        _orchestration.Setup(o => o.PrepareReviewDistributionRequestAsync(It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>())).ReturnsAsync(_prepared);
        _orchestration.Setup(o => o.PrepareDecompositionDistributionRequestAsync(It.IsAny<DecompositionDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(_prepared);
        Distributes(success: true);
    }

    private ManualDispatchService Service() => new(_projects.Object, _configs.Object, _factory.Object, _dependencies.Object, _orchestration.Object);

    private static BrowseTemplate Target(PipelineProject? project = null) => new(Template, project ?? Project);

    private void Distributes(bool success) =>
        _orchestration.Setup(o => o.DistributeAndFinalizeAsync(_prepared, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(success, Queued: success, success ? null : "unique index violation"));

    private static void HasIssue(Mock<IIssueProvider> tracker, string id, string title = "Issue", params string[] labels) =>
        tracker.Setup(t => t.GetIssueAsync(It.Is<IssueIdentifier>(i => i.Value == id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = id, Title = title, Description = $"Body of {id}", Labels = labels });

    private void ProviderGone(string id, ProviderKind kind) =>
        _configs.Setup(c => c.GetProviderConfigByIdAsync(id, kind, It.IsAny<CancellationToken>())).ReturnsAsync((ProviderConfig?)null);

    // ── Issues ──

    [Fact]
    public async Task DispatchIssueAsync_QueuesTheIssue_WithTheTemplatesProvidersAndProject()
    {
        HasIssue(_issues, "42");

        var message = await Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        message.Should().Be("⏳ Queued #42 — the job controller will start an agent pod for it");
        _orchestration.Verify(o => o.PrepareDistributionRequestAsync(It.Is<ImplementationDispatchOrchestrationRequest>(r =>
            r.IssueIdentifier.Value == "42" && r.IssueProviderId.Value == "ip-1" && r.RepoProviderId.Value == "rp-1"
            && r.BrainProviderId == "brain-1" && r.InitiatedBy == InitiatedByConstants.Manual && r.Project == Project), It.IsAny<CancellationToken>()), Times.Once);
        _orchestration.Verify(o => o.DistributeAndFinalizeAsync(_prepared, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchIssueAsync_ChecksTheDependencies_InTheTrackersDescription()
    {
        HasIssue(_issues, "42");
        _dependencies.Setup(d => d.CheckAsync(It.Is<IssueIdentifier>(i => i.Value == "42"), "Body of 42", _issues.Object, It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyCheckResult { IsReady = false, BlockedBy = [5], BlockedByUrls = ["https://example.test/issues/6"], TotalDependencies = 2 });

        var act = () => Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>())
            .WithMessage("Cannot dispatch — issue is blocked by open dependencies: #5, https://example.test/issues/6");
        _orchestration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchIssueAsync_WhenTheTemplatesProvidersAreGone_IsRefused()
    {
        ProviderGone("rp-1", ProviderKind.Repository);

        var act = () => Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("Template references providers that no longer exist.");
    }

    [Fact]
    public async Task DispatchIssueAsync_WhenTheIssueCannotBeRead_SaysWhy()
    {
        _issues.Setup(t => t.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Not Found"));

        var act = () => Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        (await act.Should().ThrowAsync<TemplateBrowseException>()).WithMessage("Failed to load issue #42: Not Found");
    }

    [Fact]
    public async Task DispatchIssueAsync_WhenThePreparationFails_IsRefused()
    {
        HasIssue(_issues, "42");
        _orchestration.Setup(o => o.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobDistributionRequest?)null);

        var act = () => Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("*orchestration preparation failed*");
    }

    [Fact]
    public async Task DispatchIssueAsync_OfATemplateWithoutAProject_DispatchesUnderAnUnnamedOne()
    {
        HasIssue(_issues, "42");

        await Service().DispatchIssueAsync(new BrowseTemplate(Template, null), "42", CancellationToken.None);

        _orchestration.Verify(o => o.PrepareDistributionRequestAsync(
            It.Is<ImplementationDispatchOrchestrationRequest>(r => r.Project.Id == "" && r.Project.Name == "Unknown"), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Manual dispatch of an issue with a blocking label is a force-requeue: agent:next is set and the
    /// blocking label removed before the work item is created. agent:epic-review is refused instead.
    /// </summary>
    [Theory]
    [InlineData(AgentLabels.Cancelled)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.NeedsRefinement)]
    [InlineData(AgentLabels.WontDo)]
    [InlineData(AgentLabels.Done)]
    public async Task DispatchIssueAsync_WithABlockingLabel_ClearsItAndSetsNext_BeforeDispatch(string blockingLabel)
    {
        HasIssue(_issues, "42", "Issue", blockingLabel);

        await Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        _issues.Verify(t => t.AddLabelAsync(It.Is<IssueIdentifier>(i => i.Value == "42"), AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once);
        _issues.Verify(t => t.RemoveLabelAsync(It.Is<IssueIdentifier>(i => i.Value == "42"), blockingLabel, It.IsAny<CancellationToken>()), Times.Once);
        _orchestration.Verify(o => o.DistributeAndFinalizeAsync(_prepared, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchIssueAsync_WithoutABlockingLabel_DoesNotTouchTheLabels()
    {
        HasIssue(_issues, "42", "Issue", AgentLabels.Next);

        await Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        _issues.Verify(t => t.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _issues.Verify(t => t.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchIssueAsync_AwaitingEpicReview_IsRefused_BeforeAnyLabelOrDispatch()
    {
        HasIssue(_issues, "99", "Epic awaiting approval", AgentLabels.EpicReview);

        var act = () => Service().DispatchIssueAsync(Target(), "99", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("*human approval*epic approval workflow*");
        _issues.Verify(t => t.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _issues.Verify(t => t.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _orchestration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchIssueAsync_WhenTheLabelCannotBeCleared_IsRefused_WithoutDispatching()
    {
        HasIssue(_issues, "42", "Issue", AgentLabels.Error);
        _issues.Setup(t => t.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate limited"));

        var act = () => Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage($"Could not clear blocking label '{AgentLabels.Error}' before dispatch — *");
        _orchestration.VerifyNoOtherCalls();
    }

    /// <summary>The label is already agent:next when the distribution fails; the scheduler picks the issue up later.</summary>
    [Fact]
    public async Task DispatchIssueAsync_WhenTheDistributionFails_AfterTheLabelWasCleared_IsRefused()
    {
        HasIssue(_issues, "42", "Issue", AgentLabels.Cancelled);
        Distributes(success: false);

        var act = () => Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("Could not dispatch — distribution failed.");
        _issues.Verify(t => t.AddLabelAsync(It.Is<IssueIdentifier>(i => i.Value == "42"), AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Pull requests ──

    [Fact]
    public async Task DispatchPullRequestAsync_QueuesTheOpenPullRequest_ForReview()
    {
        _repository.Setup(r => r.GetOpenPullRequestAsync(3363, It.IsAny<CancellationToken>())).ReturnsAsync(new PullRequestSummary
        {
            Number = 3363, Identifier = "3363", Title = "Fix it", Description = "Closes #42", Labels = [], BranchName = "agent/42-fix",
            TargetBranch = "main", Url = "https://example.test/pull/3363", IsDraft = false, Author = "bot"
        });

        var message = await Service().DispatchPullRequestAsync(Target(), 3363, CancellationToken.None);

        message.Should().Be("⏳ Queued PR #3363 for review — the job controller will start an agent pod for it");
        _orchestration.Verify(o => o.PrepareReviewDistributionRequestAsync(It.Is<ReviewDispatchRequest>(r =>
            r.PrNumber == 3363 && r.PrIdentifier == "3363" && r.PrBranchName == "agent/42-fix" && r.PrTitle == "Fix it"
            && r.PrUrl == "https://example.test/pull/3363" && r.PrTargetBranch == "main" && r.PrDescription == "Closes #42" && r.PrAuthor == "bot"
            && r.IssueProviderId.Value == "ip-1" && r.RepoProviderId.Value == "rp-1" && r.InitiatedBy == InitiatedByConstants.Manual), Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchPullRequestAsync_OfAPullRequestThatIsNotOpen_IsRefused()
    {
        _repository.Setup(r => r.GetOpenPullRequestAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync((PullRequestSummary?)null);

        var act = () => Service().DispatchPullRequestAsync(Target(), 7, CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("PR #7 is not open.");
        _orchestration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchPullRequestAsync_WhenTheRepositoryIsGone_IsRefused()
    {
        ProviderGone("rp-1", ProviderKind.Repository);

        var act = () => Service().DispatchPullRequestAsync(Target(), 7, CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("Template references providers that no longer exist.");
    }

    [Fact]
    public async Task DispatchPullRequestAsync_WhenTheDistributionFails_SaysItIsAlreadyBeingProcessed()
    {
        _repository.Setup(r => r.GetOpenPullRequestAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new PullRequestSummary
        {
            Number = 7, Identifier = "7", Title = "T", Description = "", Labels = [], BranchName = "b", TargetBranch = "main", Url = "u", IsDraft = false
        });
        Distributes(success: false);

        var act = () => Service().DispatchPullRequestAsync(Target(), 7, CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("PR #7 is already being processed or queued.");
    }

    // ── Epics ──

    [Theory]
    [InlineData(AgentLabels.Epic, PipelineRunType.DecompositionAnalysis, "analysis")]
    [InlineData(AgentLabels.EpicApproved, PipelineRunType.Decomposition, "decomposition")]
    public async Task DispatchEpicAsync_PicksThePhase_FromTheEpicsLabel(string label, PipelineRunType phase, string phaseLabel)
    {
        HasIssue(_issues, "e-1", "The epic", label);

        var message = await Service().DispatchEpicAsync(Target(), "e-1", CancellationToken.None);

        message.Should().Be($"⏳ Queued epic #e-1 for {phaseLabel} — the job controller will start an agent pod for it");
        _orchestration.Verify(o => o.PrepareDecompositionDistributionRequestAsync(It.Is<DecompositionDispatchOrchestrationRequest>(r =>
            r.PhaseType == phase && r.EpicIdentifier.Value == "e-1" && r.EpicTitle == "The epic" && r.IssueProviderId.Value == "ip-1"
            && r.RepoProviderId.Value == "rp-1" && r.BrainProviderId == "brain-1" && r.Project == Project), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The drawer lists the project's epic tracker, so the run is bound to it; the template executes the epic.</summary>
    [Fact]
    public async Task DispatchEpicAsync_OfAProjectWithAnEpicTracker_ReadsAndBindsTheEpicThere()
    {
        var project = Project with { EpicIssueProviderId = "epic-ip" };
        HasIssue(_epics, "e-1", "The epic", AgentLabels.Epic);

        await Service().DispatchEpicAsync(Target(project), "e-1", CancellationToken.None);

        _orchestration.Verify(o => o.PrepareDecompositionDistributionRequestAsync(It.Is<DecompositionDispatchOrchestrationRequest>(r =>
            r.IssueProviderId.Value == "epic-ip" && r.RepoProviderId.Value == "rp-1"), It.IsAny<CancellationToken>()), Times.Once);
        _issues.Verify(t => t.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchEpicAsync_WhenTheEpicTrackerIsGone_IsRefused()
    {
        var project = Project with { EpicIssueProviderId = "deleted-ip" };

        var act = () => Service().DispatchEpicAsync(Target(project), "e-1", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("The project's epic issue provider no longer exists.");
    }

    [Fact]
    public async Task DispatchEpicAsync_WhenTheTemplatesProvidersAreGone_IsRefused()
    {
        ProviderGone("ip-1", ProviderKind.Issue);

        var act = () => Service().DispatchEpicAsync(Target(), "e-1", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("Template references providers that no longer exist.");
    }

    [Fact]
    public async Task DispatchEpicAsync_WhenTheDistributionFails_IsRefused()
    {
        HasIssue(_issues, "e-1", "The epic", AgentLabels.Epic);
        Distributes(success: false);

        var act = () => Service().DispatchEpicAsync(Target(), "e-1", CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("Could not dispatch — epic is already being processed or queued, or no agents are available.");
    }

    // ── Spec 053 req 2.9: a refused work item shows the API's reason in the drawer ──

    private void IsRefused() =>
        _orchestration.Setup(o => o.DistributeAndFinalizeAsync(_prepared, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(false, Queued: false, AgentRouteRefusalReason.NoRouteText) { IsRefused = true });

    [Fact]
    public async Task Dispatch_ARefusedIssuePullRequestOrEpic_ShowsTheReason()
    {
        HasIssue(_issues, "42", "Issue");
        HasIssue(_issues, "e-1", "The epic", AgentLabels.Epic);
        _repository.Setup(r => r.GetOpenPullRequestAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new PullRequestSummary
        {
            Number = 7, Identifier = "7", Title = "T", Description = "", Labels = [], BranchName = "b", TargetBranch = "main", Url = "u", IsDraft = false
        });
        IsRefused();

        var issue = () => Service().DispatchIssueAsync(Target(), "42", CancellationToken.None);
        var pullRequest = () => Service().DispatchPullRequestAsync(Target(), 7, CancellationToken.None);
        var epic = () => Service().DispatchEpicAsync(Target(), "e-1", CancellationToken.None);

        (await issue.Should().ThrowAsync<DispatchRefusedException>()).WithMessage(AgentRouteRefusalReason.NoRouteText);
        (await pullRequest.Should().ThrowAsync<DispatchRefusedException>()).WithMessage(AgentRouteRefusalReason.NoRouteText);
        (await epic.Should().ThrowAsync<DispatchRefusedException>()).WithMessage(AgentRouteRefusalReason.NoRouteText);
    }

    // ── Re-dispatch ──

    private static PipelineRunSummary FailedRun(
        string? projectId = "8F0D5C1E-0000-0000-0000-000000000001", PipelineStep finalStep = PipelineStep.Failed,
        PipelineRunType runType = PipelineRunType.Implementation, string? issueProvider = "ip-1") => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Issue 42",
        FinalStep = finalStep,
        RunType = runType,
        IssueProviderConfigId = issueProvider,
        RepoProviderConfigId = "rp-1",
        BrainProviderConfigId = "brain-1",
        PipelineProviderConfigId = "ci-1",
        ProjectId = projectId,
    };

    [Fact]
    public async Task RedispatchRunAsync_QueuesTheRunsIssue_WithItsProvidersAndProject()
    {
        var message = await Service().RedispatchRunAsync(FailedRun(), CancellationToken.None);

        message.Should().Be("⏳ Queued #42 — the job controller will start an agent pod for it");
        _orchestration.Verify(o => o.PrepareDistributionRequestAsync(It.Is<ImplementationDispatchOrchestrationRequest>(r =>
            r.IssueIdentifier.Value == "42" && r.IssueProviderId.Value == "ip-1" && r.RepoProviderId.Value == "rp-1"
            && r.BrainProviderId == "brain-1" && r.PipelineProviderId == "ci-1" && r.InitiatedBy == InitiatedByConstants.Manual
            && r.Project == Project), It.IsAny<CancellationToken>()), Times.Once);
        _orchestration.Verify(o => o.DistributeAndFinalizeAsync(_prepared, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RedispatchRunAsync_WhenRefused_ShowsTheReason()
    {
        IsRefused();

        var act = () => Service().RedispatchRunAsync(FailedRun(), CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage(AgentRouteRefusalReason.NoRouteText);
    }

    [Fact]
    public async Task RedispatchRunAsync_OfARunWithoutAProject_QueuesUnderAnUnnamedOne()
    {
        await Service().RedispatchRunAsync(FailedRun(projectId: null), CancellationToken.None);

        _orchestration.Verify(o => o.PrepareDistributionRequestAsync(
            It.Is<ImplementationDispatchOrchestrationRequest>(r => r.Project.Id == "" && r.Project.Name == "Unknown"), It.IsAny<CancellationToken>()), Times.Once);
        _projects.Verify(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(PipelineStep.Completed, PipelineRunType.Implementation, "ip-1")]
    [InlineData(PipelineStep.Failed, PipelineRunType.Review, "ip-1")]
    [InlineData(PipelineStep.Failed, PipelineRunType.Implementation, null)]
    public async Task RedispatchRunAsync_OfARunThatCannotBeRedispatched_IsRefused(PipelineStep finalStep, PipelineRunType runType, string? issueProvider)
    {
        var run = FailedRun(finalStep: finalStep, runType: runType, issueProvider: issueProvider);

        var act = () => Service().RedispatchRunAsync(run, CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("Only a failed, cancelled or conflicted implementation run can be re-dispatched.");
        _orchestration.VerifyNoOtherCalls();
    }

    /// <summary>The drawers count a live item as queued; a re-dispatch created no run, so it is refused.</summary>
    [Fact]
    public async Task RedispatchRunAsync_WhenTheIssueAlreadyHasLiveWork_IsRefused()
    {
        _orchestration.Setup(o => o.DistributeAndFinalizeAsync(_prepared, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, true, null) { AlreadyExists = true });

        var act = () => Service().RedispatchRunAsync(FailedRun(), CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("Issue #42 already has queued or running work.");
    }

    [Fact]
    public async Task RedispatchRunAsync_WhenThePreparationFails_IsRefused()
    {
        _orchestration.Setup(o => o.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobDistributionRequest?)null);

        var act = () => Service().RedispatchRunAsync(FailedRun(), CancellationToken.None);

        (await act.Should().ThrowAsync<DispatchRefusedException>()).WithMessage("*orchestration preparation failed*");
    }
}
