using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Pipeline.UnitTests.Serialization;

/// <summary>
/// Verifies that <see cref="OrchestratorProxy"/> masks secret values before sending text to the
/// issue tracker via the hub. Uses the same <see cref="InMemoryAgentHub"/> wire harness as
/// <see cref="OrchestratorProxyWireContractTests"/> so assertions reflect what the hub actually receives.
/// </summary>
/// <remarks>
/// TODO: The issue (#4110) specifies this class should live in tests/CodingAgent.Agent.UnitTests/.
/// It is placed here instead because <see cref="InMemoryAgentHub"/> is internal to
/// CodingAgent.Pipeline.UnitTests and depends on CodingAgent.AgentGateway, which
/// CodingAgent.Agent.UnitTests does not reference. Moving the tests there would require
/// duplicating InMemoryAgentHub or restructuring project references. The issue text cited
/// OrchestratorProxyWireContractTests as prior art for the test location, but that file also
/// lives in CodingAgent.Pipeline.UnitTests — the stated path was factually incorrect.
/// </remarks>
public sealed class OrchestratorProxySecretMaskingTests : IAsyncLifetime
{
    private const string FakeSecret = "s3cr3t-value-for-test";
    private const string Job = "55c318ab-3ec9-4294-ab64-f2bdcfed2222";

    private static readonly IReadOnlyList<KeyValuePair<string, string>> Secrets =
    [
        new KeyValuePair<string, string>("MY_TOKEN", FakeSecret)
    ];

    private readonly InMemoryAgentHub _hub = new InMemoryAgentHub()
        .Returns(HubMethodNames.RequestCreateIssue,
            new CreatedIssueResult { Identifier = "100", Url = "https://github.test/issues/100" })
        .Returns(HubMethodNames.RequestCreateIssueForProvider,
            new CreatedIssueResult { Identifier = "101", Url = "https://github.test/issues/101" });

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

    [Fact]
    public async Task PostComment_WhenSecretsAreSet_SendsMaskedBody()
    {
        _proxy.SetSecretsToMask(Secrets);

        await _proxy.PostCommentAsync("2567", $"Analysis containing {FakeSecret} here", CancellationToken.None);

        _hub.BindingFailures.Should().BeEmpty();
        var invocation = _hub.Invocations.Should().ContainSingle().Subject;
        invocation.Target.Should().Be(HubMethodNames.RequestPostComment);
        var payload = invocation.Arguments[2].Should().BeOfType<CommentPayload>().Subject;
        payload.AnalysisMarkdown.Should().NotContain(FakeSecret);
        payload.AnalysisMarkdown.Should().Contain("***");
    }

    [Fact]
    public async Task UpdateComment_WhenSecretsAreSet_SendsMaskedBody()
    {
        _proxy.SetSecretsToMask(Secrets);

        await _proxy.UpdateCommentAsync("2567", 42L, $"Updated body with {FakeSecret} inside", CancellationToken.None);

        _hub.BindingFailures.Should().BeEmpty();
        var invocation = _hub.Invocations.Should().ContainSingle().Subject;
        invocation.Target.Should().Be(HubMethodNames.RequestUpdateComment);
        var body = invocation.Arguments[3].Should().BeOfType<string>().Subject;
        body.Should().NotContain(FakeSecret);
        body.Should().Contain("***");
    }

    [Fact]
    public async Task CreateIssue_WhenSecretsAreSet_SendsMaskedTitleAndBody()
    {
        _proxy.SetSecretsToMask(Secrets);

        await _proxy.CreateIssueAsync(
            $"Title with {FakeSecret}",
            $"Body with {FakeSecret}",
            new List<string> { AgentLabels.Next },
            CancellationToken.None);

        _hub.BindingFailures.Should().BeEmpty();
        var invocation = _hub.Invocations.Should().ContainSingle().Subject;
        invocation.Target.Should().Be(HubMethodNames.RequestCreateIssue);
        var title = invocation.Arguments[1].Should().BeOfType<string>().Subject;
        var body = invocation.Arguments[2].Should().BeOfType<string>().Subject;
        title.Should().NotContain(FakeSecret);
        title.Should().Contain("***");
        body.Should().NotContain(FakeSecret);
        body.Should().Contain("***");
    }

    [Fact]
    public async Task CreateIssueForProvider_WhenSecretsAreSet_SendsMaskedTitleAndBody()
    {
        _proxy.SetSecretsToMask(Secrets);

        await _proxy.CreateIssueForProviderAsync(
            "issue-provider-1",
            $"Title with {FakeSecret}",
            $"Body with {FakeSecret}",
            new List<string> { AgentLabels.Next },
            CancellationToken.None);

        _hub.BindingFailures.Should().BeEmpty();
        var invocation = _hub.Invocations.Should().ContainSingle().Subject;
        invocation.Target.Should().Be(HubMethodNames.RequestCreateIssueForProvider);
        var title = invocation.Arguments[2].Should().BeOfType<string>().Subject;
        var body = invocation.Arguments[3].Should().BeOfType<string>().Subject;
        title.Should().NotContain(FakeSecret);
        title.Should().Contain("***");
        body.Should().NotContain(FakeSecret);
        body.Should().Contain("***");
    }

    [Fact]
    public async Task PostComment_WhenNoSecretsAreSet_SendsBodyUnchanged()
    {
        // _proxy was created without SetSecretsToMask — _secretsToMask stays []
        const string plainBody = "Normal analysis body with no secrets";

        await _proxy.PostCommentAsync("2567", plainBody, CancellationToken.None);

        _hub.BindingFailures.Should().BeEmpty();
        var invocation = _hub.Invocations.Should().ContainSingle().Subject;
        invocation.Target.Should().Be(HubMethodNames.RequestPostComment);
        var payload = invocation.Arguments[2].Should().BeOfType<CommentPayload>().Subject;
        payload.AnalysisMarkdown.Should().Be(plainBody);
    }
}
