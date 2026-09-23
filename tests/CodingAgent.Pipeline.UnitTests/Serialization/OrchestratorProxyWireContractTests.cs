using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Pipeline.UnitTests.Serialization;

/// <summary>
/// Wire-contract tests for <see cref="OrchestratorProxy"/>: every call goes through a real
/// <see cref="HubConnection"/> configured like the agent's and is decoded the way the hub decodes it.
/// When an argument's wire form doesn't match the hub method's parameter type, SignalR rejects the call
/// before any hub code runs — nothing is logged server-side and the agent only sees
/// "Failed to invoke '…' due to an error on the server." These tests catch that mismatch in CI instead.
/// </summary>
public sealed class OrchestratorProxyWireContractTests : IAsyncLifetime
{
    private const string Job = "44b207fa-2ec8-4183-9a53-e1acdbecb11b";

    private static readonly PagedResult<IssueSummary> EmptyPage =
        new() { Items = [], Page = 1, PageSize = 25, HasMore = false };

    private readonly InMemoryAgentHub _hub = new InMemoryAgentHub()
        .Returns(HubMethodNames.RequestGetIssue,
            new IssueDetail { Identifier = "2567", Title = "Epic", Description = "Epic body", Labels = ["agent:epic"] })
        .Returns(HubMethodNames.RequestListComments,
            new[] { new IssueComment { Id = "1", Author = "Chemsorly", Body = "A comment", CreatedAt = new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc) } })
        .Returns(HubMethodNames.RequestTokenRefresh,
            new TokenRefreshResponse { Token = "token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) })
        .Returns(HubMethodNames.RequestCreateIssue,
            new CreatedIssueResult { Identifier = "2930", Url = "https://github.test/issues/2930" })
        .Returns(HubMethodNames.RequestCreateIssueForProvider,
            new CreatedIssueResult { Identifier = "2931", Url = "https://github.test/issues/2931" })
        .Returns(HubMethodNames.RequestListOpenIssues, EmptyPage)
        .Returns(HubMethodNames.RequestListClosedIssues, EmptyPage);

    private HubConnection _connection = null!;
    private OrchestratorProxy _proxy = null!;

    public async Task InitializeAsync()
    {
        _connection = await _hub.ConnectAsync();
        _proxy = new OrchestratorProxy(_connection, Job);
    }

    public async Task DisposeAsync()
    {
        _proxy.Dispose();
        await _connection.DisposeAsync();
        await _hub.DisposeAsync();
    }

    // ── Calls that take an IssueIdentifier: the hub declares these parameters as string ──────────

    [Fact]
    public async Task GetIssueAsync_SendsTheIdentifierAsTheStringTheHubExpects()
    {
        var issue = await _proxy.GetIssueAsync(new IssueIdentifier("2567"), CancellationToken.None);

        AssertHubReceived(HubMethodNames.RequestGetIssue, new JobId(Job), "2567");
        issue.Identifier.Should().Be("2567");
    }

    [Fact]
    public async Task ListCommentsAsync_SendsTheIdentifierAsTheStringTheHubExpects()
    {
        var comments = await _proxy.ListCommentsAsync(new IssueIdentifier("2567"), CancellationToken.None);

        AssertHubReceived(HubMethodNames.RequestListComments, new JobId(Job), "2567");
        comments.Should().ContainSingle().Which.Body.Should().Be("A comment");
    }

    [Fact]
    public async Task UpdateCommentAsync_SendsTheIdentifierAsTheStringTheHubExpects()
    {
        await _proxy.UpdateCommentAsync(new IssueIdentifier("2567"), 99L, "Updated body", CancellationToken.None);

        AssertHubReceived(HubMethodNames.RequestUpdateComment, new JobId(Job), "2567", "99", "Updated body");
    }

    // ── Every other proxy call ────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, (string HubMethod, Func<OrchestratorProxy, Task> Call)> OtherProxyCalls = new()
    {
        ["PostCommentAsync"] = (HubMethodNames.RequestPostComment,
            p => p.PostCommentAsync("2567", "Analysis", CancellationToken.None)),
        ["PostGateRejectionAsync"] = (HubMethodNames.RequestPostComment,
            p => p.PostGateRejectionAsync("{}", CancellationToken.None)),
        ["PostGateWontDoAsync"] = (HubMethodNames.RequestPostComment,
            p => p.PostGateWontDoAsync("{}", CancellationToken.None)),
        ["SwapLabelAsync"] = (HubMethodNames.RequestLabelChange,
            p => p.SwapLabelAsync("2567", AgentLabels.Error, CancellationToken.None)),
        ["SwapLabelAsync(targetKind)"] = (HubMethodNames.RequestLabelChange,
            p => p.SwapLabelAsync("2567", AgentLabels.Error, LabelTargetKind.PullRequest, CancellationToken.None)),
        ["RequestTokenRefreshAsync"] = (HubMethodNames.RequestTokenRefresh,
            p => p.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None, includeIssuePermission: true)),
        // Labels are built as List<string>, as the production callers do: a collection expression
        // typed IReadOnlyList<string> compiles to a synthesized type MessagePack cannot serialize.
        ["CreateIssueAsync"] = (HubMethodNames.RequestCreateIssue,
            p => p.CreateIssueAsync("Title", "Body", new List<string> { AgentLabels.Next }, CancellationToken.None)),
        ["CreateIssueForProviderAsync"] = (HubMethodNames.RequestCreateIssueForProvider,
            p => p.CreateIssueForProviderAsync("issue-provider-1", "Title", "Body", new List<string> { AgentLabels.Next }, CancellationToken.None)),
        ["ListOpenIssuesAsync"] = (HubMethodNames.RequestListOpenIssues,
            p => p.ListOpenIssuesAsync(1, 25, new List<string> { AgentLabels.Generated }, CancellationToken.None)),
        ["ListClosedIssuesAsync"] = (HubMethodNames.RequestListClosedIssues,
            p => p.ListClosedIssuesAsync(1, 25, null, new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None)),
    };

    public static IEnumerable<object[]> OtherProxyCallNames => OtherProxyCalls.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(OtherProxyCallNames))]
    public async Task OtherProxyCalls_BindToTheirHubMethod(string proxyCall)
    {
        var (hubMethod, call) = OtherProxyCalls[proxyCall];

        await call(_proxy);

        _hub.BindingFailures.Should().BeEmpty();
        _hub.Invocations.Should().ContainSingle().Which.Target.Should().Be(hubMethod);
    }

    private void AssertHubReceived(string hubMethod, params object[] expectedArguments)
    {
        _hub.BindingFailures.Should().BeEmpty();
        var invocation = _hub.Invocations.Should().ContainSingle().Subject;
        invocation.Target.Should().Be(hubMethod);
        invocation.Arguments.Should().Equal(expectedArguments);
    }
}
