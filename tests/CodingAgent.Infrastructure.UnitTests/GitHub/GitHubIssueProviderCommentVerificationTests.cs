using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure;
using Octokit;

namespace CodingAgent.Infrastructure.UnitTests;

[Collection("GitHubTelemetry")]
public class GitHubIssueProviderCommentVerificationTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";

    private GitHubIssueProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token);

    private void StubComments(object[] comments) =>
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/10/comments"), comments);

    private void StubPermission(string login, string permission) =>
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/collaborators/{login}/permission"),
            new { permission, role_name = permission });

    private int CountPermissionLookups() =>
        Server.LogEntries.Count(e => e.RequestMessage?.Path?.Contains("/collaborators/") == true);

    private static object CommentJson(long id, string login, string type, string? association) =>
        association is not null
            ? new { id, body = "x", user = new { login, id = 1, type }, author_association = association, created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
            : (object)new { id, body = "x", user = new { login, id = 1, type }, author_association = (string?)null, created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" };

    private static object CommentJsonNoUser(long id) =>
        new { id, body = "x", user = (object?)null, author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" };

    private static object CommentJsonNoAssociation(long id, string login) =>
        new { id, body = "x", user = new { login, id = 1, type = "User" }, created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" };

    // ─── Fast-path: verified association ──────────────────────────────────────

    [Theory]
    [InlineData("OWNER")]
    [InlineData("MEMBER")]
    [InlineData("COLLABORATOR")]
    [InlineData("member")]
    public async Task ListCommentsAsync_VerifiedAssociation_IsVerifiedWithoutLookup(string association)
    {
        StubComments([CommentJson(1, "alice", "User", association)]);

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeTrue();
        CountPermissionLookups().Should().Be(0);
    }

    // ─── Fast-path: bot account ────────────────────────────────────────────────

    [Theory]
    [InlineData("NONE")]
    [InlineData("CONTRIBUTOR")]
    public async Task ListCommentsAsync_BotAccount_IsVerifiedWithoutLookup(string association)
    {
        StubComments([CommentJson(1, "app[bot]", "Bot", association)]);

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeTrue();
        CountPermissionLookups().Should().Be(0);
    }

    // ─── Security trap: bot-suffix on a User account is not verified ──────────

    [Fact]
    public async Task ListCommentsAsync_BotSuffixOnAUserAccount_IsNotVerified()
    {
        StubComments([CommentJson(1, "someone[bot]", "User", "NONE")]);
        StubPermission("someone[bot]", "read");

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }

    // ─── Permission grants write ───────────────────────────────────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("maintain")]
    [InlineData("write")]
    public async Task ListCommentsAsync_PermissionGrantsWrite_IsVerified(string permission)
    {
        StubComments([CommentJson(1, "alice", "User", "CONTRIBUTOR")]);
        StubPermission("alice", permission);

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeTrue();
        CountPermissionLookups().Should().Be(1);
    }

    // ─── Permission below write ────────────────────────────────────────────────

    [Theory]
    [InlineData("read")]
    [InlineData("triage")]
    [InlineData("none")]
    public async Task ListCommentsAsync_PermissionBelowWrite_IsNotVerified(string permission)
    {
        StubComments([CommentJson(1, "alice", "User", "NONE")]);
        StubPermission("alice", permission);

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }

    // ─── Mannequin: no throw, not verified ────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_Mannequin_DoesNotThrowAndIsNotVerified()
    {
        StubComments([new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "Mannequin" }, author_association = "MANNEQUIN", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }]);
        StubPermission("alice", "none");

        await using var provider = CreateProvider();
        var act = () => provider.ListCommentsAsync("10", CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }

    // ─── No association property: uses lookup ─────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_NoAssociation_UsesLookup()
    {
        StubComments([CommentJsonNoAssociation(1, "alice")]);
        StubPermission("alice", "write");

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeTrue();
        CountPermissionLookups().Should().Be(1, "null/missing association must fall through to the permission lookup");
    }

    // ─── Login does not exist (404) ────────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_LoginDoesNotExist_IsNotVerified()
    {
        StubComments([CommentJson(1, "ghost", "User", "NONE")]);
        // No stub for permission → WireMock returns 404

        await using var provider = CreateProvider();
        var act = () => provider.ListCommentsAsync("10", CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }

    // ─── Token without push access (403 "Must have push access") ──────────────

    [Fact]
    public async Task ListCommentsAsync_TokenWithoutPushAccess_IsNotVerifiedAndDoesNotThrow()
    {
        StubComments([CommentJson(1, "alice", "User", "NONE")]);
        StubError(ApiPath($"/repos/{Owner}/{Repo}/collaborators/alice/permission"), 403,
            new { message = "Must have push access to view collaborator permission." });

        await using var provider = CreateProvider();
        var act = () => provider.ListCommentsAsync("10", CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }

    // ─── Other 403: fails the listing ─────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_OtherForbiddenAnswer_FailsTheListing()
    {
        StubComments([CommentJson(1, "alice", "User", "NONE")]);
        StubError(ApiPath($"/repos/{Owner}/{Repo}/collaborators/alice/permission"), 403,
            new { message = "Resource not accessible by integration" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.ListCommentsAsync("10", CancellationToken.None))
            .Should().ThrowAsync<ForbiddenException>();
    }

    // ─── No user field: not verified, no lookup ───────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_NoUser_IsNotVerifiedWithoutLookup()
    {
        StubComments([CommentJsonNoUser(1)]);

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
        CountPermissionLookups().Should().Be(0);
    }

    // ─── Lookup fails (500): fails the listing ────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_LookupFails_FailsTheListing()
    {
        StubComments([CommentJson(1, "alice", "User", "NONE")]);
        StubError(ApiPath($"/repos/{Owner}/{Repo}/collaborators/alice/permission"), 500,
            new { message = "Internal Server Error" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.ListCommentsAsync("10", CancellationToken.None))
            .Should().ThrowAsync<ApiException>();
    }

    // ─── Same author twice (case-insensitive): one lookup ─────────────────────

    [Fact]
    public async Task ListCommentsAsync_SameAuthorTwice_LooksUpOnce()
    {
        StubComments([
            CommentJson(1, "alice", "User", "NONE"),
            CommentJson(2, "ALICE", "User", "NONE")
        ]);
        StubPermission("alice", "write");

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().HaveCount(2);
        result.All(c => c.IsVerified == true).Should().BeTrue();
        CountPermissionLookups().Should().Be(1);
    }

    // ─── Second listing on same provider instance uses the cache ──────────────

    [Fact]
    public async Task ListCommentsAsync_SecondListingOnSameProvider_UsesTheCache()
    {
        StubComments([CommentJson(1, "alice", "User", "NONE")]);
        StubPermission("alice", "write");

        await using var provider = CreateProvider();
        await provider.ListCommentsAsync("10", CancellationToken.None);
        Server.ResetLogEntries();
        StubComments([CommentJson(1, "alice", "User", "NONE")]);

        await provider.ListCommentsAsync("10", CancellationToken.None);

        CountPermissionLookups().Should().Be(0, "second listing must use the cached result");
    }

    // ─── Failed lookup is not cached ──────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_LookupFailed_IsNotCached()
    {
        StubComments([CommentJson(1, "alice", "User", "NONE")]);
        StubError(ApiPath($"/repos/{Owner}/{Repo}/collaborators/alice/permission"), 500,
            new { message = "Internal Server Error" });

        await using var provider = CreateProvider();
        // First call — throws (500)
        await provider.Invoking(p => p.ListCommentsAsync("10", CancellationToken.None))
            .Should().ThrowAsync<ApiException>();

        // Reset and re-stub with success
        Server.Reset();
        StubComments([CommentJson(1, "alice", "User", "NONE")]);
        StubPermission("alice", "write");

        // Second call — should succeed and return IsVerified=true
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);
        result.Should().ContainSingle().Which.IsVerified.Should().BeTrue();
    }

    // ─── Pipeline author verified in every GitHub credential setup (req 8) ────

    [Theory]
    [InlineData("Bot",  "NONE",        null,   true,  "GitHub App installation token")] // bot type → no lookup
    [InlineData("User", "OWNER",       "write", false, "PAT of repo owner")]
    [InlineData("User", "MEMBER",      "write", false, "PAT of public org member")]
    [InlineData("User", "COLLABORATOR","write", false, "PAT of direct collaborator")]
    [InlineData("User", "CONTRIBUTOR", "write", true,  "PAT of private org member with write")]
    [InlineData("User", "NONE",        "write", true,  "PAT with no association but write permission")]
    public async Task ListCommentsAsync_PipelineAuthorInEveryGitHubCredentialSetup_IsVerified(
        string type, string association, string? permission, bool needsLookup, string scenario)
    {
        _ = scenario; // used only as test label via InlineData
        StubComments([CommentJson(1, "pipeline-bot", type, association)]);
        if (needsLookup && permission is not null)
            StubPermission("pipeline-bot", permission);

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle().Which.IsVerified.Should().BeTrue();
    }
}
