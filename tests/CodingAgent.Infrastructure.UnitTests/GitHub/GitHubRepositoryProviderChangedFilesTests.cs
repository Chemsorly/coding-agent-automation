using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Pipeline.Models;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// WireMock-based tests for <see cref="GitHubRepositoryProvider.GetPullRequestChangedFilesAsync"/>.
/// </summary>
[Collection("GitHubTelemetry")]
public class GitHubRepositoryProviderChangedFilesTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-99";
    private const string BaseBranch = "main";

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);

    [Fact]
    public async Task GetChangedFiles_ListsFilesAndHead()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/7"),
            BuildPr(7, changedFiles: 2, headSha: "h1"));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/7/files"),
            new[]
            {
                new { filename = "a.cs", status = "modified", previous_filename = (string?)null },
                new { filename = "b.cs", status = "modified", previous_filename = (string?)null }
            });

        var provider = CreateProvider();
        var result = await provider.GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Files.Should().BeEquivalentTo(["a.cs", "b.cs"]);
        result.IsComplete.Should().BeTrue();
        result.HeadSha.Should().Be("h1");
    }

    [Fact]
    public async Task GetChangedFiles_RenameListsBothPaths()
    {
        // GitHub counts a rename as one changed_files entry but provides both filename and previous_filename
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/7"),
            BuildPr(7, changedFiles: 1, headSha: "h1"));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/7/files"),
            new[]
            {
                new { filename = "new-name.cs", status = "renamed", previous_filename = "sonar-project.properties" }
            });

        var provider = CreateProvider();
        var result = await provider.GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result!.Files.Should().BeEquivalentTo(["new-name.cs", "sonar-project.properties"]);
        result.IsComplete.Should().BeTrue("a rename counts as 1 in changed_files, matching files.Count");
    }

    [Fact]
    public async Task GetChangedFiles_FewerFilesThanThePrReports_IsIncomplete()
    {
        // PR reports 3000 files changed but API only returns 2 → incomplete
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/7"),
            BuildPr(7, changedFiles: 3000, headSha: "h1"));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/7/files"),
            new[]
            {
                new { filename = "a.cs", status = "modified", previous_filename = (string?)null },
                new { filename = "b.cs", status = "modified", previous_filename = (string?)null }
            });

        var provider = CreateProvider();
        var result = await provider.GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        result!.IsComplete.Should().BeFalse("the API returned fewer files than the PR's changed_files count");
    }

    [Fact]
    public async Task GetChangedFiles_ProviderFails_Throws()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}/pulls/7"), 404, new { message = "Not Found" });

        var provider = CreateProvider();
        var act = async () => await provider.GetPullRequestChangedFilesAsync(7, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>("provider failure must propagate, not return null");
    }

    private static object BuildPr(int number, int changedFiles, string headSha) => new
    {
        id = number * 100,
        number,
        html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}",
        state = "open",
        title = $"PR #{number}",
        body = "body",
        draft = false,
        changed_files = changedFiles,
        node_id = $"PR_node_{number}",
        user = new { login = "testuser", id = 1 },
        labels = Array.Empty<object>(),
        head = new { @ref = "feature/branch", sha = headSha },
        @base = new { @ref = "main", sha = "def456" },
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };
}
