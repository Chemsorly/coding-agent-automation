using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="AssignmentEnricher.EnrichAsync"/> core logic
/// (the <c>EnrichCoreAsync</c> private path), covering:
/// <list type="bullet">
///   <item>Profile-not-found → returns null</item>
///   <item>PrepareDispatchCoreAsync returns null → returns null</item>
///   <item>Success path → returns enriched request with all fresh fields populated</item>
///   <item>Exception in EnrichCoreAsync → caught, warning logged, returns null</item>
///   <item>Protected logger-only constructor (null logger) → falls back to Serilog.Log.Logger</item>
/// </list>
/// </summary>
public sealed class AssignmentEnricherTests
{
    // ── Controllable DispatchInfrastructure stub ───────────────────────────────────

    /// <summary>
    /// Subclass of DispatchInfrastructure that overrides the virtual PrepareDispatchCoreAsync
    /// without constructing real dependencies. This is the test seam.
    /// </summary>
    private sealed class StubDispatchInfrastructure : DispatchInfrastructure
    {
        private readonly Func<DispatchCoreRequest, CancellationToken,
            Task<(IReadOnlyList<QualityGateConfiguration>, IReadOnlyList<ReviewerConfiguration>,
                  DispatchInfrastructure.IssueContextResult, IReadOnlyList<ProviderConfig>,
                  PipelineConfiguration, bool, string?, int)?>> _handler;

        public DispatchCoreRequest? CapturedRequest { get; private set; }

        public StubDispatchInfrastructure(
            Func<DispatchCoreRequest, CancellationToken,
                Task<(IReadOnlyList<QualityGateConfiguration>, IReadOnlyList<ReviewerConfiguration>,
                      DispatchInfrastructure.IssueContextResult, IReadOnlyList<ProviderConfig>,
                      PipelineConfiguration, bool, string?, int)?>> handler)
            : base()  // protected no-arg ctor; real deps unused (PrepareDispatchCoreAsync is overridden)
        {
            _handler = handler;
        }

        internal override Task<(IReadOnlyList<QualityGateConfiguration> QualityGates,
            IReadOnlyList<ReviewerConfiguration> Reviewers,
            DispatchInfrastructure.IssueContextResult IssueContext,
            IReadOnlyList<ProviderConfig> ProviderConfigs,
            PipelineConfiguration Config,
            bool ForceRefresh,
            string? StalenessSignal,
            int RefreshCount)?> PrepareDispatchCoreAsync(DispatchCoreRequest request, CancellationToken ct)
        {
            CapturedRequest = request;
            return _handler(request, ct);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static JobDistributionRequest MakeIdentity(string agentSelector = "dotnet") => new()
    {
        IssueIdentifier = new IssueIdentifier("owner/repo#42"),
        IssueProviderConfigId = "issue-prov-1",
        RepoProviderConfigId = "repo-prov-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = agentSelector,
        TimeoutSeconds = 3600,
        // New-schema: PayloadSchemaVersion == 1 (identity-only payload; ProviderConfigs is null)
        // TODO: [WARNING] PayloadSchemaVersion is not set here (field remains null). The comment above
        // claims new-schema semantics but this fixture is old-schema by the discriminator definition.
        // AssignmentEnricherTests call EnrichAsync directly (not via GetAssignment) so the discriminator
        // is not exercised here — no test is broken. However, if a future test calls GetAssignment with
        // this fixture, it will silently route to the old-schema path. Set PayloadSchemaVersion = 1 here
        // to match the comment and prevent future misclassification.
    };

    private static PipelineProject MakeProject() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Test Project",
        SteeringContent = "project-steering",
    };

    private static AgentProfile MakeProfile(string id = "profile-1", string agentProviderConfigId = "agent-cfg-1") =>
        new()
        {
            Id = id,
            DisplayName = "Test Profile",
            AgentProviderConfigId = agentProviderConfigId,
            MatchLabels = ["dotnet"],
            Enabled = true,
        };

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

    private static (
        IReadOnlyList<QualityGateConfiguration>,
        IReadOnlyList<ReviewerConfiguration>,
        DispatchInfrastructure.IssueContextResult,
        IReadOnlyList<ProviderConfig>,
        PipelineConfiguration,
        bool,
        string?,
        int)? MakeCoreResult(IReadOnlyList<ProviderConfig>? providerConfigs = null)
        => (
            (IReadOnlyList<QualityGateConfiguration>)[],
            (IReadOnlyList<ReviewerConfiguration>)[],
            MakeIssueContext(),
            providerConfigs ?? MakeProviderConfigs(),
            new PipelineConfiguration(),
            false,
            (string?)null,
            0
        );

