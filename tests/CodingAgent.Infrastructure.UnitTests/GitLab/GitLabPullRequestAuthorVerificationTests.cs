using System.Net;
using AwesomeAssertions;
using CodingAgent.Infrastructure.GitLab;
using Moq;
using NGitLab;
using NGitLab.Models;
using MergeRequest = NGitLab.Models.MergeRequest;
using User = NGitLab.Models.User;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Tests for the author-verified flag that <see cref="GitLabRepositoryProvider"/> sets on the merge requests it reads
/// and lists: the same rule as for comments (the token's own user, or a member with Developer or higher). The
/// provider runs over a mocked client, so the member lookups the test sets up are the only ones that exist.
/// </summary>
public class GitLabPullRequestAuthorVerificationTests
{
    private const int StubProjectId = 17;
    private const long OwnUserId = 1;
    private const long StubAuthorId = 2;
    private const long OutsiderId = 3;
    private const int DeveloperAccessLevel = 30;

    private static GitLabException NotFoundEx() =>
        new("404 Not Found") { StatusCode = HttpStatusCode.NotFound };

    private static GitLabException ServerErrorEx() =>
        new("500 Internal Server Error") { StatusCode = HttpStatusCode.InternalServerError };

    private static MergeRequest StubMergeRequest(long iid, long? authorId) => new()
    {
        Iid = iid,
        State = "opened",
        Title = "t",
        SourceBranch = "b",
        TargetBranch = "main",
        WebUrl = "u",
        Author = authorId is { } id ? new User { Id = id, Username = $"user-{id}" } : null,
    };

    /// <summary>
    /// A provider over a mocked client that serves <paramref name="mergeRequests"/> from both the indexer and the
    /// listing, with the current user <see cref="OwnUserId"/> and a member lookup the test can set up.
    /// </summary>
    private static (GitLabRepositoryProvider Provider, Mock<IMembersClient> Members, Mock<IUserClient> Users)
        BuildMockedProvider(params MergeRequest[] mergeRequests)
    {
        var mrClient = new Mock<IMergeRequestClient>();
        foreach (var mr in mergeRequests)
            mrClient.Setup(c => c[mr.Iid]).Returns(mr);
        mrClient.Setup(c => c.Get(It.IsAny<MergeRequestQuery>())).Returns(new MergeRequestCollection(mergeRequests));

        var users = new Mock<IUserClient>();
        users.Setup(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Session { Id = OwnUserId });

        var members = new Mock<IMembersClient>();

        var gitLab = new Mock<IGitLabClient>();
        gitLab.Setup(c => c.GetMergeRequest(It.IsAny<ProjectId>())).Returns(mrClient.Object);
        gitLab.Setup(c => c.Users).Returns(users.Object);
        gitLab.Setup(c => c.Members).Returns(members.Object);

        return (new GitLabRepositoryProvider(gitLab.Object, StubProjectId, "main"), members, users);
    }

