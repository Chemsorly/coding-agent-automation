using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="RefactoringExecutor"/> adversarial review integration.
/// Covers: review enabled with proposals, empty proposals skips review, review disabled,
/// refinement re-reads proposals, malformed refined file keeps original, token usage on result.
/// </summary>
public class RefactoringExecutorReviewTests : IDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IIssueProvider> _mockIssueProvider = new();
    private readonly Mock<IAgentProvider> _mockAgentProvider = new();
    private readonly string _tempDir;

    public RefactoringExecutorReviewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"refactoring-review-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Default: return empty issue lists so the new issue-context query path succeeds
        var emptyResult = new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 50, HasMore = false };
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);

        // Default: GetLatestSessionIdAsync returns a fake session ID (needed by AdversarialReviewHelper)
        _mockAgentProvider
            .Setup(x => x.GetLatestSessionIdAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("fake-session-id");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private RefactoringExecutor CreateExecutor() => new(_mockLogger.Object);

    private ConsolidationJobMessage CreateJob(bool reviewEnabled = true) => new()
    {
        JobId = Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.RefactoringDetection,
        TemplateId = "template-1",
        TemplateName = "Test Template",
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempDir,
            RefactoringReviewEnabled = reviewEnabled,
            AgentTimeout = TimeSpan.FromMinutes(5)
        }
    };

    private const string AggregationPromptMarker = "Produce the final proposals at";
    private const string Phase2aScoringPromptMarker = "Phase 2a scoring for";

    private static readonly string ValidProposalsJson = """
        [
            {
                "title": "Extract shared validation logic",
                "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                "description": "Both files duplicate input validation.",
                "rationale": "DRY principle violation."
            }
        ]
        """;

    private void SetupCloneWritesProposals(string proposalsJson)
    {
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);
    }

    private void SetupCloneWritesEmptyProposals()
    {
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                var agentDir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(Path.Combine(agentDir, "refactoring-proposals.json"), "[]");
            })
            .Returns(Task.CompletedTask);
    }

    private void SetupIssueCreation()
    {
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "99", Url = "https://github.com/test/repo/issues/99" });
    }

    /// <summary>
    /// When review is enabled and proposals exist, the discriminator agent is called
    /// (2+ agent calls total: generator + discriminator).
    /// </summary>
    [Fact]
    public async Task ReviewEnabled_WithProposals_CallsHelper()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        SetupCloneWritesProposals(ValidProposalsJson);
        SetupIssueCreation();

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                // TODO [WARNING]: callCount++ is not thread-safe. The 4 Phase 1 tasks run concurrently via
                // Task.WhenAll; two threads can read the same value simultaneously and lose an increment.
                // Replace with Interlocked.Increment(ref callCount) to prevent non-deterministic test failures.
                // See issue #3582 review finding (TestQualityReviewer).
                callCount++;
                if (callCount <= 10)
                {
                    // Phased detection calls (Phase 0 + 4× Phase 1 + 4× Phase 2a + Phase 2b)
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                // Discriminator call — write review file with only suggestions (no refinement triggered)
                var reviewPath = Path.Combine(req.WorkspacePath!, AgentWorkspacePaths.RefactoringReviewFilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                File.WriteAllText(reviewPath, "[SUGGESTION] Minor style issue");
                return new AgentResult { ExitCode = 0, OutputLines = ["Review done."] };
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — at least 11 agent calls (10 phased + discriminator)
        result.Success.Should().BeTrue();
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.AtLeast(11));
    }

    /// <summary>
    /// When proposals array is empty, no review agent call is made.
    /// </summary>
    [Fact]
    public async Task EmptyProposals_SkipsReview()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        SetupCloneWritesEmptyProposals();

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["No proposals."] });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — only phased calls (10), no discriminator since proposals are empty
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(10));
    }

    /// <summary>
    /// When RefactoringReviewEnabled = false, no review/refinement agent calls are made.
    /// </summary>
    [Fact]
    public async Task ReviewDisabled_SkipsEntirely()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: false);
        SetupCloneWritesProposals(ValidProposalsJson);
        SetupIssueCreation();

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — only phased calls (10), no discriminator or refinement since review is disabled
        result.Success.Should().BeTrue();
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(10));
    }

    /// <summary>
    /// Phase 2a agent calls receive prompts containing the Phase 2a scoring header,
    /// not Phase 1 or Phase 2b prompts. Uses <see cref="Phase2aScoringPromptMarker"/> to distinguish them.
    /// </summary>
    [Fact]
    public async Task Phase2aAgents_ReceivePhase2aScoringPrompt()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: false);
        SetupCloneWritesProposals(ValidProposalsJson);
        SetupIssueCreation();

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — exactly 4 Phase 2a agent calls (one per focus: Structure, Correctness, Design, Tests)
        // each carrying the Phase 2a prompt header, not a Phase 1 or Phase 2b prompt.
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains(Phase2aScoringPromptMarker)),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Exactly(4));
    }

    /// <summary>
    /// When refinement runs, the executor re-reads the proposals file and uses the refined version.
    /// </summary>
    [Fact]
    public async Task RefinementTriggered_ReReadsProposals()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        SetupCloneWritesProposals(ValidProposalsJson);
        SetupIssueCreation();

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                // TODO [WARNING]: callCount++ is not thread-safe. The 4 Phase 1 tasks run concurrently via
                // Task.WhenAll; two threads can read the same value simultaneously and lose an increment.
                // Replace with Interlocked.Increment(ref callCount) to prevent non-deterministic test failures.
                // See issue #3582 review finding (TestQualityReviewer).
                callCount++;
                if (callCount <= 10)
                {
                    // Phased detection calls (Phase 0 + 4× Phase 1 + 4× Phase 2a + Phase 2b)
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                if (callCount == 11)
                {
                    // Discriminator call — write CRITICAL finding to trigger refinement
                    var reviewPath = Path.Combine(req.WorkspacePath!, AgentWorkspacePaths.RefactoringReviewFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                    File.WriteAllText(reviewPath, "[CRITICAL] Proposal references non-existent file");
                    return new AgentResult { ExitCode = 0, OutputLines = ["Review done."] };
                }
                // Refinement call — rewrite proposals with refined content
                var proposalsPath = Path.Combine(req.WorkspacePath!, ".agent", "refactoring-proposals.json");
                var refinedJson = """
                    [
                        {
                            "title": "Refined: Extract shared validation logic",
                            "affectedFiles": ["src/Service.cs"],
                            "description": "Refined description.",
                            "rationale": "Refined rationale."
                        }
                    ]
                    """;
                File.WriteAllText(proposalsPath, refinedJson);
                return new AgentResult { ExitCode = 0, OutputLines = ["Refined."] };
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — 12 agent calls (10 phased + discriminator + refinement)
        result.Success.Should().BeTrue();
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(12));

        // Issue should be created with the refined title
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(
                It.Is<string>(t => t.Contains("Refined")),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// When refined file has invalid JSON, original proposals are kept.
    /// </summary>
    [Fact]
    public async Task MalformedRefinedFile_KeepsOriginal()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        SetupCloneWritesProposals(ValidProposalsJson);
        SetupIssueCreation();

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                // TODO [WARNING]: callCount++ is not thread-safe. The 4 Phase 1 tasks run concurrently via
                // Task.WhenAll; two threads can read the same value simultaneously and lose an increment.
                // Replace with Interlocked.Increment(ref callCount) to prevent non-deterministic test failures.
                // See issue #3582 review finding (TestQualityReviewer).
                callCount++;
                if (callCount <= 10)
                {
                    // Phased detection calls (Phase 0 + 4× Phase 1 + 4× Phase 2a + Phase 2b)
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                if (callCount == 11)
                {
                    // Discriminator call — write CRITICAL finding to trigger refinement
                    var reviewPath = Path.Combine(req.WorkspacePath!, AgentWorkspacePaths.RefactoringReviewFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                    File.WriteAllText(reviewPath, "[CRITICAL] Bad proposal");
                    return new AgentResult { ExitCode = 0, OutputLines = ["Review done."] };
                }
                // Refinement call — write malformed JSON to proposals file
                var proposalsPath = Path.Combine(req.WorkspacePath!, ".agent", "refactoring-proposals.json");
                File.WriteAllText(proposalsPath, "{ this is not valid json [[[");
                return new AgentResult { ExitCode = 0, OutputLines = ["Refined."] };
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — should still succeed using original proposals
        result.Success.Should().BeTrue();

        // Issue should be created with the original title (not refined)
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(
                It.Is<string>(t => t.Contains("Extract shared validation logic")),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// ReviewTokenUsage and RefinementTokenUsage are set on the ConsolidationJobResult.
    /// </summary>
    [Fact]
    public async Task TokenUsage_SetOnResult()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        SetupCloneWritesProposals(ValidProposalsJson);
        SetupIssueCreation();

        var reviewUsage = new TokenUsage { InputTokens = 1000, OutputTokens = 500 };
        var refinementUsage = new TokenUsage { InputTokens = 2000, OutputTokens = 1000 };

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                // TODO [WARNING]: callCount++ is not thread-safe. The 4 Phase 1 tasks run concurrently via
                // Task.WhenAll; two threads can read the same value simultaneously and lose an increment.
                // Replace with Interlocked.Increment(ref callCount) to prevent non-deterministic test failures.
                // See issue #3582 review finding (TestQualityReviewer).
                callCount++;
                if (callCount <= 10)
                {
                    // Phased detection calls (Phase 0 + 4× Phase 1 + 4× Phase 2a + Phase 2b)
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                if (callCount == 11)
                {
                    // Discriminator call — write CRITICAL finding to trigger refinement
                    var reviewPath = Path.Combine(req.WorkspacePath!, AgentWorkspacePaths.RefactoringReviewFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                    File.WriteAllText(reviewPath, "[CRITICAL] Issue found");
                    return new AgentResult { ExitCode = 0, OutputLines = ["Review done."], Usage = reviewUsage };
                }
                // Refinement call
                return new AgentResult { ExitCode = 0, OutputLines = ["Refined."], Usage = refinementUsage };
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — token usage should be set on the result
        result.Success.Should().BeTrue();
        result.ReviewTokenUsage.Should().Be(reviewUsage);
        result.RefinementTokenUsage.Should().Be(refinementUsage);
    }

    /// <summary>
    /// When the refinement agent deletes the proposals file instead of overwriting it,
    /// <c>ParseProposalsAsync</c> hits the <c>!File.Exists</c> branch (L716–L717) and returns null.
    /// The executor logs a Warning about the missing file, falls back to the original proposals
    /// from Phase 2, and succeeds — creating an issue with the original proposal title.
    /// Covers <c>ParseProposalsAsync</c> L716–L717.
    /// </summary>
    [Fact]
    public async Task RefinedFileDeleted_FileNotFoundBranch_OriginalProposalsPreserved()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(reviewEnabled: true);
        SetupCloneWritesProposals(ValidProposalsJson);
        SetupIssueCreation();

        var callCount = 0;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                // Interlocked.Increment avoids the race condition that arises with callCount++
                // when the 4 Phase 1 tasks run concurrently via Task.WhenAll.
                var call = Interlocked.Increment(ref callCount);
                if (call <= 10)
                {
                    // Phased detection calls (Phase 0 + 4× Phase 1 + 4× Phase 2a + Phase 2b)
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                if (call == 11)
                {
                    // Discriminator call — write a CRITICAL finding to trigger refinement
                    var reviewPath = Path.Combine(req.WorkspacePath!, AgentWorkspacePaths.RefactoringReviewFilePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(reviewPath)!);
                    File.WriteAllText(reviewPath, "[CRITICAL] Proposal references non-existent file");
                    return new AgentResult { ExitCode = 0, OutputLines = ["Review done."] };
                }
                // Refinement call — delete the proposals file instead of overwriting it.
                // This exercises the !File.Exists branch inside ParseProposalsAsync at L716–L717.
                // File.Delete does not throw when the file is absent, and the original proposals
                // variable (set before the refinement block) remains in memory for the fallback.
                File.Delete(Path.Combine(req.WorkspacePath!, ".agent", "refactoring-proposals.json"));
                return new AgentResult { ExitCode = 0, OutputLines = ["Refined."] };
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — file-not-found Warning fired (L716)
        // Production call: Logger.Warning("No refactoring proposals file found at {Path}", filePath)
        // Resolves to Warning<string>(string, string) — no Exception arg
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("No refactoring proposals file found")),
                It.IsAny<string>()),   // filePath
            Times.Once);

        // Assert — fallback-to-original Warning fired (FinalizeProposalsAsync L599)
        // Production call: Logger.Warning("Refined proposals file is missing or malformed in run {RunId}, ...", jobId)
        // Resolves to Warning<string>(string, string) — no Exception arg
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("Refined proposals file is missing or malformed")),
                It.IsAny<string>()),   // jobId
            Times.Once);

        // Assert — executor fell back to original proposals and succeeded
        result.Success.Should().BeTrue();

        // Assert — issue created with original title, confirming original proposals were preserved unchanged
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(
                It.Is<string>(t => t.Contains("Extract shared validation logic")),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert — 12 agent calls: 10 phased + discriminator + refinement
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(12));
    }
}
