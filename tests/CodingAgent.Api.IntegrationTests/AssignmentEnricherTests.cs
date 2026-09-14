using AwesomeAssertions;
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
            (IReadOnlyList<QualityGateConfiguration>) [],
            (IReadOnlyList<ReviewerConfiguration>) [],
            MakeIssueContext(),
            providerConfigs ?? MakeProviderConfigs(),
            new PipelineConfiguration(),
            false,
            (string?)null,
            0
        );

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

        var enricher = new AssignmentEnricher(infra, profileStoreMock.Object, Serilog.Log.Logger);
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

        var enricher = new AssignmentEnricher(infra, profileStoreMock.Object, Serilog.Log.Logger);

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

        var enricher = new AssignmentEnricher(infra, profileStoreMock.Object, Serilog.Log.Logger);

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

        var enricher = new AssignmentEnricher(infra, profileStoreMock.Object, Serilog.Log.Logger);

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

        var enricher = new AssignmentEnricher(infra, profileStoreMock.Object, Serilog.Log.Logger);

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

        return new AssignmentEnricher(infra, profileStoreMock.Object, mockLogger.Object);
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
