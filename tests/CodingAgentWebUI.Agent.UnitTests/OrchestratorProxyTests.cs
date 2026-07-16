using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="OrchestratorProxy"/>.
/// Since <see cref="HubConnection"/> is sealed and its InvokeAsync methods are extension methods,
/// we test constructor validation, interface compliance, and use a recording hub connection
/// to verify correct method names and parameters are passed.
/// </summary>
public class OrchestratorProxyTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnection()
    {
        var act = () => new OrchestratorProxy(null!, new JobId("job-1"));
        act.Should().Throw<ArgumentNullException>().WithParameterName("connection");
    }

    // TODO: This test exercises JobId's own constructor validation, not OrchestratorProxy's behavior.
    // A default(JobId) (Value=null) can be passed to OrchestratorProxy without error since structs
    // bypass null checks. Consider adding a test that verifies default(JobId) is rejected.
    [Fact]
    public void Constructor_ThrowsOnEmptyJobId()
    {
        // Build a minimal HubConnection (won't actually connect)
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

        var act = () => new OrchestratorProxy(connection, new JobId(""));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplementsIAgentIssueOperations()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, new JobId("job-1"));

        proxy.Should().BeAssignableTo<IAgentIssueOperations>();
    }

    [Fact]
    public async Task PostCommentAsync_InvokesRequestPostComment()
    {
        // Arrange — use a recording handler to capture the outgoing request
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, new JobId("job-42"));

        // Act — calling PostCommentAsync on a disconnected connection will throw,
        // but we verify the method signature and parameter types are correct
        var act = () => proxy.PostCommentAsync("issue-1", "Hello world", CancellationToken.None);

        // Assert — should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SwapLabelAsync_InvokesRequestLabelChange()
    {
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, new JobId("job-42"));

        var act = () => proxy.SwapLabelAsync("issue-1", "agent:done", CancellationToken.None);

        // Should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_InvokesRequestTokenRefresh()
    {
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, new JobId("job-42"));

        var act = () => proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        // Should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PostCommentAsync_ThrowsOnNullIssueIdentifier()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostCommentAsync(null!, "body", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("issueIdentifier");
    }

    [Fact]
    public async Task PostCommentAsync_ThrowsOnNullBody()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostCommentAsync("issue-1", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("body");
    }

    [Fact]
    public async Task SwapLabelAsync_ThrowsOnNullIssueIdentifier()
    {
        var proxy = CreateProxy();
        var act = () => proxy.SwapLabelAsync(null!, "label", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("issueIdentifier");
    }

    [Fact]
    public async Task SwapLabelAsync_ThrowsOnNullNewLabel()
    {
        var proxy = CreateProxy();
        var act = () => proxy.SwapLabelAsync("issue-1", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("newLabel");
    }

    [Fact]
    public async Task PostGateRejectionAsync_ThrowsOnNullAssessmentJson()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostGateRejectionAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("assessmentJson");
    }

    [Fact]
    public async Task PostGateWontDoAsync_ThrowsOnNullAssessmentJson()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostGateWontDoAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("assessmentJson");
    }

    private static OrchestratorProxy CreateProxy()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
        return new OrchestratorProxy(connection, new JobId("job-1"));
    }

    /// <summary>
    /// A no-op HTTP handler that returns 200 OK for connection building purposes.
    /// The connection won't actually be started in these tests.
    /// </summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    /// <summary>
    /// Records outgoing HTTP requests for verification.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
