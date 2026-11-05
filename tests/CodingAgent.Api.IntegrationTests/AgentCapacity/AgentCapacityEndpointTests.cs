using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.Security;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.AgentCapacity;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentCapacityEndpointCollection
{
    public const string Name = "AgentCapacityEndpointCollection";
}

/// <summary>A Job client whose agent-Job list a test sets, or which throws.</summary>
public sealed class SettableAgentJobs : IKubernetesJobClient
{
    public List<V1Job> Jobs { get; } = [];
    public Exception? ListFailure { get; set; }

    public Task<V1JobList> ListJobsAsync(string ns, string labelSelector, CancellationToken ct = default) =>
        ListFailure is { } failure ? Task.FromException<V1JobList>(failure) : Task.FromResult(new V1JobList { Items = [.. Jobs] });

    public Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteJobAsync(string name, string ns, CancellationToken ct = default) => Task.CompletedTask;
    public Task<V1Job> ReadJobAsync(string name, string ns, CancellationToken ct = default) => Task.FromResult(new V1Job());
    public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteSecretAsync(string name, string ns, CancellationToken ct = default) => Task.CompletedTask;
    public Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default) => Task.FromResult(new V1PodList { Items = [] });
    public Task<string> ReadPodLogTailAsync(string podName, string ns, string container, int tailLines, CancellationToken ct = default) => Task.FromResult("");
}

/// <summary>
/// An API host with three job templates (one with a Helm limit of 4, one without, one without labels), a Helm global
/// limit of 6 and a settable Job list. Options are replaced as a service, never through an env var, which would leak
/// into parallel tests.
/// </summary>
public sealed class AgentCapacityWebApplicationFactory : ApiWebApplicationFactory
{
    public SettableAgentJobs Jobs { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<JobTemplateStore>();
        services.AddSingleton(JobTemplateStore.LoadFromYaml("""
            - labels: "kiro,dotnet"
              image: "agent:test"
              providerType: "kiro"
              maxConcurrent: 4
            - labels: "claude"
              image: "agent:test"
              providerType: "claude"
            - labels: ""
              image: "agent:test"
              providerType: "opencode"
            """));
        services.RemoveAll<DispatchServiceOptions>();
        services.AddSingleton(new DispatchServiceOptions { MaxConcurrentAgents = 6, Namespace = "test" });
        services.RemoveAll<IKubernetesJobClient>();
        services.AddSingleton<IKubernetesJobClient>(Jobs);
    }
}

/// <summary>Spec 057 req 6.1, 6.3, 7.2, 8.1–8.3: the Capacity page's endpoints and the Scheduler's capacity endpoint.</summary>
[Collection(AgentCapacityEndpointCollection.Name)]
public sealed class AgentCapacityEndpointTests : IClassFixture<AgentCapacityWebApplicationFactory>
{
    private const string Route = "/api/ui/settings/agent-capacity";
    private readonly AgentCapacityWebApplicationFactory _factory;
    private readonly HttpClient _admin;

    public AgentCapacityEndpointTests(AgentCapacityWebApplicationFactory factory)
    {
        _factory = factory;
        factory.Jobs.Jobs.Clear();
        factory.Jobs.ListFailure = null;
        factory.Services.GetRequiredService<AgentCapacityStore>().SaveAsync(AgentCapacityLimits.Empty, CancellationToken.None)
            .GetAwaiter().GetResult();
        _admin = Client(TestUserTokens.Global(AccessRole.Admin));
    }

