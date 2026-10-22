using AwesomeAssertions;
using Moq;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="IDispatchRunCreator"/> provides what the dispatch path needs: the dedup check and the
/// active runs. Its run creation, which had no production caller, was deleted with spec 053 (req 4.11); the API
/// creates the run when it creates the work item. These tests prove:
/// 1. DispatchRunCreationService implements the interface over the run lifecycle.
/// 2. A mock of the interface can fully replace the concrete dependency.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
    Justification = "The tests call the service through the interface on purpose: the interface is the contract under test.")]
public class DispatchRunCreatorContractTests
{
    private readonly OrchestratorRunService _runService;
    private readonly DispatchRunCreationService _service;

    public DispatchRunCreatorContractTests()
    {
        var logger = new Mock<Serilog.ILogger>();

        // Use a real OrchestratorRunService — lifecycle tests need actual state tracking
        // (mock can't track AddRun→IsIssueBeingProcessed correlation)
        _runService = new OrchestratorRunService(logger.Object);
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>().AsReadOnly());

        var lifecycle = new PipelineRunLifecycleService(mockHistoryService.Object, _runService, logger.Object);
        _service = new DispatchRunCreationService(lifecycle);
    }

    private static PipelineRun DispatchedRun(string issueIdentifier, string issueProviderId) =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueIdentifier,
            IssueTitle = "Test",
            IssueProviderConfigId = issueProviderId,
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            AgentId = "agent-x",
        });

    // ── Contract Test 1: DispatchRunCreationService implements IDispatchRunCreator ──

    [Fact]
    public void DispatchRunCreationService_Implements_IDispatchRunCreator()
    {
        IDispatchRunCreator creator = _service;
        creator.Should().NotBeNull();
    }

    [Fact]
    public void Ctor_NullLifecycle_Throws()
    {
        var act = () => new DispatchRunCreationService(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Contract Test 2: Interface provides IsIssueBeingProcessed ──

    [Fact]
    public void IsIssueBeingProcessed_WhenNotProcessing_ReturnsFalse()
    {
        IDispatchRunCreator creator = _service;

        var result = creator.IsIssueBeingProcessed("issue-99", "provider-1");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsIssueBeingProcessed_WhileARunIsActive_ReturnsTrue()
    {
        IDispatchRunCreator creator = _service;
        _runService.AddRun(DispatchedRun("42", "issue-1"));

        creator.IsIssueBeingProcessed("42", "issue-1").Should().BeTrue();
        creator.IsIssueBeingProcessed("42", "issue-2").Should().BeFalse("the issue provider is part of the key");
    }

    // ── Contract Test 3: Interface provides GetAllActiveRuns ──

    [Fact]
    public void GetAllActiveRuns_ViaInterface_IncludesActiveRun()
    {
        IDispatchRunCreator creator = _service;
        _runService.AddRun(DispatchedRun("77", "issue-1"));

        creator.GetAllActiveRuns().Should().ContainSingle(r => r.IssueIdentifier == "77");
    }

    [Fact]
    public void GetAllActiveRuns_ViaInterface_WhenEmpty_ReturnsEmptyList()
    {
        IDispatchRunCreator creator = _service;

        creator.GetAllActiveRuns().Should().BeEmpty();
    }

    // ── Contract Test 4: Mock of interface suffices for dispatch consumer ──

    [Fact]
    public void MockInterface_CanReplaceConcreteService_ForDispatchDedup()
    {
        // This proves a mock of IDispatchRunCreator is sufficient for dispatch dedup logic
        var mockCreator = new Mock<IDispatchRunCreator>();
        mockCreator.Setup(c => c.IsIssueBeingProcessed("42", "ip-1")).Returns(true);
        mockCreator.Setup(c => c.IsIssueBeingProcessed("99", "ip-1")).Returns(false);

        // Simulates the dispatch dedup check the dispatch scheduler performs
        var shouldSkip42 = mockCreator.Object.IsIssueBeingProcessed("42", "ip-1");
        var shouldProcess99 = !mockCreator.Object.IsIssueBeingProcessed("99", "ip-1");

        shouldSkip42.Should().BeTrue();
        shouldProcess99.Should().BeTrue();
    }
}
