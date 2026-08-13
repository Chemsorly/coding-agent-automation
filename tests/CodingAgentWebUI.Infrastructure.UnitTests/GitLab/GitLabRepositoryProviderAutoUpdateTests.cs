using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using NGitLab.Models;
using MergeRequest = NGitLab.Models.MergeRequest;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Tests for the auto-branch-updater methods on <see cref="GitLabRepositoryProvider"/>:
/// <c>IsPullRequestBehindBaseAsync</c> and <c>UpdatePullRequestBranchAsync</c>
/// (spec 040, task 4.2 / 4.3).
/// </summary>
public class GitLabRepositoryProviderAutoUpdateTests
{
    private const string BaseBranch = "main";
    private const int ProjectId = 1;

    // ── SupportsServerSideBranchUpdate ────────────────────────────────────────

    [Fact]
    public void SupportsServerSideBranchUpdate_ReturnsTrue()
    {
        var provider = CreateProviderWithMr(DetailedMergeStatus.Mergeable);
        ((IRepositoryProvider)provider.Provider).SupportsServerSideBranchUpdate.Should().BeTrue();
    }

    // ── IsPullRequestBehindBaseAsync — Behind ─────────────────────────────────

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_NeedRebase_ReturnsBehind()
    {
        // "need_rebase" is a real GitLab API value not yet in NGitLab 12 enum.
        var provider = CreateProviderWithRawStatus("need_rebase");
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Behind, "need_rebase means the branch is behind and needs rebase");
    }

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_ConflictRawStatus_ReturnsConflicted()
    {
        // "conflict" is a real GitLab API value for textual merge conflicts,
        // distinct from "need_rebase". Must map to Conflicted so the rework path triggers
        // and the in-flight slot is freed (not stuck on Unknown indefinitely).
        var provider = CreateProviderWithRawStatus("conflict");
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Conflicted,
            "'conflict' raw status must map to Conflicted — not Unknown — to prevent permanent slot occupation");
    }

    // ── IsPullRequestBehindBaseAsync — UpToDate ───────────────────────────────

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_Mergeable_ReturnsUpToDate()
    {
        var provider = CreateProviderWithMr(DetailedMergeStatus.Mergeable);
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.UpToDate, "Mergeable means no update needed — free the slot");
    }

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_NotOpen_ReturnsUpToDate()
    {
        var provider = CreateProviderWithMr(DetailedMergeStatus.NotOpen);
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.UpToDate, "not_open means MR is closed — no update needed");
    }

    // ── IsPullRequestBehindBaseAsync — Conflicted ─────────────────────────────

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_HasConflictsTrue_ReturnsConflicted()
    {
        // GitLab has no dedicated "conflicted" DetailedMergeStatus enum value.
        // HasConflicts = true is the canonical way to detect merge conflicts.
        var mr = new MergeRequest
        {
            Iid = 1,
            Title = "Test MR",
            State = "opened",
            SourceBranch = "feature/test",
            TargetBranch = BaseBranch,
            DetailedMergeStatus = new DynamicEnum<DetailedMergeStatus>(DetailedMergeStatus.Mergeable),
            HasConflicts = true  // ← conflict signal takes precedence over DetailedMergeStatus
        };

        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[1L]).Returns(mr);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(ProjectId)).Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        var result = await provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);

        result.Should().Be(PrMergeabilityStatus.Conflicted,
            "HasConflicts=true must return Conflicted regardless of DetailedMergeStatus");
    }

    // ── IsPullRequestBehindBaseAsync — Blocked (transient states) ────────────

    [Theory]
    [InlineData(DetailedMergeStatus.Checking)]
    [InlineData(DetailedMergeStatus.Unchecked)]
    [InlineData(DetailedMergeStatus.NotApproved)]
    [InlineData(DetailedMergeStatus.CiStillRunning)]
    [InlineData(DetailedMergeStatus.Preparing)]
    public async Task IsPullRequestBehindBaseAsync_TransientStates_ReturnsBlocked(
        DetailedMergeStatus status)
    {
        var provider = CreateProviderWithMr(status);
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Blocked,
            $"DetailedMergeStatus.{status} is transient — slot must stay in-flight");
    }

    // ── UpdatePullRequestBranchAsync ──────────────────────────────────────────

    [Fact]
    public async Task UpdatePullRequestBranchAsync_WhenMrExists_DoesNotThrow()
    {
        // Use NGitLab.Mock in-memory server to test the real Rebase call path
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: BaseBranch, configure: project =>
                {
                    project.WithCommit("Branch commit", sourceBranch: "feature/test", configure: commit =>
                        commit.WithFile("test.txt", "content"));
                })
            .BuildServer();

        var client = server.CreateClient();
        var projectId = (int)client.Projects.Accessible.First().Id;
        var provider = new GitLabRepositoryProvider(client, projectId, BaseBranch);

        // Create a real MR so Rebase() has something to work with
        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Create(new MergeRequestCreate
        {
            Title = "Test MR",
            SourceBranch = "feature/test",
            TargetBranch = BaseBranch
        });

        var exception = await Record.ExceptionAsync(
            () => provider.UpdatePullRequestBranchAsync((int)mr.Iid, CancellationToken.None));
        exception.Should().BeNull("Rebase on NGitLab.Mock should not throw");
    }

    [Fact]
    public async Task UpdatePullRequestBranchAsync_WhenRebaseReturns409_ThrowsInvalidOperationException()
    {
        // Arrange: mock Rebase() to throw GitLabException with 409 Conflict.
        // This simulates the GitLab server transaction lock being busy.
        var conflictException = new GitLabException("409 Conflict — rebase already in progress")
        {
            StatusCode = System.Net.HttpStatusCode.Conflict
        };

        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[1L]).Returns(new MergeRequest
        {
            Iid = 1,
            Title = "Test MR",
            State = "opened",
            SourceBranch = "feature/test",
            TargetBranch = BaseBranch
        });
        mrClientMock.Setup(c => c.Rebase(1L)).Throws(conflictException);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(ProjectId)).Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);

        // Act & Assert: 409 from Rebase must surface as InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.UpdatePullRequestBranchAsync(1, CancellationToken.None));
    }

    // ── ListAgentBranchesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ListAgentBranchesAsync_ReturnsOnlyAgentBranches()
    {
        // Build mock that returns a mix of branch names
        var allBranches = new[]
        {
            $"{PipelineConstants.BranchPrefix}123-fix-login",
            "main",
            $"{PipelineConstants.BranchPrefix}42-update-deps",
            "feature/manual-work",
        };

        var branchObjects = allBranches
            .Select(name => new Branch { Name = name })
            .ToList();

        var repoClientMock = new Mock<IRepositoryClient>();
        var branchClientMock = new Mock<IBranchClient>();
        branchClientMock.Setup(b => b.All).Returns(branchObjects);
        repoClientMock.Setup(r => r.Branches).Returns(branchClientMock.Object);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetRepository(ProjectId)).Returns(repoClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        var result = await provider.ListAgentBranchesAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain($"{PipelineConstants.BranchPrefix}123-fix-login");
        result.Should().Contain($"{PipelineConstants.BranchPrefix}42-update-deps");
        result.Should().NotContain("main");
        result.Should().NotContain("feature/manual-work");
    }

    // ── DeleteBranchAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBranchAsync_WhenBranchExists_DoesNotThrow()
    {
        var branchName = $"{PipelineConstants.BranchPrefix}42-fix-login";

        var repoClientMock = new Mock<IRepositoryClient>();
        var branchClientMock = new Mock<IBranchClient>();
        branchClientMock.Setup(b => b.Delete(branchName));
        repoClientMock.Setup(r => r.Branches).Returns(branchClientMock.Object);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetRepository(ProjectId)).Returns(repoClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        var ex = await Record.ExceptionAsync(
            () => provider.DeleteBranchAsync(branchName, CancellationToken.None));

        ex.Should().BeNull("Delete should not throw for an existing branch");
        branchClientMock.Verify(b => b.Delete(branchName), Times.Once);
    }

    [Fact]
    public async Task DeleteBranchAsync_WhenBranchNotFound_TreatedAsNoOp()
    {
        var branchName = $"{PipelineConstants.BranchPrefix}99-already-gone";
        var notFoundEx = new GitLabException("Branch Not Found") { StatusCode = System.Net.HttpStatusCode.NotFound };

        var repoClientMock = new Mock<IRepositoryClient>();
        var branchClientMock = new Mock<IBranchClient>();
        branchClientMock.Setup(b => b.Delete(branchName)).Throws(notFoundEx);
        repoClientMock.Setup(r => r.Branches).Returns(branchClientMock.Object);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetRepository(ProjectId)).Returns(repoClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        var ex = await Record.ExceptionAsync(
            () => provider.DeleteBranchAsync(branchName, CancellationToken.None));

        ex.Should().BeNull("404 on a non-existent branch should be a no-op");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (GitLabRepositoryProvider Provider, Mock<IGitLabClient> ClientMock)
        CreateProviderWithMr(DetailedMergeStatus status)
    {
        var mr = new MergeRequest
        {
            Iid = 1,
            Title = "Test MR",
            State = "opened",
            SourceBranch = "feature/test",
            TargetBranch = BaseBranch,
            DetailedMergeStatus = new DynamicEnum<DetailedMergeStatus>(status),
            HasConflicts = false
        };

        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[1L]).Returns(mr);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(ProjectId)).Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        return (provider, clientMock);
    }

    private static (GitLabRepositoryProvider Provider, Mock<IGitLabClient> ClientMock)
        CreateProviderWithRawStatus(string rawStatus)
    {
        var mr = new MergeRequest
        {
            Iid = 1,
            Title = "Test MR",
            State = "opened",
            SourceBranch = "feature/test",
            TargetBranch = BaseBranch,
            DetailedMergeStatus = new DynamicEnum<DetailedMergeStatus>(rawStatus),
            HasConflicts = false
        };

        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[1L]).Returns(mr);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(ProjectId)).Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        return (provider, clientMock);
    }
}