    private static void SetMemberLookup(Mock<IMembersClient> members, long userId, Membership membership) =>
        members.Setup(c => c.GetMemberOfProjectAsync(
                It.IsAny<ProjectId>(), userId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(membership);

    private static void FailMemberLookup(Mock<IMembersClient> members, long userId, GitLabException failure) =>
        members.Setup(c => c.GetMemberOfProjectAsync(
                It.IsAny<ProjectId>(), userId, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

    private static void VerifyMemberLookups(Mock<IMembersClient> members, long userId, Func<Times> times) =>
        members.Verify(c => c.GetMemberOfProjectAsync(
                It.IsAny<ProjectId>(), userId, true, It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task GetOpenPullRequestAsync_NonMemberAuthor_ClearsTheFlag()
    {
        // A user who is not a member is answered with a 404 by the member lookup, which is not a failure of the read.
        var (provider, members, _) = BuildMockedProvider(StubMergeRequest(7, StubAuthorId));
        FailMemberLookup(members, StubAuthorId, NotFoundEx());

        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary.Should().NotBeNull("the 404 of the member lookup does not turn the merge request into \"not open\"");
        summary!.IsAuthorVerified.Should().BeFalse();
        summary.Author.Should().Be("user-2");
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_DeveloperAuthor_SetsTheFlag()
    {
        var (provider, members, _) = BuildMockedProvider(StubMergeRequest(7, StubAuthorId));
        SetMemberLookup(members, StubAuthorId, new Membership { AccessLevel = DeveloperAccessLevel });

        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeTrue();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_ReporterAuthor_ClearsTheFlag()
    {
        var (provider, members, _) = BuildMockedProvider(StubMergeRequest(7, StubAuthorId));
        SetMemberLookup(members, StubAuthorId, new Membership { AccessLevel = 20 });

        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeFalse("Reporter is below Developer");
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_MemberLookupFails_Throws()
    {
        var (provider, members, _) = BuildMockedProvider(StubMergeRequest(7, StubAuthorId));
        FailMemberLookup(members, StubAuthorId, ServerErrorEx());

        await provider.Invoking(p => p.GetOpenPullRequestAsync(7, CancellationToken.None))
            .Should().ThrowAsync<GitLabException>("a failed lookup must not read as \"not verified\"");
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_NoAuthor_IsNotVerifiedWithoutLookup()
    {
        var (provider, members, users) = BuildMockedProvider(StubMergeRequest(7, authorId: null));

        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeFalse();
        VerifyMemberLookups(members, StubAuthorId, Times.Never);
        users.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_ClosedMergeRequest_DoesNotLookUpTheAuthor()
    {
        var closed = StubMergeRequest(7, StubAuthorId);
        closed.State = "closed";
        var (provider, members, users) = BuildMockedProvider(closed);

        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary.Should().BeNull();
        VerifyMemberLookups(members, StubAuthorId, Times.Never);
        users.Verify(c => c.GetCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_ReturnsTheFlagPerMergeRequest()
    {
        var (provider, members, _) = BuildMockedProvider(
            StubMergeRequest(7, OwnUserId),
            StubMergeRequest(8, StubAuthorId),
            StubMergeRequest(9, OutsiderId),
            StubMergeRequest(10, OutsiderId),
            StubMergeRequest(11, authorId: null));
        SetMemberLookup(members, StubAuthorId, new Membership { AccessLevel = DeveloperAccessLevel });
        FailMemberLookup(members, OutsiderId, NotFoundEx());

        var page = await provider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);

        page.Items.Select(p => (p.Number, p.IsAuthorVerified)).Should().Equal(
            (7, true), (8, true), (9, false), (10, false), (11, false));
        VerifyMemberLookups(members, OwnUserId, Times.Never);
        VerifyMemberLookups(members, OutsiderId, Times.Once);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_MemberLookupFails_FailsTheListing()
    {
        var (provider, members, _) = BuildMockedProvider(StubMergeRequest(7, StubAuthorId));
        FailMemberLookup(members, StubAuthorId, ServerErrorEx());

        await provider.Invoking(p => p.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None))
            .Should().ThrowAsync<GitLabException>();
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_DoesNotLookUpTheExtraItem()
    {
        // Page size 2 reads one more merge request to know whether there is another page. Only the two the page
        // returns may cost a lookup.
        var (provider, members, _) = BuildMockedProvider(
            StubMergeRequest(7, StubAuthorId),
            StubMergeRequest(8, StubAuthorId),
            StubMergeRequest(9, OutsiderId));
        SetMemberLookup(members, StubAuthorId, new Membership { AccessLevel = DeveloperAccessLevel });
        FailMemberLookup(members, OutsiderId, NotFoundEx());

        var page = await provider.ListOpenPullRequestsAsync(1, 2, null, CancellationToken.None);

        page.Items.Select(p => p.Number).Should().Equal(7, 8);
        page.HasMore.Should().BeTrue();
        VerifyMemberLookups(members, OutsiderId, Times.Never);
    }

    /// <summary>Concrete subclass of the abstract <see cref="GitLabCollectionResponse{T}"/> that yields fixed merge requests.</summary>
    private sealed class MergeRequestCollection(params MergeRequest[] items) : GitLabCollectionResponse<MergeRequest>
    {
        public override IEnumerator<MergeRequest> GetEnumerator() =>
            ((IEnumerable<MergeRequest>)items).GetEnumerator();

        public override async IAsyncEnumerator<MergeRequest> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in items)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
