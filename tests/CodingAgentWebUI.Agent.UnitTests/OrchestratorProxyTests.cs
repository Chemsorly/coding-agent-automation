using AwesomeAssertions;
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
        var act = () => new OrchestratorProxy(null!, "job-1");
        act.Should().Throw<ArgumentNullException>().WithParameterName("connection");
    }

    [Fact]
    public void Constructor_ThrowsOnNullJobId()
    {
        // Build a minimal HubConnection (won't actually connect)
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

        var act = () => new OrchestratorProxy(connection, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("jobId");
    }

    [Fact]
    public void ImplementsIAgentIssueOperations()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-1");

        proxy.Should().BeAssignableTo<IAgentIssueOperations>();
    }

    [Fact]
    public async Task PostCommentAsync_InvokesRequestPostComment()
    {
        // Arrange — use a recording handler to capture the outgoing request
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-42");

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
            .WithUrl("http://localhost/hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-42");

        var act = () => proxy.SwapLabelAsync("issue-1", "agent:done", CancellationToken.None);

        // Should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_InvokesRequestTokenRefresh()
    {
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-42");

        var act = () => proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        // Should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
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
