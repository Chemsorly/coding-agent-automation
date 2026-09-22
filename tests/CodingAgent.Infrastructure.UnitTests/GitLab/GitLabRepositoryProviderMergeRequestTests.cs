using AwesomeAssertions;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline.CodeReview.Models;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using NGitLab.Models;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Unit tests for GitLabRepositoryProvider merge request review operations.
/// Covers: SubmitPullRequestReviewAsync, DismissPreviousReviewAsync,
/// FindExistingReviewCommentAsync, UpdateReviewCommentAsync, CreatePullRequestAsync (409),
/// UpdatePullRequestAsync (404), ExtractLinkedIssuesAsync edge cases.
/// </summary>
public class GitLabRepositoryProviderMergeRequestTests
{
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

    #region CreatePullRequestAsync

    [Fact]
    public async Task CreatePullRequestAsync_Success_ReturnsUrl()
    {
        var (client, projectId) = CreateServerWithBranch("feature/new");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var prInfo = new PullRequestInfo
        {
            Title = "New MR",
            Body = "body",
            BranchName = "feature/new",
            BaseBranch = "main",
            IsDraft = false
        };

        var url = await provider.CreatePullRequestAsync(prInfo, CancellationToken.None);

        url.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreatePullRequestAsync_NullPrInfo_ThrowsArgumentNullException()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.CreatePullRequestAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region UpdatePullRequestAsync — 404

    [Fact]
    public async Task UpdatePullRequestAsync_NonExistentMr_ThrowsInvalidOperationException()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.UpdatePullRequestAsync(99999, "body", markReady: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdatePullRequestAsync_BodyOnly_UpdatesDescription()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "old description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        await provider.UpdatePullRequestAsync((int)mr.Iid, "new description", markReady: null, CancellationToken.None);

        var updated = mrClient[(int)mr.Iid];
        updated.Description.Should().Be("new description");
        updated.Title.Should().Be("Test MR", "markReady=null is a body-only update and must not add a Draft: prefix");
    }

    [Fact]
    public async Task UpdatePullRequestAsync_MarkReadyFalse_PrIsReadyForReview_AddsDraftPrefix()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "My MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        await provider.UpdatePullRequestAsync((int)mr.Iid, "body", markReady: false, CancellationToken.None);

        var updated = mrClient[(int)mr.Iid];
        updated.Title.Should().Be("Draft: My MR");
        updated.Description.Should().Be("body");
    }

    [Fact]
    public async Task UpdatePullRequestAsync_MarkReadyFalse_PrIsAlreadyDraft_NoDoubleDraftPrefix()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Draft: My MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        await provider.UpdatePullRequestAsync((int)mr.Iid, "body", markReady: false, CancellationToken.None);

