using System.Net;
using AwesomeAssertions;
using Moq;
using NGitLab;
using NGitLab.Models;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Tests specifically covering the HttpStatusCode-based catch blocks introduced in:
/// - GitLabIssueProvider (GetIssueAsync, PostCommentAsync, UpdateCommentAsync,
///   RemoveLabelAsync, CloseIssueAsync, IsIssueClosedAsync, EnsureAgentLabelsAsync,
///   EnsureProjectLabelsExistAsync)
/// - GitLabProviderBase (ValidateAsync — Unauthorized/Forbidden, NotFound)
/// - GitLabRepositoryProvider.MergeRequests (CreatePullRequestAsync, DeleteBranchAsync)
///
/// These catch clauses replace the former (int)ex.StatusCode == N pattern with
/// typed HttpStatusCode comparisons. Each test fires a GitLabException with a specific
/// HttpStatusCode to verify the catch clause handles it correctly.
/// </summary>
public class GitLabHttpStatusCodeCatchTests
{
    #region Helpers

    /// <summary>
    /// Creates a mock IGitLabClient whose Issues.GetAsync throws <paramref name="ex"/> for any call.
    /// </summary>
    private static IGitLabClient IssueClientThrowingOnGet(GitLabException ex)
    {
        var issueClientMock = new Mock<IIssueClient>();
        issueClientMock
            .Setup(c => c.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Issues).Returns(issueClientMock.Object);
        return clientMock.Object;
    }

