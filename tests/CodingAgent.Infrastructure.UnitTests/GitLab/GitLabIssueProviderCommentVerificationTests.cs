using System.Net;
using AwesomeAssertions;
using Moq;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using NGitLab.Models;
using CodingAgent.Infrastructure.GitLab;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

public class GitLabIssueProviderCommentVerificationTests
{
    // ─── NGitLab.Mock-based tests ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a mock GitLab server with Dev (Developer), Rep (Reporter), and Outsider (no role)
    /// users. The default user "TestUser" is the project maintainer (token owner).
    /// Returns the server, client, project ID, and a created issue with notes from each user.
    /// </summary>
    private static async Task<(GitLabServer Server, IGitLabClient Client, int ProjectId, long IssueIid)>
        CreateServerWithMixedNotes()
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

        var dev = server.Users.First(u => u.UserName == "Dev");
        var rep = server.Users.First(u => u.UserName == "Rep");
        project.Permissions.Add(new Permission(dev, AccessLevel.Developer));
        project.Permissions.Add(new Permission(rep, AccessLevel.Reporter));

        var issue = await client.Issues.CreateAsync(
            new IssueCreate { ProjectId = projectId, Title = "t" }, CancellationToken.None);

        return (server, client, projectId, issue.IssueId);
    }

    [Fact]
    public async Task ListCommentsAsync_DeveloperAuthor_IsVerified()
    {
        var (server, client, projectId, issueIid) = await CreateServerWithMixedNotes();

        var dev = server.Users.First(u => u.UserName == "Dev");
        server.CreateClient(dev).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issueIid, Body = "developer note" });

        var provider = new GitLabIssueProvider(client, projectId);
        var comments = await provider.ListCommentsAsync(issueIid.ToString(), CancellationToken.None);

        var devComment = comments.Single(c => c.Body == "developer note");
        devComment.IsVerified.Should().BeTrue();
    }

    [Fact]
    public async Task ListCommentsAsync_ReporterAuthor_IsNotVerified()
    {
        var (server, client, projectId, issueIid) = await CreateServerWithMixedNotes();

        var rep = server.Users.First(u => u.UserName == "Rep");
        server.CreateClient(rep).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issueIid, Body = "reporter note" });

        var provider = new GitLabIssueProvider(client, projectId);
        var comments = await provider.ListCommentsAsync(issueIid.ToString(), CancellationToken.None);

        var repComment = comments.Single(c => c.Body == "reporter note");
        repComment.IsVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ListCommentsAsync_NonMemberAuthor_IsNotVerified()
    {
        var (server, client, projectId, issueIid) = await CreateServerWithMixedNotes();

        var outsider = server.Users.First(u => u.UserName == "Outsider");
        server.CreateClient(outsider).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issueIid, Body = "outsider note" });

        var provider = new GitLabIssueProvider(client, projectId);
        var act = () => provider.ListCommentsAsync(issueIid.ToString(), CancellationToken.None);

        // Should not throw
        var comments = await act.Should().NotThrowAsync();
        var outsiderComment = comments.Subject.Single(c => c.Body == "outsider note");
        outsiderComment.IsVerified.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)] // rep has no project permission
    [InlineData(true)]  // rep has Reporter permission
    public async Task ListCommentsAsync_TokensOwnUser_IsVerifiedWithAnyRole(bool hasReporterRole)
    {
        var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithUser("Rep")
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true)
            .BuildServer();

        var adminClient = server.CreateClient();
        var projectId = (int)adminClient.Projects.Accessible.First().Id;
        var project = server.AllProjects.First();
        project.Visibility = VisibilityLevel.Public;

        var rep = server.Users.First(u => u.UserName == "Rep");
        if (hasReporterRole)
            project.Permissions.Add(new Permission(rep, AccessLevel.Reporter));

        var issue = await adminClient.Issues.CreateAsync(
            new IssueCreate { ProjectId = projectId, Title = "t" }, CancellationToken.None);

        // Rep posts a note
        server.CreateClient(rep).GetProjectIssueNoteClient(projectId)
            .Create(new ProjectIssueNoteCreate { IssueId = issue.IssueId, Body = "rep note" });

        // Token is rep's own client — own user always verified
        var provider = new GitLabIssueProvider(server.CreateClient(rep), projectId);
        var comments = await provider.ListCommentsAsync(issue.IssueId.ToString(), CancellationToken.None);

        comments.Should().ContainSingle(c => c.IsVerified == true);
    }

    // ─── Moq-based tests (for failure injection) ─────────────────────────────────

    private static (IGitLabClient Client,
                    Mock<IProjectIssueNoteClient> NoteMock,
                    Mock<IUserClient> UserMock,
                    Mock<IMembersClient> MembersMock)
        CreateMockedClient(
            Membership? memberResult = null,
            GitLabException? memberException = null,
            GitLabException? userException = null,
            IEnumerable<NGitLab.Models.ProjectIssueNote>? notes = null)
    {
        var defaultNote = new NGitLab.Models.ProjectIssueNote
        {
            NoteId = 1, Body = "hi",
            Author = new Author { Id = 2, Username = "a" },
            CreatedAt = DateTime.UtcNow
        };

        var noteMock = new Mock<IProjectIssueNoteClient>();
        noteMock.Setup(c => c.ForIssue(It.IsAny<long>()))
                .Returns(notes ?? new[] { defaultNote });

        var userMock = new Mock<IUserClient>();
        if (userException is not null)
            userMock.Setup(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
                    .ThrowsAsync(userException);
        else
            userMock.Setup(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Session { Id = 1 });

        var membersMock = new Mock<IMembersClient>();
        var setup = membersMock.Setup(c => c.GetMemberOfProjectAsync(
            It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()));

        if (memberException is not null)
            setup.ThrowsAsync(memberException);
        else if (memberResult is not null)
            setup.ReturnsAsync(memberResult);
        else
            setup.ReturnsAsync(new Membership { AccessLevel = (int)AccessLevel.Developer });

        var clientMock = new Mock<IGitLabClient>(MockBehavior.Loose);
        clientMock.Setup(c => c.GetProjectIssueNoteClient(It.IsAny<NGitLab.Models.ProjectId>()))
                  .Returns(noteMock.Object);
        clientMock.Setup(c => c.Users).Returns(userMock.Object);
        clientMock.Setup(c => c.Members).Returns(membersMock.Object);

        return (clientMock.Object, noteMock, userMock, membersMock);
    }

    [Fact]
    public async Task ListCommentsAsync_AsksForInheritedMembers()
    {
        var (client, _, _, membersMock) = CreateMockedClient();
        var provider = new GitLabIssueProvider(client, 1);

        await provider.ListCommentsAsync("1", CancellationToken.None);

        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(
                It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListCommentsAsync_MemberLookupFails_FailsTheListing()
    {
        var ex500 = new GitLabException("500 Internal Server Error")
            { StatusCode = HttpStatusCode.InternalServerError };
        var (client, _, _, _) = CreateMockedClient(memberException: ex500);
        var provider = new GitLabIssueProvider(client, 1);

        var act = () => provider.ListCommentsAsync("1", CancellationToken.None);

        await act.Should().ThrowAsync<GitLabException>();
    }

    [Fact]
    public async Task ListCommentsAsync_CurrentUserLookupFails_FailsTheListing()
    {
        var ex500 = new GitLabException("500 Internal Server Error")
            { StatusCode = HttpStatusCode.InternalServerError };
        var (client, _, _, _) = CreateMockedClient(userException: ex500);
        var provider = new GitLabIssueProvider(client, 1);

        var act = () => provider.ListCommentsAsync("1", CancellationToken.None);

        await act.Should().ThrowAsync<GitLabException>();
    }

    [Fact]
    public async Task ListCommentsAsync_SameAuthorTwice_LooksUpOncePerListing()
    {
        // Two notes by the same author
        var twoNotes = new[]
        {
            new NGitLab.Models.ProjectIssueNote { NoteId = 1, Body = "first", Author = new Author { Id = 2, Username = "a" }, CreatedAt = DateTime.UtcNow },
            new NGitLab.Models.ProjectIssueNote { NoteId = 2, Body = "second", Author = new Author { Id = 2, Username = "a" }, CreatedAt = DateTime.UtcNow }
        };
        var (client, _, userMock, membersMock) = CreateMockedClient(notes: twoNotes);
        var provider = new GitLabIssueProvider(client, 1);

        // First listing: two notes, same author → 1 user lookup + 1 member lookup
        await provider.ListCommentsAsync("1", CancellationToken.None);

        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()),
            Times.Once);
        userMock.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Second listing: AuthorVerifier is created fresh → another 1+1
        await provider.ListCommentsAsync("1", CancellationToken.None);

        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), 2L, true, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        userMock.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ListCommentsAsync_NoAuthor_IsNotVerifiedWithoutLookup()
    {
        var nullAuthorNote = new NGitLab.Models.ProjectIssueNote
        {
            NoteId = 1, Body = "no author", Author = null!, CreatedAt = DateTime.UtcNow
        };
        var (client, _, _, membersMock) = CreateMockedClient(notes: new[] { nullAuthorNote });
        var provider = new GitLabIssueProvider(client, 1);

        var comments = await provider.ListCommentsAsync("1", CancellationToken.None);

        comments.Should().ContainSingle();
        comments[0].IsVerified.Should().BeFalse();
        membersMock.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