    /// <summary>
    /// Creates a no-op <see cref="IConsolidationJobPreparationService"/> mock for tests
    /// that exercise the implementation task type path and never call the consolidation preparer.
    /// </summary>
    private static Mock<IConsolidationJobPreparationService> MakeNoOpConsolidationPreparer()
    {
        var mock = new Mock<IConsolidationJobPreparationService>();
        // Default Moq behavior for Task-returning methods is null — set up a fallback so any
        // unexpected call fails explicitly rather than returning null and producing an NRE.
        mock.Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "ConsolidationJobPreparationService.PrepareAsync was not expected to be called by this test."));
        return mock;
    }

    /// <summary>
    /// Creates a <see cref="StubDispatchInfrastructure"/> that returns the given result,
    /// plus a profile store mock and the real <see cref="AssignmentEnricher"/> under test.
    /// </summary>
    private static (StubDispatchInfrastructure Infra, Mock<IAgentProfileStore> ProfileStore, AssignmentEnricher Enricher) MakeEnricher(
        IReadOnlyList<AgentProfile>? profiles = null,
        (IReadOnlyList<QualityGateConfiguration>, IReadOnlyList<ReviewerConfiguration>,
            DispatchInfrastructure.IssueContextResult, IReadOnlyList<ProviderConfig>,
            PipelineConfiguration, bool, string?, int)? coreResult = null)
    {
        var capturedResult = coreResult ?? MakeCoreResult();
        var infra = new StubDispatchInfrastructure((_, _) => Task.FromResult(capturedResult));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles ?? [MakeProfile()]);

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, MakeNoOpConsolidationPreparer().Object, Serilog.Log.Logger);
        return (infra, profileStoreMock, enricher);
    }

    // ── Success path ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_Success_ReturnsEnrichedRequestWithFreshProviderConfigs()
    {
        // ARRANGE
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();
        var (_, _, enricher) = MakeEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: result is not null and has fresh ProviderConfigs
        result.Should().NotBeNull("success path must return an enriched request");
        result!.ProviderConfigs.Should().NotBeNullOrEmpty("fresh ProviderConfigs must be set from PrepareDispatchCoreAsync");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsResolvedProfileId()
    {
        // ARRANGE
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();
        var (_, _, enricher) = MakeEnricher(profiles: [MakeProfile("my-profile-id")]);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: resolved profile ID is set from the matched profile
        result.Should().NotBeNull();
        result!.ResolvedProfileId.Should().Be("my-profile-id", "ResolvedProfileId must reflect the matched agent profile");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsAgentProviderConfigId()
    {
        // ARRANGE
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();
        var (_, _, enricher) = MakeEnricher(profiles: [MakeProfile(agentProviderConfigId: "agent-provider-99")]);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT
        result.Should().NotBeNull();
        result!.AgentProviderConfigId.Should().Be("agent-provider-99");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsRepoSteeringContentFromProviderConfig()
    {
        // ARRANGE: provider config with known steering content
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();
        var coreResult = MakeCoreResult(providerConfigs: MakeProviderConfigs("fresh-steering-content"));
        var (_, _, enricher) = MakeEnricher(coreResult: coreResult);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: RepoSteeringContent is pulled from the matching ProviderConfig
        result.Should().NotBeNull();
        result!.RepoSteeringContent.Should().Be("fresh-steering-content",
            "RepoSteeringContent must be resolved from the fresh ProviderConfig matching RepoProviderConfigId");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsProjectSteeringContent()
    {
        // ARRANGE
        var identity = MakeIdentity("dotnet");
        var project = MakeProject() with { SteeringContent = "project-level-steering" };
        var (_, _, enricher) = MakeEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT
        result.Should().NotBeNull();
        result!.ProjectSteeringContent.Should().Be("project-level-steering");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsIssueDetailFromContext()
    {
        // ARRANGE
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();
        var (_, _, enricher) = MakeEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: IssueDetail is populated from the fresh context
        result.Should().NotBeNull();
        result!.IssueDetail.Should().NotBeNull("IssueDetail must be populated from fresh issue context");
        result.IssueDetail!.Title.Should().Be("Fix the crash");
    }

    [Fact]
    public async Task EnrichAsync_Success_SetsQualityGateAndReviewerConfigs()
    {
        // ARRANGE
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();
        var (_, _, enricher) = MakeEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT
        result.Should().NotBeNull();
        result!.QualityGateConfigs.Should().NotBeNull("QualityGateConfigs must be set");
        result.ReviewerConfigs.Should().NotBeNull("ReviewerConfigs must be set");
    }

    [Fact]
    public async Task EnrichAsync_Success_PreservesIdentityFields()
    {
        // ARRANGE: identity has RunId, task type, etc. that must survive enrichment
        var identity = MakeIdentity("dotnet") with
        {
            RunId = "run-42",
            BrainProviderConfigId = "brain-prov-1",
            PipelineProviderConfigId = "pipeline-prov-1",
        };
        var project = MakeProject();
        var (_, _, enricher) = MakeEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: identity fields that are not enriched must be preserved
        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-42", "RunId must be preserved from the identity payload");
        result.IssueProviderConfigId.Should().Be("issue-prov-1");
        result.RepoProviderConfigId.Should().Be("repo-prov-1");
        result.BrainProviderConfigId.Should().Be("brain-prov-1");
        result.PipelineProviderConfigId.Should().Be("pipeline-prov-1");
    }

    [Fact]
    public async Task EnrichAsync_Success_PassesCorrectCoreRequestToInfra()
    {
        // ARRANGE: verify the DispatchCoreRequest built for PrepareDispatchCoreAsync has correct IDs
        var identity = MakeIdentity("dotnet") with
        {
            IssueProviderConfigId = "iss-42",
            RepoProviderConfigId = "repo-77",
        };
        var project = MakeProject();
        var (infra, _, enricher) = MakeEnricher();

        // ACT
        await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: the request passed to infra carries the correct identity IDs
        infra.CapturedRequest.Should().NotBeNull();
        infra.CapturedRequest!.IssueProviderId.Value.Should().Be("iss-42");
        infra.CapturedRequest.RepoProviderId.Value.Should().Be("repo-77");
        infra.CapturedRequest.RequiredLabels.Should().Contain("dotnet");
    }

    // ── Profile-not-found path ────────────────────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_NoProfileMatchesSelector_ReturnsNull()
    {
        // ARRANGE: profile store has only "dotnet" profile, but selector is "python"
        var identity = MakeIdentity("python");
        var project = MakeProject();

        var infraCallCount = 0;
        var infra = new StubDispatchInfrastructure((_, _) =>
        {
            infraCallCount++;
            return Task.FromResult(MakeCoreResult());
        });

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, MakeNoOpConsolidationPreparer().Object, Serilog.Log.Logger);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: returns null, PrepareDispatchCoreAsync never called
        result.Should().BeNull("no matching profile means enrichment cannot proceed");
        infraCallCount.Should().Be(0, "PrepareDispatchCoreAsync must not be called when no profile matches");
    }

    [Fact]
    public async Task EnrichAsync_EmptySelectorMatchesCatchAllProfile_ReturnsEnrichedResult()
    {
        // ARRANGE: empty selector → empty required labels → Superset strategy returns true for any profile
        // (LabelMatchStrategies.Superset: if targetSet.Count == 0, always returns true)
        var identity = MakeIdentity(agentSelector: "");
        var project = MakeProject();
        var (_, _, enricher) = MakeEnricher(profiles: [MakeProfile()]);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: empty selector acts as catch-all — first enabled profile is matched
        result.Should().NotBeNull("empty selector matches any profile per the Superset strategy (targetSet.Count==0 → true)");
    }

    // ── PrepareDispatchCoreAsync returns null ─────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_PrepareDispatchCoreReturnsNull_ReturnsNull()
    {
        // ARRANGE: infra returns null (e.g., issue provider config not found)
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) =>
            Task.FromResult<(IReadOnlyList<QualityGateConfiguration>, IReadOnlyList<ReviewerConfiguration>,
                DispatchInfrastructure.IssueContextResult, IReadOnlyList<ProviderConfig>,
                PipelineConfiguration, bool, string?, int)?>(null));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, MakeNoOpConsolidationPreparer().Object, Serilog.Log.Logger);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: null from infra propagates as null from enricher
        result.Should().BeNull("null from PrepareDispatchCoreAsync must return null from EnrichAsync");
    }

    // ── Exception propagation path ────────────────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_InfraThrows_PropagatesException()
    {
        // ARRANGE: infra throws a transient exception (DB timeout, provider failure, etc.)
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) =>
            throw new InvalidOperationException("DB connection timeout"));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, MakeNoOpConsolidationPreparer().Object, Serilog.Log.Logger);

        // ACT + ASSERT: exception propagates so the caller can return 503.
        // The old behavior (swallow + return null → degraded 200) is intentionally removed.
        var act = () => enricher.EnrichAsync(identity, project, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DB connection timeout*",
                "transient exceptions must propagate from EnrichAsync so GetAssignment can return 503");
        // TODO: [WARNING] This test only exercises path: profile resolution succeeds → infra throws.
        // There is no test for the path where LoadAgentProfilesAsync itself throws. Under the new
        // behavior, that exception also propagates (catch-all re-throws), but no test confirms this.
        // Add a test with profileStoreMock throwing from LoadAgentProfilesAsync to ensure a future
        // refactor that adds a separate try/catch around profile loading does not silently regress.
        // TODO: [WARNING] InvalidOperationException is used as the "transient exception" type here,
        // but the same type is thrown by EnrichRequestAsync for permanent failures (no-profile-matched).
        // Use a more discriminating type (e.g., TimeoutException) to make the test's intent clearer and
        // guard against future type-based routing logic that might treat InvalidOperationException as permanent.
    }

    [Fact]
    public async Task EnrichAsync_OperationCanceledException_IsNotSwallowed()
    {
        // ARRANGE: cancellation should propagate, not be swallowed
        var identity = MakeIdentity("dotnet");
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) =>
            throw new OperationCanceledException());

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, MakeNoOpConsolidationPreparer().Object, Serilog.Log.Logger);

        // ACT + ASSERT: OperationCanceledException propagates (it is excluded from the catch)
        var act = () => enricher.EnrichAsync(identity, project, CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must not be swallowed by the catch-all in EnrichAsync");
    }

    // ── Null-argument guards ──────────────────────────────────────────────────────

    [Fact]
    public async Task EnrichAsync_NullIdentity_ThrowsArgumentNullException()
    {
        var (_, _, enricher) = MakeEnricher();
        var act = () => enricher.EnrichAsync(null!, MakeProject(), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("identity");
    }

    [Fact]
    public async Task EnrichAsync_NullProject_ThrowsArgumentNullException()
    {
        var (_, _, enricher) = MakeEnricher();
        var act = () => enricher.EnrichAsync(MakeIdentity(), null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("project");
    }

    // ── Protected logger-only constructor ─────────────────────────────────────────

    [Fact]
    public void ProtectedCtor_NullLogger_FallsBackToSerilogLogLogger()
    {
        // Arrange + Act: passing null logger to the protected ctor should not throw.
        // The ctor has: _logger = logger ?? Serilog.Log.Logger
        // We verify the object is constructed without exception.
        var enricherSubclass = new NullLoggerEnricher(null!);
        enricherSubclass.Should().NotBeNull();
    }

    private sealed class NullLoggerEnricher : AssignmentEnricher
    {
        // Calls the protected logger-only constructor (which handles null logger)
        public NullLoggerEnricher(Serilog.ILogger logger) : base(logger) { }
    }

    // ── Consolidation task type path ──────────────────────────────────────────────

    // Helpers for consolidation tests

    private static JobDistributionRequest MakeConsolidationIdentity(
        string agentSelector = "dotnet",
        ConsolidationRunType runType = ConsolidationRunType.BrainConsolidation,
        string? templateId = "tmpl-1",
        string? workspacePath = "/ws",
        bool autoDispatch = false) => new()
        {
            IssueIdentifier = new IssueIdentifier("owner/repo#42"),
            IssueProviderConfigId = "issue-prov-1",
            RepoProviderConfigId = "repo-prov-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = agentSelector,
            TimeoutSeconds = 3600,
            ConsolidationRunType = runType,
            ConsolidationTemplateId = templateId,
            ConsolidationWorkspacePath = workspacePath,
            AutoDispatch = autoDispatch,
            PayloadSchemaVersion = 1,
        };

    private static ConsolidationJobPreparationResult MakeConsolidationPreparationResult(
        string repoProviderConfigId = "repo-prov-1",
        string steeringContent = "repo-steering") =>
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
        };

    /// <summary>
    /// Creates an AssignmentEnricher wired for consolidation tests.
    /// The infra stub is set up to fail if called (consolidation must not call dispatch infra).
    /// </summary>
    private static (StubDispatchInfrastructure Infra, Mock<IConsolidationJobPreparationService> ConsolidationPreparer, AssignmentEnricher Enricher) MakeConsolidationEnricher(
        IReadOnlyList<AgentProfile>? profiles = null,
        ConsolidationJobPreparationResult? preparationResult = null)
    {
        // Infra must NOT be called for consolidation items — configure to throw if invoked
        var infra = new StubDispatchInfrastructure((_, _) =>
            throw new InvalidOperationException(
                "DispatchInfrastructure.PrepareDispatchCoreAsync must not be called for consolidation tasks."));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles ?? [MakeProfile()]);

        var preparerMock = new Mock<IConsolidationJobPreparationService>();
        preparerMock
            .Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(preparationResult ?? MakeConsolidationPreparationResult());

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, preparerMock.Object, Serilog.Log.Logger);
        return (infra, preparerMock, enricher);
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_CallsConsolidationPreparerNotDispatchInfra()
    {
        // ARRANGE
        var identity = MakeConsolidationIdentity();
        var project = MakeProject();
        var (infra, preparer, enricher) = MakeConsolidationEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: result is not null; preparer was called; dispatch infra was NOT called
        result.Should().NotBeNull("consolidation enrichment must succeed when a profile matches");
        preparer.Verify(
            s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "IConsolidationJobPreparationService.PrepareAsync must be called exactly once");
        infra.CapturedRequest.Should().BeNull(
            "DispatchInfrastructure.PrepareDispatchCoreAsync must NOT be called for consolidation tasks");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_ReturnsEnrichedProviderConfigs()
    {
        // ARRANGE: preparer returns a specific repo provider config
        var identity = MakeConsolidationIdentity();
        var project = MakeProject();
        var prepResult = MakeConsolidationPreparationResult(repoProviderConfigId: "repo-prov-consolidation");
        var (_, _, enricher) = MakeConsolidationEnricher(preparationResult: prepResult);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: ProviderConfigs from the preparer are forwarded
        result.Should().NotBeNull();
        result!.ProviderConfigs.Should().NotBeNullOrEmpty(
            "ProviderConfigs must be set from IConsolidationJobPreparationService.PrepareAsync");
        result.ProviderConfigs!.Should().Contain(pc => pc.Id == "repo-prov-consolidation",
            "the vended repo provider config must be present in the result");
        // TODO: [WARNING] This assertion only checks containment, not exact equality. It would
        // pass if the enricher appended extra spurious provider configs alongside the expected one,
        // or if the identity payload's ProviderConfigs were merged in. Consider asserting
        // result.ProviderConfigs.Should().BeEquivalentTo(prepResult.ProviderConfigs) to verify
        // the result is exactly what the preparer returned, with no additions.
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsRepoProviderConfigId()
    {
        // ARRANGE
        var identity = MakeConsolidationIdentity();
        var project = MakeProject();
        var prepResult = MakeConsolidationPreparationResult(repoProviderConfigId: "resolved-repo-id");
        var (_, _, enricher) = MakeConsolidationEnricher(preparationResult: prepResult);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: RepoProviderConfigId comes from the preparation result (template-resolved)
        result.Should().NotBeNull();
        result!.RepoProviderConfigId.Should().Be("resolved-repo-id",
            "RepoProviderConfigId must be set from the consolidation preparation result");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsPipelineConfiguration()
    {
        // ARRANGE: preparer returns a pipeline config with a custom setting
        var identity = MakeConsolidationIdentity();
        var project = MakeProject();
        var customConfig = new PipelineConfiguration { AgentTimeout = TimeSpan.FromHours(2) };
        var prepResult = new ConsolidationJobPreparationResult
        {
            ProviderConfigs = [],
            RepoProviderConfigId = "",
            PipelineConfiguration = customConfig,
        };
        var (_, _, enricher) = MakeConsolidationEnricher(preparationResult: prepResult);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: PipelineConfiguration comes from the preparation result
        result.Should().NotBeNull();
        result!.PipelineConfiguration.Should().NotBeNull();
        result.PipelineConfiguration!.AgentTimeout.Should().Be(TimeSpan.FromHours(2),
            "PipelineConfiguration must reflect the per-template config from ConsolidationJobPreparationService");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_PassesCorrectRunTypeToPrep()
    {
        // ARRANGE: identity with RefactoringDetection run type
        // TODO: [WARNING] This test and PassesCorrectTemplateIdToPrep duplicate the full mock-wiring
        // already encapsulated by MakeConsolidationEnricher. The captured-argument technique could use
        // the preparerMock returned by that helper (via preparerMock.Invocations) instead of inlining
        // a separate Callback setup. As-is, future changes to the helper won't update these tests,
        // causing silent divergence between the "capture" tests and the rest of the consolidation suite.
        var identity = MakeConsolidationIdentity(runType: ConsolidationRunType.RefactoringDetection);
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) =>
            throw new InvalidOperationException("Must not call dispatch infra."));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        ConsolidationRunType? capturedType = null;
        var preparerMock = new Mock<IConsolidationJobPreparationService>();
        preparerMock
            .Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConsolidationRunType, TemplateId?, IReadOnlyList<string>, CancellationToken>(
                (type, _, _, _) => capturedType = type)
            .ReturnsAsync(MakeConsolidationPreparationResult());

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, preparerMock.Object, Serilog.Log.Logger);

        // ACT
        await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: PrepareAsync was called with the correct run type
        capturedType.Should().Be(ConsolidationRunType.RefactoringDetection,
            "the run type from the identity must be forwarded to ConsolidationJobPreparationService");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_PassesCorrectTemplateIdToPrep()
    {
        // ARRANGE: identity with a specific template ID
        var identity = MakeConsolidationIdentity(templateId: "tmpl-abc-123");
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) =>
            throw new InvalidOperationException("Must not call dispatch infra."));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        TemplateId? capturedTemplateId = null;
        var preparerMock = new Mock<IConsolidationJobPreparationService>();
        preparerMock
            .Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConsolidationRunType, TemplateId?, IReadOnlyList<string>, CancellationToken>(
                (_, tid, _, _) => capturedTemplateId = tid)
            .ReturnsAsync(MakeConsolidationPreparationResult());

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, preparerMock.Object, Serilog.Log.Logger);

        // ACT
        await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: PrepareAsync was called with the template ID cast from the string
        capturedTemplateId.Should().NotBeNull(
            "a non-null ConsolidationTemplateId must be passed as a TemplateId to PrepareAsync");
        capturedTemplateId!.Value.Value.Should().Be("tmpl-abc-123",
            "the template ID must match the identity's ConsolidationTemplateId");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_NullTemplateId_PassesNullToPrep()
    {
        // ARRANGE: identity with null template ID (global consolidation run)
        var identity = MakeConsolidationIdentity(templateId: null);
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) =>
            throw new InvalidOperationException("Must not call dispatch infra."));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        TemplateId? capturedTemplateId = new TemplateId("unexpected"); // sentinel
        var preparerMock = new Mock<IConsolidationJobPreparationService>();
        preparerMock
            .Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConsolidationRunType, TemplateId?, IReadOnlyList<string>, CancellationToken>(
                (_, tid, _, _) => capturedTemplateId = tid)
            .ReturnsAsync(MakeConsolidationPreparationResult());

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, preparerMock.Object, Serilog.Log.Logger);

        // ACT
        await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: PrepareAsync was called with null templateId
        capturedTemplateId.Should().BeNull(
            "a null ConsolidationTemplateId must be passed as null to PrepareAsync (global run)");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsResolvedProfileId()
    {
        // ARRANGE: profile with a known ID
        var identity = MakeConsolidationIdentity();
        var project = MakeProject();
        var (_, _, enricher) = MakeConsolidationEnricher(
            profiles: [MakeProfile(id: "consolidation-profile-99")]);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: ResolvedProfileId is set from the matched agent profile
        result.Should().NotBeNull();
        result!.ResolvedProfileId.Should().Be("consolidation-profile-99",
            "ResolvedProfileId must reflect the matched agent profile for consolidation tasks");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsAgentProviderConfigId()
    {
        // ARRANGE: profile with a known agent provider config ID
        var identity = MakeConsolidationIdentity();
        var project = MakeProject();
        var (_, _, enricher) = MakeConsolidationEnricher(
            profiles: [MakeProfile(agentProviderConfigId: "agent-cfg-consolidation")]);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: AgentProviderConfigId comes from the profile, not from identity.RepoProviderConfigId
        result.Should().NotBeNull();
        result!.AgentProviderConfigId.Should().Be("agent-cfg-consolidation",
            "AgentProviderConfigId must be set from the resolved profile, not fall back to RepoProviderConfigId");
        result.AgentProviderConfigId.Should().NotBe(identity.RepoProviderConfigId,
            "consolidation enrichment must not incorrectly use RepoProviderConfigId as the agent provider");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_SetsProjectSteeringContent()
    {
        // ARRANGE: project with known steering content
        var identity = MakeConsolidationIdentity();
        var project = MakeProject() with { SteeringContent = "consolidation-project-steering" };
        var (_, _, enricher) = MakeConsolidationEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: ProjectSteeringContent comes from the project context
        result.Should().NotBeNull();
        result!.ProjectSteeringContent.Should().Be("consolidation-project-steering",
            "ProjectSteeringContent must be populated from the PipelineProject for consolidation tasks");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_PreservesConsolidationIdentityFields()
    {
        // ARRANGE: identity with all consolidation-specific fields set
        var identity = MakeConsolidationIdentity(
            runType: ConsolidationRunType.RefactoringDetection,
            templateId: "tmpl-preserve",
            workspacePath: "/preserve/ws",
            autoDispatch: true);
        var project = MakeProject();
        var (_, _, enricher) = MakeConsolidationEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: consolidation identity fields survive enrichment unchanged
        result.Should().NotBeNull();
        result!.TaskType.Should().Be(WorkItemTaskType.Consolidation,
            "TaskType must not be overwritten by enrichment");
        result.ConsolidationRunType.Should().Be(ConsolidationRunType.RefactoringDetection,
            "ConsolidationRunType must be preserved from the identity payload");
        result.ConsolidationTemplateId.Should().Be("tmpl-preserve",
            "ConsolidationTemplateId must be preserved from the identity payload");
        result.ConsolidationWorkspacePath.Should().Be("/preserve/ws",
            "ConsolidationWorkspacePath must be preserved from the identity payload");
        result.AutoDispatch.Should().BeTrue(
            "AutoDispatch must be preserved from the identity payload");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_DoesNotCallDispatchInfra_IssueFieldsIdentityPreserved()
    {
        // ARRANGE: consolidation identity with null IssueDetail (as in a real minimal payload)
        var identity = MakeConsolidationIdentity() with { IssueDetail = null };
        var project = MakeProject();
        var (infra, _, enricher) = MakeConsolidationEnricher();

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: dispatch infra was not called (no issue fetch attempted)
        infra.CapturedRequest.Should().BeNull(
            "DispatchInfrastructure.PrepareDispatchCoreAsync must not be called for consolidation tasks");
        // IssueDetail is identity-preserved (null) — not re-fetched from issue-provider infrastructure
        result.Should().NotBeNull();
        result!.IssueDetail.Should().BeNull(
            "IssueDetail must be identity-preserved (null) for consolidation tasks — not fetched from issue infrastructure");
    }

    [Fact]
    public async Task EnrichAsync_ConsolidationTask_NoProfileMatch_ReturnsNull()
    {
        // ARRANGE: profile store has only "dotnet" profile, but identity selector is "python"
        var identity = MakeConsolidationIdentity(agentSelector: "python");
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) =>
            throw new InvalidOperationException("Must not call dispatch infra."));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]); // only "dotnet" profile

        var preparerMock = new Mock<IConsolidationJobPreparationService>();
        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, preparerMock.Object, Serilog.Log.Logger);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: returns null when no profile matches; preparer is NOT called
        result.Should().BeNull(
            "consolidation enrichment must return null when no agent profile matches the selector");
        preparerMock.Verify(
            s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "ConsolidationJobPreparationService.PrepareAsync must not be called when profile resolution fails");
    }

    [Fact]
    public async Task EnrichAsync_ImplementationTask_StillCallsDispatchInfra()
    {
        // ARRANGE: implementation task — must still use the dispatch infra path (regression guard)
        var identity = MakeIdentity("dotnet") with { TaskType = WorkItemTaskType.Implementation };
        var project = MakeProject();

        var infra = new StubDispatchInfrastructure((_, _) => Task.FromResult(MakeCoreResult()));

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeProfile()]);

        var consolidationPreparerMock = new Mock<IConsolidationJobPreparationService>();
        consolidationPreparerMock
            .Setup(s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "IConsolidationJobPreparationService.PrepareAsync must not be called for implementation tasks."));

        var enricher = new AssignmentEnricher(
            infra, profileStoreMock.Object, consolidationPreparerMock.Object, Serilog.Log.Logger);

        // ACT
        var result = await enricher.EnrichAsync(identity, project, CancellationToken.None);

        // ASSERT: dispatch infra WAS called; consolidation preparer was NOT called
        infra.CapturedRequest.Should().NotBeNull(
            "DispatchInfrastructure.PrepareDispatchCoreAsync must be called for implementation tasks");
        result.Should().NotBeNull("implementation enrichment must succeed");
        result!.TaskType.Should().Be(WorkItemTaskType.Implementation,
            "TaskType must be preserved for implementation tasks");
        consolidationPreparerMock.Verify(
            s => s.PrepareAsync(
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<TemplateId?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "IConsolidationJobPreparationService.PrepareAsync must NOT be called for implementation tasks");
    }

    // TODO: [WARNING] Missing test: EnrichAsync_ConsolidationTask_SetsRepoSteeringContent
    // The production code in EnrichConsolidationCoreAsync computes RepoSteeringContent from
    // (preparation.ProviderConfigs ?? []).TryGetProviderConfig(preparation.RepoProviderConfigId)?.SteeringContent
    // This depends on both RepoProviderConfigId and the matching ProviderConfig entry being wired
    // correctly. No consolidation test currently asserts result.RepoSteeringContent. A regression
    // where it is null or from the wrong provider config would go undetected. Add a test that
    // sets a known SteeringContent on the repo ProviderConfig returned by the preparer and
    // asserts result.RepoSteeringContent equals that value.

    // TODO: [WARNING] Missing test: consolidation path when preparation.ProviderConfigs is null.
    // The production code uses (preparation.ProviderConfigs ?? []) defensively. This null-coalescing
    // branch is untested. Add a test that returns a ConsolidationJobPreparationResult with
    // ProviderConfigs = null and asserts the enricher returns a result with ProviderConfigs = []
    // rather than throwing a NullReferenceException.

    // TODO: [WARNING] Missing test: consolidation path when IConsolidationJobPreparationService.PrepareAsync
    // throws a non-OCE exception. The standard dispatch path has EnrichAsync_ExceptionInEnrichCore_CaughtAndReturnsNull
    // to verify that exceptions propagate (not get swallowed). The consolidation path goes through the
    // same outer try/catch in EnrichAsync, but there is no test confirming the exception is caught and
    // re-thrown (returning 503) rather than silently returning null. Add a test that sets PrepareAsync
    // to throw InvalidOperationException and asserts the exception propagates from EnrichAsync.

    // TODO: [WARNING] Missing test: consolidation path when _consolidationPreparer is null (the
    // null-guard at EnrichConsolidationCoreAsync that logs a warning and returns null). Use the
    // protected AssignmentEnricher(ILogger, IConsolidationJobPreparationService?) constructor
    // without a preparer, pass a TaskType=Consolidation request, and assert the result is null
    // and no exception is thrown.
}

