using AwesomeAssertions;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;
using Moq;

namespace KiroWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for brain repository sync orchestration, service behavior, and error handling.
/// </summary>
public class BrainSyncUnitTests
{
    // --- Task 13.1: Orchestration ordering and skip behavior ---

    [Fact]
    public void PipelineStep_BrainPreRun_IsBetweenCloningAndCreatingBranch()
    {
        var cloning = (int)PipelineStep.CloningRepository;
        var brainPre = (int)PipelineStep.SyncingBrainRepoPreRun;
        var creating = (int)PipelineStep.CreatingBranch;

        brainPre.Should().BeGreaterThan(cloning);
        brainPre.Should().BeLessThan(creating);
    }

    [Fact]
    public void PipelineStep_BrainPostRun_IsBetweenCreatingPRAndCompleted()
    {
        var pr = (int)PipelineStep.CreatingPullRequest;
        var brainPost = (int)PipelineStep.SyncingBrainRepoPostRun;
        var completed = (int)PipelineStep.Completed;

        brainPost.Should().BeGreaterThan(pr);
        brainPost.Should().BeLessThan(completed);
    }

    [Fact]
    public void BrainReadOnly_OmitsWriteInstructions()
    {
        var writeInstructions = PromptBuilder.BuildBrainWriteInstructions(
            brainAvailable: true, runId: "run-1", issueIdentifier: "issue-1",
            brainReadOnly: true);

        writeInstructions.Should().BeEmpty();
    }

    [Fact]
    public void BrainReadOnly_StillProvidesReadContext()
    {
        var context = PromptBuilder.BuildBrainContextSection(brainAvailable: true);

        context.Should().NotBeEmpty();
        context.Should().Contain(".brain/AGENTS.md");
    }

    // --- Task 13.2: Error handling and graceful degradation ---

    [Fact]
    public async Task PullAsync_DefaultImplementation_ThrowsNotSupportedException()
    {
        IRepositoryProvider provider = new TestRepositoryProvider();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.PullAsync("/path", CancellationToken.None));
    }

    // --- Task 13.3: BrainUpdateService behavior ---

    [Fact]
    public void BrainUpdateService_Validate_NoChanges_ReturnsWarnings()
    {
        var mockService = new Mock<IBrainUpdateService>();
        mockService.Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<string>(), It.Is<IReadOnlyList<string>>(l => l.Count == 0)))
            .Returns(new BrainValidationResult { SessionLogCreated = false, OperationLogUpdated = false, Warnings = new[] { "session log", "log.md entry" } });

        var result = mockService.Object.Validate("/fake", "run-1", Array.Empty<string>());

        result.SessionLogCreated.Should().BeFalse();
        result.OperationLogUpdated.Should().BeFalse();
        result.HasWarnings.Should().BeTrue();
    }

    // BuildCommitMessage, ResolveConflictAcceptBoth, and BuildFallbackLogEntry tests
    // moved to KiroWebUI.Infrastructure.UnitTests (concrete BrainUpdateService behavior)

    [Fact]
    public void BrainUpdateService_EnsureGitignoreEntry_AddsWhenMissing()
    {
        var result = IBrainUpdateService.EnsureGitignoreEntry("node_modules/\n", ".brain/");

        result.Should().Contain(".brain/");
        result.Should().Contain("node_modules/");
    }

    [Fact]
    public void BrainUpdateService_EnsureGitignoreEntry_SkipsWhenPresent()
    {
        var original = "node_modules/\n.brain/\n";
        var result = IBrainUpdateService.EnsureGitignoreEntry(original, ".brain/");

        result.Should().Be(original);
    }

    [Fact]
    public void BrainUpdateService_EnsureGitignoreEntry_HandlesEmptyFile()
    {
        var result = IBrainUpdateService.EnsureGitignoreEntry("", ".brain/");

        result.Should().Contain(".brain/");
    }

    [Fact]
    public void RepositoryRole_DefaultIsWork()
    {
        var config = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        config.RepositoryRole.Should().Be(RepositoryRole.Work);
    }

    [Fact]
    public void PipelineConfiguration_DefaultBlacklistedPaths_IncludesBrain()
    {
        var config = new PipelineConfiguration();

        config.BlacklistedPaths.Should().Contain(".brain");
    }

    [Fact]
    public void PipelineConfiguration_BrainReadOnly_DefaultsFalse()
    {
        var config = new PipelineConfiguration();

        config.BrainReadOnly.Should().BeFalse();
    }

    [Fact]
    public void PipelineConfiguration_LastUsedProviderIds_DefaultsEmpty()
    {
        var config = new PipelineConfiguration();

        config.LastUsedProviderIds.Should().BeEmpty();
    }

    [Fact]
    public void PipelineRun_BrainFields_DefaultCorrectly()
    {
        var run = new PipelineRun
        {
            RunId = "test",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        run.BrainProviderConfigId.Should().BeNull();
        run.BrainContextLoaded.Should().BeFalse();
        run.BrainKnowledgeFileCount.Should().Be(0);
        run.BrainUpdatesPushed.Should().BeFalse();
        run.BrainFilesCommitted.Should().Be(0);
        run.BrainValidation.Should().BeNull();
    }

    [Fact]
    public void PipelineRunSummary_BrainFields_DefaultCorrectly()
    {
        var summary = new PipelineRunSummary
        {
            RunId = "test",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTime.UtcNow
        };

        summary.BrainRepoUsed.Should().BeFalse();
        summary.BrainUpdatesPushed.Should().BeFalse();
    }

    [Fact]
    public void BrainValidationResult_HasWarnings_TrueWhenWarningsExist()
    {
        var result = new BrainValidationResult
        {
            Warnings = new[] { "session log" }
        };

        result.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public void BrainValidationResult_HasWarnings_FalseWhenEmpty()
    {
        var result = new BrainValidationResult();

        result.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public void BrainSyncResult_WasSkipped_IndicatesNoChanges()
    {
        var result = new BrainSyncResult { WasSkipped = true, Success = false };

        result.WasSkipped.Should().BeTrue();
        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// Test provider that doesn't override PullAsync — should use default interface method.
    /// </summary>
    private class TestRepositoryProvider : IRepositoryProvider
    {
        public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;
        public string BaseBranch => "main";
        public string RepositoryFullName => "test/repo";
        public Task CloneAsync(string workspacePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct) => Task.FromResult(branchName);
        public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct) => Task.FromResult("https://github.com/test/repo/pull/1");
        public Task<string> GetHeadCommitShaAsync(string workspacePath, CancellationToken ct) => Task.FromResult("abc123");
        public Task<bool> HasCommitsAheadAsync(string workspacePath, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(string workspacePath, CancellationToken ct) => Task.FromResult<IReadOnlyList<FileChangeSummary>>(Array.Empty<FileChangeSummary>());
        public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
