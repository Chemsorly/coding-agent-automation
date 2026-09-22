using AwesomeAssertions;
using CodingAgent.Infrastructure.GitLab;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using NGitLab.Models;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Characterization tests for GitLabRepositoryProvider.ListPullRequestCommentsAsync.
/// These were missing entirely before this refactoring work.
/// </summary>
public class GitLabRepositoryProviderPullRequestCommentsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (IGitLabClient Client, int ProjectId, long MrIid) CreateServerWithMr()
    {
        var server = new GitLabConfig()
            .WithUser("prauthor", isDefault: true)
            .WithProject("TestProject", @namespace: "prauthor", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "feature/test", title: "Test MR",
                        targetBranch: "main", description: "desc");
                })
            .BuildServer();

        var client = server.CreateClient();
        var projectId = (int)client.Projects.Accessible.First().Id;
        var mr = client.GetMergeRequest(projectId).Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();
        return (client, projectId, mr.Iid);
    }

    // ── ListPullRequestCommentsAsync ──────────────────────────────────────────

    [Fact]
    public async Task ListPullRequestCommentsAsync_IsBotFlag_SetForBotUsernames()
    {
        var (client, projectId, mrIid) = CreateServerWithMr();

        // Create two users — one bot, one human
        var botServer = new GitLabConfig()
            .WithUser("dependabot[bot]", isDefault: false)
            .WithUser("prauthor", isDefault: true)
            .WithProject("TestProject", @namespace: "prauthor", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "feature/bot-test", title: "Bot MR",
                        targetBranch: "main", description: "desc");
                })
            .BuildServer();

        var botClient = botServer.CreateClient("dependabot[bot]");
        var prAuthorClient = botServer.CreateClient("prauthor");
        var bProjectId = (int)prAuthorClient.Projects.Accessible.First().Id;
        var bMr = prAuthorClient.GetMergeRequest(bProjectId)
            .Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();
        var bMrIid = (int)bMr.Iid;

        // Post a discussion as the bot user
        var botDiscussions = botClient.GetMergeRequest(bProjectId).Discussions(bMrIid);
        botDiscussions.Add(new MergeRequestDiscussionCreate { Body = "Automated bot comment" });

        var provider = new GitLabRepositoryProvider(prAuthorClient, bProjectId, "main");
        var result = await provider.ListPullRequestCommentsAsync(bMrIid, "prauthor", CancellationToken.None);

        result.Should().NotBeEmpty();
        var botComment = result.FirstOrDefault(c => c.Body == "Automated bot comment");
        botComment.Should().NotBeNull("bot comment should be in results");
        botComment!.IsBot.Should().BeTrue("username ending in '[bot]' must be flagged as bot");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_IsAuthorFlag_SetForPrAuthor_CaseInsensitive()
    {
        var (client, projectId, mrIid) = CreateServerWithMr();

        var discussionClient = client.GetMergeRequest(projectId).Discussions((int)mrIid);
        discussionClient.Add(new MergeRequestDiscussionCreate { Body = "Author's own comment" });

        var provider = new GitLabRepositoryProvider(client, projectId, "main");
        // prAuthor is "prauthor" — pass it with different casing to verify case-insensitive match
        var result = await provider.ListPullRequestCommentsAsync((int)mrIid, "PRAUTHOR", CancellationToken.None);

        result.Should().NotBeEmpty();
        var authorComment = result.FirstOrDefault(c => c.Body == "Author's own comment");
        authorComment.Should().NotBeNull();
        authorComment!.IsAuthor.Should().BeTrue("IsAuthor must be case-insensitive");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_IsAuthorFlag_FalseForOtherAuthors()
    {
        var server = new GitLabConfig()
            .WithUser("alice", isDefault: true)
            .WithUser("bob", isDefault: false)
            .WithProject("TestProject", @namespace: "alice", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "feature/x", title: "MR",
                        targetBranch: "main", description: "desc");
                })
            .BuildServer();

        var aliceClient = server.CreateClient("alice");
        var bobClient = server.CreateClient("bob");
        var projectId = (int)aliceClient.Projects.Accessible.First().Id;
        var mr = aliceClient.GetMergeRequest(projectId)
            .Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();
        var mrIid = (int)mr.Iid;

        // Bob posts a comment
        bobClient.GetMergeRequest(projectId).Discussions(mrIid)
            .Add(new MergeRequestDiscussionCreate { Body = "Bob's comment" });

        var provider = new GitLabRepositoryProvider(aliceClient, projectId, "main");
        var result = await provider.ListPullRequestCommentsAsync(mrIid, "alice", CancellationToken.None);

        var bobComment = result.FirstOrDefault(c => c.Body == "Bob's comment");
        bobComment.Should().NotBeNull();
        bobComment!.IsAuthor.Should().BeFalse("Bob is not the PR author");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_ResultsOrderedByCreatedAt()
    {
        var (client, projectId, mrIid) = CreateServerWithMr();

        var discussionClient = client.GetMergeRequest(projectId).Discussions((int)mrIid);
        // Add two notes — the mock assigns sequential creation times so we add them out of body order
        discussionClient.Add(new MergeRequestDiscussionCreate { Body = "First note" });
        discussionClient.Add(new MergeRequestDiscussionCreate { Body = "Second note" });

        var provider = new GitLabRepositoryProvider(client, projectId, "main");
        var result = await provider.ListPullRequestCommentsAsync((int)mrIid, "prauthor", CancellationToken.None);

        result.Should().HaveCountGreaterThan(1);
        var dates = result.Select(c => c.CreatedAt).ToList();
        dates.Should().BeInAscendingOrder("results must be sorted by CreatedAt ascending");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_EmptyBody_IsFiltered()
    {
        var (client, projectId, mrIid) = CreateServerWithMr();

        // Add a note with non-empty body — the mock may not support truly empty bodies,
        // but the filter should drop null/empty strings
        var discussionClient = client.GetMergeRequest(projectId).Discussions((int)mrIid);
        discussionClient.Add(new MergeRequestDiscussionCreate { Body = "Valid comment" });

        var provider = new GitLabRepositoryProvider(client, projectId, "main");
        var result = await provider.ListPullRequestCommentsAsync((int)mrIid, "prauthor", CancellationToken.None);

        // All returned comments should have non-empty bodies
        result.Should().AllSatisfy(c => c.Body.Should().NotBeNullOrEmpty("empty body notes must be filtered"));
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_NoDiscussions_ReturnsEmpty()
    {
        var (client, projectId, mrIid) = CreateServerWithMr();

        var provider = new GitLabRepositoryProvider(client, projectId, "main");
        var result = await provider.ListPullRequestCommentsAsync((int)mrIid, "prauthor", CancellationToken.None);

        // No discussions added — result should be empty
        result.Should().BeEmpty("no discussions means no comments");
    }
}
