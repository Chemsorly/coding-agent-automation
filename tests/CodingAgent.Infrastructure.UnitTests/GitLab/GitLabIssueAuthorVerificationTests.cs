using System.Net;
using AwesomeAssertions;
using Moq;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;
using NGitLab.Models;
using CodingAgent.Infrastructure.GitLab;
using Issue = NGitLab.Models.Issue;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Tests for the author-verified flag that <see cref="GitLabIssueProvider"/> sets on the issues it reads and lists:
/// the same rule as for comments (the token's own user, or a member with Developer or higher).
/// NGitLab.Mock serves <c>GetIssueAsync</c>; it does not implement <c>ForProjectAsync</c> with a query, so the
/// listing test puts a stub issue client in front of the mock's users and members.
/// </summary>
public class GitLabIssueAuthorVerificationTests
{
    /// <summary>A mock server with a public project (so that non-members can file issues) and users with no role yet.</summary>
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
        server.AllProjects.First().Visibility = VisibilityLevel.Public;

        return (server, client, projectId);
    }

    private static void GrantRole(GitLabServer server, string userName, AccessLevel role)
    {
        var user = server.Users.First(u => u.UserName == userName);
        server.AllProjects.First().Permissions.Add(new NGitLab.Mock.Permission(user, role));
    }

    /// <summary>Files an issue as <paramref name="userName"/>, so that user is its author.</summary>
    private static Task<Issue> FileIssueAsync(GitLabServer server, int projectId, string userName, string title)
    {
        var author = server.Users.First(u => u.UserName == userName);
        return server.CreateClient(author).Issues.CreateAsync(
            new IssueCreate { ProjectId = projectId, Title = title }, CancellationToken.None);
    }

    [Fact]
    public async Task GetIssueAsync_DeveloperAuthor_SetsTheFlag()
    {
        var (server, client, projectId) = BuildServer();
        GrantRole(server, "Dev", AccessLevel.Developer);
        var issue = await FileIssueAsync(server, projectId, "Dev", "by a developer");

        var detail = await new GitLabIssueProvider(client, projectId)
            .GetIssueAsync(issue.IssueId.ToString(), CancellationToken.None);

        detail.IsAuthorVerified.Should().BeTrue();
    }

    [Fact]
    public async Task GetIssueAsync_NonMemberAuthor_ClearsTheFlag()
    {
        var (server, client, projectId) = BuildServer();
        var issue = await FileIssueAsync(server, projectId, "Outsider", "by someone outside the project");

        var detail = await new GitLabIssueProvider(client, projectId)
            .GetIssueAsync(issue.IssueId.ToString(), CancellationToken.None);

        detail.IsAuthorVerified.Should().BeFalse();
    }

    [Fact]
    public async Task GetIssueAsync_ReporterAuthor_ClearsTheFlag()
    {
        var (server, client, projectId) = BuildServer();
        GrantRole(server, "Rep", AccessLevel.Reporter);
        var issue = await FileIssueAsync(server, projectId, "Rep", "by a reporter");

        var detail = await new GitLabIssueProvider(client, projectId)
            .GetIssueAsync(issue.IssueId.ToString(), CancellationToken.None);

        detail.IsAuthorVerified.Should().BeFalse("a member below Developer is not verified, like for comments");
    }

    /// <summary>
    /// The token's own user is always verified, whatever role they have on the project, including none.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(AccessLevel.Reporter)]
    public async Task GetIssueAsync_TokensOwnUser_SetsTheFlag(AccessLevel? roleForToken)
    {
        var (server, _, projectId) = BuildServer();
        if (roleForToken.HasValue)
            GrantRole(server, "Rep", roleForToken.Value);
        var issue = await FileIssueAsync(server, projectId, "Rep", "by the token's own user");

        var provider = new GitLabIssueProvider(server.CreateClient(server.Users.First(u => u.UserName == "Rep")), projectId);
        var detail = await provider.GetIssueAsync(issue.IssueId.ToString(), CancellationToken.None);

        detail.IsAuthorVerified.Should().BeTrue("the token's own user is verified regardless of membership role");
    }

    [Fact]
    public async Task ListOpenIssuesAsync_ReturnsTheFlagPerIssue()
    {
        var (server, client, projectId) = BuildServer();
        GrantRole(server, "Dev", AccessLevel.Developer);
        var byDeveloper = await FileIssueAsync(server, projectId, "Dev", "by a developer");
        var byOutsider = await FileIssueAsync(server, projectId, "Outsider", "by an outsider");
        var byToken = await FileIssueAsync(server, projectId, "TestUser", "by the token's own user");
        var byOutsiderAgain = await FileIssueAsync(server, projectId, "Outsider", "by the outsider again");

        // The mock cannot list with a query, so the issues come from a stub; users and members stay the mock's.
        var issues = new Mock<IIssueClient>();
        issues.Setup(c => c.ForProjectAsync(projectId, It.IsAny<IssueQuery>()))
            .Returns(new IssueCollection(byDeveloper, byOutsider, byToken, byOutsiderAgain));
        var gitLab = new Mock<IGitLabClient>();
        gitLab.Setup(c => c.Issues).Returns(issues.Object);
        gitLab.Setup(c => c.Users).Returns(client.Users);
        gitLab.Setup(c => c.Members).Returns(client.Members);

        var page = await new GitLabIssueProvider(gitLab.Object, projectId)
            .ListOpenIssuesAsync(1, 10, labels: null, CancellationToken.None);

        page.Items.Select(i => (i.Title, i.IsAuthorVerified)).Should().Equal(
            ("by a developer", true),
            ("by an outsider", false),
            ("by the token's own user", true),
            ("by the outsider again", false));
    }

    // ─── Moq: failure and edge paths of the verifier wiring ──────────────────

    private const int StubProjectId = 17;
    private const long StubIssueIid = 7;
    private const long StubAuthorId = 2;

    private static GitLabException ServerErrorEx() =>
        new("500 Internal Server Error") { StatusCode = HttpStatusCode.InternalServerError };

    private static Issue StubIssue(long? authorId) => new()
    {
        IssueId = StubIssueIid,
        Title = "t",
        Author = authorId is { } id ? new Author { Id = id, Username = "a" } : null,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// A provider over a mocked client that serves <paramref name="issue"/> from both <c>GetAsync</c> and the listing,
    /// with a current user of 1 and a member lookup the test can set up.
    /// </summary>
    private static (GitLabIssueProvider Provider, Mock<IMembersClient> Members, Mock<IUserClient> Users)
        BuildMockedProvider(Issue issue)
    {
        var issues = new Mock<IIssueClient>();
        issues.Setup(c => c.GetAsync(StubProjectId, StubIssueIid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issue);
        issues.Setup(c => c.ForProjectAsync(StubProjectId, It.IsAny<IssueQuery>()))
            .Returns(new IssueCollection(issue));

        var users = new Mock<IUserClient>();
        users.Setup(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Session { Id = 1 });

        var members = new Mock<IMembersClient>();

        var gitLab = new Mock<IGitLabClient>();
        gitLab.Setup(c => c.Issues).Returns(issues.Object);
        gitLab.Setup(c => c.Users).Returns(users.Object);
        gitLab.Setup(c => c.Members).Returns(members.Object);

        return (new GitLabIssueProvider(gitLab.Object, StubProjectId), members, users);
    }

    private static void FailMemberLookup(Mock<IMembersClient> members, GitLabException failure) =>
        members.Setup(c => c.GetMemberOfProjectAsync(
                It.IsAny<NGitLab.Models.ProjectId>(), StubAuthorId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

    /// <summary>
    /// A failed member lookup must surface as itself. <c>GetIssueAsync</c> awaits the verifier inside the
    /// catch that reports a 404 as "Issue ... was not found", so a swallowed or re-mapped failure would hide here.
    /// </summary>
    [Fact]
    public async Task GetIssueAsync_MemberLookupFails_PropagatesTheFailure()
    {
        var (provider, members, _) = BuildMockedProvider(StubIssue(StubAuthorId));
        FailMemberLookup(members, ServerErrorEx());

        await provider.Invoking(p => p.GetIssueAsync(StubIssueIid.ToString(), CancellationToken.None))
            .Should().ThrowAsync<GitLabException>();
    }

    [Fact]
    public async Task GetIssueAsync_CurrentUserLookupFails_PropagatesTheFailure()
    {
        var (provider, _, users) = BuildMockedProvider(StubIssue(StubAuthorId));
        users.Setup(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>())).ThrowsAsync(ServerErrorEx());

        await provider.Invoking(p => p.GetIssueAsync(StubIssueIid.ToString(), CancellationToken.None))
            .Should().ThrowAsync<GitLabException>();
    }

    [Fact]
    public async Task GetIssueAsync_NoAuthor_IsNotVerifiedWithoutLookup()
    {
        var (provider, members, users) = BuildMockedProvider(StubIssue(authorId: null));

        var detail = await provider.GetIssueAsync(StubIssueIid.ToString(), CancellationToken.None);

        detail.IsAuthorVerified.Should().BeFalse();
        members.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        users.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListOpenIssuesAsync_MemberLookupFails_FailsTheListing()
    {
        var (provider, members, _) = BuildMockedProvider(StubIssue(StubAuthorId));
        FailMemberLookup(members, ServerErrorEx());

        await provider.Invoking(p => p.ListOpenIssuesAsync(1, 10, labels: null, CancellationToken.None))
            .Should().ThrowAsync<GitLabException>();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_NoAuthor_IsNotVerifiedWithoutLookup()
    {
        var (provider, members, users) = BuildMockedProvider(StubIssue(authorId: null));

        var page = await provider.ListOpenIssuesAsync(1, 10, labels: null, CancellationToken.None);

        page.Items.Should().ContainSingle().Which.IsAuthorVerified.Should().BeFalse();
        members.Verify(
            c => c.GetMemberOfProjectAsync(It.IsAny<NGitLab.Models.ProjectId>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        users.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Concrete subclass of the abstract <see cref="GitLabCollectionResponse{T}"/> that yields fixed issues.</summary>
    private sealed class IssueCollection(params Issue[] items) : GitLabCollectionResponse<Issue>
    {
        public override IEnumerator<Issue> GetEnumerator() =>
            ((IEnumerable<Issue>)items).GetEnumerator();

        public override async IAsyncEnumerator<Issue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in items)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
