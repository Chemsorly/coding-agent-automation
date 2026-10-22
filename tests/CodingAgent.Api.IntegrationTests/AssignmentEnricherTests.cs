using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Moq;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// <see cref="AssignmentEnricher.EnrichAsync"/> (spec 053 design "Claim", DD6): the claim runs on the route pinned at
/// creation, or for an item queued before agent routing on the current routing; the profile must exist and be enabled,
/// its job template must still be the one the item was queued for and must have a tool, and the built agent config's
/// Id must be unique. Each of those is <see cref="EnrichmentResult.Refused"/>; every other reason to serve no assignment
/// is <see cref="EnrichmentResult.Unavailable"/>. The repository template's stack labels pick the quality gates and
/// reviewers. Also the fresh configuration of an assignment, and the consolidation path.
/// </summary>
public sealed class AssignmentEnricherTests
{
    private const string ProfileId = "profile-1";
    private const string ProfileName = "Test Profile";
    private const string JobTemplate = AgentRoutingTestData.DefaultJobTemplate;

    /// <summary>The route the API pinned on the work item when it created it.</summary>
    private static readonly AgentRouteSnapshot PinnedRoute =
        new(ProfileId, ProfileName, JobTemplateProviderType.Kiro, "opus", "high", AgentRouteSource.GlobalDefault);

    // ── Controllable DispatchInfrastructure stub ───────────────────────────────────

    /// <summary>
    /// Subclass of DispatchInfrastructure that overrides the virtual PrepareDispatchCoreAsync
    /// without constructing real dependencies. This is the test seam.
    /// </summary>
    private sealed class StubDispatchInfrastructure : DispatchInfrastructure
    {
        private readonly Func<DispatchCoreRequest, CancellationToken, Task<CoreResult?>> _handler;

        public DispatchCoreRequest? CapturedRequest { get; private set; }

        public StubDispatchInfrastructure(Func<DispatchCoreRequest, CancellationToken, Task<CoreResult?>> handler)
            : base()  // protected no-arg ctor; real deps unused (PrepareDispatchCoreAsync is overridden)
        {
            _handler = handler;
        }

        internal override async Task<(IReadOnlyList<QualityGateConfiguration> QualityGates,
            IReadOnlyList<ReviewerConfiguration> Reviewers,
            DispatchInfrastructure.IssueContextResult IssueContext,
            IReadOnlyList<ProviderConfig> ProviderConfigs,
            PipelineConfiguration Config,
            bool ForceRefresh,
            string? StalenessSignal,
            int RefreshCount)?> PrepareDispatchCoreAsync(DispatchCoreRequest request, CancellationToken ct)
        {
            CapturedRequest = request;
            if (await _handler(request, ct) is not { } r)
                return null;
            return (r.QualityGates, r.Reviewers, r.IssueContext, r.ProviderConfigs, r.Config, false, null, 0);
        }

        /// <summary>The repository list <see cref="BuildProjectEpicContextAsync"/> returns (null: none usable).</summary>
        public DecompositionProjectContext? ProjectEpicContext { get; set; }

        public int ProjectEpicContextCalls { get; private set; }

        internal override Task<DecompositionProjectContext?> BuildProjectEpicContextAsync(
            PipelineProject project, Serilog.ILogger logger, CancellationToken ct)
        {
            ProjectEpicContextCalls++;
            return Task.FromResult(ProjectEpicContext);
        }

        /// <summary>The other repositories <see cref="BuildProjectReviewRepositoriesAsync"/> returns.</summary>
        public IReadOnlyList<RepositoryTarget> ProjectReviewRepositories { get; set; } = [];

        public string? ProjectReviewOwnRepository { get; private set; }

        public int ProjectReviewRepositoriesCalls { get; private set; }

        internal override Task<IReadOnlyList<RepositoryTarget>> BuildProjectReviewRepositoriesAsync(
            PipelineProject project, string ownRepoProviderId, Serilog.ILogger logger, CancellationToken ct)
        {
            ProjectReviewRepositoriesCalls++;
            ProjectReviewOwnRepository = ownRepoProviderId;
            return Task.FromResult(ProjectReviewRepositories);
        }
    }

    /// <summary>What the stub's <see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/> returns.</summary>
    private sealed record CoreResult(
        IReadOnlyList<QualityGateConfiguration> QualityGates,
        IReadOnlyList<ReviewerConfiguration> Reviewers,
        DispatchInfrastructure.IssueContextResult IssueContext,
        IReadOnlyList<ProviderConfig> ProviderConfigs,
        PipelineConfiguration Config);

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static JobDistributionRequest MakeIdentity() => new()
    {
        IssueIdentifier = new IssueIdentifier("owner/repo#42"),
        IssueProviderConfigId = "issue-prov-1",
        RepoProviderConfigId = "repo-prov-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = JobTemplate,
        ResolvedProfileId = ProfileId,
        Route = PinnedRoute,
        TimeoutSeconds = 3600,
        PayloadSchemaVersion = 1,
    };

