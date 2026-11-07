using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Runs <see cref="ConsolidationEnqueueEndpointTests"/> alone. Its host is built from its own factory, which sets
/// process-global state (environment variables, the static Serilog logger) that the shared
/// <see cref="ApiIntegrationTestCollection"/> host must not see change under it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsolidationEnqueueCollection
{
    public const string Name = "ConsolidationEnqueueCollection";
}

/// <summary>
/// An API host whose <see cref="JobTemplateStore"/> holds one template, <c>kiro,dotnet</c>. The shared
/// <see cref="ApiWebApplicationFactory"/> starts with an empty store, so every consolidation request
/// is refused there (issue #3642).
/// </summary>
public sealed class ConsolidationTemplateWebApplicationFactory : ApiWebApplicationFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<JobTemplateStore>();
        services.AddSingleton(JobTemplateStore.LoadFromYaml("""
            - labels: "kiro,dotnet"
              image: "test-image:latest"
              imagePullPolicy: "Always"
              providerType: "kiro"
              maxConcurrent: 5
            """));
    }
}

/// <summary>
/// <c>POST /api/work-items</c> for a Consolidation request, against a host that has a job template
/// (issue #3642). The refusal for a selector without one is covered here for a populated store, and in
/// <c>WorkItemEndpointTests</c> for the empty store of the shared host.
/// </summary>
[Collection(ConsolidationEnqueueCollection.Name)]
public sealed class ConsolidationEnqueueEndpointTests : IClassFixture<ConsolidationTemplateWebApplicationFactory>
{
    private readonly ConsolidationTemplateWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConsolidationEnqueueEndpointTests(ConsolidationTemplateWebApplicationFactory factory)
    {
        _factory = factory;
        factory.SeedDefaultRoute();
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    private static JobDistributionRequest MakeConsolidationRequest(string agentSelector) => new()
    {
        IssueIdentifier = new IssueIdentifier($"consol-pending-{Guid.NewGuid():N}"),
        IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Consolidation,
        RunType = PipelineRunType.Consolidation,
        AgentSelector = agentSelector,
        TimeoutSeconds = 3600
    };

    [Fact]
    public async Task PostWorkItems_Consolidation_WithTemplate_Returns201_AndInsertsPendingRow()
    {
        var response = await _client.PostAsJsonAsync("/api/work-items", MakeConsolidationRequest("kiro,dotnet"),
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "a consolidation run whose selector has a job template is enqueued as before");
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);
        try
        {
            using var db = _factory.CreateDbContext();
            var entity = await db.WorkItems.FindAsync(id);
            entity.Should().NotBeNull();
            entity!.Status.Should().Be(WorkItemStatus.Pending);
            entity.TaskType.Should().Be(WorkItemTaskType.Consolidation);
        }
        finally
        {
            _factory.Services.GetRequiredService<IOrchestratorRunService>().RemoveRun((RunId)id.ToString());
        }
    }

    [Fact]
    public async Task PostWorkItems_Consolidation_RoutedToAProfileWithoutAJobTemplate_IsCreated_AndHeldAtDispatch()
    {
        // Spec 056 DD1 replaces #3642's refusal at creation: the dispatcher holds it as no_template. The next test's
        // constructor restores the default route.
        const string profileId = "0e5e0000-0000-4000-8000-0000000000a1";
        await _factory.Services.GetRequiredService<IAgentProfileStore>().SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(profileId, "kiro,python,python312", "Python agent"), CancellationToken.None);
        await _factory.Services.GetRequiredService<AgentRoutingStore>().SaveAsync(
            AgentRoutingTestData.Routing(profileId), CancellationToken.None);
        var request = MakeConsolidationRequest("");

        var response = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>();
        var dispatch = await _client.PostAsync($"/api/work-items/{id}/dispatch", null);
        var answer = await dispatch.Content.ReadFromJsonAsync<DispatchPendingResponse>(PipelineJsonOptions.Default);
        answer!.Reason.Should().Be("no_template");
        answer.HoldText.Should().Be("Job template kiro,python,python312 of agent profile 'Python agent' does not exist in the Helm values");
    }

    /// <summary>
    /// Regression test for issue #3088: a Pending consolidation WorkItem registered as an active run
    /// MUST appear in the includeActive=true merge, even though it is Pending. The Consolidation page
    /// needs to see Pending consolidation items to render the Cancel button.
    /// Contrast with <c>PipelineRunEndpointTests.GetRunHistory_IncludeActive_PendingWorkItem_IsExcludedFromMerge</c>
    /// which asserts that Pending NON-consolidation items are still excluded.
    /// Moved here from <c>PipelineRunEndpointTests</c> (issue #3642): it enqueues through the real
    /// POST /api/work-items, which now needs a job template for the selector.
    /// </summary>
    [Fact]
    public async Task GetRunHistory_IncludeActive_PendingConsolidationWorkItem_IsIncluded()
    {
        // Create a consolidation work item via the API (inserts as Status=Pending + calls AddRun).
        var pendingRequest = MakeConsolidationRequest("kiro,dotnet");
        var createResponse = await _client.PostAsJsonAsync("/api/work-items", pendingRequest,
            PipelineJsonOptions.Default);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var pendingRunId = await createResponse.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
        try
        {
            // The Consolidation page queries with runType=Consolidation; the carve-out applies
            // regardless of that filter (it is in the .Where predicate, not the runType filter).
            var response = await _client.GetAsync(
                "/api/pipeline-runs?includeActive=true&runType=Consolidation&pageSize=500");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
            body.Should().NotBeNull();
            body!.Items.Should().Contain(r => r.RunId == pendingRunId.ToString(),
                "a Pending consolidation WorkItem must appear in the includeActive merge so the " +
                "Consolidation page can show the Cancel button (issue #3088)");
        }
        finally
        {
            runService.RemoveRun((RunId)pendingRunId.ToString());
        }
    }
}