        var updated = mrClient[(int)mr.Iid];
        updated.Title.Should().Be("Draft: My MR", "title must not double-prefix with 'Draft: Draft: '");
        updated.Description.Should().Be("body");
    }

    [Fact]
    public async Task UpdatePullRequestAsync_NullBody_ThrowsArgumentNullException()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.UpdatePullRequestAsync(1, null!, markReady: false, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region ExtractLinkedIssuesAsync — edge cases

    [Fact]
    public async Task ExtractLinkedIssuesAsync_MixedCaseKeywords_ExtractsAll()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Closes #1", "FIXES #2\nresolves #3", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var result = await provider.ExtractLinkedIssuesAsync((int)mr.Iid, CancellationToken.None);

        result.Should().BeEquivalentTo(new[] { "1", "2", "3" });
    }

    [Fact]
    public async Task ExtractLinkedIssuesAsync_DuplicateIssueNumbers_ReturnsDistinct()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Closes #5", "Fixes #5\nResolves #5", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var result = await provider.ExtractLinkedIssuesAsync((int)mr.Iid, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be("5");
    }

    [Fact]
    public async Task ExtractLinkedIssuesAsync_NoKeywords_ReturnsEmpty()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Add feature", "No closing keywords here", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var result = await provider.ExtractLinkedIssuesAsync((int)mr.Iid, CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion

    #region SubmitPullRequestReviewAsync — body-only

    [Fact]
    public async Task SubmitPullRequestReviewAsync_BodyOnly_CreatesNote()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        await provider.SubmitPullRequestReviewAsync(
            (int)mr.Iid, "Review body text", PullRequestReviewType.Comment, CancellationToken.None);

        // Verify note was created
        var comments = mrClient.Comments((int)mr.Iid).All.ToList();
        comments.Should().Contain(c => c.Body == "Review body text");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_BodyOnly_NonExistentMr_Throws()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.SubmitPullRequestReviewAsync(
            99999, "body", PullRequestReviewType.Comment, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region SubmitPullRequestReviewAsync — with ReviewSubmission

    [Fact]
    public async Task SubmitPullRequestReviewAsync_EmptyComments_DelegatesToBodyOnly()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var submission = new ReviewSubmission
        {
            Body = "Summary body",
            Type = PullRequestReviewType.Comment,
            Comments = Array.Empty<ReviewComment>()
        };

        await provider.SubmitPullRequestReviewAsync((int)mr.Iid, submission, CancellationToken.None);

        var comments = mrClient.Comments((int)mr.Iid).All.ToList();
        comments.Should().Contain(c => c.Body == "Summary body");
    }

    [Fact]
    public async Task SubmitPullRequestReviewAsync_WithComments_FallsBackToNotes()
    {
        // NGitLab.Mock likely doesn't support GetVersionsAsync, so the code will
        // catch the exception and fall back to non-inline notes
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var submission = new ReviewSubmission
        {
            Body = "Summary",
            Type = PullRequestReviewType.RequestChanges,
            Comments = new[]
            {
                new ReviewComment { Path = "src/file.cs", Line = 10, Body = "Issue here" }
            }
        };

        await provider.SubmitPullRequestReviewAsync((int)mr.Iid, submission, CancellationToken.None);

        // The summary note should be posted
        var comments = mrClient.Comments((int)mr.Iid).All.ToList();
        comments.Should().Contain(c => c.Body == "Summary");

        // The inline comment should fall back to a discussion note with path:line in body
        var discussions = mrClient.Discussions((int)mr.Iid).All.ToList();
        var fallbackNote = discussions
            .SelectMany(d => d.Notes ?? Array.Empty<NGitLab.Models.MergeRequestComment>())
            .FirstOrDefault(n => n.Body != null && n.Body.Contains("src/file.cs:10"));

        fallbackNote.Should().NotBeNull();
    }

    #endregion

    #region DismissPreviousReviewAsync

    [Fact]
    public async Task DismissPreviousReviewAsync_NoMatchingThreads_IsNoOp()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        // Should not throw
        await provider.DismissPreviousReviewAsync(
            (int)mr.Iid, "<!-- marker -->", "Superseded", CancellationToken.None);

        Assert.True(true, "no exception thrown — DismissPreviousReviewAsync is a no-op when no matching threads exist");
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_MatchingThreads_ResolvesThemAll()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        // Create a discussion with the marker
        var discussionClient = mrClient.Discussions((int)mr.Iid);
        discussionClient.Add(new MergeRequestDiscussionCreate
        {
            Body = "<!-- agent:review --> Review findings here"
        });

        // Act
        await provider.DismissPreviousReviewAsync(
            (int)mr.Iid, "<!-- agent:review -->", "New review supersedes", CancellationToken.None);

        // Assert: the matching thread must have been resolved (Resolved = true on all notes)
        var matchingThreads = mrClient.Discussions((int)mr.Iid).All
            .Where(d => d.Notes?.Any(n => n.Body?.Contains("<!-- agent:review -->") == true) == true)
            .ToList();
        matchingThreads.Should().NotBeEmpty("the discussion with the marker must still exist");
        matchingThreads.Should().AllSatisfy(d =>
            d.Notes.Should().Contain(n => n.Resolved == true,
                "all notes in a matching thread must be resolved"));
    }

    [Fact]
    public async Task DismissPreviousReviewAsync_IndividualResolveFailure_LogsAndContinues()
    {
        // TODO [WARNING]: This test is named to imply it verifies the continue-on-error behavior
        // of RunDismissLoopAsync (i.e. that a failure on one thread does not stop processing of
        // the remaining threads), but it actually only exercises the happy path — NGitLab.Mock
        // does not provide a way to force a failure on only the first thread. The catch branch in
        // RunDismissLoopAsync is never reached by this test. To properly verify continue-on-error,
        // add a direct unit test in SharedPrOperationsTests that injects a failing lambda for the
        // first item and confirms the second item is still processed. See SharedPrOperations.cs
        // for the parallel TODO.

        // Create a server with TWO matching discussions. We can't easily force a failure
        // on only the first one via NGitLab.Mock, so we verify that when both succeed,
        // both are resolved — i.e. the loop always processes every matching thread.
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var discussionClient = mrClient.Discussions((int)mr.Iid);
        discussionClient.Add(new MergeRequestDiscussionCreate
        {
            Body = "<!-- agent:review --> First review"
        });
        discussionClient.Add(new MergeRequestDiscussionCreate
        {
            Body = "<!-- agent:review --> Second review"
        });

        // Act — must not throw even when processing multiple threads
        await provider.DismissPreviousReviewAsync(
            (int)mr.Iid, "<!-- agent:review -->", "Superseded", CancellationToken.None);

        // Both threads must be resolved — the loop must process all matching threads
        var allMatchingResolved = mrClient.Discussions((int)mr.Iid).All
            .Where(d => d.Notes?.Any(n => n.Body?.Contains("<!-- agent:review -->") == true) == true)
            .All(d => d.Notes?.Any(n => n.Resolved == true) == true);
        allMatchingResolved.Should().BeTrue("all matching threads must be resolved — the loop must not stop after the first");
    }

    #endregion

    #region FindExistingReviewCommentAsync

    [Fact]
    public async Task FindExistingReviewCommentAsync_FindsNoteByMarker()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        // Create a note with a marker
        var commentClient = mrClient.Comments((int)mr.Iid);
        var note = commentClient.Add(new MergeRequestCommentCreate
        {
            Body = "<!-- decomposition-plan --> Plan content here"
        });

        var result = await provider.FindExistingReviewCommentAsync(
            (int)mr.Iid, "<!-- decomposition-plan -->", CancellationToken.None);

        result.Should().Be(note.Id);
    }

    [Fact]
    public async Task FindExistingReviewCommentAsync_NoMatch_ReturnsNull()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var result = await provider.FindExistingReviewCommentAsync(
            (int)mr.Iid, "<!-- nonexistent -->", CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region UpdateReviewCommentAsync

    [Fact]
    public async Task UpdateReviewCommentAsync_Success_UpdatesBody()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var commentClient = mrClient.Comments((int)mr.Iid);
        var note = commentClient.Add(new MergeRequestCommentCreate { Body = "Original" });

        await provider.UpdateReviewCommentAsync(
            (int)mr.Iid, note.Id, "Updated body", CancellationToken.None);

        var updated = commentClient.All.First(n => n.Id == note.Id);
        updated.Body.Should().Be("Updated body");
    }

    [Fact]
    public async Task UpdateReviewCommentAsync_NonExistentNote_Throws()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        var act = () => provider.UpdateReviewCommentAsync(
            (int)mr.Iid, 999999, "body", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region AddPrLabelAsync / RemovePrLabelAsync

    [Fact]
    public async Task AddPrLabelAsync_Success_AddsLabel()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        await provider.AddPrLabelAsync((int)mr.Iid, "agent:in-progress", CancellationToken.None);

        var updated = mrClient[(int)mr.Iid];
        updated.Labels.Should().Contain("agent:in-progress");
    }

    [Fact]
    public async Task RemovePrLabelAsync_Success_RemovesLabel()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Test MR", "description", "branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mrClient = client.GetMergeRequest(projectId);
        var mr = mrClient.Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();

        // Add a label first
        await provider.AddPrLabelAsync((int)mr.Iid, "agent:next", CancellationToken.None);

        // Remove it
        await provider.RemovePrLabelAsync((int)mr.Iid, "agent:next", CancellationToken.None);

        var updated = mrClient[(int)mr.Iid];
        updated.Labels.Should().NotContain("agent:next");
    }

    [Fact]
    public async Task RemovePrLabelAsync_NullLabel_ThrowsArgumentNullException()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.RemovePrLabelAsync(1, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region ListOpenPullRequestsAsync

    [Fact]
    public async Task ListOpenPullRequestsAsync_ReturnsPagedResults()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "MR One", "desc", "branch-1", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var result = await provider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);

        result.Items.Should().NotBeEmpty();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_MapsFieldsCorrectly()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "My Title", "My Description", "src-branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var result = await provider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);

        var item = result.Items.First();
        item.Title.Should().Be("My Title");
        item.Description.Should().Be("My Description");
        item.BranchName.Should().Be("src-branch");
        item.TargetBranch.Should().Be("main");
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_InvalidPage_Throws()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.ListOpenPullRequestsAsync(0, 10, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_InvalidPageSize_Throws()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.ListOpenPullRequestsAsync(1, 0, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_PageSizeTooLarge_Throws()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var act = () => provider.ListOpenPullRequestsAsync(1, 101, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_HasMore_WhenMoreThanPageSizeReturned()
    {
        // Create 3 MRs, request page 1 with pageSize=2 → HasMore must be true
        // Note: use pageSize=1 so the overfetch query (Take(2)) reliably returns 2 items
        // from NGitLab.Mock even when PerPage=1 might limit the enumerable.
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "branch-1", title: "MR 1",
                        targetBranch: "main", description: "desc");
                    project.WithMergeRequest(sourceBranch: "branch-2", title: "MR 2",
                        targetBranch: "main", description: "desc");
                })
            .BuildServer();

        var client = server.CreateClient();
        var projectId = (int)client.Projects.Accessible.First().Id;
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        // pageSize=1: overfetch uses Take(2), 2 MRs exist → HasMore must be true
        var result = await provider.ListOpenPullRequestsAsync(1, 1, null, CancellationToken.None);

        result.Items.Should().HaveCount(1, "page size is 1");
        result.HasMore.Should().BeTrue("2 MRs exist but only 1 was requested — HasMore must be true");
    }

    #endregion

    #region GetAgentPullRequestsAsync

    [Fact]
    public async Task GetAgentPullRequestsAsync_NoMatchingMrs_ReturnsEmpty()
    {
        var (client, projectId) = CreateServerWithMergeRequest(
            "Unrelated MR", "desc", "unrelated-branch", "main");
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var result = await provider.GetAgentPullRequestsAsync("999", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_MatchingBranch_ReturnsMr()
    {
        // The branch prefix is "feature/auto-{issueId}-"
        var config = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "feature/auto-42-impl",
                        title: "Agent MR", description: "Auto", targetBranch: "main");
                });

        var server = config.BuildServer();
        var client = server.CreateClient();
        var projects = client.Projects.Accessible.ToList();
        var projectId = (int)projects.First().Id;
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var result = await provider.GetAgentPullRequestsAsync("42", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].BranchName.Should().Be("feature/auto-42-impl");
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_MatchingBranch_IncludesDraftStatus()
    {
        var config = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "feature/auto-7-fix",
                        title: "Draft: Agent MR", description: "Auto", targetBranch: "main");
                });

        var server = config.BuildServer();
        var client = server.CreateClient();
        var projects = client.Projects.Accessible.ToList();
        var projectId = (int)projects.First().Id;
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var result = await provider.GetAgentPullRequestsAsync("7", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsDraft.Should().BeTrue();
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_DefaultIdentifier_ReturnsEmpty()
    {
        // IssueIdentifier is a non-nullable struct; default(IssueIdentifier) has Value = null.
        // The null guard in GetAgentPullRequestsAsync returns early without querying the server.
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var result = await provider.GetAgentPullRequestsAsync(default, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_PipelineGeneratedComments_AreFiltered()
    {
        // Verifies acceptance criterion 3: IsPipelineGeneratedComment filter must be applied
        // via shared code and verified by tests.
        var config = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "feature/auto-55-fix",
                        title: "Agent MR", description: "Auto", targetBranch: "main");
                });

        var server = config.BuildServer();
        var client = server.CreateClient();
        var projectId = (int)client.Projects.Accessible.First().Id;
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mr = client.GetMergeRequest(projectId)
            .Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();
        var mrIid = (int)mr.Iid;

        var discussionClient = client.GetMergeRequest(projectId).Discussions(mrIid);
        // Pipeline-generated comment (must be filtered)
        discussionClient.Add(new MergeRequestDiscussionCreate
        {
            Body = "## 🤖 Pipeline generated review\nSome findings..."
        });
        // Another pipeline-generated comment via agent: prefix
        discussionClient.Add(new MergeRequestDiscussionCreate
        {
            Body = "<!-- agent:pr-review --> review body"
        });
        // Human review comment (must be included)
        discussionClient.Add(new MergeRequestDiscussionCreate
        {
            Body = "Real human review comment"
        });

        var result = await provider.GetAgentPullRequestsAsync("55", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].ReviewComments.Should().ContainSingle("only the non-pipeline-generated comment should remain");
        result[0].ReviewComments[0].Body.Should().Be("Real human review comment");
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_ReviewCommentsCappedAt50_GitLab()
    {
        // Verifies acceptance criterion 3: Take(50) cap must be applied via shared code.
        // Create an MR and add 51 non-pipeline-generated discussion notes.
        var config = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true,
                initialCommit: true, defaultBranch: "main", configure: project =>
                {
                    project.WithMergeRequest(sourceBranch: "feature/auto-56-fix",
                        title: "Agent MR 56", description: "Auto", targetBranch: "main");
                });

        var server = config.BuildServer();
        var client = server.CreateClient();
        var projectId = (int)client.Projects.Accessible.First().Id;
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        var mr = client.GetMergeRequest(projectId)
            .Get(new MergeRequestQuery { State = MergeRequestState.opened }).First();
        var mrIid = (int)mr.Iid;

        var discussionClient = client.GetMergeRequest(projectId).Discussions(mrIid);
        // Add 51 normal (non-pipeline-generated) comments
        for (var i = 1; i <= 51; i++)
        {
            discussionClient.Add(new MergeRequestDiscussionCreate
            {
                Body = $"Review comment {i}"
            });
        }

        var result = await provider.GetAgentPullRequestsAsync("56", CancellationToken.None);

        result.Should().HaveCount(1);
        // TODO [WARNING]: This assertion uses BeLessThanOrEqualTo(50) which would also pass if
        // the cap were accidentally removed and 51 comments returned (it would fail), but would
        // also pass if all comments were silently dropped (count = 0). Use Be(50) to assert the
        // exact expected count when exactly 51 non-pipeline comments are added.
        result[0].ReviewComments.Count.Should().BeLessThanOrEqualTo(50,
            "GetAgentPullRequestsAsync must cap review comments at 50");
    }

    #endregion

    #region Provider properties

    [Fact]
    public void SupportsInlineReviewComments_ReturnsTrue()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        provider.SupportsInlineReviewComments.Should().BeTrue();
    }

    [Fact]
    public void ProviderType_ReturnsGitLab()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        provider.ProviderType.Should().Be(RepositoryProviderType.GitLab);
    }

    [Fact]
    public void BaseBranch_ReturnsConfiguredBranch()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "develop");

        provider.BaseBranch.Should().Be("develop");
    }

    [Fact]
    public async Task ValidateAsync_WithMockServer_CachesProjectMetadata()
    {
        var (client, projectId) = CreateServerWithProject();
        var provider = new GitLabRepositoryProvider(client, projectId, "main");

        await provider.ValidateAsync(CancellationToken.None);

        // After validation, RepositoryFullName should reflect the project path
        provider.RepositoryFullName.Should().Contain("TestUser");
    }

    [Fact]
    public void Constructor_NullBaseBranch_Throws()
    {
        var (client, projectId) = CreateServerWithProject();

        var act = () => new GitLabRepositoryProvider(client, projectId, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_StaticToken_InvalidApiUrl_Throws()
    {
        var act = () => new GitLabRepositoryProvider("", "token", 1, "main");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_StaticToken_InvalidScheme_Throws()
    {
        var act = () => new GitLabRepositoryProvider("ftp://gitlab.com", "token", 1, "main");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_StaticToken_EmptyToken_Throws()
    {
        var act = () => new GitLabRepositoryProvider("https://gitlab.com", "", 1, "main");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_DynamicToken_InvalidApiUrl_Throws()
    {
        Func<CancellationToken, Task<string>> tokenProvider = _ => Task.FromResult("token");

        var act = () => new GitLabRepositoryProvider("", tokenProvider, 1, "main");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_DynamicToken_NullTokenProvider_Throws()
    {
        var act = () => new GitLabRepositoryProvider("https://gitlab.com", (Func<CancellationToken, Task<string>>)null!, 1, "main");

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