    private static PipelineProject MakeProject() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Test Project",
        SteeringContent = "project-steering",
    };

    private static AgentProfile MakeProfile(
        string id = ProfileId, string jobTemplate = JobTemplate, bool enabled = true,
        IReadOnlyList<McpServerConfig>? mcpServers = null) =>
        AgentRoutingTestData.Profile(id, jobTemplate, id == ProfileId ? ProfileName : $"{id} profile", enabled,
            new Dictionary<string, string> { [ProviderSettingKeys.AgentName] = "dev" }, mcpServers);

    private static DispatchInfrastructure.IssueContextResult MakeIssueContext() =>
        new(
            IssueDetail: new IssueDetail
            {
                Identifier = "owner/repo#42",
                Title = "Fix the crash",
                Description = "Bug description",
                Labels = []
            },
            ParsedIssue: new ParsedIssue
            {
                RequirementsSection = "Fix the crash",
                AcceptanceCriteria = []
            },
            IssueComments: [],
            ExistingAnalysis: null,
            ForceRefreshAnalysis: false,
            StalenessSignal: null,
            RefreshCount: 0);

    private static IReadOnlyList<ProviderConfig> MakeProviderConfigs(string steeringContent = "fresh-repo-steering") =>
    [
        new ProviderConfig
        {
            Id = "repo-prov-1",
            Kind = ProviderKind.Repository,
            DisplayName = "Test Repo",
            ProviderType = "GitHub",
            SteeringContent = steeringContent
        }
    ];

    /// <summary>The core result, with the agent config the enricher passed added as given, as the real infra does.</summary>
    private static CoreResult MakeCoreResult(DispatchCoreRequest request, IReadOnlyList<ProviderConfig>? providerConfigs = null) =>
        new([], [], MakeIssueContext(),
            [.. providerConfigs ?? MakeProviderConfigs(), .. request.AgentConfig is { } agent ? new[] { agent } : []],
            new PipelineConfiguration());

    /// <summary>A consolidation preparer that fails the test if it is called (implementation-path tests).</summary>
    private static Mock<IConsolidationJobPreparationService> MakeNoOpConsolidationPreparer()
    {
        var mock = new Mock<IConsolidationJobPreparationService>();
        mock.Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<ProviderConfig?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "ConsolidationJobPreparationService.PrepareAsync was not expected to be called by this test."));
        return mock;
    }

    /// <summary>The enricher under test with its stubs; every part can be replaced per test.</summary>
    private sealed class Setup
    {
        public IReadOnlyList<AgentProfile> Profiles { get; init; } = [MakeProfile()];

        /// <summary>All templates: the repository template (stack labels, repository rows) and consolidation templates.</summary>
        public IReadOnlyList<PipelineJobTemplate> Templates { get; init; } = [];

        /// <summary>The project's templates (the decomposition tracker check).</summary>
        public IReadOnlyList<PipelineJobTemplate> ProjectTemplates { get; init; } = [];

        /// <summary>The stored routing table; null stores none.</summary>
        public RoutingTable? Routing { get; init; }

        public JobTemplateStore JobTemplates { get; init; } = AgentRoutingTestData.JobTemplates();

        public StubDispatchInfrastructure Infra { get; init; } =
            new((request, _) => Task.FromResult<CoreResult?>(MakeCoreResult(request)));

        public Mock<IConsolidationJobPreparationService> Preparer { get; init; } = MakeNoOpConsolidationPreparer();

        public IPipelineRunHistoryService? RunHistory { get; init; }

        public Serilog.ILogger Logger { get; init; } = Serilog.Log.Logger;

        public AssignmentEnricher Build()
        {
            var profileStore = new Mock<IAgentProfileStore>();
            profileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Profiles);
            var projectStore = new Mock<IProjectStore>();
            projectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Templates);
            projectStore.Setup(s => s.LoadTemplatesForProjectAsync(It.IsAny<ProjectId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProjectTemplates);
            var db = new AgentRoutingTestSupport.InMemoryFactory(Guid.NewGuid().ToString());
            if (Routing is not null)
                AgentRoutingTestSupport.SeedRouting(db, Routing);

            return new AssignmentEnricher(
                Infra, profileStore.Object, Preparer.Object, projectStore.Object,
                new ConsolidationTemplateResolver(projectStore.Object), new AgentRoutingStore(db), JobTemplates, Logger,
                new ConsolidationRunContextSources(RunHistory));
        }
    }

    private static JobDistributionRequest Enriched(EnrichmentResult result) =>
        result.Should().BeOfType<EnrichmentResult.Enriched>().Subject.Request;

    private static EnrichmentResult.Refused Refused(EnrichmentResult result, string reason)
    {
        var refused = result.Should().BeOfType<EnrichmentResult.Refused>().Subject;
        refused.Reason.Should().Be(reason);
        return refused;
    }

    private static PipelineJobTemplate RepoTemplate(
        IReadOnlyList<string>? stackLabels = null, IReadOnlyDictionary<WorkItemTaskType, AgentRoute>? rows = null) =>
        new()
        {
            Id = "t-repo", Name = "repo", IssueProviderId = "issue-prov-1", RepoProviderId = "repo-prov-1", Enabled = true,
            RequiredLabels = stackLabels, AgentRoutes = rows,
        };

    // ── Success path ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_Success_ReturnsEnrichedRequestWithFreshProviderConfigs()
    {
        var enricher = new Setup().Build();

        var result = Enriched(await enricher.EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None));

        result.ProviderConfigs.Should().Contain(c => c.Id == "repo-prov-1", "fresh ProviderConfigs come from PrepareDispatchCoreAsync");
    }

    [Fact]
    public async Task EnrichAsync_PinnedRoute_BuildsTheAgentConfigFromTheProfileAndTheRoute()
    {
        var setup = new Setup();

        var result = Enriched(await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None));

        var agentConfig = setup.Infra.CapturedRequest!.AgentConfig;
        agentConfig.Should().NotBeNull("spec 053 DD5: the agent config is built from the profile and its route");
        agentConfig!.Should().BeEquivalentTo(new
        {
            Id = ProfileId,
            Kind = ProviderKind.Agent,
            ProviderType = AgentToolCatalog.Kiro.ProviderConfigType,
        });
        agentConfig.Settings.Should().Contain(ProviderSettingKeys.Model, "opus")
            .And.Contain(ProviderSettingKeys.Effort, "high")
            .And.Contain(ProviderSettingKeys.AgentName, "dev");
        result.ResolvedProfileId.Should().Be(ProfileId);
        result.AgentProviderConfigId.Should().Be(ProfileId, "the agent looks its config up by this Id");
        result.Route.Should().Be(PinnedRoute);
    }

    [Fact]
    public async Task EnrichAsync_PinnedRoute_IsNotResolvedAgain_WhenTheRoutingChanged()
    {
        // The default row now names another profile; the item keeps the route it was created with.
        var enricher = new Setup
        {
            Profiles = [MakeProfile(), MakeProfile("profile-2")],
            Routing = AgentRoutingTestData.Routing("profile-2", "sonnet"),
        }.Build();

        var result = Enriched(await enricher.EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None));

        result.ResolvedProfileId.Should().Be(ProfileId);
        result.Route.Should().Be(PinnedRoute);
    }

    [Fact]
    public async Task EnrichAsync_ItemQueuedBeforeAgentRouting_IsRoutedAtClaim()
    {
        // Spec 053 req 4.6: an item stored without a route runs on the current routing, recorded as source "claim".
        var setup = new Setup { Routing = AgentRoutingTestData.Routing(ProfileId, "sonnet", "low") };
        var identity = MakeIdentity() with { Route = null, ResolvedProfileId = null };

        var result = Enriched(await setup.Build().EnrichAsync(identity, MakeProject(), CancellationToken.None));

        result.Route.Should().Be(new AgentRouteSnapshot(
            ProfileId, ProfileName, JobTemplateProviderType.Kiro, "sonnet", "low", AgentRouteSource.Claim));
        result.ResolvedProfileId.Should().Be(ProfileId);
        setup.Infra.CapturedRequest!.AgentConfig!.Settings.Should().Contain(ProviderSettingKeys.Model, "sonnet");
    }

    [Fact]
    public async Task EnrichAsync_RepositoryRowsApply_ToAnItemRoutedAtClaim()
    {
        var setup = new Setup
        {
            Profiles = [MakeProfile(), MakeProfile("profile-2")],
            Routing = AgentRoutingTestData.Routing(ProfileId),
            Templates = [RepoTemplate(rows: new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Implementation] = AgentRoutingTestData.Route("profile-2", "row-model"),
            })],
        };
        var identity = MakeIdentity() with { Route = null };

        var result = Enriched(await setup.Build().EnrichAsync(identity, MakeProject(), CancellationToken.None));

        result.ResolvedProfileId.Should().Be("profile-2");
        result.Route!.Model.Should().Be("row-model");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsRepoSteeringContentFromProviderConfig()
    {
        var setup = new Setup
        {
            Infra = new((request, _) => Task.FromResult<CoreResult?>(MakeCoreResult(request, MakeProviderConfigs("fresh-steering-content")))),
        };

        var result = Enriched(await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None));

        result.RepoSteeringContent.Should().Be("fresh-steering-content",
            "RepoSteeringContent must be resolved from the fresh ProviderConfig matching RepoProviderConfigId");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsProjectSteeringContent()
    {
        var project = MakeProject() with { SteeringContent = "project-level-steering" };

        var result = Enriched(await new Setup().Build().EnrichAsync(MakeIdentity(), project, CancellationToken.None));

        result.ProjectSteeringContent.Should().Be("project-level-steering");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsIssueDetailFromContext()
    {
        var result = Enriched(await new Setup().Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None));

        result.IssueDetail.Should().NotBeNull("IssueDetail must be populated from fresh issue context");
        result.IssueDetail!.Title.Should().Be("Fix the crash");
    }

    [Fact]
    public async Task EnrichAsync_MergesTheProfilesMcpServersWithTheProjects_ProjectWins()
    {
        var enricher = new Setup
        {
            Profiles = [MakeProfile(mcpServers:
            [
                new McpServerConfig { Name = "context7", Command = "uvx" },
                new McpServerConfig { Name = "web-search", Command = "uvx", Disabled = false },
            ])],
        }.Build();
        var project = MakeProject() with
        {
            McpServers = [new McpServerConfig { Name = "web-search", Command = "uvx", Disabled = true }],
        };

        var result = Enriched(await enricher.EnrichAsync(MakeIdentity(), project, CancellationToken.None));

        result.McpServers.Should().HaveCount(2);
        result.McpServers.Should().Contain(s => s.Name == "context7" && s.Command == "uvx");
        result.McpServers.Single(s => s.Name == "web-search").Disabled.Should().BeTrue("the project's server wins on a name clash");
    }

    // ── Stack labels (spec 053 DD7) ──────────────────────────────────────────────────

    /// <summary>
    /// The repository template's stack labels pick the quality gates and reviewers; the agent profile has no say.
    /// The stub runs a real <see cref="QualityGateResolver"/> on the stack labels it receives.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_TheRepositoryTemplatesStackLabels_PickTheQualityGates()
    {
        var dotnetQgc = new QualityGateConfiguration { Id = "qgc-dotnet", DisplayName = ".NET", MatchLabels = ["dotnet"], Enabled = true };
        var pythonQgc = new QualityGateConfiguration { Id = "qgc-python", DisplayName = "Python", MatchLabels = ["python"], Enabled = true };
        var setup = new Setup
        {
            Templates = [RepoTemplate(stackLabels: ["dotnet", "dotnet10"])],
            Infra = new((request, _) => Task.FromResult<CoreResult?>(MakeCoreResult(request) with
            {
                QualityGates = new QualityGateResolver().Resolve([dotnetQgc, pythonQgc], request.StackLabels!),
            })),
        };

        var result = Enriched(await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None));

        setup.Infra.CapturedRequest!.StackLabels.Should().Equal("dotnet", "dotnet10");
        result.QualityGateConfigs.Should().ContainSingle().Which.Id.Should().Be("qgc-dotnet");
    }

    [Fact]
    public async Task EnrichAsync_WithoutARepositoryTemplate_HasNoStackLabels()
    {
        var setup = new Setup();

        await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        setup.Infra.CapturedRequest!.StackLabels.Should().NotBeNull("an empty list resolves the label-less gates and reviewers")
            .And.BeEmpty();
    }

    [Fact]
    public async Task EnrichAsync_Success_PreservesIdentityFields()
    {
        var identity = MakeIdentity() with
        {
            RunId = "run-42",
            BrainProviderConfigId = "brain-prov-1",
            PipelineProviderConfigId = "pipeline-prov-1",
        };

        var result = Enriched(await new Setup().Build().EnrichAsync(identity, MakeProject(), CancellationToken.None));

        result.RunId.Should().Be("run-42", "RunId must be preserved from the identity payload");
        result.IssueProviderConfigId.Should().Be("issue-prov-1");
        result.RepoProviderConfigId.Should().Be("repo-prov-1");
        result.BrainProviderConfigId.Should().Be("brain-prov-1");
        result.PipelineProviderConfigId.Should().Be("pipeline-prov-1");
        result.AgentSelector.Should().Be(JobTemplate);
    }

    [Fact]
    public async Task EnrichAsync_Success_PassesCorrectCoreRequestToInfra()
    {
        var identity = MakeIdentity() with
        {
            IssueProviderConfigId = "iss-42",
            RepoProviderConfigId = "repo-77",
        };
        var setup = new Setup();

        await setup.Build().EnrichAsync(identity, MakeProject(), CancellationToken.None);

        var captured = setup.Infra.CapturedRequest;
        captured.Should().NotBeNull();
        captured!.IssueProviderId.Value.Should().Be("iss-42");
        captured.RepoProviderId.Value.Should().Be("repo-77");
        captured.AdditionalRepoProviderIds.Should().BeNull("only a project epic clones other repositories");
        captured.PullRequest.Should().BeNull("only a review is about a pull request");
    }

    [Fact]
    public async Task EnrichAsync_Review_TakesItsContextFromThePullRequestItWasDispatchedWith()
    {
        // A review's subject is a pull request in the repository. In GitLab, and whenever the tracker is a
        // different system, the tracker's #5 is an unrelated issue, so the context never comes from it.
        var identity = MakeIdentity() with
        {
            IssueIdentifier = new IssueIdentifier("5"),
            IssueProviderConfigId = "repo-prov-1",
            TaskType = WorkItemTaskType.Review,
            RunType = PipelineRunType.Review,
            IssueDetail = new IssueDetail { Identifier = "5", Title = "Add pagination", Description = "", Labels = [] },
            ReviewPrDescription = "Pages the API",
            LinkedPullRequest = new LinkedPullRequest
            {
                Url = "https://gitlab.example.com/group/repo/-/merge_requests/5",
                BranchName = "feature/pages",
                IsDraft = false,
                Number = 5
            }
        };
        var setup = new Setup();

        await setup.Build().EnrichAsync(identity, MakeProject(), CancellationToken.None);

        var pullRequest = setup.Infra.CapturedRequest!.PullRequest;
        pullRequest.Should().NotBeNull();
        pullRequest!.Identifier.Should().Be("5");
        pullRequest.Title.Should().Be("Add pagination");
        pullRequest.Description.Should().Be("Pages the API");
        pullRequest.Url.Should().Be("https://gitlab.example.com/group/repo/-/merge_requests/5");
    }

    // ── Decomposition scope ──────────────────────────────────────────────────────────

    private static PipelineJobTemplate ApiTemplate() =>
        new() { Id = "t-api", Name = "api", IssueProviderId = "issue-api", RepoProviderId = "repo-api" };

    private static DecompositionProjectContext ShopProjectContext() => new()
    {
        ProjectName = "Shop",
        Repositories =
        [
            new RepositoryTarget { TemplateName = "api", Description = "", IssueProviderId = "issue-api", RepoProviderId = "repo-api" },
            new RepositoryTarget { TemplateName = "web", Description = "", IssueProviderId = "issue-web", RepoProviderId = "repo-web" }
        ]
    };

    private static JobDistributionRequest EpicIdentity(string issueProviderConfigId) => MakeIdentity() with
    {
        TaskType = WorkItemTaskType.Decomposition,
        IssueProviderConfigId = issueProviderConfigId,
        RepoProviderConfigId = "repo-api",
    };

    [Fact]
    public async Task EnrichAsync_ProjectEpic_UsesTheCurrentRepoListAndRequestsItsRepositoriesForCloning()
    {
        // An epic in its project's epic tracker is a project epic; the repo list comes from the current
        // configuration, not from the payload
        var project = MakeProject() with { EpicIssueProviderId = "issue-epics" };
        var setup = new Setup();
        setup.Infra.ProjectEpicContext = ShopProjectContext();

        var result = Enriched(await setup.Build().EnrichAsync(EpicIdentity("issue-epics"), project, CancellationToken.None));

        setup.Infra.CapturedRequest!.AdditionalRepoProviderIds.Should().Equal("repo-api", "repo-web");
        result.ProjectContext.Should().BeSameAs(setup.Infra.ProjectEpicContext);
    }

    [Fact]
    public async Task EnrichAsync_RepoEpicWithAStaleRepoListInThePayload_DropsItAndClonesNothing()
    {
        // A WorkItem queued before the deploy carries a repo list, but its epic lives in a template's own tracker
        var identity = EpicIdentity("issue-api") with { ProjectContext = ShopProjectContext() };
        var project = MakeProject() with { EpicIssueProviderId = "issue-epics" };
        var setup = new Setup { ProjectTemplates = [ApiTemplate()] };

        var result = Enriched(await setup.Build().EnrichAsync(identity, project, CancellationToken.None));

        result.ProjectContext.Should().BeNull("a repo epic's sub-issues stay in its own tracker");
        setup.Infra.CapturedRequest!.AdditionalRepoProviderIds.Should().BeNull();
        setup.Infra.ProjectEpicContextCalls.Should().Be(0);
    }

    [Fact]
    public async Task EnrichAsync_ProjectEpicWithoutAUsableRepoList_IsUnavailable()
    {
        // Without the repo list the sub-issues would land in the epic tracker
        var project = MakeProject() with { EpicIssueProviderId = "issue-epics" };
        var setup = new Setup();
        setup.Infra.ProjectEpicContext = null;

        var result = await setup.Build().EnrichAsync(EpicIdentity("issue-epics"), project, CancellationToken.None);

        result.Should().Be(EnrichmentResult.Unavailable.Instance, "not a routing reason: the agent retries (DD6)");
        setup.Infra.CapturedRequest.Should().BeNull("the job is not prepared at all");
    }

    [Fact]
    public async Task EnrichAsync_ProjectEpic_RepoListReadFails_IsUnavailable()
    {
        // The real infrastructure swallows a failed template read into "no repo list" (DD6: a 503, not a refusal).
        var configStore = new Mock<IConfigurationStore>();
        configStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("DB timeout"));
        var infra = new DispatchInfrastructure(
            Mock.Of<ITokenVendingService>(), Mock.Of<IProviderFactory>(), Mock.Of<ILabelService>(),
            new DispatchResolutionService(new QualityGateResolver(), new ReviewerResolver(), configStore.Object));
        var profileStore = new Mock<IAgentProfileStore>();
        profileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([MakeProfile()]);
        var projectStore = new Mock<IProjectStore>();
        projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var enricher = new AssignmentEnricher(
            infra, profileStore.Object, MakeNoOpConsolidationPreparer().Object, projectStore.Object,
            new ConsolidationTemplateResolver(projectStore.Object),
            new AgentRoutingStore(new AgentRoutingTestSupport.InMemoryFactory(Guid.NewGuid().ToString())),
            AgentRoutingTestData.JobTemplates(), Serilog.Log.Logger);
        var project = MakeProject() with { EpicIssueProviderId = "issue-epics", TemplateIds = ["t-api"] };

        var result = await enricher.EnrichAsync(EpicIdentity("issue-epics"), project, CancellationToken.None);

        result.Should().Be(EnrichmentResult.Unavailable.Instance);
    }

    [Fact]
    public async Task EnrichAsync_EpicInNeitherTheEpicTrackerNorATemplateTracker_IsUnavailable()
    {
        // The project's epic tracker changed after the WorkItem was queued; the epic's sub-issues would land where
        // nothing polls
        var project = MakeProject() with { EpicIssueProviderId = "issue-epics" };
        var setup = new Setup { ProjectTemplates = [ApiTemplate()] };

        var result = await setup.Build().EnrichAsync(EpicIdentity("issue-old-epics"), project, CancellationToken.None);

        result.Should().Be(EnrichmentResult.Unavailable.Instance);
        setup.Infra.CapturedRequest.Should().BeNull("the job is not prepared at all");
    }

    // ── Refusals (spec 053 design "Claim") ───────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrichAsync_PinnedProfileGoneOrDisabled_IsRefused_ProfileUnavailable(bool disabled)
    {
        var setup = new Setup { Profiles = disabled ? [MakeProfile(enabled: false)] : [MakeProfile("profile-other")] };

        var result = await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.ProfileUnavailable).Text
            .Should().Be($"agent profile '{ProfileName}' no longer exists");
        setup.Infra.CapturedRequest.Should().BeNull("a refused claim prepares nothing");
    }

    [Fact]
    public async Task EnrichAsync_TheProfilesJobTemplateChangedSinceQueueing_IsRefused_TemplateChanged()
    {
        var setup = new Setup
        {
            Profiles = [MakeProfile(jobTemplate: "java,opencode")],
            JobTemplates = AgentRoutingTestData.JobTemplates((JobTemplate, JobTemplateProviderType.Kiro), ("java,opencode", JobTemplateProviderType.Opencode)),
        };

        var result = await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.TemplateChanged).Text
            .Should().Be($"the route now names job template java,opencode; the work was queued for {JobTemplate}");
        setup.Infra.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task EnrichAsync_TheJobTemplateIsGone_IsRefused_NoTool()
    {
        var setup = new Setup { JobTemplates = AgentRoutingTestData.JobTemplates(("java,opencode", JobTemplateProviderType.Opencode)) };

        var result = await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.NoTool).Text
            .Should().Be($"Job template {JobTemplate} of agent profile '{ProfileName}' has no providerType");
        setup.Infra.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task EnrichAsync_TheJobTemplateHasNoProviderType_IsRefused_NoTool()
    {
        var setup = new Setup { JobTemplates = AgentRoutingTestData.JobTemplates((JobTemplate, "")) };

        var result = await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.NoTool);
    }

    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Triage)]
    [InlineData(WorkItemTaskType.Consolidation)]
    public async Task EnrichAsync_ItemQueuedBeforeAgentRouting_WithoutARoute_IsRefused_NoRoute(WorkItemTaskType taskType)
    {
        // D15: nothing picks a profile no route names, at claim either.
        var setup = new Setup { Routing = RoutingTable.Empty, Preparer = new Mock<IConsolidationJobPreparationService>() };
        var identity = MakeIdentity() with { TaskType = taskType, Route = null, ResolvedProfileId = null };

        var result = await setup.Build().EnrichAsync(identity, MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.NoRoute).Text.Should().Be(AgentRouteRefusalReason.NoRouteText);
        setup.Infra.CapturedRequest.Should().BeNull();
        setup.Preparer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnrichAsync_TheAgentConfigIdCollidesWithAnotherProviderConfig_IsRefused_ConfigCollision()
    {
        // The agent takes the first provider config with its Id, so a clash would hand it the wrong config.
        var clash = new ProviderConfig { Id = ProfileId, Kind = ProviderKind.Repository, DisplayName = "clash", ProviderType = "GitHub" };
        var setup = new Setup
        {
            Infra = new((request, _) => Task.FromResult<CoreResult?>(MakeCoreResult(request, [.. MakeProviderConfigs(), clash]))),
        };

        var result = await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.ConfigCollision).Text
            .Should().Be($"agent config Id {ProfileId} collides with another provider config");
    }

    // ── Project review ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Review)]
    public async Task EnrichAsync_ProjectReviewOn_AddsTheReviewersAndTheOtherRepositoriesToClone(WorkItemTaskType taskType)
    {
        var identity = MakeIdentity() with { TaskType = taskType };
        var project = MakeProject() with { ProjectReviewEnabled = true };
        var setup = new Setup();
        setup.Infra.ProjectReviewRepositories =
        [
            new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" },
            new RepositoryTarget { TemplateName = "mobile", Description = "", RepoProviderId = "repo-mobile" }
        ];

        var result = Enriched(await setup.Build().EnrichAsync(identity, project, CancellationToken.None));

        var reviewer = result.ProjectReviewers.Should().ContainSingle().Subject;
        reviewer.Name.Should().Be(PipelineConfigurationDefaults.DefaultProjectReviewerName);
        reviewer.Prompt.Should().Be(PipelineConfigurationDefaults.DefaultProjectReviewPrompt);
        result.ProjectReviewRepositories!.Select(r => r.TemplateName).Should().Equal("api", "mobile");
        setup.Infra.ProjectReviewOwnRepository.Should().Be("repo-prov-1", "the run's own repository is the workspace, not a clone");
        setup.Infra.CapturedRequest!.AdditionalRepoProviderIds.Should().Equal(["repo-api", "repo-mobile"],
            "the other repositories get clone-only provider configs");
    }

    [Fact]
    public async Task EnrichAsync_ProjectReviewOff_AddsNoReviewersAndClonesNothing()
    {
        var project = MakeProject() with
        {
            ProjectReviewers = [new ReviewAgent { Name = "Product", Prompt = "Check it" }]
        };
        var setup = new Setup();

        var result = Enriched(await setup.Build().EnrichAsync(MakeIdentity(), project, CancellationToken.None));

        result.ProjectReviewers.Should().BeEmpty();
        result.ProjectReviewRepositories.Should().BeNull();
        setup.Infra.ProjectReviewRepositoriesCalls.Should().Be(0);
        setup.Infra.CapturedRequest!.AdditionalRepoProviderIds.Should().BeNull();
    }

    [Fact]
    public async Task EnrichAsync_RepoEpicDecomposition_GetsNoProjectReview()
    {
        // A decomposition has no code review, so it gets no project reviewers even when the project review is on
        var identity = MakeIdentity() with { TaskType = WorkItemTaskType.Decomposition };
        var project = MakeProject() with { ProjectReviewEnabled = true };
        var setup = new Setup { ProjectTemplates = [RepoTemplate()] };

        var result = Enriched(await setup.Build().EnrichAsync(identity, project, CancellationToken.None));

        result.ProjectReviewers.Should().BeEmpty();
        setup.Infra.ProjectReviewRepositoriesCalls.Should().Be(0);
        setup.Infra.CapturedRequest!.AdditionalRepoProviderIds.Should().BeNull("a repo epic clones no other repository");
    }

    // ── PrepareDispatchCoreAsync returns null ─────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_PrepareDispatchCoreReturnsNull_IsUnavailable()
    {
        // E.g. the issue provider config is gone: not a routing reason, so the agent retries.
        var setup = new Setup { Infra = new((_, _) => Task.FromResult<CoreResult?>(null)) };

        var result = await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        result.Should().Be(EnrichmentResult.Unavailable.Instance);
    }

    // ── Exception propagation path ────────────────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_InfraThrows_PropagatesException()
    {
        // A transient failure propagates so GetAssignment answers 503 and the agent retries.
        var setup = new Setup { Infra = new((_, _) => throw new TimeoutException("DB connection timeout")) };

        var act = () => setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*DB connection timeout*");
    }

    [Fact]
    public async Task EnrichAsync_OperationCanceledException_IsNotSwallowed()
    {
        var setup = new Setup { Infra = new((_, _) => throw new OperationCanceledException()) };

        var act = () => setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must not be swallowed by the catch-all in EnrichAsync");
    }

    // ── Null-argument guards ──────────────────────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_NullIdentity_ThrowsArgumentNullException()
    {
        var act = () => new Setup().Build().EnrichAsync(null!, MakeProject(), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("identity");
    }

    [Fact]
    public async Task EnrichAsync_NullProject_ThrowsArgumentNullException()
    {
        var act = () => new Setup().Build().EnrichAsync(MakeIdentity(), null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("project");
    }

    // ── Protected logger-only constructor ─────────────────────────────────────────

    [Fact]
    public void ProtectedCtor_NullLogger_FallsBackToSerilogLogLogger()
    {
        var enricherSubclass = new NullLoggerEnricher(null!);
        enricherSubclass.Should().NotBeNull();
    }

    private sealed class NullLoggerEnricher : AssignmentEnricher
    {
        // Calls the protected logger-only constructor (which handles null logger)
        public NullLoggerEnricher(Serilog.ILogger logger) : base(logger) { }
    }

    // ── Consolidation task type path ──────────────────────────────────────────────

    private static JobDistributionRequest MakeConsolidationIdentity(
        ConsolidationRunType runType = ConsolidationRunType.BrainConsolidation,
        string? templateId = "tmpl-1",
        bool autoDispatch = false) => MakeIdentity() with
        {
            TaskType = WorkItemTaskType.Consolidation,
            ConsolidationRunType = runType,
            ConsolidationTemplateId = templateId,
            AutoDispatch = autoDispatch,
        };

    private static ConsolidationJobPreparationResult MakeConsolidationPreparationResult(
        string repoProviderConfigId = "repo-prov-1",
        string steeringContent = "repo-steering",
        string? brainProviderConfigId = null) =>
        new()
        {
            ProviderConfigs =
            [
                new ProviderConfig
                {
                    Id = repoProviderConfigId,
                    Kind = ProviderKind.Repository,
                    DisplayName = "Test Repo",
                    ProviderType = "GitHub",
                    SteeringContent = steeringContent,
                }
            ],
            RepoProviderConfigId = repoProviderConfigId,
            PipelineConfiguration = new PipelineConfiguration(),
            BrainProviderConfigId = brainProviderConfigId,
        };

    /// <summary>A preparer returning <paramref name="result"/>; dispatch infra must not be called for consolidation.</summary>
    private static Setup ConsolidationSetup(
        ConsolidationJobPreparationResult? result = null,
        IPipelineRunHistoryService? runHistory = null,
        IReadOnlyList<AgentProfile>? profiles = null,
        IReadOnlyList<PipelineJobTemplate>? templates = null,
        RoutingTable? routing = null)
    {
        var preparer = new Mock<IConsolidationJobPreparationService>();
        preparer.Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<ProviderConfig?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? MakeConsolidationPreparationResult());
        return new Setup
        {
            Infra = new((_, _) => throw new InvalidOperationException(
                "DispatchInfrastructure.PrepareDispatchCoreAsync must not be called for consolidation tasks.")),
            Preparer = preparer,
            RunHistory = runHistory,
            Profiles = profiles ?? [MakeProfile()],
            Templates = templates ?? [],
            Routing = routing,
        };
    }

    /// <summary>The arguments of the preparer's one call.</summary>
    private static (ConsolidationRunType Type, TemplateId? TemplateId, ProviderConfig? AgentConfig) PrepareCall(Setup setup)
    {
        var call = setup.Preparer.Invocations.Should().ContainSingle().Subject;
        return ((ConsolidationRunType)call.Arguments[0], (TemplateId?)call.Arguments[1], (ProviderConfig?)call.Arguments[2]);
    }

    private static PipelineRunSummary MakeConsolidationRunSummary(string scopeKey, DateTimeOffset completedAt) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = new IssueIdentifier(scopeKey),
        IssueTitle = scopeKey,
        FinalStep = PipelineStep.Completed,
        CompletedAtOffset = completedAt,
    };

    private static Mock<IPipelineRunHistoryService> MakeRunHistory(
        IReadOnlyList<PipelineRunSummary> successfulConsolidations)
    {
        var history = new Mock<IPipelineRunHistoryService>();
        history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.Page == 1 && !q.FeedbackOnly && q.FinalStep == PipelineStep.Completed
                    && q.ProjectId == null && q.Since == null && q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = successfulConsolidations, Page = 1, PageSize = 100, HasMore = false });
        return history;
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsLastSuccessfulRunOfTheSameScope()
    {
        // The work item key "{type}:{scope}" is also the issue identifier of the consolidation's run history entries
        var identity = MakeConsolidationIdentity() with { IssueIdentifier = new IssueIdentifier("BrainConsolidation:brain-1") };
        var latest = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var history = MakeRunHistory(
        [
            MakeConsolidationRunSummary("BrainConsolidation:brain-1", latest.AddDays(-3)),
            MakeConsolidationRunSummary("BrainConsolidation:brain-2", latest.AddDays(1)),
            MakeConsolidationRunSummary("BrainConsolidation:brain-1", latest),
        ]);

        var result = Enriched(await ConsolidationSetup(runHistory: history.Object).Build()
            .EnrichAsync(identity, MakeProject(), CancellationToken.None));

        result.ConsolidationLastSuccessfulRunUtc.Should().Be(latest);
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_RunHistoryFails_StillEnrichesWithoutIt()
    {
        var history = new Mock<IPipelineRunHistoryService>();
        history
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("DB timeout"));

        var result = Enriched(await ConsolidationSetup(runHistory: history.Object).Build()
            .EnrichAsync(MakeConsolidationIdentity(), MakeProject(), CancellationToken.None));

        result.ConsolidationLastSuccessfulRunUtc.Should().BeNull("the run history only adds context; dispatch must not fail on it");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_PassesTheBuiltAgentConfigToThePreparer_NotDispatchInfra()
    {
        var setup = ConsolidationSetup();

        Enriched(await setup.Build().EnrichAsync(MakeConsolidationIdentity(), MakeProject(), CancellationToken.None));

        var (_, _, agentConfig) = PrepareCall(setup);
        agentConfig.Should().NotBeNull("spec 053 DD5: the consolidation runs on the routed profile's config");
        agentConfig!.Id.Should().Be(ProfileId);
        agentConfig.Settings.Should().Contain(ProviderSettingKeys.Model, "opus").And.Contain(ProviderSettingKeys.Effort, "high");
        setup.Infra.CapturedRequest.Should().BeNull(
            "DispatchInfrastructure.PrepareDispatchCoreAsync must NOT be called for consolidation tasks");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_ReturnsThePreparersProviderConfigs()
    {
        var prepResult = MakeConsolidationPreparationResult(repoProviderConfigId: "repo-prov-consolidation");

        var result = Enriched(await ConsolidationSetup(prepResult).Build()
            .EnrichAsync(MakeConsolidationIdentity(), MakeProject(), CancellationToken.None));

        result.ProviderConfigs.Should().BeEquivalentTo(prepResult.ProviderConfigs);
        result.RepoProviderConfigId.Should().Be("repo-prov-consolidation",
            "RepoProviderConfigId must be set from the consolidation preparation result");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsPipelineConfiguration()
    {
        var prepResult = new ConsolidationJobPreparationResult
        {
            ProviderConfigs = [],
            RepoProviderConfigId = "",
            PipelineConfiguration = new PipelineConfiguration { AgentTimeout = TimeSpan.FromHours(2) },
        };

        var result = Enriched(await ConsolidationSetup(prepResult).Build()
            .EnrichAsync(MakeConsolidationIdentity(), MakeProject(), CancellationToken.None));

        result.PipelineConfiguration!.AgentTimeout.Should().Be(TimeSpan.FromHours(2),
            "PipelineConfiguration must reflect the per-template config from ConsolidationJobPreparationService");
    }

    [Theory]
    [InlineData("tmpl-abc-123")]
    [InlineData(null)]
    public async Task EnrichAsync_ConsolidationTask_PassesTheRunTypeAndTemplateIdToThePreparer(string? templateId)
    {
        var setup = ConsolidationSetup();
        var identity = MakeConsolidationIdentity(runType: ConsolidationRunType.RefactoringDetection, templateId: templateId);

        await setup.Build().EnrichAsync(identity, MakeProject(), CancellationToken.None);

        var (type, passedTemplateId, _) = PrepareCall(setup);
        type.Should().Be(ConsolidationRunType.RefactoringDetection);
        passedTemplateId?.Value.Should().Be(templateId);
        if (templateId is null)
            passedTemplateId.Should().BeNull("a global run has no template");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsTheRoutedProfile()
    {
        var result = Enriched(await ConsolidationSetup().Build()
            .EnrichAsync(MakeConsolidationIdentity(), MakeProject(), CancellationToken.None));

        result.ResolvedProfileId.Should().Be(ProfileId);
        result.AgentProviderConfigId.Should().Be(ProfileId,
            "AgentProviderConfigId is the built agent config's Id, never the repository provider");
        result.Route.Should().Be(PinnedRoute);
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_ItemQueuedBeforeAgentRouting_UsesTheConsolidationTemplatesRow()
    {
        // Req 4.3: a consolidation routes by the template that triggered it, not by the repository.
        var trigger = new PipelineJobTemplate
        {
            Id = "tmpl-1", Name = "brain", IssueProviderId = "ip", RepoProviderId = "other-repo",
            AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Consolidation] = AgentRoutingTestData.Route("profile-2", "brain-model"),
            },
        };
        var setup = ConsolidationSetup(
            profiles: [MakeProfile(), MakeProfile("profile-2")], templates: [trigger], routing: AgentRoutingTestData.Routing(ProfileId));
        var identity = MakeConsolidationIdentity() with { Route = null };

        var result = Enriched(await setup.Build().EnrichAsync(identity, MakeProject(), CancellationToken.None));

        result.ResolvedProfileId.Should().Be("profile-2");
        result.Route!.Source.Should().Be(AgentRouteSource.Claim);
        PrepareCall(setup).AgentConfig!.Settings.Should().Contain(ProviderSettingKeys.Model, "brain-model");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsProjectSteeringContent()
    {
        var project = MakeProject() with { SteeringContent = "consolidation-project-steering" };

        var result = Enriched(await ConsolidationSetup().Build().EnrichAsync(MakeConsolidationIdentity(), project, CancellationToken.None));

        result.ProjectSteeringContent.Should().Be("consolidation-project-steering");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_PreservesConsolidationIdentityFields()
    {
        var identity = MakeConsolidationIdentity(
            runType: ConsolidationRunType.RefactoringDetection,
            templateId: "tmpl-preserve",
            autoDispatch: true) with { IssueDetail = null };

        var result = Enriched(await ConsolidationSetup().Build().EnrichAsync(identity, MakeProject(), CancellationToken.None));

        result.TaskType.Should().Be(WorkItemTaskType.Consolidation);
        result.ConsolidationRunType.Should().Be(ConsolidationRunType.RefactoringDetection);
        result.ConsolidationTemplateId.Should().Be("tmpl-preserve");
        result.AutoDispatch.Should().BeTrue();
        result.IssueDetail.Should().BeNull("consolidation agents do not fetch or process issues");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_ProfileGone_IsRefused_WithoutCallingThePreparer()
    {
        var setup = ConsolidationSetup(profiles: [MakeProfile("profile-other")]);

        var result = await setup.Build().EnrichAsync(MakeConsolidationIdentity(), MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.ProfileUnavailable);
        setup.Preparer.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_AgentConfigIdCollision_IsRefused()
    {
        var prepResult = MakeConsolidationPreparationResult(repoProviderConfigId: ProfileId) with
        {
            ProviderConfigs =
            [
                new ProviderConfig { Id = ProfileId, Kind = ProviderKind.Repository, DisplayName = "clash", ProviderType = "GitHub" },
                new ProviderConfig { Id = ProfileId, Kind = ProviderKind.Agent, DisplayName = "agent", ProviderType = "KiroCli" },
            ],
        };

        var result = await ConsolidationSetup(prepResult).Build()
            .EnrichAsync(MakeConsolidationIdentity(), MakeProject(), CancellationToken.None);

        Refused(result, AgentRouteRefusalReason.ConfigCollision);
    }

    [Fact]
    public async Task EnrichAsync_ImplementationTask_DoesNotCallTheConsolidationPreparer()
    {
        var setup = new Setup();

        var result = Enriched(await setup.Build().EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None));

        setup.Infra.CapturedRequest.Should().NotBeNull(
            "DispatchInfrastructure.PrepareDispatchCoreAsync must be called for implementation tasks");
        result.TaskType.Should().Be(WorkItemTaskType.Implementation);
        setup.Preparer.Invocations.Should().BeEmpty();
    }

    // ── BrainProviderConfigId forwarding (issue #2618) ───────────────────────────

    [Theory]
    [InlineData(null, "brain-prov-42", "brain-prov-42")]
    [InlineData(null, null, null)]
    [InlineData("stale-brain", "fresh-brain", "fresh-brain")]
    public async Task EnrichAsync_ConsolidationTask_TakesTheBrainProviderFromThePreparation(
        string? stored, string? prepared, string? expected)
    {
        // The preparation result is authoritative: a stale or missing stored value is overwritten.
        var identity = MakeConsolidationIdentity() with { BrainProviderConfigId = stored };

        var result = Enriched(await ConsolidationSetup(MakeConsolidationPreparationResult(brainProviderConfigId: prepared)).Build()
            .EnrichAsync(identity, MakeProject(), CancellationToken.None));

        result.BrainProviderConfigId.Should().Be(expected);
    }
}

/// <summary>
/// An OperationCanceledException from enrichment propagates without an Error log (issue #2575, Fix A), and any other
/// exception is logged at Error and propagates, so the caller answers 503.
/// </summary>
public sealed class AssignmentEnricherOceCancellationTests
{
    /// <summary>Throws a supplied exception from PrepareDispatchCoreAsync to simulate a downstream failure.</summary>
    private sealed class ThrowingDispatchInfrastructure : DispatchInfrastructure
    {
        private readonly Exception _toThrow;

        public ThrowingDispatchInfrastructure(Exception toThrow) : base()
        {
            _toThrow = toThrow;
        }

        internal override Task<(IReadOnlyList<QualityGateConfiguration> QualityGates,
            IReadOnlyList<ReviewerConfiguration> Reviewers,
            DispatchInfrastructure.IssueContextResult IssueContext,
            IReadOnlyList<ProviderConfig> ProviderConfigs,
            PipelineConfiguration Config,
            bool ForceRefresh,
            string? StalenessSignal,
            int RefreshCount)?> PrepareDispatchCoreAsync(DispatchCoreRequest request, CancellationToken ct)
            => throw _toThrow;
    }

    private static JobDistributionRequest MakeIdentity() => new()
    {
        IssueIdentifier = new IssueIdentifier("owner/repo#42"),
        IssueProviderConfigId = "issue-prov-1",
        RepoProviderConfigId = "repo-prov-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = AgentRoutingTestData.DefaultJobTemplate,
        Route = new AgentRouteSnapshot("profile-1", "Test Profile", JobTemplateProviderType.Kiro, "auto", "auto", AgentRouteSource.GlobalDefault),
        TimeoutSeconds = 3600,
    };

    private static PipelineProject MakeProject() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Test Project",
    };

    private static AssignmentEnricher MakeEnricherWithThrowingInfra(Exception toThrow, Mock<Serilog.ILogger> mockLogger)
    {
        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([AgentRoutingTestData.Profile("profile-1", displayName: "Test Profile")]);

        var noopProjectStore = new Mock<IProjectStore>();
        noopProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        noopProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new AssignmentEnricher(
            new ThrowingDispatchInfrastructure(toThrow), profileStoreMock.Object, new Mock<IConsolidationJobPreparationService>().Object,
            noopProjectStore.Object, new ConsolidationTemplateResolver(noopProjectStore.Object),
            new AgentRoutingStore(new AgentRoutingTestSupport.InMemoryFactory(Guid.NewGuid().ToString())),
            AgentRoutingTestData.JobTemplates(), mockLogger.Object);
    }

    private static Mock<Serilog.ILogger> MockLogger()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        // Serilog calls ForContext internally; return the same mock logger
        mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(mockLogger.Object);
        mockLogger.Setup(l => l.ForContext<It.IsAnyType>()).Returns(mockLogger.Object);
        return mockLogger;
    }

    [Fact]
    public async Task EnrichAsync_WhenEnrichCoreThrowsOce_DoesNotLogError()
    {
        var mockLogger = MockLogger();
        var enricher = MakeEnricherWithThrowingInfra(new OperationCanceledException(), mockLogger);

        var act = () => enricher.EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the OCE must propagate from EnrichAsync — the `when (ex is not OCE)` guard must not catch it");

        mockLogger.Verify(
            l => l.Error(It.IsAny<string>(), It.IsAny<IssueIdentifier>()),
            Times.Never,
            "a client cancellation must not be recorded as a dispatch failure");
    }

    [Fact]
    public async Task EnrichAsync_WhenEnrichCoreThrowsNonOce_LogsError()
    {
        var mockLogger = MockLogger();
        var enricher = MakeEnricherWithThrowingInfra(new InvalidOperationException("simulated DB timeout"), mockLogger);

        var act = () => enricher.EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        mockLogger.Verify(
            l => l.Error(It.IsAny<string>(), It.IsAny<IssueIdentifier>()),
            Times.Once,
            "a non-OCE exception must still trigger the Error log in EnrichAsync");
    }
}