    private HttpClient Client(AccessGrant grant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private static V1Job Job(string label, bool run) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Labels = run
                ? new Dictionary<string, string> { ["caa/agent-selector"] = label, ["caa/work-item-id"] = Guid.NewGuid().ToString() }
                : new Dictionary<string, string> { ["caa/agent-selector"] = label },
        },
        Status = new V1JobStatus(),
    };

    private Task<UiAgentCapacityPage?> GetPageAsync() =>
        _admin.GetFromJsonAsync<UiAgentCapacityPage>(Route, PipelineJsonOptions.Default);

    private static AgentCapacityLimits Limits(int global, params (string Key, int Value)[] entries) => new()
    {
        MaxConcurrentAgents = global,
        MaxConcurrentByJobTemplate = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
    };

    // req 6.1
    [Fact]
    public async Task Get_ShowsRunningAgents_HelmUiAndEffectiveLimits()
    {
        _factory.Jobs.Jobs.AddRange([Job("dotnet.kiro", run: true), Job("dotnet.kiro", run: false), Job("gone.template", run: true)]);
        await _factory.Services.GetRequiredService<AgentCapacityStore>().SaveAsync(Limits(5, ("claude", 2), ("dotnet,kiro", 9)), CancellationToken.None);

        var page = await GetPageAsync();

        page!.CountsAvailable.Should().BeTrue();
        page.All.Should().Be(new UiAgentCapacityRow(null, "", 2, 1, 6, 5, 5, "ui"));
        page.Templates.Select(r => r.TemplateKey).Should().Equal("", "claude", "dotnet,kiro");
        page.Templates.Single(r => r.TemplateKey == "dotnet,kiro").Should().Be(
            new UiAgentCapacityRow("dotnet,kiro", "kiro", 1, 1, 4, 9, 4, "helm"), "a UI value above Helm is kept but Helm applies");
        page.Templates.Single(r => r.TemplateKey == "claude").Should().Be(
            new UiAgentCapacityRow("claude", "claude", 0, 0, 0, 2, 2, "ui"));
        page.Templates.Single(r => r.TemplateKey == "").EffectiveLimit.Should().BeNull();
        page.UnknownTemplateRuns.Should().Be(1);
    }

    // req 2.4: the page still shows (and saves) when the Jobs cannot be listed.
    [Fact]
    public async Task Get_WhenTheJobsCannotBeListed_ShowsNoCounts()
    {
        _factory.Jobs.ListFailure = new HttpRequestException("api server down");

        var page = await GetPageAsync();

        page!.CountsAvailable.Should().BeFalse();
        page.All.RunningRuns.Should().Be(0);
        page.All.HelmLimit.Should().Be(6);
    }

    // req 6.3, 1.7: normalized keys are saved; unknown keys and 0 values are dropped.
    [Fact]
    public async Task Put_SavesNormalizedKeys_AndDropsUnknownKeysAndZeros()
    {
        var response = await _admin.PutAsJsonAsync(Route, Limits(3, (" dotnet , kiro ", 2), ("claude", 0), ("not,deployed", 5)), PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stored = await _factory.Services.GetRequiredService<AgentCapacityStore>().LoadAsync(CancellationToken.None);
        stored.MaxConcurrentAgents.Should().Be(3);
        stored.MaxConcurrentByJobTemplate.Should().Equal(new Dictionary<string, int> { ["dotnet,kiro"] = 2 });
    }

    // req 6.3, 8.3: invalid input is refused with the reason, not silently dropped.
    [Theory]
    [InlineData(101, "dotnet,kiro", 1, "The limit for all agents must be between 0 and 100 (was 101).")]
    [InlineData(0, "dotnet,kiro", -1, "The limit for dotnet,kiro must be between 0 and 100 (was -1).")]
    [InlineData(0, "dotnet;kiro", 1, "dotnet;kiro is not a valid job template key.")]
    public async Task Put_InvalidLimits_Is400WithTheReason(int global, string key, int value, string reason)
    {
        var response = await _admin.PutAsJsonAsync(Route, Limits(global, (key, value)), PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(reason);
    }

    // req 8.1, D15
    [Theory]
    [InlineData(AccessRole.Operator)]
    [InlineData(AccessRole.ReadOnly)]
    public async Task NonAdmins_Get403(AccessRole role)
    {
        var client = Client(TestUserTokens.Global(role));

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync(Route, Limits(1), PipelineJsonOptions.Default)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // req 7.2, 8.2
    [Fact]
    public async Task ServiceCapacity_AnswersTheScheduler_WithTheSameRows()
    {
        _factory.Jobs.Jobs.Add(Job("claude", run: true));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        var status = await client.GetFromJsonAsync<AgentCapacityStatus>("/api/agents/capacity", PipelineJsonOptions.Default);

        status!.All.Should().Be(new AgentCapacityStatusRow(null, 1, 0, 6, "helm"));
        status.Templates.Single(r => r.TemplateKey == "claude").RunningRuns.Should().Be(1);
        status.CountsAvailable.Should().BeTrue();
    }
}
