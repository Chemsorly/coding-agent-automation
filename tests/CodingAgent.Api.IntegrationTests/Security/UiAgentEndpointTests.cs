using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.5a: the agent list on the UI surface, filtered per D3 rule 5. A global reader sees every
/// agent; a project-scoped user sees only the agents running work of their projects, so neither idle
/// agents nor other projects' issues reach them. The web's real <see cref="UiAgentClient"/> drives the
/// requests, so this doubles as its contract test.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiAgentEndpointTests(ApiWebApplicationFactory factory)
{
    private const string ProjectA = "6f1c2a9e-0000-0000-0000-0000000000e1";
    private const string ProjectB = "6f1c2a9e-0000-0000-0000-0000000000e2";

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    private UiAgentClient Client(AccessGrant grant) => new(factory.CreateClient(), new FixedToken(TestUserTokens.For(grant)));

    private string RegisterAgent()
    {
        var agentId = $"ui-agent-{Guid.NewGuid():N}";
        factory.Services.GetRequiredService<AgentRegistryService>().Register(
            new AgentRegistrationMessage { AgentId = new AgentId(agentId), Hostname = $"host-{agentId}", Labels = ["dotnet"] },
            connectionId: $"conn-{agentId}");
        return agentId;
    }

    /// <summary>Registers an agent that is busy with a run of <paramref name="projectId"/>.</summary>
    private string RegisterBusyAgent(string projectId, string issueTitle)
    {
        var agentId = RegisterAgent();
        var runId = $"ui-agent-run-{Guid.NewGuid():N}";
        factory.Services.GetRequiredService<OrchestratorRunService>().AddRun(new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("owner/repo#1"),
            IssueTitle = issueTitle,
            IssueProviderConfigId = "ip-ui-agents",
            RepoProviderConfigId = "rp-ui-agents",
            ProjectId = projectId,
        });
        var registry = factory.Services.GetRequiredService<AgentRegistryService>();
        registry.TransitionStatus(new AgentId(agentId), AgentStatus.Busy);
        var entry = registry.GetByAgentId(agentId)!;
        lock (entry.SyncRoot)
            entry.ActiveJobId = runId;
        return agentId;
    }

    [Fact]
    public async Task GlobalReader_SeesEveryAgent_WithItsRun()
    {
        var idle = RegisterAgent();
        var busyOnB = RegisterBusyAgent(ProjectB, "Bravo work");

        var agents = await Client(TestUserTokens.Global(AccessRole.ReadOnly)).GetAgentsAsync();

        agents.Select(a => a.AgentId.Value).Should().Contain([idle, busyOnB]);
        agents.Single(a => a.AgentId.Value == busyOnB).ActiveIssueTitle.Should().Be("Bravo work");
    }

    [Fact]
    public async Task ScopedUser_SeesOnlyTheAgentsRunningWorkOfTheirProjects()
    {
        var idle = RegisterAgent();
        var busyOnA = RegisterBusyAgent(ProjectA, "Alpha work");
        var busyOnB = RegisterBusyAgent(ProjectB, "Bravo work");

        var agents = await Client(TestUserTokens.Scoped(ProjectA, AccessRole.ReadOnly)).GetAgentsAsync();

        var ids = agents.Select(a => a.AgentId.Value).ToList();
        ids.Should().Contain(busyOnA);
        ids.Should().NotContain([idle, busyOnB]);
    }

    [Fact]
    public async Task CredentialPool_NeedsGlobalRead()
    {
        var scoped = factory.CreateClient();
        scoped.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(TestUserTokens.Scoped(ProjectA, AccessRole.Operator)));

        (await scoped.GetAsync("/api/ui/agents/credential-pool")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(TestUserTokens.Global(AccessRole.ReadOnly)).GetCredentialPoolAsync())
            .Should().Be(new CredentialPoolStatus(0, 0, 0));
    }
}