    /// <summary>
    /// Creates a mock IGitLabClient whose Issues.GetAsync succeeds (returns a minimal issue)
    /// and Issues.EditAsync throws <paramref name="ex"/>.
    /// </summary>
    private static IGitLabClient IssueClientThrowingOnEdit(GitLabException ex)
    {
        var minimalIssue = new Issue
        {
            IssueId = 1,
            Title = "Test Issue",
            State = "opened",
            Labels = []
        };

        var issueClientMock = new Mock<IIssueClient>();
        issueClientMock
            .Setup(c => c.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(minimalIssue);
        issueClientMock
            .Setup(c => c.EditAsync(It.IsAny<IssueEdit>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Issues).Returns(issueClientMock.Object);
        return clientMock.Object;
    }

    /// <summary>
    /// Creates a mock IGitLabClient whose Labels.CreateProjectLabel throws <paramref name="ex"/>.
    /// </summary>
    private static IGitLabClient LabelClientThrowingOnCreate(GitLabException ex)
    {
        var labelClientMock = new Mock<ILabelClient>();
        labelClientMock
            .Setup(c => c.CreateProjectLabel(It.IsAny<long>(), It.IsAny<ProjectLabelCreate>()))
            .Throws(ex);
        labelClientMock
            .Setup(c => c.ForProject(It.IsAny<long>()))
            .Returns([]);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Labels).Returns(labelClientMock.Object);
        return clientMock.Object;
    }

    /// <summary>
    /// Creates a mock IGitLabClient whose GetProjectIssueNoteClient().Create throws <paramref name="ex"/>.
    /// </summary>
    private static IGitLabClient NoteClientThrowingOnCreate(GitLabException ex)
    {
        var noteClientMock = new Mock<IProjectIssueNoteClient>();
        noteClientMock
            .Setup(c => c.Create(It.IsAny<ProjectIssueNoteCreate>()))
            .Throws(ex);

        var clientMock = new Mock<IGitLabClient>(MockBehavior.Loose);
        clientMock
            .Setup(c => c.GetProjectIssueNoteClient(It.IsAny<NGitLab.Models.ProjectId>()))
            .Returns(noteClientMock.Object);

        return clientMock.Object;
    }

    /// <summary>
    /// Creates a mock IGitLabClient whose GetProjectIssueNoteClient().Edit throws <paramref name="ex"/>.
    /// </summary>
    private static IGitLabClient NoteClientThrowingOnEdit(GitLabException ex)
    {
        var noteClientMock = new Mock<IProjectIssueNoteClient>();
        noteClientMock
            .Setup(c => c.Edit(It.IsAny<ProjectIssueNoteEdit>()))
            .Throws(ex);

        var clientMock = new Mock<IGitLabClient>(MockBehavior.Loose);
        clientMock
            .Setup(c => c.GetProjectIssueNoteClient(It.IsAny<NGitLab.Models.ProjectId>()))
            .Returns(noteClientMock.Object);

        return clientMock.Object;
    }

    private static GitLabException NotFoundEx() =>
        new GitLabException("404 Not Found") { StatusCode = HttpStatusCode.NotFound };

    private static GitLabException ConflictEx() =>
        new GitLabException("409 Conflict") { StatusCode = HttpStatusCode.Conflict };

    private static GitLabException BadRequestEx() =>
        new GitLabException("400 Bad Request") { StatusCode = HttpStatusCode.BadRequest };

    private static GitLabException UnauthorizedEx() =>
        new GitLabException("401 Unauthorized") { StatusCode = HttpStatusCode.Unauthorized };

    private static GitLabException ForbiddenEx() =>
        new GitLabException("403 Forbidden") { StatusCode = HttpStatusCode.Forbidden };

    #endregion

    // ─── GitLabIssueProvider — GetIssueAsync ──────────────────────────────────

    /// <summary>
    /// GetIssueAsync — NotFound catch: when Issues.GetAsync throws 404, an
    /// InvalidOperationException (not GitLabException) is thrown.
    /// Verifies the HttpStatusCode.NotFound catch clause in GetIssueAsync.
    /// </summary>
    [Fact]
    public async Task GetIssueAsync_NotFound_ThrowsInvalidOperationException()
    {
        var provider = new GitLabIssueProvider(IssueClientThrowingOnGet(NotFoundEx()), 1);

        var act = () => provider.GetIssueAsync("1", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("not found");
    }

    // ─── GitLabIssueProvider — PostCommentAsync ────────────────────────────────

    /// <summary>
    /// PostCommentAsync — NotFound catch: when note creation throws 404,
    /// the provider re-throws as InvalidOperationException.
    /// Verifies the HttpStatusCode.NotFound catch clause in PostCommentAsync.
    /// </summary>
    [Fact]
    public async Task PostCommentAsync_NotFound_ThrowsInvalidOperationException()
    {
        var provider = new GitLabIssueProvider(NoteClientThrowingOnCreate(NotFoundEx()), 1);

        var act = () => provider.PostCommentAsync("1", "body", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── GitLabIssueProvider — UpdateCommentAsync ─────────────────────────────

    /// <summary>
    /// UpdateCommentAsync — NotFound catch: when note edit throws 404,
    /// an InvalidOperationException is thrown containing meaningful context.
    /// Verifies the HttpStatusCode.NotFound catch clause in UpdateCommentAsync.
    /// </summary>
    [Fact]
    public async Task UpdateCommentAsync_NotFound_ThrowsInvalidOperationException()
    {
        var provider = new GitLabIssueProvider(NoteClientThrowingOnEdit(NotFoundEx()), 1);

        var act = () => provider.UpdateCommentAsync("1", 42L, "new body", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    // ─── GitLabIssueProvider — CloseIssueAsync ────────────────────────────────

    /// <summary>
    /// CloseIssueAsync — NotFound catch: when Issues.GetAsync throws 404,
    /// the provider re-throws as InvalidOperationException.
    /// Verifies the HttpStatusCode.NotFound catch clause in CloseIssueAsync.
    /// </summary>
    [Fact]
    public async Task CloseIssueAsync_NotFound_ThrowsInvalidOperationException()
    {
        var provider = new GitLabIssueProvider(IssueClientThrowingOnGet(NotFoundEx()), 1);

        var act = () => provider.CloseIssueAsync("1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── GitLabIssueProvider — IsIssueClosedAsync ─────────────────────────────

    /// <summary>
    /// IsIssueClosedAsync — NotFound catch: when Issues.GetAsync throws 404,
    /// the method returns false (issue not found is treated as closed/absent).
    /// Verifies the HttpStatusCode.NotFound catch clause in IsIssueClosedAsync.
    /// </summary>
    [Fact]
    public async Task IsIssueClosedAsync_NotFound_ReturnsFalse()
    {
        var provider = new GitLabIssueProvider(IssueClientThrowingOnGet(NotFoundEx()), 1);

        var result = await provider.IsIssueClosedAsync("1", CancellationToken.None);

        result.Should().BeFalse(because: "a 404 means the issue was not found, treated as not-closed");
    }

    // ─── GitLabIssueProvider — EnsureAgentLabelsAsync ─────────────────────────

    /// <summary>
    /// EnsureAgentLabelsAsync — Conflict catch: when label creation returns 409,
    /// it is silently swallowed (label already exists), and the method returns true.
    /// Verifies the HttpStatusCode.Conflict catch clause in EnsureAgentLabelsAsync.
    /// </summary>
    [Fact]
    public async Task EnsureAgentLabelsAsync_ConflictOnCreate_ReturnsTrue()
    {
        var provider = new GitLabIssueProvider(LabelClientThrowingOnCreate(ConflictEx()), 1);

        var result = await provider.EnsureAgentLabelsAsync(CancellationToken.None);

        result.Should().BeTrue(because: "409 Conflict means label already exists — not an error");
    }

    /// <summary>
    /// EnsureAgentLabelsAsync — BadRequest catch: when label creation returns 400,
    /// it is silently swallowed (label already exists with 400), and the method returns true.
    /// Verifies the HttpStatusCode.BadRequest catch clause in EnsureAgentLabelsAsync.
    /// </summary>
    [Fact]
    public async Task EnsureAgentLabelsAsync_BadRequestOnCreate_ReturnsTrue()
    {
        var provider = new GitLabIssueProvider(LabelClientThrowingOnCreate(BadRequestEx()), 1);

        var result = await provider.EnsureAgentLabelsAsync(CancellationToken.None);

        result.Should().BeTrue(because: "400 BadRequest for duplicate label creation — not an error");
    }

    // ─── GitLabProviderBase — ValidateAsync ───────────────────────────────────

    /// <summary>
    /// ValidateAsync — Unauthorized catch (ex.StatusCode is HttpStatusCode.Unauthorized):
    /// when the Projects API returns 401, ValidateAsync throws InvalidOperationException.
    /// Verifies the HttpStatusCode.Unauthorized catch clause in ValidateAsync.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_Unauthorized_ThrowsInvalidOperationException()
    {
        // Use a concrete subclass since GitLabProviderBase is abstract.
        // GitLabIssueProvider inherits ValidateAsync from the base class.
        // IProjectClient's indexer is Projects[long id] → accessed via Projects[id].
        var projectMock = new Mock<IProjectClient>();
        projectMock
            .SetupGet(p => p[It.IsAny<long>()])
            .Throws(UnauthorizedEx());

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Projects).Returns(projectMock.Object);

        var provider = new GitLabIssueProvider(clientMock.Object, 1);

        var act = () => provider.ValidateAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Authentication or authorization failure");
    }

    /// <summary>
    /// ValidateAsync — Forbidden catch (ex.StatusCode is HttpStatusCode.Forbidden):
    /// when the Projects API returns 403, ValidateAsync throws InvalidOperationException.
    /// Verifies the HttpStatusCode.Forbidden branch in the Unauthorized/Forbidden combined catch.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_Forbidden_ThrowsInvalidOperationException()
    {
        var projectMock = new Mock<IProjectClient>();
        projectMock
            .SetupGet(p => p[It.IsAny<long>()])
            .Throws(ForbiddenEx());

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Projects).Returns(projectMock.Object);

        var provider = new GitLabIssueProvider(clientMock.Object, 1);

        var act = () => provider.ValidateAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Authentication or authorization failure");
    }

    /// <summary>
    /// ValidateAsync — NotFound catch (ex.StatusCode == HttpStatusCode.NotFound):
    /// when the Projects API returns 404, ValidateAsync throws InvalidOperationException
    /// with a "not found" message.
    /// Verifies the HttpStatusCode.NotFound catch clause in ValidateAsync.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_NotFound_ThrowsInvalidOperationException()
    {
        var projectMock = new Mock<IProjectClient>();
        projectMock
            .SetupGet(p => p[It.IsAny<long>()])
            .Throws(NotFoundEx());

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Projects).Returns(projectMock.Object);

        var provider = new GitLabIssueProvider(clientMock.Object, 1);

        var act = () => provider.ValidateAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("not found");
    }

    // ─── GitLabRepositoryProvider.MergeRequests — CreatePullRequestAsync ──────

    /// <summary>
    /// CreatePullRequestAsync — Conflict catch (ex.StatusCode == HttpStatusCode.Conflict):
    /// GitLab returns 409 when a merge request between the same branches already exists.
    /// The provider must convert this to an InvalidOperationException.
    /// Verifies the HttpStatusCode.Conflict catch clause in CreatePullRequestAsync.
    /// </summary>
    [Fact]
    public async Task CreatePullRequestAsync_ConflictOnCreate_ThrowsInvalidOperationException()
    {
        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock
            .Setup(c => c.Create(It.IsAny<MergeRequestCreate>()))
            .Throws(ConflictEx());

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetMergeRequest(It.IsAny<NGitLab.Models.ProjectId>())).Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, 1, "main");

        var act = () => provider.CreatePullRequestAsync(
            new PullRequestInfo
            {
                Title = "PR Title",
                Body = "PR Body",
                BranchName = "feature/branch",
                BaseBranch = "main"
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── GitLabRepositoryProvider.MergeRequests — DeleteBranchAsync ──────────

    /// <summary>
    /// DeleteBranchAsync — NotFound catch (ex.StatusCode == HttpStatusCode.NotFound):
    /// GitLab returns 404 when deleting a branch that doesn't exist or was already deleted.
    /// The provider must swallow this as a no-op rather than propagating the error.
    /// Verifies the HttpStatusCode.NotFound catch clause in DeleteBranchAsync.
    /// </summary>
    [Fact]
    public async Task DeleteBranchAsync_NotFound_IsNoOp()
    {
        var branchClientMock = new Mock<IBranchClient>();
        branchClientMock
            .Setup(c => c.Delete(It.IsAny<string>()))
            .Throws(NotFoundEx());

        var repoClientMock = new Mock<IRepositoryClient>();
        repoClientMock.Setup(r => r.Branches).Returns(branchClientMock.Object);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.GetRepository(It.IsAny<NGitLab.Models.ProjectId>())).Returns(repoClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, 1, "main");

        // Should not throw — 404 on branch delete is treated as already-deleted
        var act = () => provider.DeleteBranchAsync("feature/gone", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ─── GitLabRepositoryProvider.MergeRequests — RemovePrLabelAsync ─────────

    /// <summary>
    /// RemovePrLabelAsync — NotFound catch (ex.StatusCode == HttpStatusCode.NotFound):
    /// GitLab returns 404 when the MR doesn't exist. The provider must swallow this
    /// as a no-op rather than propagating the error.
    /// Verifies the HttpStatusCode.NotFound catch clause in RemovePrLabelAsync.
    /// </summary>
    [Fact]
    public async Task RemovePrLabelAsync_NotFound_IsNoOp()
    {
        var mrClientMock = new Mock<IMergeRequestClient>();
        mrClientMock
            .Setup(c => c.Update(It.IsAny<long>(), It.IsAny<MergeRequestUpdate>()))
            .Throws(NotFoundEx());

        var clientMock = new Mock<IGitLabClient>();
        clientMock
            .Setup(c => c.GetMergeRequest(It.IsAny<NGitLab.Models.ProjectId>()))
            .Returns(mrClientMock.Object);

        var provider = new GitLabRepositoryProvider(clientMock.Object, 1, "main");

        // Should not throw — 404 on RemovePrLabel is treated as no-op
        var act = () => provider.RemovePrLabelAsync(1, "some-label", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ─── GitLabIssueProvider — EnsureProjectLabelsExistAsync (via AddLabelsAsync) ─

    /// <summary>
    /// EnsureProjectLabelsExistAsync — Conflict catch (ex.StatusCode is HttpStatusCode.Conflict):
    /// When project label creation returns 409, the catch clause swallows it (label already exists).
    /// Verified indirectly through AddLabelsAsync which calls EnsureProjectLabelsExistAsync.
    /// Verifies the HttpStatusCode.Conflict catch clause in EnsureProjectLabelsExistAsync.
    /// </summary>
    [Fact]
    public async Task AddLabelsAsync_LabelCreationConflict_SilentlySkips()
    {
        // Labels.CreateProjectLabel throws 409, Issues.GetAsync and EditAsync succeed
        var minimalIssue = new Issue { IssueId = 1, Title = "Test", State = "opened", Labels = [] };

        var labelClientMock = new Mock<ILabelClient>();
        labelClientMock
            .Setup(c => c.CreateProjectLabel(It.IsAny<long>(), It.IsAny<ProjectLabelCreate>()))
            .Throws(ConflictEx());

        var issueClientMock = new Mock<IIssueClient>();
        issueClientMock
            .Setup(c => c.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(minimalIssue);
        issueClientMock
            .Setup(c => c.EditAsync(It.IsAny<IssueEdit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(minimalIssue);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Labels).Returns(labelClientMock.Object);
        clientMock.Setup(c => c.Issues).Returns(issueClientMock.Object);

        var provider = new GitLabIssueProvider(clientMock.Object, 1);

        // Should not throw — 409 on EnsureProjectLabelsExistAsync is a no-op
        var act = () => provider.AddLabelsAsync("1", ["bug"], CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// EnsureProjectLabelsExistAsync — BadRequest catch (ex.StatusCode is HttpStatusCode.BadRequest):
    /// When project label creation returns 400, the catch clause swallows it (label already exists).
    /// Verifies the HttpStatusCode.BadRequest catch clause in EnsureProjectLabelsExistAsync.
    /// </summary>
    [Fact]
    public async Task AddLabelsAsync_LabelCreationBadRequest_SilentlySkips()
    {
        var minimalIssue = new Issue { IssueId = 1, Title = "Test", State = "opened", Labels = [] };

        var labelClientMock = new Mock<ILabelClient>();
        labelClientMock
            .Setup(c => c.CreateProjectLabel(It.IsAny<long>(), It.IsAny<ProjectLabelCreate>()))
            .Throws(BadRequestEx());

        var issueClientMock = new Mock<IIssueClient>();
        issueClientMock
            .Setup(c => c.GetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(minimalIssue);
        issueClientMock
            .Setup(c => c.EditAsync(It.IsAny<IssueEdit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(minimalIssue);

        var clientMock = new Mock<IGitLabClient>();
        clientMock.Setup(c => c.Labels).Returns(labelClientMock.Object);
        clientMock.Setup(c => c.Issues).Returns(issueClientMock.Object);

        var provider = new GitLabIssueProvider(clientMock.Object, 1);

        // Should not throw — 400 on EnsureProjectLabelsExistAsync is a no-op
        var act = () => provider.AddLabelsAsync("1", ["bug"], CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
