using System.Globalization;
using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="RefactoringExecutor"/>.
/// Tests: creates no issues when agent finds nothing, handles malformed JSON.
/// </summary>
public class RefactoringExecutorTests : IDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IIssueProvider> _mockIssueProvider = new();
    private readonly Mock<IAgentProvider> _mockAgentProvider = new();
    private readonly string _tempDir;

    public RefactoringExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"refactoring-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        // Default: return empty issue lists so the issue-context query path succeeds
        var emptyResult = new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 50, HasMore = false };
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);
        // Default: return empty closed issues so CollectScanIssueReferencesAsync succeeds without additional setups
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private RefactoringExecutor CreateExecutor() => new(_mockLogger.Object);

    private ConsolidationJobMessage CreateJob(string? jobId = null)
    {
        var id = jobId ?? Guid.NewGuid().ToString();
        return new ConsolidationJobMessage
        {
            JobId = id,
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            TemplateName = "Test Template",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir }
        };
    }

    private const string AggregationPromptMarker = "Produce the final proposals at";

    private ConsolidationJobMessage CreateJobWithoutReview()
    {
        var job = CreateJob();
        return new ConsolidationJobMessage
        {
            JobId = job.JobId,
            Type = job.Type,
            TemplateId = job.TemplateId,
            TemplateName = job.TemplateName,
            ProviderConfigs = job.ProviderConfigs,
            PipelineConfiguration = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = _tempDir,
                RefactoringReviewEnabled = false
            }
        };
    }

    [Fact]
    public async Task ExecuteAsync_AgentFindsNothing_CreatesNoIssues()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        // The aggregation dropped every finding and wrote an empty array
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) => RefactoringTestWorkspace.WriteProposals(path, "[]"))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["No refactoring opportunities found."]
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");

        // Should never call CreateIssueAsync
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The aggregation read the findings, filtered them and stopped without writing the proposals file.
    /// That is a broken step, not a scan that found nothing.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AggregationWritesNoProposalsFile_Fails()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Wrote the analysis log."] });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("The aggregation step wrote no refactoring-proposals.json");
        CompletionOutcomeResolver.Resolve(PipelineStep.Failed, result.ErrorMessage, result.FailureCategory, "fallback")
            .FailureReason.Should().Be(FailureReason.AgentError, "the agent broke off, the platform did not");
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AggregationWritesNoProposalsFileOnce_RetriesAndCreatesIssues()
    {
        var executor = CreateExecutor();
        var job = CreateJobWithoutReview();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);   // do NOT write the proposals file here: that would skip the retry
        _mockAgentProvider                  // general setup first: Phase 0 and Phase 1 calls
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """;

        var aggregationCalls = 0;
        _mockAgentProvider                  // aggregation setup second: Moq uses the last matching setup
            .Setup(x => x.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains(AggregationPromptMarker)), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                aggregationCalls++;
                if (aggregationCalls == 2) RefactoringTestWorkspace.WriteProposals(req.WorkspacePath!, proposalsJson);
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Aggregation done."] });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        aggregationCalls.Should().Be(2);
        _mockIssueProvider.Verify(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AggregationWritesNoProposalsFileTwice_FailsAfterOneRetry()
    {
        var executor = CreateExecutor();
        var job = CreateJobWithoutReview();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);   // do NOT write the proposals file here: that would skip the retry
        _mockAgentProvider                  // general setup first: Phase 0 and Phase 1 calls
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var aggregationCalls = 0;
        _mockAgentProvider                  // aggregation setup second: Moq uses the last matching setup
            .Setup(x => x.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains(AggregationPromptMarker)), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                aggregationCalls++;
                // write nothing: simulates the agent breaking off both times
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Aggregation done."] });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("The aggregation step wrote no refactoring-proposals.json");
        aggregationCalls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_AggregationExitsNonZero_DoesNotRetry()
    {
        var executor = CreateExecutor();
        var job = CreateJobWithoutReview();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);   // do NOT write the proposals file here: that would skip the retry
        _mockAgentProvider                  // general setup first: Phase 0 and Phase 1 calls
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var aggregationCalls = 0;
        _mockAgentProvider                  // aggregation setup second: Moq uses the last matching setup
            .Setup(x => x.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains(AggregationPromptMarker)), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                aggregationCalls++;
                // write nothing and exit non-zero: no retry should happen
            })
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = ["error"] });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Agent exited with code 1");
        aggregationCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AggregationWritesEmptyArray_DoesNotRetry()
    {
        var executor = CreateExecutor();
        var job = CreateJobWithoutReview();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);   // do NOT write the proposals file here: that would skip the retry
        _mockAgentProvider                  // general setup first: Phase 0 and Phase 1 calls
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var aggregationCalls = 0;
        _mockAgentProvider                  // aggregation setup second: Moq uses the last matching setup
            .Setup(x => x.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains(AggregationPromptMarker)), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                aggregationCalls++;
                RefactoringTestWorkspace.WriteProposals(req.WorkspacePath!, "[]");
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Aggregation done."] });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");
        aggregationCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AggregationFirstCallExits0ThenRetryExitsNonZero_FailsWithExitCodeMessage()
    {
        // Covers the guard at src/CodingAgent.Agent/Executors/RefactoringExecutor.cs line 499:
        // if (failure is not null) return (failure, detectors) — inside the retry block.
        // First aggregation exits 0 but writes nothing → retry fires.
        // Retry exits non-zero → the executor returns the exit-code failure, not MissingProposalsFileMessage.
        var executor = CreateExecutor();
        var job = CreateJobWithoutReview();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);   // do NOT write the proposals file here: that triggers the retry
        _mockAgentProvider                  // general setup first: Phase 0 and Phase 1 calls
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var aggregationCalls = 0;
        _mockAgentProvider                  // aggregation setup second: Moq uses the last matching setup
            .Setup(x => x.ExecuteAsync(It.Is<AgentRequest>(r => r.Prompt.Contains(AggregationPromptMarker)), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                aggregationCalls++;
                if (aggregationCalls == 1)
                    return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = ["Aggregation done."] });
                return Task.FromResult(new AgentResult { ExitCode = 1, OutputLines = ["error"] });
            });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Agent exited with code 1");
        aggregationCalls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedJson_ReturnsFailedResult()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        // Setup clone to create workspace and write malformed JSON
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                // Create the .agent directory and write malformed JSON
                var agentDir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(Path.Combine(agentDir, "refactoring-proposals.json"), "{ invalid json [[[");
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Analysis complete."]
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("parse");

        // ScanOutcome must be set even on the malformed-JSON failure; proposal fields must be empty
        result.ScanOutcome.Should().NotBeNull("FinalizeProposalsAsync sets ScanOutcome on the malformed-JSON early exit");
        result.ScanOutcome!.ProposalCount.Should().Be(0, "malformed JSON means no proposals were parsed");
        result.ScanOutcome.CreatedIssues.Should().BeEmpty();
        result.ScanOutcome.DroppedProposals.Should().BeEmpty();
        result.ScanOutcome.UnfiledProposals.Should().BeEmpty();

        // Should never call CreateIssueAsync when JSON is malformed
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ValidProposals_CreatesIssues()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Found 1 refactoring opportunity."]
            });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(1);
        result.Summary.Should().Contain("1");
        result.Summary.Should().Contain("42");

        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(
                It.Is<string>(t => t.Contains("Extract shared validation logic")),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ValidProposals_AccumulatesUsageOfEveryAgentCall()
    {
        // Arrange — same proposals setup as ExecuteAsync_ValidProposals_CreatesIssues,
        // but with RefactoringReviewEnabled = false (removes adversarial review calls)
        // and Usage set on every agent response.
        //
        // With RefactoringFocus = All (default) and RefactoringReviewEnabled = false:
        //   Phase 0 (refactoring_conventions), Phase 1 ×4 (structural/correctness/design/test-gaps
        //   all get named phases), Phase 2 (refactoring_aggregation) → 6 agent calls total.
        //   All 6 named-phase calls accumulate tokens: 6 × 100 InputTokens = TotalTokens 600.
        var job = CreateJob();
        job = new ConsolidationJobMessage
        {
            JobId = job.JobId,
            Type = job.Type,
            TemplateId = job.TemplateId,
            TemplateName = job.TemplateName,
            ProviderConfigs = job.ProviderConfigs,
            PipelineConfiguration = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = _tempDir,
                RefactoringReviewEnabled = false
            }
        };

        var run = ConsolidationProgress.CreateRun(job);
        var progress = new ConsolidationProgress(run, _ => { });
        var executor = new RefactoringExecutor(_mockLogger.Object, progress);

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Analysis complete."],
                Usage = new TokenUsage { InputTokens = 100 }
            });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        progress.Run.TotalTokens.Should().Be(600,
            "Phase 0 + 4 Phase 1 agents (structural/correctness/design/test-gaps) + Phase 2 = 6 calls × 100 tokens; " +
            "all Phase 1 agents now have named usage phases (refactoring_test_gaps fixed in issue #3860)");
    }

    [Fact]
    public async Task Phase1_RecordsUsageForEveryAgent()
    {
        // Verify that all four Phase 1 agents (structural, correctness, design, test-gaps)
        // accumulate token usage after fix for issue #3860 (test-gaps previously had usagePhase=null).
        var job = CreateJob();
        job = new ConsolidationJobMessage
        {
            JobId = job.JobId,
            Type = job.Type,
            TemplateId = job.TemplateId,
            TemplateName = job.TemplateName,
            ProviderConfigs = job.ProviderConfigs,
            PipelineConfiguration = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = _tempDir,
                RefactoringReviewEnabled = false
            }
        };

        var run = ConsolidationProgress.CreateRun(job);
        var progress = new ConsolidationProgress(run, _ => { });
        var executor = new RefactoringExecutor(_mockLogger.Object, progress);

        var proposalsJson = """
            [
                {
                    "title": "Example proposal",
                    "affectedFiles": ["src/Service.cs"],
                    "description": "Example description.",
                    "rationale": "Example rationale."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Analysis complete."],
                Usage = new TokenUsage { InputTokens = 100 }
            });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        // Act — focus=All runs all four Phase 1 agents
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — all four Phase 1 phases must appear in the breakdown
        var phaseKeys = progress.Run.Metrics.PhaseBreakdown.Keys.ToHashSet();
        phaseKeys.Should().Contain("refactoring_structural", "structural phase must record usage");
        phaseKeys.Should().Contain("refactoring_correctness", "correctness phase must record usage");
        phaseKeys.Should().Contain("refactoring_design", "design phase must record usage");
        phaseKeys.Should().Contain("refactoring_test_gaps",
            "test-gaps phase must record usage after fix for issue #3860");
    }

    [Fact]
    public void FormatRefactoringSummary_NoIssues_ReturnsNoOpportunities()
    {
        // Act
        var summary = RefactoringIssueBodyFormatter.FormatRefactoringSummary([]);

        // Assert
        summary.Should().Contain("No refactoring opportunities identified");
    }

    [Fact]
    public void FormatRefactoringSummary_WithIssues_IncludesCountAndIdentifiers()
    {
        // Arrange
        var issues = new List<CreatedIssueInfo>
        {
            new() { Identifier = "10", Title = "Fix duplication", Url = "https://example.com/10" },
            new() { Identifier = "11", Title = "Rename methods", Url = "https://example.com/11" }
        };

        // Act
        var summary = RefactoringIssueBodyFormatter.FormatRefactoringSummary(issues);

        // Assert
        summary.Should().Contain("2");
        summary.Should().Contain("#10");
        summary.Should().Contain("#11");
    }

    [Fact]
    public async Task ExecuteAsync_IssueQuerySucceeds_PromptContainsIssueContext()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        var openIssues = new PagedResult<IssueSummary>
        {
            Items =
            [
                new IssueSummary
                {
                    Identifier = "100", Title = "Extract retry logic", Labels = ["agent:generated"], CreatedAt = DateTime.UtcNow.AddDays(-5),
                    Description = $"## Problem\n...\n*{RefactoringIssueBodyFormatter.GeneratedIssueFooter}.*"
                },
                new IssueSummary { Identifier = "200", Title = "Add caching layer", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-2) }
            ],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openIssues);

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — the scan's own issue is told apart by its footer, not by a label
        capturedPrompt.Should().Contain("Do Not Duplicate");
        capturedPrompt.Should().Contain("### Open Refactoring Scan Issues (still pending)\n- #100 \"Extract retry logic\"".ReplaceLineEndings());
        capturedPrompt.Should().Contain("### Other Open Issues (may overlap)\n- #200 \"Add caching layer\"".ReplaceLineEndings());
    }

    [Fact]
    public async Task ExecuteAsync_IssueQueryThrows_ContinuesWithoutContext()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider unavailable"));

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) => RefactoringTestWorkspace.WriteProposals(path, "[]"))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        var result = await executor.ExecuteAsync(job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — run should succeed (graceful degradation), not fail due to issue query
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_IssueQueryReturnsOldIssues_IncludesThem()
    {
        // Arrange — an open issue is a duplicate risk however old it is
        var executor = CreateExecutor();
        var job = CreateJob();

        var oldIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "50", Title = "Old issue", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-60) }],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldIssues);

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        capturedPrompt.Should().Contain("Do Not Duplicate");
        capturedPrompt.Should().Contain("#50 \"Old issue\"");
    }

    [Fact]
    public async Task ExecuteAsync_ClosedIssueQueryFails_ContinuesWithoutContext()
    {
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) => RefactoringTestWorkspace.WriteProposals(path, "[]"))
            .Returns(Task.CompletedTask);

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API failure"));

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Should succeed (graceful degradation) — the aggregation wrote [] (no proposals)
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");
    }

    [Fact]
    public async Task ExecuteAsync_ClosedIssuesFound_InjectsOutcomeContextIntoPrompt()
    {
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var scanFooter = $"*{RefactoringIssueBodyFormatter.GeneratedIssueFooter}.*";
        var closedIssues = new PagedResult<IssueSummary>
        {
            Items = new[]
            {
                new IssueSummary { Identifier = "100", Title = "Implemented refactoring", Labels = new[] { "agent:generated", "agent:done" }, Description = scanFooter },
                new IssueSummary { Identifier = "101", Title = "Rejected refactoring", Labels = new[] { "agent:generated", "agent:wont-do" }, Description = scanFooter },
                // A decomposition sub-issue shares agent:generated but is not a scan proposal
                new IssueSummary { Identifier = "102", Title = "Sub-issue of an epic", Labels = new[] { "agent:generated", "agent:done" }, Description = "## Summary\nPart 2 of the epic." }
            },
            Page = 1,
            PageSize = 50,
            HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(1, 50,
                It.Is<IReadOnlyList<string>>(l => l.Contains("agent:generated")),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(closedIssues);
        _mockIssueProvider
            .Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        AgentRequest? capturedRequest = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedRequest = req)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Prompt.Should().Contain("Past Proposal Outcomes");
        capturedRequest.Prompt.Should().Contain("#100 \"Implemented refactoring\"");
        capturedRequest.Prompt.Should().Contain("#101 \"Rejected refactoring\"");
        capturedRequest.Prompt.Should().NotContain("Sub-issue of an epic");
        capturedRequest.Prompt.Should().Contain("Do NOT propose refactorings similar to rejected items above.");
    }

    [Fact]
    public async Task ExecuteAsync_ClosedScanIssueHasImplementerFeedback_InjectsTheLatestIntoPrompt()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var closedIssues = new PagedResult<IssueSummary>
        {
            Items =
            [
                new IssueSummary
                {
                    Identifier = "3236", Title = "Stop swallowing cancellation", Labels = ["agent:generated", "agent:done"],
                    Description = $"*{RefactoringIssueBodyFormatter.GeneratedIssueFooter}.*"
                }
            ],
            Page = 1,
            PageSize = 50,
            HasMore = false
        };
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(closedIssues);

        static IssueComment Comment(string id, string body, int day) =>
            new() { Id = id, Author = "coding-agent-webui", Body = body, CreatedAt = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc) };
        var older = FeedbackCommentFormatter.FormatComment(new IssueFeedback { Category = "stale", Description = "Older note." })!;
        var latest = FeedbackCommentFormatter.FormatComment(new IssueFeedback
        {
            Category = "partial scope",
            Description = "The outer catch at L777 was missed."
        })!;
        _mockIssueProvider
            .Setup(x => x.ListCommentsAsync(It.Is<IssueIdentifier>(i => i.Value == "3236"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Comment("1", "## 🤖 Agent Analysis\nplan", 1), Comment("2", older, 2), Comment("3", latest, 3) });

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        capturedPrompt.Should().Contain("- #3236 \"Stop swallowing cancellation\" — partial scope: The outer catch at L777 was missed.");
        capturedPrompt.Should().NotContain("Older note.");
    }

    [Fact]
    public async Task ExecuteAsync_CommentsOfAPastIssueCannotBeRead_KeepsTheOutcomeContext()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items =
                [
                    new IssueSummary
                    {
                        Identifier = "7", Title = "Remove dead helper", Labels = ["agent:generated", "agent:done"],
                        Description = $"*{RefactoringIssueBodyFormatter.GeneratedIssueFooter}.*"
                    }
                ],
                Page = 1,
                PageSize = 50,
                HasMore = false
            });
        _mockIssueProvider
            .Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        capturedPrompt.Should().Contain("#7 \"Remove dead helper\"");
        capturedPrompt.Should().NotContain("Implementer Feedback");
    }

    [Fact]
    public void FormatIssueBody_WithNewFields_RendersMetadata()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Extract validation",
            AffectedFiles = ["src/A.cs"],
            Description = "Extract shared logic",
            Rationale = "DRY violation",
            Prerequisites = ["Add tests for A.cs"],
            EstimatedEffort = "medium",
            RiskLevel = "low",
            Technique = "Extract Method"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("**Effort:** medium");
        body.Should().Contain("**Risk:** low");
        body.Should().Contain("**Technique:** Extract Method");
        body.Should().Contain("## Prerequisites");
        body.Should().Contain("- Add tests for A.cs");
    }

    [Fact]
    public void FormatIssueBody_WithNullFields_OmitsOptionalSections()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Rename methods",
            AffectedFiles = ["src/X.cs"],
            Description = "Inconsistent naming",
            Rationale = "Convention violation"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().NotContain("**Effort:**");
        body.Should().NotContain("**Risk:**");
        body.Should().NotContain("**Technique:**");
        body.Should().NotContain("## Prerequisites");
        body.Should().NotContain("## Scope");
        body.Should().NotContain("## Evidence");
        body.Should().Contain("## Problem");
        body.Should().Contain("## Affected Components");
    }

    [Fact]
    public void ParseHotspotOutput_WithValidOutput_ReturnsFormattedSummary()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = "COMMIT_DATE:2026-07-19 14:00:00 +0000\nsrc/File1.cs\nsrc/File2.cs\nCOMMIT_DATE:2026-07-18 10:00:00 +0000\nsrc/File1.cs\nsrc/File1.cs\nsrc/File2.cs\n";

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Summary.Should().NotBeNull();
        result.Summary.Should().Contain("src/File1.cs");
        result.Summary.Should().Contain("3 changes");
        result.Summary.Should().Contain("src/File2.cs");
        result.Summary.Should().Contain("2 changes");
        result.Summary.Should().Contain("last 90 days");
        result.Summary.Should().Contain("time-decay weighted");
        result.Summary.Should().Contain("avg");
        result.Summary.Should().Contain("days ago");
    }

    [Fact]
    public void ParseHotspotOutput_WithEmptyOutput_ReturnsNull()
    {
        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput("", TimeSpan.FromDays(90));

        result.Summary.Should().BeNull();
    }

    [Fact]
    public void ParseHotspotOutput_WithOnlyWhitespace_ReturnsNull()
    {
        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput("\n\n  \n", TimeSpan.FromDays(90));

        result.Summary.Should().BeNull();
    }

    [Fact]
    public void ParseHotspotOutput_CapsAtThirtyFiles()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("COMMIT_DATE:2026-07-19 10:00:00 +0000");
        for (int i = 0; i < 50; i++)
            sb.AppendLine($"src/File{i:D2}.cs");

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(sb.ToString(), TimeSpan.FromDays(90), referenceTime);

        result.Summary.Should().NotBeNull();
        // Each file appears once, so 30 lines with score data
        var lines = result.Summary!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        lines.Should().HaveCount(30);
    }

    [Fact]
    public void ParseHotspotOutput_SortsDescendingByScore()
    {
        // Common.cs has 3 recent changes (high score), Medium.cs has 2 recent changes, Rare.cs has 1
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = "COMMIT_DATE:2026-07-19 10:00:00 +0000\nsrc/Rare.cs\nsrc/Common.cs\nsrc/Common.cs\nsrc/Common.cs\nsrc/Medium.cs\nsrc/Medium.cs\n";

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Summary.Should().NotBeNull();
        var lines = result.Summary!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        lines[0].Should().Contain("src/Common.cs");
        lines[1].Should().Contain("src/Medium.cs");
        lines[2].Should().Contain("src/Rare.cs");
    }

    [Fact]
    public void ParseHotspotOutput_RecentChangesScoreHigherThanOldChangesWithSameCount()
    {
        // Both files have 3 changes, but RecentFile's changes are 1 day old vs OldFile's 80 days old
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:2026-07-19 12:00:00 +0000\nsrc/RecentFile.cs\nsrc/RecentFile.cs\nsrc/RecentFile.cs\n" +
            "COMMIT_DATE:2026-05-01 12:00:00 +0000\nsrc/OldFile.cs\nsrc/OldFile.cs\nsrc/OldFile.cs\n";

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Summary.Should().NotBeNull();
        var lines = result.Summary!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        // RecentFile should rank first (higher score due to recency)
        lines[0].Should().Contain("src/RecentFile.cs");
        lines[1].Should().Contain("src/OldFile.cs");
    }

    [Fact]
    public void ParseHotspotOutput_FewerRecentChangesOutrankManyOldChanges()
    {
        // ActiveFile: 3 changes, 1 day old → factor ≈ 1/(1+1/30) ≈ 0.97 each → score ≈ 2.9
        // StaleFile: 10 changes, 80 days old → factor = 1/(1+80/30) ≈ 0.27 each → score ≈ 2.7
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("COMMIT_DATE:2026-07-19 12:00:00 +0000");
        sb.AppendLine("src/ActiveFile.cs");
        sb.AppendLine("src/ActiveFile.cs");
        sb.AppendLine("src/ActiveFile.cs");
        sb.AppendLine("COMMIT_DATE:2026-05-01 12:00:00 +0000");
        for (int i = 0; i < 10; i++)
            sb.AppendLine("src/StaleFile.cs");

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(sb.ToString(), TimeSpan.FromDays(90), referenceTime);

        result.Summary.Should().NotBeNull();
        var lines = result.Summary!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        // ActiveFile should rank first despite fewer changes
        lines[0].Should().Contain("src/ActiveFile.cs");
        lines[1].Should().Contain("src/StaleFile.cs");
    }

    [Fact]
    public void ParseHotspotOutput_MalformedDateLinesDontCrash()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:not-a-date\nsrc/File1.cs\nsrc/File2.cs\n" +
            "COMMIT_DATE:2026-07-19 12:00:00 +0000\nsrc/File3.cs\n";

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        // Should not throw and should produce output
        result.Summary.Should().NotBeNull();
        result.Summary.Should().Contain("src/File1.cs");
        result.Summary.Should().Contain("src/File2.cs");
        result.Summary.Should().Contain("src/File3.cs");
    }

    [Fact]
    public void FormatIssueBody_WithNullEntriesInPrerequisites_SkipsNulls()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            Description = "desc",
            Rationale = "rationale",
            AffectedFiles = ["src/File.cs"],
            Prerequisites = ["valid prerequisite", null!, "another valid one"]
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("valid prerequisite");
        body.Should().Contain("another valid one");
    }

    [Fact]
    public void FormatIssueBody_SanitizesMetadataFields()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            EstimatedEffort = "<script>alert('xss')</script>",
            RiskLevel = "@admin injection",
            Technique = "low <!-- hidden -->"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("**Effort:** &lt;script>alert('xss')&lt;/script>");
        body.Should().Contain("**Risk:** @\u200Badmin injection");
        body.Should().Contain("**Technique:** low &lt;!-- hidden -->");
    }

    [Fact]
    public async Task RunGitCommandAsync_NonZeroExitCode_ThrowsWithStderr()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"git-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var act = () => RefactoringExecutor.RunGitCommandAsync(tempDir, "log --oneline -1", CancellationToken.None);

            var ex = await act.Should().ThrowAsync<InvalidOperationException>();
            ex.Which.Message.Should().Contain("failed with exit code");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AutoDispatchTrue_CreatesIssuesWithAgentNextLabel()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = new ConsolidationJobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            TemplateName = "Test Template",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            AutoDispatch = true
        };

        var proposalsJson = """
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Shared logic.",
                    "rationale": "DRY."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        IReadOnlyList<string>? capturedLabels = null;
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "10", Url = "https://github.com/t/r/issues/10" });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain("agent:generated");
        capturedLabels.Should().Contain("agent:next");
    }

    [Fact]
    public async Task ExecuteAsync_AutoDispatchFalse_CreatesIssuesWithOnlyGeneratedLabel()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(); // AutoDispatch defaults to false

        var proposalsJson = """
            [
                {
                    "title": "Inline helper method",
                    "affectedFiles": ["src/B.cs"],
                    "description": "Unused abstraction.",
                    "rationale": "Simplicity."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        IReadOnlyList<string>? capturedLabels = null;
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "11", Url = "https://github.com/t/r/issues/11" });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain("agent:generated");
        capturedLabels.Should().NotContain("agent:next");
    }

    // TODO: This test verifies each criterion appears as a checkbox but does not assert positional ordering
    // (i.e., that criteria appear under the "## Acceptance Criteria" heading and before "---"). A section-
    // ordering bug could cause criteria to render in the wrong section without failing this test.
    // TODO: [WARNING] After the S3776 refactoring (#1790), AppendAcceptanceCriteria returns without appending
    // the footer (`---` separator + attribution line); the footer is now appended by the calling method after
    // AppendAcceptanceCriteria returns. A regression that re-orders footer-before-criteria would not be caught
    // by this test. Consider adding an assertion that the `---` separator appears *after* the last criterion.
    [Fact]
    public void FormatIssueBody_WithPopulatedAcceptanceCriteria_RendersAllItemsAsCheckboxes()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = ["Criterion A", "Criterion B", "Criterion C"]
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] Criterion A");
        body.Should().Contain("- [ ] Criterion B");
        body.Should().Contain("- [ ] Criterion C");
        body.Should().NotContain("Refactoring applied without changing observable behavior");
        body.Should().NotContain("All existing tests continue to pass");
    }

    [Fact]
    public void FormatIssueBody_WithNullAcceptanceCriteria_RendersFallbackAC()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = null
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] Refactoring applied without changing observable behavior");
        body.Should().Contain("- [ ] All existing tests continue to pass");
    }

    [Fact]
    public void FormatIssueBody_WithEmptyAcceptanceCriteria_RendersFallbackAC()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = []
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] Refactoring applied without changing observable behavior");
        body.Should().Contain("- [ ] All existing tests continue to pass");
    }

    [Fact]
    public void FormatIssueBody_WithNullEntriesInAcceptanceCriteria_SkipsNulls()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = ["valid one", null!, "another valid"]
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] valid one");
        body.Should().Contain("- [ ] another valid");
    }

    // TODO: No test verifies the boundary condition where AcceptanceCriteria contains exactly 1 item or
    // more than 4 items. The issue specifies "Range: 2-4 criteria per proposal" but the rendering logic
    // doesn't enforce this range. Consider adding tests documenting that single-item lists render correctly
    // without falling through to the fallback.
    [Fact]
    public void FormatIssueBody_SanitizesAcceptanceCriteriaEntries()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = ["@admin injection", "<script>xss</script>"]
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] @\u200Badmin injection");
        body.Should().Contain("- [ ] &lt;script>xss&lt;/script>");
    }

    // ─── Autolink escaping in prerequisites (issue #1450) ───────────────────────

    private static RefactoringProposal Proposal(
        string title, IReadOnlyList<string>? prerequisites = null) => new()
        {
            Title = title,
            AffectedFiles = ["x"],
            Description = "d",
            Rationale = "r",
            Prerequisites = prerequisites
        };

    [Fact]
    public async Task ExecuteAsync_ProposalWithDependsOnInJson_CreatesInFileOrderWithNoDependsOnLines()
    {
        // Requirement 6: when the proposals JSON contains a "dependsOn" field (from an older agent
        // run), the field is silently ignored (System.Text.Json drops unknown properties by default),
        // proposals are created in file order, and neither issue body contains a "Depends on" line.
        var executor = CreateExecutor();
        var job = CreateJob();

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Validator.cs"],
                    "description": "Duplicated validation logic.",
                    "rationale": "DRY principle."
                },
                {
                    "title": "Simplify callers of validation",
                    "affectedFiles": ["src/Handler.cs"],
                    "description": "Use the extracted validator.",
                    "rationale": "Follows from the extraction.",
                    "dependsOn": ["Extract shared validation logic"]
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var capturedTitles = new List<string>();
        var capturedBodies = new List<string>();
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((title, body, _, _) =>
            {
                capturedTitles.Add(title);
                capturedBodies.Add(body);
            })
            .ReturnsAsync(new CreatedIssueResult { Identifier = "1", Url = "https://github.com/test/repo/issues/1" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — both issues are created in file order, neither body has a "Depends on" line
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(2);
        // TODO: capturedTitles captures the raw title argument passed to CreateIssueAsync, which is
        // the sanitized title (SanitizeTitle is called before CreateIssueAsync). If SanitizeTitle
        // ever transforms these titles (e.g. truncation), this assertion will fail with a misleading
        // message unrelated to the ordering contract being verified. Use SanitizeTitle-aware expected
        // values if the titles become more complex.
        capturedTitles.Should().Equal("Extract shared validation logic", "Simplify callers of validation");
        // TODO: capturedBodies is populated by a separate mock callback, not derived from CreatedIssues.
        // The HaveCount(2) on CreatedIssues above does not guard capturedBodies. If CreateIssueAsync
        // is called fewer than 2 times, the index access below throws ArgumentOutOfRangeException
        // instead of a meaningful FluentAssertions failure. Add capturedBodies.Should().HaveCount(2)
        // here to produce a clear message when the callback count diverges from CreatedIssues.
        capturedBodies[0].Should().NotContain("Depends on");
        capturedBodies[1].Should().NotContain("Depends on");
    }

    [Fact]
    public void FormatIssueBody_PrerequisiteWithHashNumber_EscapesTheAutolink()
    {
        var proposal = Proposal("Extract class",
            prerequisites: ["Complete proposal #1 (dead code removal)", "After #2 is merged"]);

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        // GitHub turns a bare #N into a link to issue N, which is unrelated to the proposal numbering.
        body.Should().Contain("- Complete proposal `#1` (dead code removal)");
        body.Should().Contain("- After `#2` is merged");
    }

    [Fact]
    public void FormatIssueBody_PrerequisiteWithLanguageName_IsNotEscaped()
    {
        var proposal = Proposal("Use primary constructors", prerequisites: ["Requires C# 12 and F#8 support"]);

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- Requires C# 12 and F#8 support");
    }

    [Fact]
    public void FormatIssueBody_PrerequisiteWithoutHashNumber_PassesThrough()
    {
        var proposal = Proposal("Rename", prerequisites: ["Add characterization tests for X before refactoring"]);

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- Add characterization tests for X before refactoring");
    }

    // ── Issue #2681: exception-swallowing / misleading permissions hint ───────────────────────

    private void SetupEmptyClosedIssues()
    {
        var emptyClosedResult = new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 20, HasMore = false };
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyClosedResult);
    }

    private void SetupProposalsFile(string proposalsJson)
    {
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp401_ReturnsFailedResultWithTokenHint()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("401");
        // AC #1: the run summary (not just ErrorMessage) must contain "401" to confirm hint threading
        // through FormatRefactoringSummary. Also verify the old hardcoded string is absent.
        result.Summary.Should().Contain("401");
        result.Summary.Should().NotContain("check GitHub App permissions");
        // TODO [WARNING]: The compound null-or-empty check below accepts a null CreatedIssues, which is a
        // weaker contract than the implementation provides (it always returns a non-null list). Consider
        // replacing with result.CreatedIssues.Should().HaveCount(0) to enforce the non-null contract.
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp403_ReturnsFailedResultWithPermissionsHint()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Remove dead code",
                    "affectedFiles": ["src/B.cs"],
                    "description": "Unused method.",
                    "rationale": "Cleanliness."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("403");
        result.ErrorMessage.Should().NotContain("401");
        // AC #2: the run summary must contain "403" and be distinct from the 401 message to confirm
        // hint threading through FormatRefactoringSummary is intact.
        result.Summary.Should().Contain("403");
        result.Summary.Should().NotContain("401 Unauthorized");
        // TODO [WARNING]: The compound null-or-empty check below accepts a null CreatedIssues, which is a
        // weaker contract than the implementation provides (it always returns a non-null list). Consider
        // replacing with result.CreatedIssues.Should().HaveCount(0) to enforce the non-null contract.
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PartialIssueCreationFailure_ReturnsSuccessTrueWithPartialCountAndErrorMessage()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY."
                },
                {
                    "title": "Remove dead code",
                    "affectedFiles": ["src/B.cs"],
                    "description": "Unused method.",
                    "rationale": "Cleanliness."
                }
            ]
            """);

        _mockIssueProvider
            .SetupSequence(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "10", Url = "https://github.com/test/repo/issues/10" })
            .ThrowsAsync(new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(1);
        // AC #3: ErrorMessage must contain the specific first-failure hint, not just be non-empty.
        // The second proposal throws 403, so the hint must identify that classification.
        result.ErrorMessage.Should().Contain("403");
        // TODO [WARNING]: This test does not verify that CreateIssueAsync was called exactly twice.
        // If MaxRefactoringProposals is configured to 1 in CreateJob(), the second proposal (the
        // failing one) is silently skipped, ErrorMessage ends up null, and the partial-failure path
        // is never actually exercised. Add _mockIssueProvider.Verify(x => x.CreateIssueAsync(...),
        // Times.Exactly(2)) after the act step to make this precondition explicit.
    }

    [Fact]
    public void FormatRefactoringSummary_ZeroCreatedNonZeroProposals_WithHint_IncludesHintInSummary()
    {
        // Act
        var summary = RefactoringIssueBodyFormatter.FormatRefactoringSummary(
            [],
            proposalCount: 3,
            firstFailureHint: "401 Unauthorized — token expired or revoked");

        // Assert
        summary.Should().Contain("3");
        summary.Should().Contain("401 Unauthorized — token expired or revoked");
        summary.Should().NotContain("check GitHub App permissions");
    }

    [Fact]
    public void FormatRefactoringSummary_PartialCreation_WithHint_IncludesHintInSummary()
    {
        // Arrange
        var createdIssues = new List<CreatedIssueInfo>
        {
            new() { Identifier = "42", Title = "Extract validation", Url = "https://example.com/42" }
        };

        // Act
        var summary = RefactoringIssueBodyFormatter.FormatRefactoringSummary(
            createdIssues,
            proposalCount: 3,
            firstFailureHint: "403 Forbidden — app missing 'issues: write' permission");

        // Assert
        summary.Should().Contain("1/3");
        summary.Should().Contain("403 Forbidden");
        summary.Should().Contain("2 failed");
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp422_ReturnsFailedResultWithUnprocessableHint()
    {
        // Arrange — covers ClassifyIssueCreationException HTTP 422 branch
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unprocessable Entity", null, System.Net.HttpStatusCode.UnprocessableEntity));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("422");
        result.Summary.Should().Contain("422");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp429_ReturnsFailedResultWithRateLimitHint()
    {
        // Arrange — covers ClassifyIssueCreationException HTTP 429 branch
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("429");
        result.Summary.Should().Contain("429");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsUnknownHttpStatus_ReturnsFailedResultWithHttpCodeHint()
    {
        // Arrange — covers ClassifyIssueCreationException default HTTP branch ("HTTP {code}")
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        // 503 falls through to the default arm: "HTTP 503"
        result.ErrorMessage.Should().Contain("503");
        result.Summary.Should().Contain("503");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsNonHttpException_ReturnsFailedResultWithExceptionTypeHint()
    {
        // Arrange — covers ClassifyIssueCreationException non-HTTP fallback branch
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        // Non-HTTP exception: "{TypeName}: {Message}"
        result.ErrorMessage.Should().Contain("InvalidOperationException");
        result.Summary.Should().Contain("InvalidOperationException");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    // ── Proposal validation before issue creation ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ProposalFailsValidation_IsNotFiledAndSummaryCountsIt()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Correct the stale note in the conventions file",
                    "affectedFiles": [".agent/refactoring-conventions.json"],
                    "description": "Update the note.",
                    "rationale": "It is stale."
                },
                {
                    "title": "Remove dead helper",
                    "category": "dead-code",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Delete it.",
                    "rationale": "No callers."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "7", Url = "https://github.com/test/repo/issues/7" });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Summary.Should().Be("Created 1 refactoring issue(s): #7 (1 proposal(s) dropped by validation)");
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.Is<string>(t => t.Contains("conventions file")), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalDuplicatesOpenIssueTitle_IsNotFiled()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        var openIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "300", Title = "Remove dead helper", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-90) }],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openIssues);
        SetupProposalsFile("""
            [
                {
                    "title": "Remove dead helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Delete it.",
                    "rationale": "No callers."
                }
            ]
            """);

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // An open issue blocks a duplicate however old it is
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");
        result.Summary.Should().Contain("1 proposal(s) dropped by validation");
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WritesIssueContextFileForTheReviewStep()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        var openIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "100", Title = "Extract retry logic", Labels = ["agent:generated"], CreatedAt = DateTime.UtcNow }],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openIssues);
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? contextAtAggregation = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                var path = Path.Combine(req.WorkspacePath!, AgentWorkspacePaths.RefactoringIssueContextFilePath);
                if (File.Exists(path))
                    contextAtAggregation = File.ReadAllText(path);
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        contextAtAggregation.Should().NotBeNull();
        contextAtAggregation.Should().Contain("#100 \"Extract retry logic\"");
    }

    [Fact]
    public async Task ExecuteAsync_EvidenceAndScopeQueryWrittenAsArrays_AreJoinedInsteadOfFailingTheParse()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Stop swallowing cancellation",
                    "category": "bug",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Add the filter.",
                    "rationale": "Cancellation is logged as a failure.",
                    "evidence": ["src/A.cs:L10", "catch (Exception ex)"],
                    "scopeQuery": ["git grep -n 'catch (Exception ex)' -- src/A.cs"]
                }
            ]
            """);

        string? capturedBody = null;
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, body, _, _) => capturedBody = body)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "8", Url = "https://github.com/test/repo/issues/8" });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        capturedBody!.ReplaceLineEndings("\n").Should().Contain("```\nsrc/A.cs:L10\ncatch (Exception ex)\n```");
        capturedBody.Should().Contain("git grep -n 'catch (Exception ex)' -- src/A.cs");
    }

    // ── Issue body format ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatIssueBody_PutsProblemFromRationaleBeforeApproachFromDescription()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix it",
            AffectedFiles = ["src/A.cs"],
            Description = "THE-CHANGE",
            Rationale = "THE-PROBLEM"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        var problem = body.IndexOf("## Problem", StringComparison.Ordinal);
        var approach = body.IndexOf("## Suggested Approach", StringComparison.Ordinal);
        problem.Should().BeGreaterThanOrEqualTo(0);
        approach.Should().BeGreaterThan(problem);
        body.IndexOf("THE-PROBLEM", StringComparison.Ordinal).Should().BeInRange(problem, approach);
        body.IndexOf("THE-CHANGE", StringComparison.Ordinal).Should().BeGreaterThan(approach);
    }

    [Fact]
    public void FormatIssueBody_RendersCategoryInMetadataLine()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Remove dead code",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            Category = "dead-code",
            EstimatedEffort = "small"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("**Category:** dead-code | **Effort:** small");
    }

    [Fact]
    public void FormatIssueBody_RendersEvidenceVerbatimInAFenceItCannotClose()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            Evidence = "src/A.cs:L10\nif (x < 0) { /* ``` */ }",
            EvidenceSources = ["usage-search:Foo<T>:0-callers"]
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal).ReplaceLineEndings("\n");

        body.Should().Contain("## Evidence");
        body.Should().Contain("````\nsrc/A.cs:L10\nif (x < 0) { /* ``` */ }\n````");
        // Code spans render literally, so no HTML escaping that would show up as "&lt;"
        body.Should().Contain("- `usage-search:Foo<T>:0-callers`");
    }

    [Fact]
    public void FormatIssueBody_WithScopeQuery_RendersScopeSection()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            ScopeQuery = "git grep -n 'catch (Exception ex)' -- src"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal).ReplaceLineEndings("\n");

        body.Should().Contain("## Scope");
        body.Should().Contain("```sh\ngit grep -n 'catch (Exception ex)' -- src\n```");
    }

    [Fact]
    public void FormatIssueBody_WithCommitSha_NamesTheAnalyzedCommit()
    {
        var proposal = new RefactoringProposal { Title = "Fix", AffectedFiles = ["src/A.cs"], Description = "d", Rationale = "r" };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal, "0123456789abcdef0123");

        body.Should().EndWith(
            "*This issue was automatically generated by the refactoring detection consolidation loop at commit `0123456789ab`. Line numbers refer to that commit.*");
    }

    [Fact]
    public void FormatIssueBody_WithoutCommitSha_KeepsThePlainFooter()
    {
        var proposal = new RefactoringProposal { Title = "Fix", AffectedFiles = ["src/A.cs"], Description = "d", Rationale = "r" };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().EndWith("*This issue was automatically generated by the refactoring detection consolidation loop.*");
    }

    [Fact]
    public void FormatIssueBody_BugWithoutCriteria_DefaultsToAReproductionTestNotUnchangedBehavior()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix null dereference",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            Category = "bug"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] A test reproduces the failure described under Problem and passes after the fix");
        body.Should().NotContain("without changing observable behavior");
    }

    // ── Hotspot input ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".agent/hotspot-analysis.txt")]
    [InlineData(".brain/projects/x/SKILL.md")]
    [InlineData("docs/configuration.md")]
    [InlineData("src/web/package-lock.json")]
    [InlineData("src/App/packages.lock.json")]
    [InlineData("poetry.lock")]
    public void ParseHotspotOutput_LeavesOutFilesWhoseChurnSaysNothingAboutCode(string excluded)
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = $"COMMIT_DATE:2026-07-19 12:00:00 +0000\n{excluded}\n{excluded}\nsrc/Kept.cs\n";

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Summary.Should().Contain("src/Kept.cs");
        result.Summary.Should().NotContain(excluded);
    }

    [Fact]
    public void ParseHotspotOutput_WithExistenceCheck_LeavesOutDeletedFiles()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = "COMMIT_DATE:2026-07-19 12:00:00 +0000\nsrc/Deleted.cs\nsrc/Deleted.cs\nsrc/Kept.cs\n";

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(
            gitOutput, TimeSpan.FromDays(90), referenceTime, fileExists: f => f != "src/Deleted.cs");

        result.Summary.Should().Contain("src/Kept.cs");
        result.Summary.Should().NotContain("src/Deleted.cs");
    }

    [Fact]
    public void ParseHotspotOutput_ScanCommitExcluded_DoesNotCountItsFiles()
    {
        // Two commits both change src/A.cs.
        // One has subject "feat: x (#5) (#9)" — excluded by a predicate that checks for (#5).
        // The other has subject "feat: y (#7)" — not excluded.
        // The output should list src/A.cs with 1 change (only the non-excluded commit counts).
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:2026-07-19 14:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: x (#5) (#9)\n" +
            "src/A.cs\n" +
            "COMMIT_DATE:2026-07-18 10:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: y (#7)\n" +
            "src/A.cs\n";

        Func<string, bool> isExcluded = subject => subject.Contains("(#5)", StringComparison.Ordinal);

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime, isExcludedCommit: isExcluded);

        result.Summary.Should().NotBeNull();
        result.Summary.Should().Contain("src/A.cs");
        result.Summary.Should().Contain("1 changes");
        // The excluded commit's contribution must not be counted
        result.Summary.Should().NotContain("2 changes");
        // The score should reflect only the included commit's recency weight (≈0.9 at F1 precision),
        // not the sum of both commits' weights (which would be ≈1.9 at F1 precision).
        result.Summary.Should().NotContain("1.9");
    }

    [Fact]
    public void ParseHotspotOutput_NonScanCommitStillCounts_AndNoCommitSubjectLines()
    {
        // A commit whose subject references a non-scan issue (#7) still counts.
        // No output line should start with "COMMIT_SUBJECT:".
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:2026-07-19 14:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: some change (#7)\n" +
            "src/B.cs\n";

        // With predicate (nothing excluded)
        Func<string, bool> isExcluded = subject => subject.Contains("(#99)", StringComparison.Ordinal);
        var resultWithPredicate = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime, isExcludedCommit: isExcluded);

        resultWithPredicate.Summary.Should().NotBeNull();
        resultWithPredicate.Summary.Should().Contain("src/B.cs");
        resultWithPredicate.Summary.Should().Contain("1 changes");
        // No output line should contain "COMMIT_SUBJECT:" — a parser bug treating a subject line as a
        // file path would produce a score-prefixed line like "0.9 — COMMIT_SUBJECT:feat:..." which
        // would not start with "COMMIT_SUBJECT:" but would still contain it.
        foreach (var line in resultWithPredicate.Summary!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            line.Should().NotContain("COMMIT_SUBJECT:");

        // Without predicate (null)
        var resultWithoutPredicate = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        resultWithoutPredicate.Summary.Should().NotBeNull();
        resultWithoutPredicate.Summary.Should().Contain("src/B.cs");
        resultWithoutPredicate.Summary.Should().Contain("1 changes");
        foreach (var line in resultWithoutPredicate.Summary!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            line.Should().NotContain("COMMIT_SUBJECT:");
    }

    // ── CountExcludedCommits tests ───────────────────────────────────────────────────────────

    [Fact]
    public void ParseHotspotOutput_OneMatchingExcludedCommit_ExcludedCountIsOne()
    {
        // Two commits: one whose subject matches the predicate and one that does not.
        // Only the matching one should be counted.
        var gitLog =
            "COMMIT_DATE:2026-07-19 14:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: x (#5)\n" +
            "src/A.cs\n" +
            "COMMIT_DATE:2026-07-18 10:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: y (#7)\n" +
            "src/B.cs\n";

        Func<string, bool> isExcluded = subject => subject.Contains("(#5)", StringComparison.Ordinal);

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitLog, TimeSpan.FromDays(90), isExcludedCommit: isExcluded);

        result.ExcludedCommitCount.Should().Be(1);
    }

    [Fact]
    public void ParseHotspotOutput_NoMatchingExcludedCommits_ExcludedCountIsZero()
    {
        // No commit subject matches — count should be zero.
        var gitLog =
            "COMMIT_DATE:2026-07-19 14:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: some unrelated change\n" +
            "src/A.cs\n";

        Func<string, bool> isExcluded = subject => subject.Contains("(#99)", StringComparison.Ordinal);

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitLog, TimeSpan.FromDays(90), isExcludedCommit: isExcluded);

        result.ExcludedCommitCount.Should().Be(0);
    }

    [Fact]
    public void ParseHotspotOutput_TwoExcludedCommitsOneNot_ExcludedCountIsTwo()
    {
        // Three commits: two whose subjects match the exclusion predicate, one that does not.
        // ExcludedCommitCount must be exactly 2.
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitLog =
            "COMMIT_DATE:2026-07-19 14:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: implement scan issue (#42)\n" +
            "src/A.cs\n" +
            "COMMIT_DATE:2026-07-18 10:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: another scan commit (#42)\n" +
            "src/B.cs\n" +
            "COMMIT_DATE:2026-07-17 08:00:00 +0000\n" +
            "COMMIT_SUBJECT:fix: unrelated bug\n" +
            "src/C.cs\n";

        Func<string, bool> isExcluded = subject => subject.Contains("(#42)", StringComparison.Ordinal);

        var result = RefactoringHotspotAnalyzer.ParseHotspotOutput(gitLog, TimeSpan.FromDays(90), referenceTime, isExcludedCommit: isExcluded);

        result.ExcludedCommitCount.Should().Be(2);
    }

    // ── CollectScanIssueReferencesAsync tests ────────────────────────────────────────────────

    private static IssueSummary MakeScanIssue(string identifier, string title = "Refactor X") =>
        new() { Identifier = identifier, Title = title, Labels = ["agent:generated"], Description = $"*{RefactoringIssueBodyFormatter.GeneratedIssueFooter}.*" };

    private static IssueSummary MakeNonScanIssue(string identifier, string title = "Other issue") =>
        new() { Identifier = identifier, Title = title, Labels = [], Description = "Some other description." };

    [Fact]
    public async Task CollectScanIssueReferences_HasMoreOnPage1_RequestsPage2()
    {
        // When page 1 HasMore = true, page 2 should be requested.
        var executor = CreateExecutor();

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(1, 100,
                It.Is<IReadOnlyList<string>>(l => l.Contains("agent:generated")),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [MakeScanIssue("10")],
                Page = 1,
                PageSize = 100,
                HasMore = true
            });
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(2, 100,
                It.Is<IReadOnlyList<string>>(l => l.Contains("agent:generated")),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [MakeScanIssue("11")],
                Page = 2,
                PageSize = 100,
                HasMore = false
            });
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns<IssueIdentifier>(id => $"#{id.Value}");

        var refs = await executor.CollectScanIssueReferencesAsync(
            _mockIssueProvider.Object, openIssues: [], TimeSpan.FromDays(90), "job1", CancellationToken.None);

        refs.Should().Contain("#10");
        refs.Should().Contain("#11");
        _mockIssueProvider.Verify(x => x.ListClosedIssuesAsync(2, 100,
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // TODO [WARNING]: This test verifies page 2 was called once, but does not explicitly verify that
        // page 1 was also called exactly once. An implementation that skipped page 1 and jumped directly
        // to page 2 would fail the refs.Should().Contain("#10") assertion (providing indirect coverage),
        // but adding an explicit Verify for page 1 (Times.Once) would make the intent clear and guard
        // against a future refactor that changes initial page logic without breaking the refs assertions.
    }

    [Fact]
    public async Task CollectScanIssueReferences_NonScanIssuesNotIncluded()
    {
        // Issues without the footer are not included in the result.
        var executor = CreateExecutor();

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), 100,
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [MakeScanIssue("20"), MakeNonScanIssue("21")],
                Page = 1,
                PageSize = 100,
                HasMore = false
            });
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns<IssueIdentifier>(id => $"#{id.Value}");

        var refs = await executor.CollectScanIssueReferencesAsync(
            _mockIssueProvider.Object, openIssues: [], TimeSpan.FromDays(90), "job1", CancellationToken.None);

        refs.Should().Contain("#20");
        refs.Should().NotContain("#21");
    }

    [Fact]
    public async Task CollectScanIssueReferences_ClosedQueryThrows_ReturnsOpenScanRefs()
    {
        // An exception from ListClosedIssuesAsync still returns the open scan issues' references.
        var executor = CreateExecutor();

        var openScanIssue = MakeScanIssue("5");

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), 100,
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns<IssueIdentifier>(id => $"#{id.Value}");

        var refs = await executor.CollectScanIssueReferencesAsync(
            _mockIssueProvider.Object, openIssues: [openScanIssue], TimeSpan.FromDays(90), "job1", CancellationToken.None);

        // Open scan issues are still collected despite the closed query failing
        refs.Should().Contain("#5");
    }

    [Fact]
    public async Task ExecuteAsync_ListOpenIssuesCalledOnce_EvenWithScanIssueRefCollection()
    {
        // ListOpenIssuesAsync must be called exactly once per ExecuteAsync — CollectScanIssueReferencesAsync
        // must reuse the result rather than making a second call.
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        _mockIssueProvider.Verify(
            x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // NOTE (issue #3540): This test verifies ListOpenIssuesAsync is called once but relies on the default
        // empty mock setup, so it does not verify that the openIssues result is actually passed through to
        // CollectScanIssueReferencesAsync. A bug where openIssues was always passed as an empty list would
        // not be caught. Add a stronger test that populates the open-issues mock with scan issues and then
        // verifies those refs are present in the hotspot exclusion path (e.g., by inspecting that the git
        // log call uses an exclusion predicate matching those refs, or by checking a log message).
    }

    [Fact]
    public async Task ExecuteAsync_WithOpenScanIssues_HotspotAnalysisRunsWithExclusionPredicate()
    {
        // When open scan issues are present, WriteHotspotAnalysisAsync must construct the isExcludedCommit
        // predicate (scanIssueRefs.Count > 0 path) and log the excluded commit count.
        // This covers the exclusion predicate construction and CountExcludedCommits logging branches.
        var executor = CreateExecutor();
        var job = CreateJob();

        var openScanIssue = MakeScanIssue("42");

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [openScanIssue],
                Page = 1,
                PageSize = 50,
                HasMore = false
            });
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns<IssueIdentifier>(id => $"#{id.Value}");

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // The test should complete without throwing — if the exclusion predicate or CountExcludedCommits
        // throws due to a null-ref or logic error, the exception would surface here via WrapWithCancellationHandlingAsync.
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // The run should succeed (or return a summary result) — what matters is that the
        // scanIssueRefs.Count > 0 branch was exercised without error.
        result.Should().NotBeNull();
        // FormatIssueReference was called for the open scan issue — confirming it was added to refs.
        _mockIssueProvider.Verify(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()), Times.AtLeastOnce);
    }

    [Fact]
    public void FormatRefactoringSummary_WithDroppedProposals_AppendsTheCount()
    {
        var summary = RefactoringIssueBodyFormatter.FormatRefactoringSummary([], proposalCount: 0, droppedCount: 2);

        summary.Should().Be("No refactoring opportunities identified (2 proposal(s) dropped by validation)");
    }

    // ── Before You Start section (issue #3541) ────────────────────────────────────────────────

    // TODO: Add a test that asserts the footer (GeneratedIssueFooter / "automatically generated" line) is still the
    // body's last line when the ## Before You Start section is present. The issue explicitly requires "the footer is
    // still the body's last line", but no test currently covers that invariant for bodies that contain the new section.
    // See review finding #9 (Correctness) and TestQualityReviewer finding at line 1966.

    [Fact]
    public void FormatIssueBody_WithScopeQueryAndEvidenceAndCommitSha_RendersBeforeYouStartSection()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix it",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            ScopeQuery = "git grep -n 'catch (Exception ex)' -- src",
            Evidence = "src/A.cs:L10\ncatch (Exception ex) { }"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal, "50e1ff446xyz012");

        // Section is present and before ## Suggested Approach
        var beforeYouStart = body.IndexOf("## Before You Start", StringComparison.Ordinal);
        var suggestedApproach = body.IndexOf("## Suggested Approach", StringComparison.Ordinal);
        beforeYouStart.Should().BeGreaterThanOrEqualTo(0);
        suggestedApproach.Should().BeGreaterThan(beforeYouStart);

        // 12-char SHA truncation
        // TODO: Add a test case with a commitSha shorter than 12 characters to explicitly verify that Math.Min(12,
        // commitSha.Length) produces the full string rather than truncating. The current input "50e1ff446xyz012"
        // (15 chars) validates the happy-path truncation, but a short SHA (e.g. "abc") is untested.
        // See TestQualityReviewer finding at line 1907.
        body.Should().Contain("`50e1ff446xyz`");

        // Both bullets present
        body.Should().Contain("Run the search under **Scope**.");
        body.Should().Contain("Look for the code quoted under **Evidence**.");

        // wont_do instruction
        body.Should().Contain("`wont_do`");
    }

    [Fact]
    public void FormatIssueBody_WithEvidenceButNoScopeQuery_RendersBeforeYouStartWithOnlyEvidenceBullet()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix it",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            ScopeQuery = null,
            Evidence = "src/A.cs:L10\ncatch (Exception ex) { }"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal, "abc123");

        body.Should().Contain("## Before You Start");
        body.Should().Contain("Look for the code quoted under **Evidence**.");
        body.Should().NotContain("Run the search under **Scope**.");
    }

    [Fact]
    public void FormatIssueBody_WithScopeQueryButNoEvidence_RendersBeforeYouStartWithOnlyScopeBullet()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix it",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            ScopeQuery = "git grep -n 'pattern' -- src",
            Evidence = null
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal, "abc123");

        body.Should().Contain("## Before You Start");
        body.Should().Contain("Run the search under **Scope**.");
        body.Should().NotContain("Look for the code quoted under **Evidence**.");
    }

    [Fact]
    public void FormatIssueBody_WithNeitherScopeQueryNorEvidence_OmitsBeforeYouStartSection()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix it",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            ScopeQuery = null,
            Evidence = null
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal, "abc123");

        body.Should().NotContain("## Before You Start");
    }

    [Fact]
    public void FormatIssueBody_WithNullCommitSha_RendersEarlierCommitWording()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix it",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            ScopeQuery = "git grep -n 'pattern' -- src",
            Evidence = "src/A.cs:L10\nsome code"
        };

        var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal, commitSha: null);

        body.Should().Contain("## Before You Start");
        body.Should().Contain("written against an earlier commit");
        // TODO: The NotContain assertion below passes both when the null branch correctly uses "an earlier commit"
        // and if the section were omitted entirely. The positive Contain assertion above is the real guard; this
        // negative assertion gives a false sense of completeness. Consider replacing or supplementing it with a
        // stricter assertion (e.g. asserting the full intro sentence). See TestQualityReviewer finding at line 1940.
        body.Should().NotContain("written against commit `");
    }

    [Fact]
    public async Task ExecuteAsync_Phase1_RunsTestGapAgent()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — one of the Phase 1 agent calls must carry the Agent D prompt
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt != null && r.Prompt.Contains("# Agent D: Test Gap Detection")),
                It.IsAny<CancellationToken>(),
                null),
            Times.Once);
    }

    // ─── Focus tests ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FocusAll_RunsAllFourAgents()
    {
        // Arrange — default focus (All) must dispatch all four Phase 1 agents
        var executor = CreateExecutor();
        var job = CreateJob(); // RefactoringFocus defaults to All

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                var dir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "refactoring-proposals.json"), "[]");
            })
            .Returns(Task.CompletedTask);

        var capturedPrompts = new System.Collections.Concurrent.ConcurrentBag<string>();
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompts.Add(req.Prompt ?? ""))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — all four Phase 1 agent headers must appear in captured prompts
        capturedPrompts.Should().Contain(p => p.Contains("# Agent A: Structural Debt Detection"),
            "Phase 1 Agent A must run when focus is All");
        capturedPrompts.Should().Contain(p => p.Contains("# Agent B: Correctness & Hygiene Detection"),
            "Phase 1 Agent B must run when focus is All");
        capturedPrompts.Should().Contain(p => p.Contains("# Agent C: Design Consistency Detection"),
            "Phase 1 Agent C must run when focus is All");
        capturedPrompts.Should().Contain(p => p.Contains("# Agent D: Test Gap Detection"),
            "Phase 1 Agent D must run when focus is All");
    }

    [Fact]
    public async Task ExecuteAsync_FocusTests_RunsOnlyTestGapAgent()
    {
        // Arrange — only Agent D (test gaps) should run in Phase 1 when focus is Tests
        var executor = CreateExecutor();
        var job = new ConsolidationJobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            RefactoringFocus = RefactoringScanFocus.Tests
        };

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                var dir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "refactoring-proposals.json"), "[]");
            })
            .Returns(Task.CompletedTask);

        var capturedPrompts = new System.Collections.Concurrent.ConcurrentBag<string>();
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompts.Add(req.Prompt ?? ""))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — Agent D runs, Agents A/B/C do not
        capturedPrompts.Should().Contain(p => p.Contains("# Agent D: Test Gap Detection"),
            "Phase 1 Agent D must run when focus is Tests");
        capturedPrompts.Should().NotContain(p => p.Contains("# Agent A: Structural Debt Detection"),
            "Phase 1 Agent A must NOT run when focus is Tests");
        capturedPrompts.Should().NotContain(p => p.Contains("# Agent B: Correctness & Hygiene Detection"),
            "Phase 1 Agent B must NOT run when focus is Tests");
        capturedPrompts.Should().NotContain(p => p.Contains("# Agent C: Design Consistency Detection"),
            "Phase 1 Agent C must NOT run when focus is Tests");
    }

    [Fact]
    public async Task ExecuteAsync_FocusStructure_Phase0OmitsCoverageStep()
    {
        // Arrange — Phase 0 must skip the coverage step when focus is Structure (not All or Tests)
        var executor = CreateExecutor();
        var job = new ConsolidationJobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            RefactoringFocus = RefactoringScanFocus.Structure
        };

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                var dir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "refactoring-proposals.json"), "[]");
            })
            .Returns(Task.CompletedTask);

        string? phase0Prompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                // Capture the Phase 0 prompt (identified by its header)
                if (req.Prompt?.Contains("# Phase 0: Project Context Extraction") == true)
                    phase0Prompt = req.Prompt;
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — Phase 0 must not include the coverage step when focus is Structure
        phase0Prompt.Should().NotBeNull("Phase 0 agent must have been called");
        phase0Prompt!.Should().NotContain("**Test coverage.**",
            "Phase 0 must skip the coverage step for non-All/non-Tests focus");
    }

    [Fact]
    public async Task ExecuteAsync_FocusStructure_DropsBugProposal()
    {
        // Arrange — a bug proposal must be rejected when focus=Structure (only Structural categories allowed)
        var executor = CreateExecutor();
        var job = new ConsolidationJobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = _tempDir,
                RefactoringReviewEnabled = false
            },
            RefactoringFocus = RefactoringScanFocus.Structure
        };

        var bugProposalsJson = """
            [
                {
                    "title": "Null ref in parser",
                    "category": "bug",
                    "affectedFiles": ["src/Service.cs"],
                    "description": "Null dereference at L42.",
                    "rationale": "Will crash."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                // Write the proposals file (agent will not overwrite it since the mock just returns success)
                RefactoringTestWorkspace.WriteProposals(path, bugProposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — the bug proposal is rejected (outside scan focus), so no issues are created
        result.Success.Should().BeTrue("the run itself succeeds even if all proposals are rejected");
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "bug proposals must not be filed when focus=Structure");
    }

    [Fact]
    public async Task ExecuteAsync_ValidProposals_ReportsScanStepsInOrder()
    {
        // Arrange
        var job = CreateJob();
        var steps = new List<PipelineStep>();
        var progress = new ConsolidationProgress(ConsolidationProgress.CreateRun(job), steps.Add);
        var executor = new RefactoringExecutor(_mockLogger.Object, progress);

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Found 1 refactoring opportunity."]
            });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        steps.Should().Equal(
            PipelineStep.CloningRepository,
            PipelineStep.AnalyzingHotspots,
            PipelineStep.ReadingIssueContext,
            PipelineStep.ExtractingConventions,
            PipelineStep.DetectingFindings,
            PipelineStep.AggregatingProposals,
            PipelineStep.ReviewingProposals,
            PipelineStep.CreatingRefactoringIssues);
    }

    [Fact]
    public async Task ExecuteAsync_WithOutputCallback_PrefixesDetectorOutput()
    {
        // Arrange
        var job = CreateJob();
        var executor = CreateExecutor();
        var lines = new List<string>();

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        // Set up to match any callback (including non-null from Phase 1) and invoke it with "line"
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((_, _, cb) => cb?.Invoke("line"))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Done."]
            });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object,
            CancellationToken.None, onOutputLine: lines.Add);

        // Assert: Phase 1 structural/correctness/design get prefixed; TestGaps and Phases 0/2 do not
        lines.Should().Contain("[A] line");
        lines.Should().Contain("[B] line");
        lines.Should().Contain("[C] line");
    }

    // ── AC1: Brain-clone failure ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the brain repository provider throws a non-cancellation exception,
    /// a Warning is logged and execution continues to Phase 2 (not aborted).
    /// Covers the catch block in <c>TryCloneBrainRepoAsync</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BrainCloneThrows_WarningLoggedAndExecutionContinuesToPhase2()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        var brainProviderMock = new Mock<IRepositoryProvider>();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) => RefactoringTestWorkspace.WriteProposals(path, "[]"))
            .Returns(Task.CompletedTask);

        // Brain clone throws — executor must log a Warning and continue
        brainProviderMock
            .Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("simulated brain failure"));

        // All agent calls succeed so the full Phase 0 → Phase 1 × 4 → Phase 2 pipeline runs
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, brainProviderMock.Object,
            _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — Warning logged for the brain clone failure
        // Production call: Logger.Warning(ex, "Failed to clone brain repo for context in run {RunId}, ...", jobId)
        // Resolves to Warning<string>(Exception?, string, string)
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("brain repo")),
                It.IsAny<string>()),   // jobId
            Times.Once);

        // Phase 2 was reached: Phase 0 (1) + Phase 1 × 4 (4) + Phase 2 (1) = 6 calls
        // If the executor aborted after brain failure it would have 0 agent calls
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(6));
    }

    /// <summary>
    /// When the brain repository provider succeeds (CloneAsync returns normally),
    /// Logger.Information containing "Brain repo cloned" is emitted once and
    /// execution continues through Phase 2 as normal.
    /// Covers the success path (try block body) in <c>TryCloneBrainRepoAsync</c> at L150–L151.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BrainCloneSucceeds_InformationLogged()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        var brainProviderMock = new Mock<IRepositoryProvider>();

        // Code-repo clone writes an empty proposals array so Phase 2 completes normally
        // and the executor returns "No proposals" success — no issue creation needed.
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) => RefactoringTestWorkspace.WriteProposals(path, "[]"))
            .Returns(Task.CompletedTask);

        // Brain clone succeeds — executor must log an Information and continue normally
        brainProviderMock
            .Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // All agent calls succeed so the full Phase 0 → Phase 1 × 4 → Phase 2 pipeline runs
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, brainProviderMock.Object,
            _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — Information logged for the brain clone success
        // Production call: Logger.Information("Brain repo cloned for architectural context in run {RunId}", jobId)
        // Resolves to Information<string>(string, string)
        _mockLogger.Verify(
            l => l.Information(
                It.Is<string>(s => s.Contains("Brain repo cloned")),
                It.IsAny<string>()),   // jobId
            Times.Once);

        // Phase 2 was reached: Phase 0 (1) + Phase 1 × 4 (4) + Phase 2 (1) = 6 calls
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(6));
    }

    // ── AC2: Phase 1 partial failure ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When one of the four parallel Phase 1 analysis agents fails (ExitCode != 0),
    /// a Warning noting the failure count is logged and Phase 2 still runs.
    /// Covers the <c>if (phase1Failures.Count &gt; 0)</c> branch in <c>ExecutePhasedRefactoringAsync</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OnePhase1AgentFails_WarningLoggedAndPhase2Reached()
    {
        // Arrange
        var executor = CreateExecutor();
        // Default RefactoringScanFocus is All (= 0, the C# default), which dispatches 4 Phase 1 agents.
        // Explicit assignment here documents the intent and protects against future enum reordering.
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) => RefactoringTestWorkspace.WriteProposals(path, "[]"))
            .Returns(Task.CompletedTask);

        // Phase 0 (call 1) + Phase 1 × 4 (calls 2–5, one fails) + Phase 2 (call 6).
        // Phase 1 calls run via Task.WhenAll; SetupSequence is monitor-protected so concurrent
        // consumption is safe. The failure lands on one of calls 2–5 — not Phase 0 (awaited
        // first) or Phase 2 (awaited after Task.WhenAll).
        _mockAgentProvider
            .SetupSequence(x => x.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] })   // Phase 0
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] })   // Phase 1 (success)
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = [] })   // Phase 1 (fail)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] })   // Phase 1 (success)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] })   // Phase 1 (success)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });  // Phase 2

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null,
            _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — Warning noting partial failure was logged.
        // Production call: Logger.Warning("{FailCount}/{TotalCount} Phase 1 agents failed in run {RunId}, ...",
        //     phase1Failures.Count, phase1Results.Length, job.JobId)
        // Types: (int, int, string) → resolves to Warning<int,int,string>(string, int, int, string).
        // Must use It.IsAny<int>() — not It.IsAny<object>() — to select the same generic overload.
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("Phase 1 agents failed")),
                It.IsAny<int>(),      // FailCount
                It.IsAny<int>(),      // TotalCount
                It.IsAny<string>()), // jobId
            Times.Once);

        // Phase 2 was reached: the abort path (all Phase 1 failed) would produce only 5 calls
        _mockAgentProvider.Verify(
            p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(6));
    }

    // ── AC3: Issue context file write failure ─────────────────────────────────────────────────────

    /// <summary>
    /// When writing the issue-context file fails (IOException from <c>Directory.CreateDirectory</c>),
    /// a Warning is logged and no exception propagates to the caller.
    /// Covers the catch block in <c>TryWriteIssueContextFileAsync</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_IssueContextFileWriteFails_WarningLoggedAndNoExceptionPropagated()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        // Override the default empty-issues mock so TryBuildIssueContextAsync returns non-empty
        // content. Without at least one open issue, BuildOpenIssueContext returns an empty string,
        // and TryWriteIssueContextFileAsync exits early before attempting the write (content guard).
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [new IssueSummary { Identifier = "1", Title = "Existing open issue", Labels = [] }],
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        // The clone callback creates workspacePath/.agent as a FILE (not a directory).
        // workspacePath = Path.Combine(_tempDir, job.JobId) — NOT _tempDir directly.
        // When TryWriteIssueContextFileAsync calls Directory.CreateDirectory(workspacePath/.agent),
        // the OS rejects it because that path already exists as a file → IOException.
        // TODO [WARNING]: This test depends on WriteHotspotAnalysisAsync failing on git-log BEFORE it
        // reaches its own Directory.CreateDirectory(workspacePath/.agent) call. That ordering holds
        // because RunGitCommandAsync throws immediately when the workspace is not a git repo. If the
        // clone callback ever initialises a git repo (e.g. git init), hotspot analysis would hit the
        // blocking .agent file first and log "Hotspot analysis failed" instead; the Times.Once verify
        // for "issue context file" would then fail even though the catch block is still correct.
        // Consider isolating the two methods with distinct workspace sub-paths or using separate tests.
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                File.WriteAllText(Path.Combine(path, ".agent"), "block-dir-creation");
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act — must not throw
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null,
            _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — Warning logged for the write failure.
        // Production call: Logger.Warning(ex, "Failed to write the issue context file in run {RunId}, ...", jobId)
        // Resolves to Warning<string>(Exception?, string, string)
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("issue context file")),
                It.IsAny<string>()),   // jobId
            Times.Once);

        // No exception propagated — the implicit proof is that the await above did not throw.
        // TODO [WARNING]: result.Should().NotBeNull() is vacuous — WrapWithCancellationHandlingAsync
        // always returns a non-null ConsolidationJobResult (it converts every unhandled exception into
        // CreateFailureResult). A stronger signal is result.Success.Should().BeTrue(): that would fail
        // if the IOException propagated past TryWriteIssueContextFileAsync (the outer wrapper would set
        // Success=false), catching regressions that merely swallow the write failure silently.
        result.Should().NotBeNull();
    }

    // ── AC2 (#3756): Git log returns no file changes → hotspot early exit (L308) ───────────────────────

    /// <summary>
    /// When the workspace is a real git repo but all commits are empty (no file changes),
    /// ParseHotspotOutput returns null, L308 fires, and the hotspot output file is not written.
    /// Covers <c>WriteHotspotAnalysisAsync</c> L308.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GitLogHasNoFileChanges_HotspotFileNotWrittenAndNoHistoryLogged()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        var workspacePath = Path.Combine(_tempDir, job.JobId);

        // Use .Returns<T1,T2>(async …) — NOT .Callback + .Returns(Task.CompletedTask) —
        // so the returned Task is not resolved until the git commands inside complete.
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns<WorkspacePath, CancellationToken>(async (path, ct) =>
            {
                await RefactoringExecutor.RunGitCommandAsync(path, "init", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "config user.email test@example.com", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "config user.name Test", ct);
                // Empty commit: git log succeeds but produces no file-path lines.
                // ParseHotspotOutput will find no entries and return null, triggering L308.
                await RefactoringExecutor.RunGitCommandAsync(path, "commit --allow-empty -m initial", ct);
            });

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act — result.Success will be false (no proposals file written by the mock); that is unrelated to this criterion
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — L308: Logger.Information("No git history found within lookback window for hotspot analysis")
        _mockLogger.Verify(
            l => l.Information(It.Is<string>(s => s.Contains("No git history found"))),
            Times.Once);

        // The hotspot file must not exist: the early return at L308 skips File.WriteAllTextAsync
        File.Exists(Path.Combine(workspacePath, AgentWorkspacePaths.HotspotAnalysisFilePath))
            .Should().BeFalse("git log produced no file-change lines so no hotspot file should be written");
    }

    // ── AC3 (#3756): scanIssueRefs exclusion path (L581-L613) ───────────────────────────────────────────

    /// <summary>
    /// When scanIssueRefs is non-empty and a commit's subject contains a matching reference,
    /// that commit's files are excluded from the hotspot output and the exclusion count is logged.
    /// Covers <c>WriteHotspotAnalysisAsync</c> L581-L613: isExcludedCommit lambda, CountExcludedCommits,
    /// and Logger.Information("Hotspot analysis left out {ExcludedCommitCount} commits…").
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ScanIssueRefMatchesCommitSubject_FileExcludedAndExclusionCountLogged()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        var workspacePath = Path.Combine(_tempDir, job.JobId);

        // Open scan issue #42 — FormatIssueReference returns "#42" so scanIssueRefs = {"#42"}.
        // The isExcludedCommit predicate checks subject.Contains("(#42)") (L584-L589).
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [MakeScanIssue("42")],
                Page = 1,
                PageSize = 50,
                HasMore = false
            });
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns<IssueIdentifier>(id => $"#{id.Value}");

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns<WorkspacePath, CancellationToken>(async (path, ct) =>
            {
                await RefactoringExecutor.RunGitCommandAsync(path, "init", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "config user.email test@example.com", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "config user.name Test", ct);

                // Commit A — subject contains "(#42)" → matched by isExcludedCommit → files skipped
                Directory.CreateDirectory(Path.Combine(path, "src"));
                File.WriteAllText(Path.Combine(path, "src", "Excluded.cs"), "// test");
                await RefactoringExecutor.RunGitCommandAsync(path, "add .", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "commit -m \"feat: refactoring (#42)\"", ct);

                // Commit B — no scan issue ref → not excluded; contributes src/Kept.cs to the hotspot
                File.WriteAllText(Path.Combine(path, "src", "Kept.cs"), "// test");
                await RefactoringExecutor.RunGitCommandAsync(path, "add .", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "commit -m \"feat: something else\"", ct);
            });

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act — result.Success will be false (no proposals file written by the mock); that is unrelated to this criterion
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — L607: Logger.Information("Hotspot analysis left out {ExcludedCommitCount} commits…", 1)
        // It.Is<int>(n => n == 1) selects the Information<int>(string, int) overload and verifies the exact count.
        _mockLogger.Verify(
            l => l.Information(
                It.Is<string>(s => s.Contains("left out")),
                It.Is<int>(n => n == 1)),
            Times.Once);

        // The hotspot file must have been written (Commit B contributed src/Kept.cs)
        var hotspotPath = Path.Combine(workspacePath, AgentWorkspacePaths.HotspotAnalysisFilePath);
        File.Exists(hotspotPath).Should().BeTrue("Commit B contributed src/Kept.cs to the hotspot analysis");
        var hotspotContent = File.ReadAllText(hotspotPath);
        hotspotContent.Should().Contain("src/Kept.cs");
        hotspotContent.Should().NotContain("src/Excluded.cs", "Commit A was excluded because its subject contains (#42)");
    }

    /// <summary>
    /// When scanIssueRefs is empty, isExcludedCommit is null, so excludedCount is 0.
    /// Logger.Information("Hotspot analysis left out {ExcludedCommitCount} commits…", 0) must still
    /// be emitted exactly once — covering the unconditional log path added in issue #3798.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EmptyScanIssueRefs_ExclusionCountLoggedAsZero()
    {
        // Arrange — no open scan issues (default setup returns empty list), so scanIssueRefs will be empty
        // and isExcludedCommit will be null.
        var executor = CreateExecutor();
        var job = CreateJob();
        var workspacePath = Path.Combine(_tempDir, job.JobId);

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns<WorkspacePath, CancellationToken>(async (path, ct) =>
            {
                await RefactoringExecutor.RunGitCommandAsync(path, "init", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "config user.email test@example.com", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "config user.name Test", ct);

                // One commit with no scan-issue ref in its subject.
                Directory.CreateDirectory(Path.Combine(path, "src"));
                File.WriteAllText(Path.Combine(path, "src", "Foo.cs"), "// test");
                await RefactoringExecutor.RunGitCommandAsync(path, "add .", ct);
                await RefactoringExecutor.RunGitCommandAsync(path, "commit -m \"feat: some work\"", ct);
            });

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — the log must fire unconditionally with excludedCount == 0 (null-branch of ternary)
        _mockLogger.Verify(
            l => l.Information(
                It.Is<string>(s => s.Contains("left out")),
                It.Is<int>(n => n == 0)),
            Times.Once);

        // The hotspot file must have been written (the commit contributes src/Foo.cs)
        File.Exists(Path.Combine(workspacePath, AgentWorkspacePaths.HotspotAnalysisFilePath))
            .Should().BeTrue("one non-excluded commit contributed src/Foo.cs");
    }

    // ── Scan outcome tests (issue #3569) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllPhase1AgentsFail_ReturnsScanOutcomeWithFailedDetectors()
    {
        // Arrange: Phase 0 succeeds; all 4 Phase 1 agents fail; Phase 2 is never reached.
        // Verifies that phasedResult.ScanOutcome is populated on the phased-failure early-exit path.
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Phase 0 (call 1): success; Phase 1 × 4 (calls 2–5): all fail; Phase 2 is never dispatched.
        _mockAgentProvider
            .SetupSequence(x => x.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] })   // Phase 0
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = [] })   // Phase 1 (fail)
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = [] })   // Phase 1 (fail)
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = [] })   // Phase 1 (fail)
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = [] });  // Phase 1 (fail)

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse("all Phase 1 agents failed");
        result.ScanOutcome.Should().NotBeNull(
            "ScanOutcome must be set on the phased-failure early-exit path in ExecuteAsync");

        var outcome = result.ScanOutcome!;
        // Three detectors are always returned, all Succeeded=false because all Phase 1 agents failed
        outcome.Detectors.Should().HaveCount(3,
            "ExecutePhasedRefactoringAsync always returns the three structural/correctness/design detectors");
        outcome.Detectors.All(d => !d.Succeeded).Should().BeTrue(
            "all detectors must be Succeeded=false when all Phase 1 agents fail");
        // Settings from the job must be present on the outcome
        outcome.MaxIssues.Should().Be(job.PipelineConfiguration.MaxRefactoringProposals);
        outcome.HotspotLookbackDays.Should().Be((int)job.PipelineConfiguration.HotspotAnalysisLookback.TotalDays);
    }

    [Fact]
    public async Task ExecuteAsync_ValidProposals_ReturnsScanOutcome()
    {
        // Arrange — same setup as ExecuteAsync_ValidProposals_CreatesIssues,
        // with category and structural findings file.
        var executor = CreateExecutor();
        var job = CreateJob();

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "category": "duplication",
                    "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
                // Write structural findings file with 2 findings
                var agentDir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(
                    Path.Combine(agentDir, AgentWorkspacePaths.RefactoringStructuralFindingsFilePath.Replace(".agent/", "")),
                    """{"findings":[{},{}],"notChecked":[]}""");
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Found 1 refactoring opportunity."] });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ScanOutcome.Should().NotBeNull();
        var outcome = result.ScanOutcome!;

        outcome.ProposalCount.Should().Be(1, "one proposal after adversarial review");
        outcome.CreatedIssues.Should().HaveCount(1);
        outcome.CreatedIssues[0].Identifier.Should().Be("42");
        outcome.CreatedIssues[0].Category.Should().Be("duplication");
        outcome.DroppedProposals.Should().BeEmpty();
        outcome.UnfiledProposals.Should().BeEmpty();

        // Three detectors, all succeeded (all agents return ExitCode=0)
        outcome.Detectors.Should().HaveCount(3);
        outcome.Detectors.All(d => d.Succeeded).Should().BeTrue();
        // Structural detector has 2 findings; others have no findings file → null
        outcome.Detectors[0].Name.Should().Be("Structural debt");
        outcome.Detectors[0].FindingCount.Should().Be(2);
        outcome.Detectors[1].Name.Should().Be("Correctness and hygiene");
        outcome.Detectors[1].FindingCount.Should().BeNull();
        outcome.Detectors[2].Name.Should().Be("Design consistency");
        outcome.Detectors[2].FindingCount.Should().BeNull();

        // Settings from job
        outcome.MaxIssues.Should().Be(job.PipelineConfiguration.MaxRefactoringProposals);
        outcome.HotspotLookbackDays.Should().Be((int)job.PipelineConfiguration.HotspotAnalysisLookback.TotalDays);
        outcome.ReviewEnabled.Should().Be(job.PipelineConfiguration.RefactoringReviewEnabled);
        outcome.AutoDispatch.Should().Be(job.AutoDispatch);
    }

    [Fact]
    public async Task ExecuteAsync_IssueCreationFails_ReportsUnfiledProposals()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract shared validation logic",
                    "category": "duplication",
                    "affectedFiles": ["src/Service.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ScanOutcome.Should().NotBeNull();
        var outcome = result.ScanOutcome!;

        outcome.UnfiledProposals.Should().HaveCount(1);
        outcome.UnfiledProposals[0].Title.Should().Be("Extract shared validation logic");
        outcome.UnfiledProposals[0].Category.Should().Be("duplication");
        outcome.UnfiledProposals[0].Reason.Should().BeNull("unfiled proposals have no reason, only dropped ones do");
        outcome.CreatedIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ProposalNamingAgentFile_ReportsDroppedProposal()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        // Two proposals: one valid, one with a file in .agent/ (validation drops it)
        SetupProposalsFile("""
            [
                {
                    "title": "Remove dead helper",
                    "category": "dead-code",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Delete it.",
                    "rationale": "No callers."
                },
                {
                    "title": "Update the agent notes file",
                    "category": "documentation",
                    "affectedFiles": [".agent/notes.md"],
                    "description": "Fix the stale note.",
                    "rationale": "It is outdated."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "7", Url = "https://github.com/test/repo/issues/7" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ScanOutcome.Should().NotBeNull();
        var outcome = result.ScanOutcome!;

        outcome.DroppedProposals.Should().HaveCount(1);
        outcome.DroppedProposals[0].Title.Should().Be("Update the agent notes file");
        outcome.DroppedProposals[0].Category.Should().Be("documentation");
        outcome.DroppedProposals[0].Reason.Should().NotBeNullOrEmpty("validation must provide a reason");
    }

    [Theory]
    [InlineData("valid3", 3)]
    [InlineData("missing", null)]
    [InlineData("notjson", null)]
    [InlineData("nofindings", null)]
    public void CountFindings_Theory(string scenario, int? expected)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"count-findings-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath;
            switch (scenario)
            {
                case "valid3":
                    filePath = Path.Combine(tempDir, "findings.json");
                    File.WriteAllText(filePath, """{"findings":[{},{},{}],"notChecked":[]}""");
                    break;
                case "missing":
                    filePath = Path.Combine(tempDir, "nonexistent.json");
                    break;
                case "notjson":
                    filePath = Path.Combine(tempDir, "findings.json");
                    File.WriteAllText(filePath, "not json");
                    break;
                case "nofindings":
                    filePath = Path.Combine(tempDir, "findings.json");
                    File.WriteAllText(filePath, """{"notChecked":[]}""");
                    break;
                default:
                    throw new ArgumentException($"Unknown scenario: {scenario}");
            }

            var result = RefactoringExecutor.CountFindings(filePath);
            result.Should().Be(expected);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
