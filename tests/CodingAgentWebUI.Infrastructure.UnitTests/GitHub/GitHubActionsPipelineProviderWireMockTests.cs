using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure;
using Octokit;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using System.Text.Json;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class GitHubActionsPipelineProviderWireMockTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private GitHubActionsPipelineProvider CreateProvider() =>
        new(Server.Url!, Token, Owner, Repo, PollInterval);

    private GitHubActionsPipelineProvider CreateProviderWithTokenProvider(
        Func<CancellationToken, Task<string>> tokenProvider) =>
        new(Server.Url!, tokenProvider, Owner, Repo, PollInterval);

    private string RunsPath => ApiPath($"/repos/{Owner}/{Repo}/actions/runs");
    private string JobsPath(long runId) => ApiPath($"/repos/{Owner}/{Repo}/actions/runs/{runId}/jobs");
    private string LogsPath(long jobId) => ApiPath($"/repos/{Owner}/{Repo}/actions/jobs/{jobId}/logs");

    [Fact]
    public async Task GetRunStatusAsync_ReturnsDeserializedStatus()
    {
        var run = BuildWorkflowRunJson(100, "abc123", "completed", "success");
        StubGet(RunsPath, new { total_count = 1, workflow_runs = new[] { run } });

        var job = BuildWorkflowJobJson(200, "build", "completed", "success");
        StubGet(JobsPath(100), new { total_count = 1, jobs = new[] { job } });

        await using var provider = CreateProvider();
        var result = await provider.GetRunStatusAsync("main", "abc123", CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Passed);
        result.Jobs.Should().HaveCount(1);
        result.Jobs[0].Name.Should().Be("build");
        result.Jobs[0].State.Should().Be(PipelineRunState.Passed);
        result.CommitSha.Should().Be("abc123");
    }

    [Fact]
    public async Task GetRunStatusAsync_NoRuns_ReturnsPending()
    {
        StubGet(RunsPath, new { total_count = 0, workflow_runs = Array.Empty<object>() });

        await using var provider = CreateProvider();
        var result = await provider.GetRunStatusAsync("main", null, CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Pending);
        result.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunStatusAsync_FiltersByCommitSha()
    {
        var run1 = BuildWorkflowRunJson(100, "abc123", "completed", "success");
        var run2 = BuildWorkflowRunJson(101, "def456", "completed", "failure");
        StubGet(RunsPath, new { total_count = 2, workflow_runs = new[] { run1, run2 } });

        var job = BuildWorkflowJobJson(200, "build", "completed", "success");
        StubGet(JobsPath(100), new { total_count = 1, jobs = new[] { job } });

        await using var provider = CreateProvider();
        var result = await provider.GetRunStatusAsync("main", "abc123", CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Passed);
        result.CommitSha.Should().Be("abc123");
    }

    [Fact]
    public async Task GetRunStatusAsync_FailedRun_MapsCorrectly()
    {
        var run = BuildWorkflowRunJson(100, "abc123", "completed", "failure");
        StubGet(RunsPath, new { total_count = 1, workflow_runs = new[] { run } });

        var job = BuildWorkflowJobJson(200, "test", "completed", "failure");
        StubGet(JobsPath(100), new { total_count = 1, jobs = new[] { job } });

        await using var provider = CreateProvider();
        var result = await provider.GetRunStatusAsync("main", "abc123", CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Failed);
        result.Jobs[0].State.Should().Be(PipelineRunState.Failed);
        result.Jobs[0].FailureReason.Should().Contain("test");
    }

    [Fact]
    public async Task GetRunStatusAsync_404_ThrowsNotFoundException()
    {
        StubError(RunsPath, 404, new { message = "Not Found" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.GetRunStatusAsync("main", null, CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task WaitForCompletionAsync_PollsUntilComplete()
    {
        var inProgressRun = BuildWorkflowRunJson(100, "abc123", "in_progress", null);
        var completedRun = BuildWorkflowRunJson(100, "abc123", "completed", "success");
        var job = BuildWorkflowJobJson(200, "build", "completed", "success");

        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var inProgressBody = JsonSerializer.Serialize(
            new { total_count = 1, workflow_runs = new[] { inProgressRun } }, jsonOpts);
        var completedBody = JsonSerializer.Serialize(
            new { total_count = 1, workflow_runs = new[] { completedRun } }, jsonOpts);

        // First call: in_progress, second call: completed
        Server.Given(Request.Create().WithPath(RunsPath).UsingGet())
            .InScenario("polling")
            .WillSetStateTo("poll-1")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(inProgressBody));

        Server.Given(Request.Create().WithPath(RunsPath).UsingGet())
            .InScenario("polling")
            .WhenStateIs("poll-1")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(completedBody));

        StubGet(JobsPath(100), new { total_count = 1, jobs = new[] { job } });

        await using var provider = CreateProvider();
        var result = await provider.WaitForCompletionAsync(
            "main", "abc123", TimeSpan.FromSeconds(10), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Passed);
    }

    [Fact]
    public async Task WaitForCompletionAsync_Timeout_ReturnsLastStatus()
    {
        var run = BuildWorkflowRunJson(100, "abc123", "in_progress", null);
        StubGet(RunsPath, new { total_count = 1, workflow_runs = new[] { run } });

        var job = BuildWorkflowJobJson(200, "build", "in_progress", null);
        StubGet(JobsPath(100), new { total_count = 1, jobs = new[] { job } });

        // Use a long poll interval so the timeout fires during Task.Delay (between polls),
        // not during an HTTP call where the resilience pipeline's own timeout could interfere.
        // The 3s timeout is generous enough for the first poll's HTTP calls to complete,
        // and the 30s poll interval ensures we're waiting in Task.Delay when cancellation fires.
        var provider = new GitHubActionsPipelineProvider(
            Server.Url!, Token, Owner, Repo, TimeSpan.FromSeconds(30));
        await using (provider)
        {
            var result = await provider.WaitForCompletionAsync(
                "main", "abc123", TimeSpan.FromSeconds(3), CancellationToken.None);

            result.State.Should().Be(PipelineRunState.Running);
        }
    }

    [Fact]
    public async Task GetJobLogsAsync_ReturnsLogContent()
    {
        StubGetRaw(LogsPath(200),
            "2026-01-01T00:00:00Z Build started\n2026-01-01T00:01:00Z Build completed");

        await using var provider = CreateProvider();
        var result = await provider.GetJobLogsAsync(200, CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain("Build started");
        result.Should().Contain("Build completed");
    }

    [Fact]
    public async Task GetJobLogsAsync_404_ReturnsNull()
    {
        StubError(LogsPath(999), 404, new { message = "Not Found" });

        await using var provider = CreateProvider();
        var result = await provider.GetJobLogsAsync(999, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AllRequests_IncludeAuthorizationHeader()
    {
        StubGet(RunsPath, new { total_count = 0, workflow_runs = Array.Empty<object>() });

        await using var provider = CreateProvider();
        await provider.GetRunStatusAsync("main", null, CancellationToken.None);

        AssertAllRequestsHaveAuthHeader(Token);
    }

    [Fact]
    public async Task DynamicTokenProvider_EachRequestUsesCurrentToken()
    {
        var callCount = 0;
        var tokens = new[] { "token-alpha", "token-beta" };

        StubGet(RunsPath, new { total_count = 0, workflow_runs = Array.Empty<object>() });

        await using var provider = CreateProviderWithTokenProvider(ct =>
        {
            var token = tokens[Math.Min(Interlocked.Increment(ref callCount) - 1, tokens.Length - 1)];
            return Task.FromResult(token);
        });

        await provider.GetRunStatusAsync("main", null, CancellationToken.None);
        await provider.GetRunStatusAsync("main", null, CancellationToken.None);

#pragma warning disable CS8602 // WireMock types use nullable references
        var authHeaders = Server.LogEntries
            .Select(e => GetHeaderValue(e.RequestMessage.Headers, "Authorization"))
            .ToList();
#pragma warning restore CS8602

        // Token is cached: both requests use the first token (no redundant refresh)
        authHeaders.Should().HaveCount(2);
        authHeaders[0].Should().Contain("token-alpha");
        authHeaders[1].Should().Contain("token-alpha");
        callCount.Should().Be(1, "token provider should only be called once due to caching");
    }
}
