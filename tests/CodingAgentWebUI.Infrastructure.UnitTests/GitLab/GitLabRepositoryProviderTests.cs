using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using NGitLab.Models;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Property-based tests for GitLabRepositoryProvider.
/// Feature: 029-gitlab-providers, Properties 13–16, 19.
/// </summary>
public class GitLabRepositoryProviderTests
{
    /// <summary>
    /// Creates a mock GitLab server with a project that has an initial commit (required for branches).
    /// </summary>
    private static (IGitLabClient Client, int ProjectId) CreateServerWithProject()
    {
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main")
            .BuildServer();

        var client = server.CreateClient();
        var projects = client.Projects.Accessible.ToList();
        var projectId = (int)projects.First().Id;
        return (client, projectId);
    }

    /// <summary>
    /// Creates a mock GitLab server with a project that has a source branch (via commit on that branch).
    /// This is needed because NGitLab.Mock validates branch existence when creating MRs via the client API.
    /// </summary>
    private static (IGitLabClient Client, int ProjectId) CreateServerWithBranch(string branchName)
    {
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithCommit("Branch commit", sourceBranch: branchName, configure: commit =>
                    {
                        commit.WithFile("test.txt", "content");
                    });
                })
            .BuildServer();

        var client = server.CreateClient();
        var projects = client.Projects.Accessible.ToList();
        var projectId = (int)projects.First().Id;
        return (client, projectId);
    }

    /// <summary>
    /// Creates a mock GitLab server with a project that has a merge request pre-configured.
    /// Uses the config-based approach which doesn't validate branch existence.
    /// </summary>
    private static (IGitLabClient Client, int ProjectId) CreateServerWithMergeRequest(
        string title, string description, string sourceBranch, string targetBranch)
    {
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: sourceBranch, title: title,
                        targetBranch: targetBranch, description: description);
                })
            .BuildServer();

        var client = server.CreateClient();
        var projects = client.Projects.Accessible.ToList();
        var projectId = (int)projects.First().Id;
        return (client, projectId);
    }

    #region Property 13: Merge request creation maps fields correctly

    /// <summary>
    /// Property 13: Merge request creation maps fields correctly.
    /// For any valid PullRequestInfo with IsDraft=true, CreatePullRequestAsync creates
    /// a merge request with "Draft: " prefix on the title, the correct body, and correct branches.
    /// **Validates: Requirements 10.1, 24.1, 24.2**
    /// </summary>
    [Property(Arbitrary = [typeof(MergeRequestDataArbitrary)])]
    public void CreatePullRequestAsync_MapsFieldsCorrectly_WithDraftPrefix(MergeRequestTestData data)
    {
        // Arrange — create server with the source branch already existing
        var (client, projectId) = CreateServerWithBranch(data.SourceBranch);
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var prInfo = new PullRequestInfo
        {
            Title = data.Title,
            Body = data.Body,
            BranchName = data.SourceBranch,
            BaseBranch = "main",
            IsDraft = true
        };

        // Act
        var url = provider.CreatePullRequestAsync(prInfo, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — verify the MR was created with Draft: prefix
        var mrClient = client.GetMergeRequest(projectId);
        var allMrs = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).ToList();
        var createdMr = allMrs.First();

        createdMr.Title.Should().Be($"Draft: {data.Title}");
        createdMr.Description.Should().Be(data.Body);
        createdMr.SourceBranch.Should().Be(data.SourceBranch);
        createdMr.TargetBranch.Should().Be("main");
        url.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Property 13: Merge request creation without draft prefix.
    /// For any valid PullRequestInfo with IsDraft=false, CreatePullRequestAsync creates
    /// a merge request without the "Draft: " prefix.
    /// **Validates: Requirements 10.1, 24.1**
    /// </summary>
    [Property(Arbitrary = [typeof(MergeRequestDataArbitrary)])]
    public void CreatePullRequestAsync_MapsFieldsCorrectly_WithoutDraftPrefix(MergeRequestTestData data)
    {
        // Arrange — create server with the source branch already existing
        var (client, projectId) = CreateServerWithBranch(data.SourceBranch);
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var prInfo = new PullRequestInfo
        {
            Title = data.Title,
            Body = data.Body,
            BranchName = data.SourceBranch,
            BaseBranch = "main",
            IsDraft = false
        };

        // Act
        provider.CreatePullRequestAsync(prInfo, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — verify the MR was created without Draft: prefix
        var mrClient = client.GetMergeRequest(projectId);
        var allMrs = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).ToList();
        var createdMr = allMrs.First();

        createdMr.Title.Should().Be(data.Title);
        createdMr.Title.Should().NotStartWith("Draft: ");
    }

    #endregion

    #region Property 14: Draft prefix removal on markReady

    /// <summary>
    /// Property 14: Draft prefix removal on markReady.
    /// When UpdatePullRequestAsync is called with markReady=true on a MR whose title
    /// starts with "Draft: ", the prefix is removed.
    /// **Validates: Requirements 10.3, 24.3**
    /// </summary>
    [Property(Arbitrary = [typeof(MergeRequestDataArbitrary)])]
    public void UpdatePullRequestAsync_RemovesDraftPrefix_WhenMarkReady(MergeRequestTestData data)
    {
        // Arrange — create MR with Draft: prefix via config
        var draftTitle = $"Draft: {data.Title}";
        var (client, projectId) = CreateServerWithMergeRequest(
            draftTitle, data.Body, data.SourceBranch, "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        // Get the MR IID
        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        // Act
        provider.UpdatePullRequestAsync((int)mr.Iid, "Updated body", markReady: true, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — title should have Draft: prefix removed
        var updated = mrClient[(int)mr.Iid];
        updated.Title.Should().Be(data.Title);
        updated.Title.Should().NotStartWith("Draft: ");
    }

    /// <summary>
    /// Property 14: Title unchanged when no Draft: prefix and markReady=true.
    /// When UpdatePullRequestAsync is called with markReady=true on a MR whose title
    /// does NOT start with "Draft: ", the title remains unchanged.
    /// **Validates: Requirements 10.3, 24.4**
    /// </summary>
    [Property(Arbitrary = [typeof(MergeRequestDataArbitrary)])]
    public void UpdatePullRequestAsync_TitleUnchanged_WhenNoDraftPrefix(MergeRequestTestData data)
    {
        // Arrange — create MR without Draft: prefix via config
        var (client, projectId) = CreateServerWithMergeRequest(
            data.Title, data.Body, data.SourceBranch, "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        // Get the MR IID
        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        // Act
        provider.UpdatePullRequestAsync((int)mr.Iid, "Updated body", markReady: true, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — title should remain unchanged (no Draft: prefix to remove)
        var updated = mrClient[(int)mr.Iid];
        updated.Title.Should().Be(data.Title);
    }

    #endregion

    #region Property 15: Agent MR branch prefix filtering

    /// <summary>
    /// Property 15: Agent MR branch prefix filtering.
    /// GetAgentPullRequestsAsync returns only MRs whose source branch starts with
    /// "feature/auto-{issueId}-" for the given issue identifier.
    /// **Validates: Requirements 10.5, 24.1**
    /// </summary>
    [Property(Arbitrary = [typeof(BranchFilterArbitrary)])]
    public void GetAgentPullRequestsAsync_FiltersOnBranchPrefix(BranchFilterTestData data)
    {
        // Arrange — use config-based MR creation (doesn't validate branch existence)
        var config = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    // Create matching MRs (with the correct branch prefix)
                    foreach (var branch in data.MatchingBranches)
                    {
                        project.WithMergeRequest(sourceBranch: branch, title: $"MR for {branch}",
                            description: "Test MR", targetBranch: "main");
                    }

                    // Create non-matching MRs (different branch patterns)
                    foreach (var branch in data.NonMatchingBranches)
                    {
                        project.WithMergeRequest(sourceBranch: branch, title: $"MR for {branch}",
                            description: "Test MR", targetBranch: "main");
                    }
                });

        var server = config.BuildServer();
        var client = server.CreateClient();
        var projects = client.Projects.Accessible.ToList();
        var projectId = (int)projects.First().Id;
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        // Act
        var results = provider.GetAgentPullRequestsAsync(data.IssueId, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — only matching branches should be returned
        results.Count.Should().Be(data.MatchingBranches.Count);
        foreach (var result in results)
        {
            result.BranchName.Should().StartWith($"{PipelineConstants.BranchPrefix}{data.IssueId}-");
        }
    }

    #endregion

    #region Property 16: Closing keyword extraction

    /// <summary>
    /// Property 16: Closing keyword extraction.
    /// ExtractLinkedIssuesAsync correctly extracts issue numbers from Closes/Fixes/Resolves #N
    /// patterns in MR title and description.
    /// **Validates: Requirements 10.9**
    /// </summary>
    [Property(Arbitrary = [typeof(ClosingKeywordArbitrary)])]
    public void ExtractLinkedIssuesAsync_ExtractsClosingKeywords(ClosingKeywordTestData data)
    {
        // Arrange — create MR with closing keywords in title and description via config
        var (client, projectId) = CreateServerWithMergeRequest(
            data.Title, data.Description, "feature-branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        // Get the MR IID
        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        // Act
        var linkedIssues = provider.ExtractLinkedIssuesAsync((int)mr.Iid, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — all expected issue numbers should be extracted
        linkedIssues.Should().BeEquivalentTo(data.ExpectedIssueNumbers);
    }

    #endregion

    #region Property 19: Clone URL format construction

    /// <summary>
    /// Property 19: Clone URL format construction.
    /// The BuildAuthenticatedCloneUrl method constructs URLs in the format:
    /// https://oauth2:{token}@{host}/{path}.git
    /// We test this indirectly by exposing the private method via reflection.
    /// **Validates: Requirements 24.1, 24.2, 24.3, 24.4, 27.3**
    /// </summary>
    [Property(Arbitrary = [typeof(CloneUrlArbitrary)])]
    public void BuildAuthenticatedCloneUrl_ConstructsCorrectFormat(CloneUrlTestData data)
    {
        // Arrange — use a testable subclass to expose the private method
        var (client, projectId) = CreateServerWithProject();
        var provider = new TestableGitLabRepositoryProvider(client, projectId, "main");

        // Simulate ValidateAsync populating the HttpUrlToRepo
        provider.SetHttpUrlToRepo(data.HttpUrlToRepo);

        // Act
        var cloneUrl = provider.ExposeBuildAuthenticatedCloneUrl(data.Token);

        // Assert — URL should follow the format: https://oauth2:{token}@{host}/{path}.git
        var uri = new Uri(cloneUrl);
        uri.Scheme.Should().Be("https");
        uri.UserInfo.Should().Be($"{GitConstants.GitLabTokenUsername}:{data.Token}");
        uri.Host.Should().Be(data.ExpectedHost);
        uri.AbsolutePath.Should().Be(data.ExpectedPath);
    }

    #endregion
}

#region Test Helpers

/// <summary>
/// Testable subclass that exposes private/protected methods for testing.
/// </summary>
internal sealed class TestableGitLabRepositoryProvider : GitLabRepositoryProvider
{
    public TestableGitLabRepositoryProvider(IGitLabClient client, int projectId, string baseBranch)
        : base(client, projectId, baseBranch)
    {
    }

    /// <summary>
    /// Sets the HttpUrlToRepo field to simulate ValidateAsync having been called.
    /// Uses reflection to set the private backing field on the base class.
    /// </summary>
    public void SetHttpUrlToRepo(string url)
    {
        var field = typeof(GitLabProviderBase).GetField("_httpUrlToRepo",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(this, url);
    }

    /// <summary>
    /// Exposes the private BuildAuthenticatedCloneUrl method for testing.
    /// </summary>
    public string ExposeBuildAuthenticatedCloneUrl(string token)
    {
        var method = typeof(GitLabRepositoryProvider).GetMethod("BuildAuthenticatedCloneUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)method!.Invoke(this, [token])!;
    }
}

#endregion

#region Test Data Types and Arbitraries

/// <summary>
/// Test data for merge request creation property tests.
/// </summary>
public record MergeRequestTestData(string Title, string Body, string SourceBranch)
{
    public override string ToString() =>
        $"Title={Title[..Math.Min(20, Title.Length)]}..., Source={SourceBranch}";
}

/// <summary>
/// Test data for branch prefix filtering property tests.
/// </summary>
public record BranchFilterTestData(
    string IssueId,
    IReadOnlyList<string> MatchingBranches,
    IReadOnlyList<string> NonMatchingBranches)
{
    public override string ToString() =>
        $"IssueId={IssueId}, Matching={MatchingBranches.Count}, NonMatching={NonMatchingBranches.Count}";
}

/// <summary>
/// Test data for closing keyword extraction property tests.
/// </summary>
public record ClosingKeywordTestData(
    string Title,
    string Description,
    IReadOnlyList<string> ExpectedIssueNumbers)
{
    public override string ToString() =>
        $"Expected=[{string.Join(",", ExpectedIssueNumbers.Select(n => $"#{n}"))}]";
}

/// <summary>
/// Test data for clone URL format property tests.
/// </summary>
public record CloneUrlTestData(
    string HttpUrlToRepo,
    string Token,
    string ExpectedHost,
    string ExpectedPath)
{
    public override string ToString() =>
        $"Host={ExpectedHost}, Token={Token[..Math.Min(8, Token.Length)]}...";
}

/// <summary>
/// Generates valid merge request data (non-empty title, body, branch name).
/// Title is guaranteed NOT to start with "Draft: " to test the prefix addition.
/// Branch names use only lowercase alphanumeric and dashes (valid git branch names).
/// </summary>
public static class MergeRequestDataArbitrary
{
    public static Arbitrary<MergeRequestTestData> MergeRequestTestData()
    {
        var alphanumChars = "abcdefghijklmnopqrstuvwxyz0123456789 ".ToCharArray();
        var branchChars = "abcdefghijklmnopqrstuvwxyz0123456789-".ToCharArray();

        var titleGen =
            from len in Gen.Choose(5, 40)
            from chars in Gen.Elements(alphanumChars).ArrayOf(len)
            let title = new string(chars).Trim()
            where title.Length > 0 && !title.StartsWith("Draft: ", StringComparison.Ordinal)
            select title;

        var bodyGen =
            from len in Gen.Choose(10, 80)
            from chars in Gen.Elements(alphanumChars).ArrayOf(len)
            select new string(chars).Trim() is { Length: > 0 } s ? s : "Default body";

        var branchGen =
            from len in Gen.Choose(5, 15)
            from chars in Gen.Elements(branchChars).ArrayOf(len)
            let branch = new string(chars).Trim('-')
            where branch.Length > 2
            select branch;

        var combined =
            from title in titleGen
            from body in bodyGen
            from source in branchGen
            where source != "main"
            select new MergeRequestTestData(title, body, source);

        return combined.ToArbitrary();
    }
}

/// <summary>
/// Generates branch filter test data with matching and non-matching branches.
/// </summary>
public static class BranchFilterArbitrary
{
    public static Arbitrary<BranchFilterTestData> BranchFilterTestData()
    {
        var suffixChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var issueIdGen =
            from id in Gen.Choose(1, 999)
            select id.ToString();

        var suffixGen =
            from len in Gen.Choose(3, 8)
            from chars in Gen.Elements(suffixChars).ArrayOf(len)
            select new string(chars);

        var combined =
            from issueId in issueIdGen
            from matchCount in Gen.Choose(1, 2)
            from matchSuffixes in suffixGen.ArrayOf(matchCount)
            from nonMatchCount in Gen.Choose(1, 2)
            from nonMatchSuffixes in suffixGen.ArrayOf(nonMatchCount)
            let matchingBranches = matchSuffixes
                .Select(s => $"{PipelineConstants.BranchPrefix}{issueId}-{s}")
                .Distinct()
                .ToList()
            let nonMatchingBranches = nonMatchSuffixes
                .Select(s => $"feature/{s}")
                .Where(b => !b.StartsWith($"{PipelineConstants.BranchPrefix}{issueId}-"))
                .Distinct()
                .ToList()
            where matchingBranches.Count > 0 && nonMatchingBranches.Count > 0
            select new BranchFilterTestData(
                issueId,
                matchingBranches.AsReadOnly(),
                nonMatchingBranches.AsReadOnly());

        return combined.ToArbitrary();
    }
}

/// <summary>
/// Generates closing keyword test data with various patterns.
/// </summary>
public static class ClosingKeywordArbitrary
{
    public static Arbitrary<ClosingKeywordTestData> ClosingKeywordTestData()
    {
        var keywords = new[] { "Closes", "Fixes", "Resolves", "closes", "fixes", "resolves" };

        var gen =
            from titleIssueCount in Gen.Choose(0, 2)
            from descIssueCount in Gen.Choose(0, 3)
            from titleIssueIds in Gen.Choose(1, 500).ArrayOf(titleIssueCount)
            from descIssueIds in Gen.Choose(1, 500).ArrayOf(descIssueCount)
            from titleKeywords in Gen.Elements(keywords).ArrayOf(titleIssueCount)
            from descKeywords in Gen.Elements(keywords).ArrayOf(descIssueCount)
            let titleParts = titleIssueIds.Zip(titleKeywords, (id, kw) => $"{kw} #{id}")
            let descParts = descIssueIds.Zip(descKeywords, (id, kw) => $"{kw} #{id}")
            let title = titleParts.Any()
                ? $"Feature: {string.Join(", ", titleParts)}"
                : "Simple feature title"
            let description = descParts.Any()
                ? $"This MR implements the feature.\n\n{string.Join("\n", descParts)}"
                : "Simple description without closing keywords"
            let allIssueIds = titleIssueIds.Concat(descIssueIds)
                .Select(id => id.ToString())
                .Distinct()
                .ToList()
            select new ClosingKeywordTestData(title, description, allIssueIds.AsReadOnly());

        return gen.ToArbitrary();
    }
}

/// <summary>
/// Generates clone URL test data with valid HTTPS URLs and tokens.
/// </summary>
public static class CloneUrlArbitrary
{
    public static Arbitrary<CloneUrlTestData> CloneUrlTestData()
    {
        var tokenChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
        var hostChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        var pathChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var tokenGen =
            from len in Gen.Choose(10, 20)
            from chars in Gen.Elements(tokenChars).ArrayOf(len)
            select new string(chars);

        var hostGen =
            from len in Gen.Choose(4, 8)
            from chars in Gen.Elements(hostChars).ArrayOf(len)
            select $"{new string(chars)}.com";

        var namespaceGen =
            from len in Gen.Choose(3, 8)
            from chars in Gen.Elements(pathChars).ArrayOf(len)
            select new string(chars);

        var projectGen =
            from len in Gen.Choose(3, 8)
            from chars in Gen.Elements(pathChars).ArrayOf(len)
            select new string(chars);

        var combined =
            from token in tokenGen
            from host in hostGen
            from ns in namespaceGen
            from proj in projectGen
            let httpUrl = $"https://{host}/{ns}/{proj}.git"
            let expectedPath = $"/{ns}/{proj}.git"
            select new CloneUrlTestData(httpUrl, token, host, expectedPath);

        return combined.ToArbitrary();
    }
}

#endregion
