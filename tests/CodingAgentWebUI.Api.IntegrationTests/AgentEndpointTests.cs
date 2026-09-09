using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Integration tests for <c>GET /api/agents</c>.
///
/// <para>
/// This endpoint is the monolith's only window onto agent presence: since Spec 044 the hub — and
/// therefore the registry — lives only in this process. Two things are load-bearing and both are
/// asserted here. First, the payload has to survive a round trip through
/// <see cref="PipelineJsonOptions.Default"/>, because that is what
/// <c>IPipelineApiAgentClient</c> deserializes with and <see cref="AgentEntryDto"/> carries an
/// <see cref="AgentId"/> struct and an enum that a naive contract would mangle. Second, the route
/// must reject an agent-derived key: it lists every agent in the cluster with hostnames, labels and
/// connection IDs, which is control-plane data, not something a pod should be able to enumerate.
/// </para>
///
/// <para>
/// The factory is shared across the collection, so tests use unique agent IDs and assert on
/// containment rather than on registry-wide counts.
/// </para>
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class AgentEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AgentEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        // Operator auth: master key, no agentId query parameter.
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    /// <summary>
    /// Registers an agent in the API's own registry — the same instance
    /// <c>AgentHub.RegisterAgent</c> writes to — and returns its id.
    /// </summary>
    private string RegisterAgent(IReadOnlyList<string>? labels = null)
    {
        var agentId = $"endpoint-agent-{Guid.NewGuid():N}";
        var registry = _factory.Services.GetRequiredService<AgentRegistryService>();

        registry.Register(
            new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = $"host-{agentId}",
                Labels = labels ?? ["dotnet", "linux"]
            },
            connectionId: $"conn-{agentId}");

        return agentId;
    }

    // ── Happy path ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAgents_WithOperatorKey_Returns200()
    {
        var response = await _client.GetAsync("/api/agents");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAgents_IncludesARegisteredAgent_WithItsRegistrationDetails()
    {
        var agentId = RegisterAgent(["dotnet", "gpu"]);

        var response = await _client.GetAsync("/api/agents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentEntryDto>>(PipelineJsonOptions.Default);
        agents.Should().NotBeNull();

        var agent = agents!.SingleOrDefault(a => a.AgentId.Value == agentId);
        agent.Should().NotBeNull("the agent was registered on the registry this endpoint reads");
        agent!.Hostname.Should().Be($"host-{agentId}");
        agent.ConnectionId.Should().Be($"conn-{agentId}");
        agent.Labels.Should().BeEquivalentTo("dotnet", "gpu");
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    /// <summary>
    /// The status field is what drives every badge on the monitoring page, and it crosses the wire
    /// as a string. An agent moved to Busy must arrive as Busy, not as the enum's default.
    /// </summary>
    [Fact]
    public async Task GetAgents_ReportsCurrentStatus_ForABusyAgent()
    {
        var agentId = RegisterAgent();
        var registry = _factory.Services.GetRequiredService<AgentRegistryService>();
        registry.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);

        var agents = await _client.GetFromJsonAsync<List<AgentEntryDto>>("/api/agents", PipelineJsonOptions.Default);

        agents.Should().NotBeNull();
        agents!.Single(a => a.AgentId.Value == agentId).Status.Should().Be(AgentStatus.Busy);
    }

    /// <summary>
    /// A deregistered agent must disappear from the response — the monolith replaces its snapshot
    /// wholesale on each poll, so anything still listed here is still shown in the UI.
    /// </summary>
    [Fact]
    public async Task GetAgents_OmitsADeregisteredAgent()
    {
        var agentId = RegisterAgent();
        var registry = _factory.Services.GetRequiredService<AgentRegistryService>();
        registry.Deregister(new AgentId(agentId)).Should().BeTrue();

        var agents = await _client.GetFromJsonAsync<List<AgentEntryDto>>("/api/agents", PipelineJsonOptions.Default);

        agents.Should().NotBeNull();
        agents!.Should().NotContain(a => a.AgentId.Value == agentId);
    }

    // ── Enrichment: active run fields ─────────────────────────────────────────

    /// <summary>
    /// When a busy agent has a matching active run, the response must include the issue/run/PR
    /// enrichment fields populated from that run.
    /// </summary>
    [Fact]
    public async Task GetAgents_BusyAgent_WithActiveRun_IncludesIssueAndRunLinks()
    {
        var agentId = RegisterAgent();
        var registry = _factory.Services.GetRequiredService<AgentRegistryService>();
        var runService = _factory.Services.GetRequiredService<OrchestratorRunService>();

        var runId = $"run-enrich-{Guid.NewGuid():N}";
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("owner/repo#42"),
            IssueTitle = "Fix the widget",
            IssueUrl = "https://github.com/owner/repo/issues/42",
            PullRequestUrl = "https://github.com/owner/repo/pull/7",
            IssueProviderConfigId = "ip-enrich",
            RepoProviderConfigId = "rp-enrich",
        };
        runService.AddRun(run);

        // Mark the agent busy with the run's ID as its active job.
        registry.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);
        var entry = registry.GetByAgentId(agentId)!;
        lock (entry.SyncRoot)
            entry.ActiveJobId = runId;

        var agents = await _client.GetFromJsonAsync<List<AgentEntryDto>>("/api/agents", PipelineJsonOptions.Default);
        agents.Should().NotBeNull();

        var dto = agents!.Single(a => a.AgentId.Value == agentId);
        dto.ActiveIssueIdentifier.Should().Be("owner/repo#42");
        dto.ActiveIssueTitle.Should().Be("Fix the widget");
        dto.ActiveIssueUrl.Should().Be("https://github.com/owner/repo/issues/42");
        dto.ActiveRunId.Should().Be(runId);
        dto.ActivePullRequestUrl.Should().Be("https://github.com/owner/repo/pull/7");
    }

    /// <summary>
    /// An idle agent has no active run, so all enrichment fields must be null.
    /// </summary>
    [Fact]
    public async Task GetAgents_IdleAgent_HasNullEnrichmentFields()
    {
        var agentId = RegisterAgent();

        var agents = await _client.GetFromJsonAsync<List<AgentEntryDto>>("/api/agents", PipelineJsonOptions.Default);
        agents.Should().NotBeNull();

        var dto = agents!.Single(a => a.AgentId.Value == agentId);
        dto.Status.Should().Be(AgentStatus.Idle);
        dto.ActiveIssueIdentifier.Should().BeNull();
        dto.ActiveIssueTitle.Should().BeNull();
        dto.ActiveIssueUrl.Should().BeNull();
        dto.ActiveRunId.Should().BeNull();
        dto.ActivePullRequestUrl.Should().BeNull();
    }

    /// <summary>
    /// When a busy agent's ActiveJobId points to a run that is no longer in the service (e.g.
    /// the run completed between the registry read and enrichment), all enrichment fields must be
    /// null — graceful degradation, not an error.
    /// </summary>
    [Fact]
    public async Task GetAgents_BusyAgent_WithCompletedRun_ReturnsNullEnrichment()
    {
        var agentId = RegisterAgent();
        var registry = _factory.Services.GetRequiredService<AgentRegistryService>();

        registry.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);
        var entry = registry.GetByAgentId(agentId)!;
        lock (entry.SyncRoot)
            entry.ActiveJobId = $"run-gone-{Guid.NewGuid():N}";

        var agents = await _client.GetFromJsonAsync<List<AgentEntryDto>>("/api/agents", PipelineJsonOptions.Default);
        agents.Should().NotBeNull();

        var dto = agents!.Single(a => a.AgentId.Value == agentId);
        dto.Status.Should().Be(AgentStatus.Busy);
        dto.ActiveIssueIdentifier.Should().BeNull();
        dto.ActiveRunId.Should().BeNull();
        dto.ActivePullRequestUrl.Should().BeNull();
    }

    /// <summary>
    /// When the active run has no PR yet, ActivePullRequestUrl must be null — no broken link.
    /// </summary>
    [Fact]
    public async Task GetAgents_BusyAgent_WithNoPullRequest_HasNullPrUrl()
    {
        var agentId = RegisterAgent();
        var registry = _factory.Services.GetRequiredService<AgentRegistryService>();
        var runService = _factory.Services.GetRequiredService<OrchestratorRunService>();

        var runId = $"run-nopr-{Guid.NewGuid():N}";
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("owner/repo#99"),
            IssueTitle = "Add feature",
            IssueUrl = "https://github.com/owner/repo/issues/99",
            PullRequestUrl = null,   // no PR yet
            IssueProviderConfigId = "ip-nopr",
            RepoProviderConfigId = "rp-nopr",
        };
        runService.AddRun(run);

        registry.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);
        var entry = registry.GetByAgentId(agentId)!;
        lock (entry.SyncRoot)
            entry.ActiveJobId = runId;

        var agents = await _client.GetFromJsonAsync<List<AgentEntryDto>>("/api/agents", PipelineJsonOptions.Default);
        agents.Should().NotBeNull();

        var dto = agents!.Single(a => a.AgentId.Value == agentId);
        dto.ActiveIssueIdentifier.Should().Be("owner/repo#99");
        dto.ActiveRunId.Should().Be(runId);
        dto.ActivePullRequestUrl.Should().BeNull("no PR has been created for this run yet");
    }

    // ── Auth: operator vs agent key ───────────────────────────────────────────────

    /// <summary>
    /// The response enumerates the whole fleet. An agent pod authenticates with a key derived from
    /// its own id (HMAC-SHA256(master, agentId)), which stamps <c>auth_kind=agent</c> — that tier
    /// must not reach this route.
    /// </summary>
    [Fact]
    public async Task GetAgents_RejectsAgentDerivedKey_WithForbidden()
    {
        const string agentId = "agent-endpoint-probe";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        using var agentClient = _factory.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", derivedKey);

        var response = await agentClient.GetAsync($"/api/agents?agentId={agentId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "OperatorApiKey requires auth_kind=operator; a per-pod derived key must not enumerate its peers");
    }

    [Fact]
    public async Task GetAgents_WithoutCredentials_IsNotAnonymous()
    {
        using var anonClient = _factory.CreateClient();

        var response = await anonClient.GetAsync("/api/agents");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAgents_WithAnUnknownKey_IsRejected()
    {
        using var badClient = _factory.CreateClient();
        badClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-key");

        var response = await badClient.GetAsync("/api/agents");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetCredentialPool_ReturnsStatus_ThatRoundTripsThroughTheContract()
    {
        var response = await _client.GetAsync("/api/agents/credential-pool");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Load-bearing: the client deserializes with PipelineJsonOptions.Default.
        var status = await response.Content.ReadFromJsonAsync<CredentialPoolStatus>(PipelineJsonOptions.Default);
        status.Should().NotBeNull();
        status!.Total.Should().BeGreaterThanOrEqualTo(0);
        status.Available.Should().BeGreaterThanOrEqualTo(0);
        status.Claimed.Should().BeGreaterThanOrEqualTo(0);
        // Available can never exceed the configured pool.
        status.Available.Should().BeLessThanOrEqualTo(status.Total);
    }

    [Fact]
    public async Task GetCredentialPool_RejectsAgentKey()
    {
        using var badClient = _factory.CreateClient();
        badClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-key");

        var response = await badClient.GetAsync("/api/agents/credential-pool");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
