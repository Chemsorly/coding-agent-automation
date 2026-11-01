using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodingAgent.Contracts.Auth;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Infrastructure.SystemTier;

/// <summary>
/// Abstract base class for System E2E tests that use real Postgres and Redis containers.
///
/// <para>
/// Each test class that inherits this base receives a shared <see cref="SystemContainers"/>
/// instance via xUnit's collection fixture, and this base class handles per-test container
/// health restoration and API host lifecycle.
/// </para>
/// </summary>
public abstract class SystemE2ETestBase(SystemContainers containers) : IAsyncLifetime
{
    private readonly List<SystemApiFactory> _factories = [];

    /// <summary>The shared container pair for this collection.</summary>
    protected SystemContainers Containers { get; } = containers;

    public async Task InitializeAsync()
    {
        // Ensure both containers are running before each test. A prior test may have
        // stopped one (for failure-scenario testing); restarting here prevents cascading failures.
        await Containers.EnsureRunningAsync();
        // Flush Redis state so tests start clean regardless of what a previous test left behind.
        await Containers.FlushRedisAsync();
    }

    public async Task DisposeAsync()
    {
        // Dispose all API hosts started by this test, in reverse order.
        // WebApplicationFactory implements IAsyncDisposable; use DisposeAsync to allow
        // hosted services (which run real background work in this tier) to stop gracefully.
        for (var i = _factories.Count - 1; i >= 0; i--)
        {
            await _factories[i].DisposeAsync();
        }
        _factories.Clear();
    }

    /// <summary>
    /// Creates and starts a new <see cref="SystemApiFactory"/> against the shared containers.
    /// Each call returns a distinct factory (useful for two-replica tests). The factory is
    /// disposed automatically when the test ends.
    /// </summary>
    protected async Task<SystemApiFactory> StartApiAsync()
    {
        var factory = new SystemApiFactory(Containers, new FakeProviderFactory(), new FakeKubernetesJobClient());
        // Calling CreateClient() forces Kestrel to bind and sets ClientOptions.BaseAddress,
        // which is required before ServerAddress is readable.
        factory.CreateClient().Dispose();
        _factories.Add(factory);
        return factory;
    }

    /// <summary>
    /// Sends an anonymous GET request to <paramref name="path"/> on <paramref name="api"/> and
    /// returns the HTTP status code plus the <c>status</c> and <c>reason</c> JSON fields from the body.
    /// </summary>
    protected static async Task<(HttpStatusCode Status, string? StatusField, string? Reason)>
        GetProbeAsync(SystemApiFactory api, string path)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(api.ServerAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        string? statusField = null;
        string? reason = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("status", out var s))
                    statusField = s.GetString();
                if (doc.RootElement.TryGetProperty("reason", out var r))
                    reason = r.GetString();
            }
            catch (JsonException)
            {
                // Body is not JSON; leave fields null.
            }
        }

        return (response.StatusCode, statusField, reason);
    }

    /// <summary>
    /// Polls <paramref name="condition"/> every 250 ms until it returns <c>true</c> or
    /// <paramref name="timeout"/> expires.
    /// </summary>
    /// <exception cref="TimeoutException">Thrown when the deadline is reached without <paramref name="condition"/> returning <c>true</c>.</exception>
    protected static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s waiting for: {because}");
    }

    /// <summary>
    /// Creates a signed user token with <see cref="AccessRole.Admin"/> for use in UI API
    /// requests. Uses the same signing key and subject as the harness's other fixtures.
    /// </summary>
    protected static string CreateAdminUserToken()
    {
        var now = DateTimeOffset.UtcNow;
        return UserTokens.Create(
            new UserTokenSubject("e2e:tester", "tester", "e2e"),
            new AccessGrant(AccessRole.Admin, new Dictionary<string, AccessRole>(), []),
            now,
            now.AddMinutes(30),
            UserTokens.CreateSigningKey(E2ETestDefaults.UserTokenSigningKey));
    }

    /// <summary>
    /// Creates an HTTP client for the API's UI surface, pre-authenticated with an admin token.
    /// </summary>
    protected static HttpClient CreateUiClient(SystemApiFactory api)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(api.ServerAddress),
            Timeout = TimeSpan.FromSeconds(120)
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAdminUserToken());
        return client;
    }

    /// <summary>
    /// Inserts a <see cref="WorkItemStatus.Running"/> work item directly into the database via
    /// EF Core. Used by failure-scenario tests that need an in-flight item without going through
    /// the full dispatch path.
    /// </summary>
    protected static async Task<Guid> SeedRunningWorkItemAsync(
        SystemApiFactory api, string issueIdentifier, string agentId)
    {
        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var payload = JsonSerializer.Serialize(
            new JobDistributionRequest
            {
                IssueIdentifier = issueIdentifier,
                IssueProviderConfigId = "issue-e2e",
                RepoProviderConfigId = "repo-e2e",
                InitiatedBy = "system-test",
                TaskType = WorkItemTaskType.Implementation,
                AgentSelector = "e2e",
                TimeoutSeconds = 3600,
                PayloadSchemaVersion = 1
            },
            PipelineJsonOptions.Default);

        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Running,
            AssignedAgentId = agentId,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            CreatedAt = now,
            DispatchedAt = now,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            Payload = payload
        });

        await db.SaveChangesAsync();
        return id;
    }
}