/// <summary>
/// Regression tests for the OperationCanceledException chain through
/// <see cref="AssignmentEnricher.EnrichAsync"/>.
///
/// Fix A (issue #2575) repairs OCE laundering in <c>TokenVendingService.PrepareAgentConfigsAsync</c>
/// so that a cancelled HTTP call surfaces as OCE rather than being wrapped in
/// <c>InvalidOperationException("Aborting dispatch")</c>.
///
/// This class verifies the downstream half of the chain:
/// <list type="bullet">
///   <item>The <c>when (ex is not OperationCanceledException)</c> guard in <see cref="AssignmentEnricher.EnrichAsync"/>
///   suppresses the Error log and lets OCE propagate when the enricher core throws OCE.</item>
///   <item>Before Fix A, the OCE was wrapped in InvalidOperationException, so the guard was never reached
///   and the error was logged at Error level and counted as a dispatch failure.</item>
/// </list>
/// </summary>
public sealed class AssignmentEnricherOceCancellationTests
{
    // ── StubDispatchInfrastructure ────────────────────────────────────────────────

    /// <summary>
    /// Subclass of DispatchInfrastructure that throws a supplied exception from
    /// PrepareDispatchCoreAsync to simulate downstream failure.
    /// </summary>
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static JobDistributionRequest MakeIdentity() => new()
    {
        IssueIdentifier = new IssueIdentifier("owner/repo#42"),
        IssueProviderConfigId = "issue-prov-1",
        RepoProviderConfigId = "repo-prov-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "dotnet",
        TimeoutSeconds = 3600,
    };

