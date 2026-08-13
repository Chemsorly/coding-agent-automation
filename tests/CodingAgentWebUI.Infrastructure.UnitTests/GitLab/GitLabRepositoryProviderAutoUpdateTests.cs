using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Pipeline.Interfaces;
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

    // ── IsPullRequestBehindBaseAsync — detailed_merge_status mapping ──────────

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_NeedRebase_ReturnsTrue()
    {
        // "need_rebase" is a real GitLab API value not yet in NGitLab 12 enum.
        // Use a raw string DynamicEnum to simulate what the real API would return.
        var provider = CreateProviderWithRawStatus("need_rebase");
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().BeTrue("need_rebase means the branch is behind and needs rebase");
    }

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_Mergeable_ReturnsFalse()
    {
        var provider = CreateProviderWithMr(DetailedMergeStatus.Mergeable);
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().BeFalse("Mergeable means no update needed — free the slot");
    }

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_NotOpen_ReturnsFalse()
    {
        var provider = CreateProviderWithMr(DetailedMergeStatus.NotOpen);
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().BeFalse("not_open means MR is closed — no update needed");
    }

    [Theory]
    [InlineData(DetailedMergeStatus.Checking)]
    [InlineData(DetailedMergeStatus.Unchecked)]
    [InlineData(DetailedMergeStatus.NotApproved)]
    [InlineData(DetailedMergeStatus.CiStillRunning)]
    [InlineData(DetailedMergeStatus.Preparing)]
    public async Task IsPullRequestBehindBaseAsync_TransientStates_ReturnsNull(
        DetailedMergeStatus status)
    {
        var provider = CreateProviderWithMr(status);
        var result = await provider.Provider.IsPullRequestBehindBaseAsync(1, CancellationToken.None);
        result.Should().BeNull($"DetailedMergeStatus.{status} is transient — slot must stay in-flight");
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a GitLabRepositoryProvider backed by a mocked IGitLabClient that returns
    /// a MergeRequest with the given DetailedMergeStatus enum value.
    /// </summary>
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
            DetailedMergeStatus = new DynamicEnum<DetailedMergeStatus>(status)
        };

        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[1L]).Returns(mr);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(ProjectId)).Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        return (provider, clientMock);
    }

    /// <summary>
    /// Creates a GitLabRepositoryProvider that returns a MergeRequest with a raw string
    /// DetailedMergeStatus (for values not yet in the NGitLab enum, e.g. "need_rebase").
    /// </summary>
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
            DetailedMergeStatus = new DynamicEnum<DetailedMergeStatus>(rawStatus)
        };

        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock.Setup(c => c[1L]).Returns(mr);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(ProjectId)).Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, ProjectId, BaseBranch);
        return (provider, clientMock);
    }
}
