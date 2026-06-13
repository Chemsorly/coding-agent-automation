// Feature: 029-pipeline-projects
// Property 6: Epic Deduplication
// Verify an epic from the project-level provider cannot be dispatched if already in-progress or queued.
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for Epic Deduplication.
/// Verifies that an epic from the project-level provider cannot be dispatched
/// if it is already in-progress or queued, preventing duplicate processing.
/// **Validates: Requirements 14.4**
/// </summary>
public class EpicDeduplicationPropertyTests
{
    /// <summary>
    /// Creates a fresh set of services for each property test invocation.
    /// Avoids state leaking between FsCheck iterations since we cannot access
    /// internal Reset() from this test project.
    /// </summary>
    private static (AgentJobDispatcher Dispatcher, OrchestratorRunService RunService, JobDispatcherService JobService, List<IDisposable> Disposables)
        CreateFreshServices()
    {
        var mockLogger = new Mock<ILogger>();
        var mockConfigStore = new Mock<IConfigurationStore>();
        var mockProviderFactory = new Mock<IProviderFactory>();
        var mockLabelSwapper = new Mock<ILabelSwapper>();
        var mockAgentComm = new Mock<IAgentCommunication>();

        var registry = new AgentRegistryService(mockLogger.Object);
        var jobService = new JobDispatcherService(registry, mockLogger.Object);
        var runService = new OrchestratorRunService(mockLogger.Object);

        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var mockQualityGateValidator = new Mock<IQualityGateValidator>();
        var mockBrainUpdateService = new Mock<IBrainUpdateService>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        var issueParser = new IssueDescriptionParser();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var tokenVending = new TokenVendingService(mockLogger.Object, mockHttpClientFactory.Object);

        var orchestration = new PipelineOrchestrationService(
            mockConfigStore.Object,
            mockProviderFactory.Object,
            issueParser,
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockQualityGateValidator.Object, new PullRequestOrchestrator(mockLogger.Object), new CiLogWriter(mockLogger.Object), new FeedbackService(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            mockBrainUpdateService.Object,
            mockHistoryService.Object,
            runService);

        var dispatcher = new AgentJobDispatcher(
            jobService,
            registry,
            runService,
            orchestration,
            tokenVending,
            mockProviderFactory.Object,
            mockLabelSwapper.Object,
            new DispatchResolutionService(
                new ProfileResolver(),
                new QualityGateResolver(),
                new ReviewerResolver(),
                mockConfigStore.Object,
                mockLogger.Object),
            mockAgentComm.Object,
            new ShutdownSignal(),
            mockLogger.Object);        return (dispatcher, runService, jobService, new List<IDisposable> { orchestration });
    }

    /// <summary>
    /// Property 6a: Epic already in-progress cannot be dispatched.
    /// For any epic identifier that is currently being processed (has an active PipelineRun),
    /// TryDispatchDecompositionAsync returns false — the epic is NOT dispatched again.
    /// **Validates: Requirements 14.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(EpicDeduplicationArbitraries) })]
    public async Task<bool> EpicInProgress_CannotBeDispatched(EpicDeduplicationInput input)
    {
        var (dispatcher, runService, _, disposables) = CreateFreshServices();
        try
        {
            // Arrange: Add a run with the epic identifier (marks it as in-progress)
            runService.AddRun(new PipelineRun
            {
                RunId = $"run-{Guid.NewGuid()}",
                IssueIdentifier = input.EpicIdentifier,
                IssueTitle = "In-Progress Epic",
                IssueProviderConfigId = "ip-test",
                RepoProviderConfigId = "rp-test",
                StartedAt = DateTime.UtcNow
            });

            // Act
            var result = await dispatcher.TryDispatchDecompositionAsync(
                input.EpicIdentifier,
                "Epic Title",
                input.PhaseType,
                "ip-test",
                "rp-test",
                null,
                "loop",
                CancellationToken.None,
                decompositionSource: "project-level");

            // Property: must return false when already in-progress
            return result == false;
        }
        finally
        {
            foreach (var d in disposables) d.Dispose();
        }
    }

    /// <summary>
    /// Property 6b: Epic already queued cannot be dispatched.
    /// For any epic identifier that is currently queued (enqueued in the job dispatcher queue),
    /// TryDispatchDecompositionAsync returns false — the epic is NOT dispatched again.
    /// **Validates: Requirements 14.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(EpicDeduplicationArbitraries) })]
    public async Task<bool> EpicQueued_CannotBeDispatched(EpicDeduplicationInput input)
    {
        var (dispatcher, _, jobService, disposables) = CreateFreshServices();
        try
        {
            // Arrange: Enqueue the epic (marks it as queued)
            jobService.EnqueueJob(new PendingJob
            {
                IssueIdentifier = input.EpicIdentifier,
                IssueProviderId = "ip-test",
                RepoProviderId = "rp-test",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test"
            });

            // Act
            var result = await dispatcher.TryDispatchDecompositionAsync(
                input.EpicIdentifier,
                "Epic Title",
                input.PhaseType,
                "ip-test",
                "rp-test",
                null,
                "loop",
                CancellationToken.None,
                decompositionSource: "project-level");

            // Property: must return false when already queued
            return result == false;
        }
        finally
        {
            foreach (var d in disposables) d.Dispose();
        }
    }

    /// <summary>
    /// Property 6c: Epic NOT in-progress and NOT queued CAN be dispatched.
    /// For any epic identifier that is neither being processed nor queued,
    /// TryDispatchDecompositionAsync returns true (enqueued for dispatch since no idle agents).
    /// **Validates: Requirements 14.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(EpicDeduplicationArbitraries) })]
    public async Task<bool> EpicNotProcessedNotQueued_CanBeDispatched(EpicDeduplicationInput input)
    {
        var (dispatcher, _, _, disposables) = CreateFreshServices();
        try
        {
            // Arrange: ensure the epic is NOT in-progress and NOT queued
            // (fresh services — no run added, no job enqueued for this identifier)

            // Act: With no idle agents, successful dispatch means it was enqueued (returns true)
            var result = await dispatcher.TryDispatchDecompositionAsync(
                input.EpicIdentifier,
                "Epic Title",
                input.PhaseType,
                "ip-test",
                "rp-test",
                null,
                "loop",
                CancellationToken.None,
                decompositionSource: "project-level");

            // Property: must return true (dispatched/enqueued) when not already in-progress or queued
            return result == true;
        }
        finally
        {
            foreach (var d in disposables) d.Dispose();
        }
    }
}

// --- Input type for FsCheck ---

/// <summary>Input for epic deduplication property tests.</summary>
public sealed class EpicDeduplicationInput
{
    /// <summary>Random epic identifier (e.g., "org/repo#42").</summary>
    public required string EpicIdentifier { get; init; }

