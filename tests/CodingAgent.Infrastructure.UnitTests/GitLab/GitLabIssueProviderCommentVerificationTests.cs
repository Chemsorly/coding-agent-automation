using System.Net;
using AwesomeAssertions;
using Moq;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using NGitLab.Models;
using CodingAgent.Infrastructure.GitLab;
using PipelineNote = NGitLab.Models.ProjectIssueNote;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Tests for IsVerified flag population in GitLabIssueProvider.ListCommentsAsync.
/// NGitLab.Mock-based tests cover the happy paths; Moq-based tests cover failure injection.
/// </summary>
public class GitLabIssueProviderCommentVerificationTests
{
    // ─── NGitLab.Mock helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Builds a mock server with standard users and a public project, plus returns useful handles.
    /// </summary>
    private static (GitLabServer Server, IGitLabClient DefaultClient, int ProjectId) BuildServer()
    {
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithUser("Outsider")
            .WithUser("Dev")
            .WithUser("Rep")
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true)
            .BuildServer();

        var client = server.CreateClient();
        var projectId = (int)client.Projects.Accessible.First().Id;

        var project = server.AllProjects.First();
        project.Visibility = VisibilityLevel.Public; // so non-members can comment

        return (server, client, projectId);
    }

    // ─── NGitLab.Mock: happy paths ─────────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_DeveloperAuthor_IsVerified()
    {
        var (server, client, projectId) = BuildServer();

        var dev = server.Users.First(u => u.UserName == "Dev");
        server.AllProjects.First().Permissions.Add(new NGitLab.Mock.Permission(dev, AccessLevel.Developer));

        var issue = await client.Issues.CreateAsync(
            new IssueCreate { ProjectId = projectId, Title = "t" }, CancellationToken.None);

        server.CreateClient(dev).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issue.IssueId, Body = "hi" });

        var provider = new GitLabIssueProvider(client, projectId);
        var comments = await provider.ListCommentsAsync(issue.IssueId.ToString(), CancellationToken.None);

        comments.Should().ContainSingle().Which.IsVerified.Should().BeTrue();
    }

    [Fact]
    public async Task ListCommentsAsync_ReporterAuthor_IsNotVerified()
    {
        var (server, client, projectId) = BuildServer();

        var rep = server.Users.First(u => u.UserName == "Rep");
        server.AllProjects.First().Permissions.Add(new NGitLab.Mock.Permission(rep, AccessLevel.Reporter));

        var issue = await client.Issues.CreateAsync(
            new IssueCreate { ProjectId = projectId, Title = "t" }, CancellationToken.None);

        server.CreateClient(rep).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issue.IssueId, Body = "hi" });

        var provider = new GitLabIssueProvider(client, projectId);
        var comments = await provider.ListCommentsAsync(issue.IssueId.ToString(), CancellationToken.None);

        comments.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ListCommentsAsync_NonMemberAuthor_IsNotVerified()
    {
        var (server, client, projectId) = BuildServer();

        var outsider = server.Users.First(u => u.UserName == "Outsider");

        var issue = await client.Issues.CreateAsync(
            new IssueCreate { ProjectId = projectId, Title = "t" }, CancellationToken.None);

        server.CreateClient(outsider).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issue.IssueId, Body = "hi" });

        var provider = new GitLabIssueProvider(client, projectId);
        var act = () => provider.ListCommentsAsync(issue.IssueId.ToString(), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }

    /// <summary>
    /// The token's own user is always verified, regardless of their role (or absence of one) on the project.
    /// Uses null for "no explicit role" and Reporter for "below Developer".
    /// </summary>
    [Theory]
    [InlineData(null)]              // no explicit role on the project
    [InlineData(AccessLevel.Reporter)]
    public async Task ListCommentsAsync_TokensOwnUser_IsVerifiedWithAnyRole(AccessLevel? roleForToken)
    {
        var (server, client, projectId) = BuildServer();

        var rep = server.Users.First(u => u.UserName == "Rep");
        if (roleForToken.HasValue)
            server.AllProjects.First().Permissions.Add(new NGitLab.Mock.Permission(rep, roleForToken.Value));

        var issue = await client.Issues.CreateAsync(
            new IssueCreate { ProjectId = projectId, Title = "t" }, CancellationToken.None);

        // Rep creates a note using rep's token
        server.CreateClient(rep).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issue.IssueId, Body = "hi from rep" });

        // Provider uses rep's client — token's own user is rep
        var provider = new GitLabIssueProvider(server.CreateClient(rep), projectId);
        var comments = await provider.ListCommentsAsync(issue.IssueId.ToString(), CancellationToken.None);

        comments.Should().ContainSingle().Which.IsVerified.Should().BeTrue(
            because: "the token's own user is always verified regardless of membership role");
    }

    // ─── Moq-based helpers ────────────────────────────────────────────────────

    private static (Mock<IGitLabClient> ClientMock, Mock<IMembersClient> MembersMock, Mock<IUserClient> UsersMock)
        BuildMockedClientWithNotes(IEnumerable<PipelineNote> notes, long currentUserId = 1)
    {
        var noteClientMock = new Mock<IProjectIssueNoteClient>();
        noteClientMock
            .Setup(c => c.ForIssue(It.IsAny<long>()))
            .Returns(notes);

        var usersMock = new Mock<IUserClient>();
        usersMock
            .Setup(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Session { Id = currentUserId });

        var membersMock = new Mock<IMembersClient>();

        var clientMock = new Mock<IGitLabClient>(MockBehavior.Loose);
        clientMock
            .Setup(c => c.GetProjectIssueNoteClient(It.IsAny<NGitLab.Models.ProjectId>()))
            .Returns(noteClientMock.Object);
        clientMock.Setup(c => c.Users).Returns(usersMock.Object);
        clientMock.Setup(c => c.Members).Returns(membersMock.Object);

        return (clientMock, membersMock, usersMock);
    }

    private static PipelineNote MakeNote(long authorId = 2, string username = "a") => new()
    {
        NoteId = 1,
        Body = "note body",
        Author = new Author { Id = authorId, Username = username },
        CreatedAt = DateTime.UtcNow
    };

    private static GitLabException ServerErrorEx() =>
        new GitLabException("500 Internal Server Error") { StatusCode = HttpStatusCode.InternalServerError };

    // ─── Moq: inherited members flag is set ────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_AsksForInheritedMembers()
    {
        var note = MakeNote();
        var (clientMock, membersMock, _) = BuildMockedClientWithNotes([note]);

        membersMock
            .Setup(c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Membership { AccessLevel = (int)AccessLevel.Developer });

        var provider = new GitLabIssueProvider(clientMock.Object, 1);
        await provider.ListCommentsAsync("1", CancellationToken.None);

        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── Moq: member lookup failure propagates ────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_MemberLookupFails_FailsTheListing()
    {
        var note = MakeNote();
        var (clientMock, membersMock, _) = BuildMockedClientWithNotes([note]);

        membersMock
            .Setup(c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(ServerErrorEx());

        var provider = new GitLabIssueProvider(clientMock.Object, 1);
        await provider.Invoking(p => p.ListCommentsAsync("1", CancellationToken.None))
            .Should().ThrowAsync<GitLabException>();
    }

    // ─── Moq: current-user lookup failure propagates ──────────────────────────

    [Fact]
    public async Task ListCommentsAsync_CurrentUserLookupFails_FailsTheListing()
    {
        var note = MakeNote();
        var (clientMock, _, usersMock) = BuildMockedClientWithNotes([note]);

        usersMock
            .Setup(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(ServerErrorEx());

        var provider = new GitLabIssueProvider(clientMock.Object, 1);
        await provider.Invoking(p => p.ListCommentsAsync("1", CancellationToken.None))
            .Should().ThrowAsync<GitLabException>();
    }

    // ─── Moq: same author twice → one lookup; per-listing cache ──────────────

    [Fact]
    public async Task ListCommentsAsync_SameAuthorTwice_LooksUpOncePerListing()
    {
        // Two notes by author 2
        var notes = new[]
        {
            new PipelineNote { NoteId = 1, Body = "a", Author = new Author { Id = 2, Username = "a" }, CreatedAt = DateTime.UtcNow },
            new PipelineNote { NoteId = 2, Body = "b", Author = new Author { Id = 2, Username = "a" }, CreatedAt = DateTime.UtcNow }
        };

        var (clientMock, membersMock, usersMock) = BuildMockedClientWithNotes(notes);

        membersMock
            .Setup(c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Membership { AccessLevel = (int)AccessLevel.Developer });

        var provider = new GitLabIssueProvider(clientMock.Object, 1);

        // First listing
        await provider.ListCommentsAsync("1", CancellationToken.None);

        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "first listing should look up author 2 once (two notes, same author)");

        usersMock.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Once,
            "first listing should call GetCurrentUserAsync once");

        // Second listing — cache is per listing, not per provider
        await provider.ListCommentsAsync("1", CancellationToken.None);

        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "second listing creates a new AuthorVerifier, so it looks up again");

        usersMock.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Exactly(2),
            "second listing calls GetCurrentUserAsync again");
    }

    // ─── Moq: null author → not verified, no member lookup ───────────────────

    [Fact]
    public async Task ListCommentsAsync_NoAuthor_IsNotVerifiedWithoutLookup()
    {
        var note = new PipelineNote
        {
            NoteId = 1,
            Body = "note body",
            Author = null,
            CreatedAt = DateTime.UtcNow
        };

        var (clientMock, membersMock, usersMock) = BuildMockedClientWithNotes([note]);

        var provider = new GitLabIssueProvider(clientMock.Object, 1);
        var result = await provider.ListCommentsAsync("1", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        usersMock.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
