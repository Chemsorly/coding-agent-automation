using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Moq;
using Polly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Integration tests verifying that the standard resilience handler retries transient HTTP failures
/// transparently for <see cref="WorkItemHttpClient"/>.
/// Uses WireMock to simulate server responses and a real DI container with the resilience handler configured.
/// </summary>
public class WorkItemHttpClientResilienceTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly ServiceProvider _serviceProvider;
    private readonly WorkItemHttpClient _client;

    public WorkItemHttpClientResilienceTests()
    {
        _server = WireMockServer.Start();

        var services = new ServiceCollection();
        services.AddSingleton(new Mock<Serilog.ILogger>().Object);
        services.AddHttpClient<WorkItemHttpClient>(client =>
            {
                client.BaseAddress = new Uri(_server.Url!);
            })
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = false;
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.MinimumThroughput = 100; // High threshold to avoid tripping in tests
            });

        _serviceProvider = services.BuildServiceProvider();
        _client = _serviceProvider.GetRequiredService<WorkItemHttpClient>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    // ── Retry Behavior ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignment_TransientThen200_RetriesAndReturns()
    {
        var assignment = CreateMinimalAssignment("job-retry", "owner/repo#1");
        var json = JsonSerializer.Serialize(assignment, PipelineJsonOptions.Default);

        // First two calls: 503, third call: 200 OK
        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-1/assignment").UsingGet())
            .InScenario("retry")
            .WillSetStateTo("attempt-2")
            .RespondWith(Response.Create().WithStatusCode(503));

        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-1/assignment").UsingGet())
            .InScenario("retry")
            .WhenStateIs("attempt-2")
            .WillSetStateTo("attempt-3")
            .RespondWith(Response.Create().WithStatusCode(503));

        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-1/assignment").UsingGet())
            .InScenario("retry")
            .WhenStateIs("attempt-3")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));

        var result = await _client.GetAssignmentAsync("wi-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.JobId.Should().Be("job-retry");
    }

    [Fact]
    public async Task PostStatus_TransientThen200_RetriesAndReturnsTrue()
    {
        // First call: 502, second call: 200 OK
        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-1/status").UsingPost())
            .InScenario("post-retry")
            .WillSetStateTo("attempt-2")
            .RespondWith(Response.Create().WithStatusCode(502));

        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-1/status").UsingPost())
            .InScenario("post-retry")
            .WhenStateIs("attempt-2")
            .RespondWith(Response.Create().WithStatusCode(200));

        var update = new WorkItemStatusUpdate { Status = "Running" };
        var result = await _client.PostStatusAsync("wi-1", update, CancellationToken.None);

        result.Should().BeTrue();
    }

    // ── Non-Retryable Responses ──────────────────────────────────────────

    [Fact]
    public async Task GetAssignment_404_DoesNotRetry_ThrowsImmediately()
    {
        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-missing/assignment").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var act = () => _client.GetAssignmentAsync("wi-missing", CancellationToken.None);

        await act.Should().ThrowAsync<WorkItemFetchException>()
            .WithMessage("*not found*404*");

        // Should have been called exactly once (no retry for 404)
        _server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task PostStatus_400_DoesNotRetry_ReturnsFalse()
    {
        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-1/status").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        var update = new WorkItemStatusUpdate { Status = "Invalid" };
        var result = await _client.PostStatusAsync("wi-1", update, CancellationToken.None);

        result.Should().BeFalse();
        // Should have been called exactly once (no retry for 400)
        _server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAssignment_410Gone_DoesNotRetry_ReturnsNull()
    {
        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-terminal/assignment").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(410));

        var result = await _client.GetAssignmentAsync("wi-terminal", CancellationToken.None);

        result.Should().BeNull();
        _server.LogEntries.Should().HaveCount(1);
    }

    // ── Exhausted Retries ────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignment_PersistentServerError_ThrowsAfterRetries()
    {
        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-fail/assignment").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        var act = () => _client.GetAssignmentAsync("wi-fail", CancellationToken.None);

        // After all retries exhausted, should throw WorkItemFetchException
        await act.Should().ThrowAsync<WorkItemFetchException>();

        // Should have been called multiple times (initial + retries)
        _server.LogEntries.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task PostStatus_PersistentServerError_ThrowsAfterRetries()
    {
        _server
            .Given(Request.Create().WithPath("/api/work-items/wi-fail/status").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        var update = new WorkItemStatusUpdate { Status = "Failed" };
        var act = () => _client.PostStatusAsync("wi-fail", update, CancellationToken.None);

        // After all retries exhausted, should throw WorkItemStatusPostException
        await act.Should().ThrowAsync<WorkItemStatusPostException>();

        // Should have been called multiple times (initial + retries)
        _server.LogEntries.Count.Should().BeGreaterThan(1);
    }

    // ── Test Helpers ─────────────────────────────────────────────────────

    private static JobAssignmentMessage CreateMinimalAssignment(string jobId, string issueId) => new()
    {
        JobId = jobId,
        IssueIdentifier = issueId,
        IssueDetail = new IssueDetail { Identifier = issueId, Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        RepoProviderConfigId = "repo-1",
        AgentProviderConfigId = "agent-1",
        PipelineConfiguration = new PipelineConfiguration(),
        ProviderConfigs = [],
        ReviewerConfigs = [],
        QualityGateConfigs = [],
        IssueComments = [],
        InitiatedBy = "test"
    };
}
