using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// WireMock tests for <see cref="GitHubRepositoryProvider.WriteFileToBaseBranchAsync"/>.
/// Spec 054 req 4.
/// </summary>
[Collection("GitHubTelemetry")]
public class GitHubRepositoryProviderWriteFileTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-99";
    private const string BaseBranch = "main";

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);

    private string FilePath => ApiPath($"/repos/{Owner}/{Repo}/contents/inbox/2026-01-01_run.md");

    [Fact]
    public async Task WriteFile_NewFile_CreatesWithoutSha()
    {
        // GET is left unstubbed: WireMock returns 404 for unmatched requests → "file doesn't exist"
        // Do not use StubError (it matches every verb, including PUT)
        StubPut(FilePath, new { content = new { sha = "b1" }, commit = new { sha = "c1" } }, 201);

        await using var provider = CreateProvider();
        var result = await provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "new content", "brain: inbox", CancellationToken.None);

        result.CommitSha.Should().Be("c1");

        // Assert the PUT body: message, branch, no top-level sha
        // WireMock logs all requests; GET is first (404 for new file), PUT is last
        var putEntry = Server.LogEntries.Last(e => e.RequestMessage?.Method == "PUT");
        var putBody = putEntry.RequestMessage!.Body;
        putBody.Should().Contain("brain: inbox");
        putBody.Should().Contain("\"branch\":\"main\"");
        putBody.Should().NotContain("\"sha\":", "a create request must not include any sha key at top level");
    }

    [Fact]
    public async Task WriteFile_ExistingFile_UpdatesWithBlobSha()
    {
        StubGet(FilePath, new { name = "f", path = "inbox/2026-01-01_run.md", sha = "blob-1", size = 1, type = "file" });
        StubPut(FilePath, new { content = new { sha = "b2" }, commit = new { sha = "c2" } }, 201);

        await using var provider = CreateProvider();
        var result = await provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "updated content", "brain: inbox update", CancellationToken.None);

        result.CommitSha.Should().Be("c2");

        var putEntryExisting = Server.LogEntries.Last(e => e.RequestMessage?.Method == "PUT");
        var putBody = putEntryExisting.RequestMessage!.Body;
        putBody.Should().Contain("\"sha\":\"blob-1\"");
    }

    [Fact]
    public async Task WriteFile_SendsPipelineIdentity()
    {
        // GET unstubbed → 404 → new file
        StubPut(FilePath, new { content = new { sha = "b3" }, commit = new { sha = "c3" } }, 201);

        await using var provider = CreateProvider();
        await provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "content", "brain: inbox", CancellationToken.None);

        var putEntryIdentity = Server.LogEntries.Last(e => e.RequestMessage?.Method == "PUT");
        var putBody = putEntryIdentity.RequestMessage!.Body;
        putBody.Should().Contain(GitConstants.CommitAuthorName);
        putBody.Should().Contain(GitConstants.CommitAuthorEmail);
        // Both author and committer should be set
        var authorCount = putBody!.Split("\"name\":\"" + GitConstants.CommitAuthorName + "\"").Length - 1;
        authorCount.Should().BeGreaterThanOrEqualTo(2, "both author and committer should contain the pipeline name");
    }

    [Fact]
    public async Task WriteFile_Conflict409_ThrowsRemoteFileConflict()
    {
        // GET unstubbed → 404 → attempts create → 409
        StubPut(FilePath, new { message = "conflict" }, 409);

        await using var provider = CreateProvider();
        var act = () => provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "content", "brain: inbox", CancellationToken.None);

        await act.Should().ThrowAsync<RemoteFileConflictException>();
    }

    [Fact]
    public async Task WriteFile_Validation422_ThrowsRemoteFileConflict()
    {
        // GET unstubbed → 404 → attempts create → 422
        StubPut(FilePath, new { message = "sha wasn't supplied" }, 422);

        await using var provider = CreateProvider();
        var act = () => provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "content", "brain: inbox", CancellationToken.None);

        await act.Should().ThrowAsync<RemoteFileConflictException>();
    }

    [Fact]
    public async Task WriteFile_Forbidden403_IsNotAConflict()
    {
        // 403 is retried by Polly (AuthorizationException handler) — this test will be slow (~5s).
        // GET unstubbed → 404 → attempts create → 403
        StubPut(FilePath, new { message = "denied" }, 403);

        await using var provider = CreateProvider();
        var act = () => provider.WriteFileToBaseBranchAsync(
            "inbox/2026-01-01_run.md", "content", "brain: inbox", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Should().NotBeOfType<RemoteFileConflictException>(
            "403 Forbidden is not a conflict — it must not be mapped to RemoteFileConflictException");
    }
}
