using AwesomeAssertions;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests.Executors;

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
        WorkspacePath = _tempDir,
        PipelineConfiguration = new PipelineConfiguration
        {
            RefactoringReviewEnabled = reviewEnabled,
            AgentTimeout = TimeSpan.FromMinutes(5)
        }
    };

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
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((path, _) =>
            {
                var agentDir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(Path.Combine(agentDir, "refactoring-proposals.json"), proposalsJson);
            })
            .Returns(Task.CompletedTask);
    }

    private void SetupCloneWritesEmptyProposals()
    {
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((path, _) =>
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
                callCount++;
                if (callCount == 1)
                {
                    // Generator call — proposals already written by clone callback
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

        // Assert — at least 2 agent calls (generator + discriminator)
        result.Success.Should().BeTrue();
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.AtLeast(2));
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

        // Assert — only 1 agent call (generator), no discriminator
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Once);
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

        // Assert — only 1 agent call (generator), no discriminator or refinement
        result.Success.Should().BeTrue();
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Once);
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
                callCount++;
                if (callCount == 1)
                {
                    // Generator call
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                if (callCount == 2)
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

        // Assert — 3 agent calls (generator + discriminator + refinement)
        result.Success.Should().BeTrue();
        _mockAgentProvider.Verify(
            x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(3));

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
                callCount++;
                if (callCount == 1)
                {
                    // Generator call
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                if (callCount == 2)
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
                callCount++;
                if (callCount == 1)
                {
                    // Generator call
                    return new AgentResult { ExitCode = 0, OutputLines = ["Done."] };
                }
                if (callCount == 2)
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
}
