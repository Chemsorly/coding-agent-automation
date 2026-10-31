using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Persistence;
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

    private string CommentsPath => ApiPath($"/repos/{Owner}/{Repo}/issues/10/comments");
    private string PermissionPath(string login) => ApiPath($"/repos/{Owner}/{Repo}/collaborators/{login}/permission");

    private int CollaboratorLookupCount =>
        Server.LogEntries.Count(e => e.RequestMessage?.Path?.Contains("/collaborators/") == true);

    // ─── Verified by association (no lookup needed) ────────────────────────────────

    [Theory]
    [InlineData("OWNER")]
    [InlineData("MEMBER")]
    [InlineData("COLLABORATOR")]
    [InlineData("member")]
    public async Task ListCommentsAsync_VerifiedAssociation_IsVerifiedWithoutLookup(string association)
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = association, created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeTrue();
        CollaboratorLookupCount.Should().Be(0);
    }

    // ─── Verified by bot account type (no lookup needed) ──────────────────────────

    [Theory]
    [InlineData("NONE")]
    [InlineData("CONTRIBUTOR")]
    public async Task ListCommentsAsync_BotAccount_IsVerifiedWithoutLookup(string association)
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "app[bot]", id = 1, type = "Bot" },
                  author_association = association, created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeTrue();
        CollaboratorLookupCount.Should().Be(0);
    }

    // ─── Bot suffix on a User account → NOT verified ──────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_BotSuffixOnAUserAccount_IsNotVerified()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "someone[bot]", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        // Lookup returns read — not write
        StubGet(PermissionPath("someone[bot]"), new { permission = "read", role_name = "read" });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeFalse();
        // Lookup must have been performed — the decision is based on account type, not login name
        CollaboratorLookupCount.Should().Be(1);
    }

    [Fact]
    public async Task ListCommentsAsync_BotSuffixOnAUserAccountWithWrite_IsVerified()
    {
        // Positive companion to BotSuffixOnAUserAccount_IsNotVerified.
        // Pins that a "[bot]"-suffixed login on a User account is verified when the permission lookup
        // returns write — i.e., the login name alone never gates the decision.
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "someone[bot]", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubGet(PermissionPath("someone[bot]"), new { permission = "write", role_name = "write" });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeTrue();
        CollaboratorLookupCount.Should().Be(1);
    }

    // ─── Verified by permission (lookup required) ─────────────────────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("maintain")]
    [InlineData("write")]
    public async Task ListCommentsAsync_PermissionGrantsWrite_IsVerified(string permission)
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "CONTRIBUTOR", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubGet(PermissionPath("alice"), new { permission, role_name = permission });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeTrue();
        CollaboratorLookupCount.Should().Be(1);
    }

    // ─── Not verified by permission below write ───────────────────────────────────

    [Theory]
    [InlineData("read")]
    [InlineData("triage")]
    [InlineData("none")]
    public async Task ListCommentsAsync_PermissionBelowWrite_IsNotVerified(string permission)
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubGet(PermissionPath("alice"), new { permission, role_name = permission });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeFalse();
    }

    // ─── MANNEQUIN does not throw ─────────────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_Mannequin_DoesNotThrowAndIsNotVerified()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "ghost", id = 1, type = "Mannequin" },
                  author_association = "MANNEQUIN", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubGet(PermissionPath("ghost"), new { permission = "none", role_name = "none" });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeFalse();
    }

    // ─── No association field → lookup ────────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_NoAssociation_UsesLookup()
    {
        // No author_association property at all (null → StringValue is null)
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubGet(PermissionPath("alice"), new { permission = "write", role_name = "write" });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeTrue();
    }

    // ─── Login does not exist (404) → not verified, no exception ─────────────────

    [Fact]
    public async Task ListCommentsAsync_LoginDoesNotExist_IsNotVerified()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        // Unstubbed → WireMock returns 404

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeFalse();
    }

    // ─── 403 "push access" → not verified, no exception ──────────────────────────

    [Fact]
    public async Task ListCommentsAsync_TokenWithoutPushAccess_IsNotVerifiedAndDoesNotThrow()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubError(PermissionPath("alice"), 403, new { message = "Must have push access to view collaborator permission." });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeFalse();
    }

    // ─── Other 403 → listing fails ────────────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_OtherForbiddenAnswer_FailsTheListing()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubError(PermissionPath("alice"), 403, new { message = "Resource not accessible by integration" });

        await using var provider = CreateProvider();
        var act = () => provider.ListCommentsAsync("10", CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    // ─── No user → not verified, no lookup ───────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_NoUser_IsNotVerifiedWithoutLookup()
    {
        // user omitted from JSON
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", author_association = "NONE",
                  created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeFalse();
        CollaboratorLookupCount.Should().Be(0);
    }

    // ─── 500 lookup → listing fails ───────────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_LookupFails_FailsTheListing()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubError(PermissionPath("alice"), 500, new { message = "Internal Server Error" });

        await using var provider = CreateProvider();
        var act = () => provider.ListCommentsAsync("10", CancellationToken.None);

        await act.Should().ThrowAsync<ApiException>();
    }

    // ─── Same author twice (case-insensitive) → 1 lookup ──────────────────────────

    [Fact]
    public async Task ListCommentsAsync_SameAuthorTwice_LooksUpOnce()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" },
            new { id = 2L, body = "y", user = new { login = "ALICE", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T11:00:00Z", updated_at = "2026-01-15T11:00:00Z" }
        });
        StubGet(PermissionPath("alice"), new { permission = "write", role_name = "write" });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(c => c.IsVerified.Should().BeTrue());
        CollaboratorLookupCount.Should().Be(1);
    }

    // ─── Second listing on same provider uses cache ───────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_SecondListingOnSameProvider_UsesTheCache()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubGet(PermissionPath("alice"), new { permission = "write", role_name = "write" });

        await using var provider = CreateProvider();
        await provider.ListCommentsAsync("10", CancellationToken.None);
        await provider.ListCommentsAsync("10", CancellationToken.None);

        CollaboratorLookupCount.Should().Be(1);
    }

    // ─── Failed lookup is not cached ─────────────────────────────────────────────

    [Fact]
    public async Task ListCommentsAsync_LookupFailed_IsNotCached()
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubError(PermissionPath("alice"), 500, new { message = "Internal Server Error" });

        await using var provider = CreateProvider();

        // First call: lookup fails → listing throws
        var act1 = () => provider.ListCommentsAsync("10", CancellationToken.None);
        await act1.Should().ThrowAsync<ApiException>();

        // Reset server, now stub with write permission
        Server.Reset();
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "alice", id = 1, type = "User" },
                  author_association = "NONE", created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        StubGet(PermissionPath("alice"), new { permission = "write", role_name = "write" });

        // Second call: lookup succeeds
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);
        result[0].IsVerified.Should().BeTrue();
    }

    // ─── Pipeline author verified in every GitHub credential setup ────────────────

    [Theory]
    [InlineData("Bot", "NONE", null, true)]       // GitHub App installation token — bot account
    [InlineData("User", "OWNER", null, false)]    // PAT of repository owner
    [InlineData("User", "MEMBER", null, false)]   // PAT of public org member
    [InlineData("User", "COLLABORATOR", null, false)] // PAT of direct collaborator
    [InlineData("User", "CONTRIBUTOR", "write", true)] // PAT of private org member with write
    public async Task ListCommentsAsync_PipelineAuthorInEveryGitHubCredentialSetup_IsVerified(
        string accountType, string association, string? permissionToStub, bool needsLookup)
    {
        StubGet(CommentsPath, new[]
        {
            new { id = 1L, body = "x", user = new { login = "pipeline-user", id = 42, type = accountType },
                  author_association = association, created_at = "2026-01-15T10:30:00Z", updated_at = "2026-01-15T10:30:00Z" }
        });
        if (needsLookup && permissionToStub is not null)
            StubGet(PermissionPath("pipeline-user"), new { permission = permissionToStub, role_name = permissionToStub });

        await using var provider = CreateProvider();
        var result = await provider.ListCommentsAsync("10", CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsVerified.Should().BeTrue();
    }
}