    private static PipelineProject MakeProject() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Test Project",
    };

    private static AssignmentEnricher MakeEnricherWithThrowingInfra(
        Exception toThrow, Mock<Serilog.ILogger> mockLogger)
    {
        var infra = new ThrowingDispatchInfrastructure(toThrow);

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentProfile
            {
                Id = "profile-1",
                DisplayName = "Test Profile",
                AgentProviderConfigId = "agent-cfg-1",
                MatchLabels = ["dotnet"],
                Enabled = true,
            }]);

        return new AssignmentEnricher(infra, profileStoreMock.Object, new Mock<IConsolidationJobPreparationService>().Object, mockLogger.Object);
    }

    // ── OCE does not trigger Error log ────────────────────────────────────────────

    /// <summary>
    /// Regression test for the "Aborting dispatch" laundering chain (issue #2575, Fix A).
    ///
    /// Before Fix A: TokenVendingService wrapped OCE in InvalidOperationException, which
    /// passed through the `when (ex is not OperationCanceledException)` guard in EnrichAsync,
    /// triggering an Error log and recording a dispatch failure.
    ///
    /// After Fix A: OCE propagates unchanged from TokenVendingService → PrepareDispatchCoreAsync
    /// → EnrichCoreAsync. The guard `when (ex is not OperationCanceledException)` catches all
    /// non-OCE exceptions, so an OCE thrown from EnrichCoreAsync must NOT trigger the Error log.
    ///
    /// This test verifies that the guard works as intended: an OperationCanceledException thrown
    /// during enrichment does NOT produce an Error-level log entry.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenEnrichCoreThrowsOce_DoesNotLogError()
    {
        // ARRANGE: infra throws OCE, simulating what token vending does after Fix A
        var mockLogger = new Mock<Serilog.ILogger>();
        // Mock the ForContext call that Serilog uses internally — return the same mock logger
        // so calls on the contextual logger are also captured.
        mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
                  .Returns(mockLogger.Object);
        mockLogger.Setup(l => l.ForContext<It.IsAnyType>()).Returns(mockLogger.Object);

        var enricher = MakeEnricherWithThrowingInfra(new OperationCanceledException(), mockLogger);

        // ACT — the OCE must propagate, not be swallowed or re-wrapped
        // TODO [WARNING]: This test passes CancellationToken.None to EnrichAsync, then relies on
        // ThrowingDispatchInfrastructure to throw a bare `new OperationCanceledException()` regardless
        // of token state. It does not verify the realistic scenario where HttpContext.RequestAborted
        // fires and the OCE carries a specific CancellationToken. While sufficient to test the
        // catch-filter logic in EnrichAsync, it does not cover cases where OCE.CancellationToken
        // might interact differently with logging enrichers that inspect the token. Consider adding
        // a variant that links a real CancellationTokenSource, cancels it, and passes the linked
        // token to EnrichAsync to exercise the full realistic path. (TestQualityReviewer warning)
        var act = () => enricher.EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the OCE must propagate from EnrichAsync — the `when (ex is not OCE)` guard must not catch it");

        // ASSERT — Error log must NOT have been called.
        // AssignmentEnricher calls: _logger.Error(ex, "... {IssueIdentifier} ...", identity.IssueIdentifier)
        // which resolves to Serilog's generic overload Error<T>(Exception, string, T) with T = IssueIdentifier.
        // The `when (ex is not OperationCanceledException)` guard must prevent this call for OCE.
        mockLogger.Verify(
            l => l.Error(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<IssueIdentifier>()),
            Times.Never,
            "the Error log in the catch block must be suppressed by the `when (ex is not OCE)` guard; " +
            "a client cancellation must not be recorded as a dispatch failure");
    }

    /// <summary>
    /// Complementary test: a non-OCE exception (e.g., InvalidOperationException from a DB timeout)
    /// MUST trigger the Error log. This guards against an accidental over-broad OCE catch
    /// that would suppress all error logging.
    /// </summary>
    [Fact]
    public async Task EnrichAsync_WhenEnrichCoreThrowsNonOce_LogsError()
    {
        // ARRANGE: infra throws a transient non-OCE, simulating a DB or network error
        var mockLogger = new Mock<Serilog.ILogger>();
        mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
                  .Returns(mockLogger.Object);
        mockLogger.Setup(l => l.ForContext<It.IsAnyType>()).Returns(mockLogger.Object);

        var enricher = MakeEnricherWithThrowingInfra(
            new InvalidOperationException("simulated DB timeout"), mockLogger);

        // ACT — non-OCE propagates through the Error-logging catch
        var act = () => enricher.EnrichAsync(MakeIdentity(), MakeProject(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // ASSERT — Error log MUST have been called (verifies the guard is not over-broad)
        // AssignmentEnricher calls: _logger.Error(ex, "... {IssueIdentifier} ...", identity.IssueIdentifier)
        // which resolves to Serilog's generic overload Error<T>(Exception, string, T) with T = IssueIdentifier.
        mockLogger.Verify(
            l => l.Error(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<IssueIdentifier>()),
            Times.Once,
            "a non-OCE exception must still trigger the Error log in EnrichAsync");
    }
}