    /// <summary>Random decomposition phase type.</summary>
    public required PipelineRunType PhaseType { get; init; }

    public override string ToString() =>
        $"Epic={EpicIdentifier}, Phase={PhaseType}";
}

// --- Arbitrary generators ---

/// <summary>
/// FsCheck generators for epic deduplication tests.
/// Generates random epic identifiers and valid decomposition phase types.
/// </summary>
public class EpicDeduplicationArbitraries
{
    private static readonly string[] OrgPool = ["acme", "contoso", "fabrikam", "github", "myorg"];
    private static readonly string[] RepoPool = ["backend", "frontend", "api", "infra", "docs"];

    private static Gen<string> GenEpicIdentifier() =>
        from org in Gen.Elements(OrgPool)
        from repo in Gen.Elements(RepoPool)
        from number in Gen.Choose(1, 9999)
        select $"{org}/{repo}#{number}";

    private static Gen<PipelineRunType> GenPhaseType() =>
        Gen.Elements(PipelineRunType.DecompositionAnalysis, PipelineRunType.Decomposition);

    public static Arbitrary<EpicDeduplicationInput> EpicDeduplicationInputArb()
    {
        var gen =
            from epicId in GenEpicIdentifier()
            from phaseType in GenPhaseType()
            select new EpicDeduplicationInput
            {
                EpicIdentifier = epicId,
                PhaseType = phaseType
            };

        return gen.ToArbitrary();
    }
}
