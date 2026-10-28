using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="AgentEntryDtoFactory.From"/> URL sanitization behavior.
/// Both <c>ActiveIssueUrl</c> and <c>ActivePullRequestUrl</c> must be null when the
/// underlying run URL carries a non-https/http scheme such as <c>javascript:</c>.
/// </summary>
public class AgentEntryDtoFactoryTests
{
    private static AgentEntry MakeEntry() => new()
    {
        AgentId = new AgentId("agent-test"),
        ConnectionId = "conn-test",
        Hostname = "host-test",
        Labels = [],
        RegisteredAt = DateTimeOffset.UtcNow,
        Status = AgentStatus.Idle,
    };

    private static PipelineRun MakeRun(string? issueUrl = null, string? prUrl = null) => new()
    {
        RunId = "run-test-1",
        IssueIdentifier = new IssueIdentifier("owner/repo#1"),
        IssueTitle = "Test issue",
        IssueProviderConfigId = "ip-test",
        RepoProviderConfigId = "rp-test",
        IssueUrl = issueUrl,
        PullRequestUrl = prUrl,
    };

    // ── javascript: scheme rejection ──────────────────────────────────────────

    [Fact]
    public void From_ActiveIssueUrl_RejectsJavascriptScheme()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: "javascript:alert(1)"));

        dto.ActiveIssueUrl.Should().BeNull(
            "javascript: URIs must not be rendered as hrefs — they execute arbitrary JS when clicked");
    }

    [Fact]
    public void From_ActivePullRequestUrl_RejectsJavascriptScheme()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(prUrl: "javascript:alert(1)"));

        dto.ActivePullRequestUrl.Should().BeNull(
            "javascript: URIs in PR URLs must be rejected for the same XSS reason");
    }

    [Fact]
    public void From_ActiveIssueUrl_RejectsJavascriptScheme_CaseInsensitive()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: "JAVASCRIPT:alert(1)"));

        dto.ActiveIssueUrl.Should().BeNull("scheme check must be case-insensitive");
    }

    // ── other dangerous schemes ───────────────────────────────────────────────

    [Fact]
    public void From_ActiveIssueUrl_RejectsDataUri()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: "data:text/html,<script>alert(1)</script>"));

        dto.ActiveIssueUrl.Should().BeNull("data: URIs must be rejected");
    }

    [Fact]
    public void From_ActiveIssueUrl_RejectsVbscriptScheme()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: "vbscript:msgbox(1)"));

        dto.ActiveIssueUrl.Should().BeNull("vbscript: URIs must be rejected");
    }

    [Fact]
    public void From_ActiveIssueUrl_RejectsRelativeUrl()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: "/relative/path"));

        dto.ActiveIssueUrl.Should().BeNull("relative paths are not absolute URIs and must be rejected");
    }

    // ── accepted schemes ──────────────────────────────────────────────────────

    [Fact]
    public void From_ActiveIssueUrl_AcceptsHttps()
    {
        const string url = "https://github.com/owner/repo/issues/42";
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: url));

        dto.ActiveIssueUrl.Should().Be(url, "https:// URLs must pass through unchanged");
    }

    [Fact]
    public void From_ActiveIssueUrl_AcceptsHttp()
    {
        const string url = "http://internal-tracker/issues/7";
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: url));

        dto.ActiveIssueUrl.Should().Be(url, "http:// URLs must pass through unchanged");
    }

    [Fact]
    public void From_ActivePullRequestUrl_AcceptsHttps()
    {
        const string url = "https://github.com/owner/repo/pull/3";
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(prUrl: url));

        dto.ActivePullRequestUrl.Should().Be(url, "valid https:// PR URL must pass through");
    }

    // ── null preservation ─────────────────────────────────────────────────────
    //
    // These tests verify the null-passthrough path (null input stays null). They
    // are supplementary to the rejection tests above, which ARE the regression
    // guard for the scheme-validation fix: removing ValidateUrl would cause those
    // tests to fail, not these.

    [Fact]
    public void From_ActiveIssueUrl_PreservesNull()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(issueUrl: null));

        dto.ActiveIssueUrl.Should().BeNull("null input must remain null");
    }

    [Fact]
    public void From_ActivePullRequestUrl_PreservesNull()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), MakeRun(prUrl: null));

        dto.ActivePullRequestUrl.Should().BeNull("null input must remain null");
    }

    [Fact]
    public void From_WithNullRun_AllEnrichmentFieldsAreNull()
    {
        var dto = AgentEntryDtoFactory.From(MakeEntry(), run: null);

        dto.ActiveIssueUrl.Should().BeNull();
        dto.ActivePullRequestUrl.Should().BeNull();
        dto.ActiveIssueIdentifier.Should().BeNull();
        dto.ActiveRunId.Should().BeNull();
    }
}
