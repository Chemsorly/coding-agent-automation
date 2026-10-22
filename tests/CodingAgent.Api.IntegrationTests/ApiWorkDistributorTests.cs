using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Spec 051 2.3a: the API's in-process <see cref="IWorkDistributor"/>. It must report what
/// <c>KubernetesWorkDistributor</c> reports over HTTP, because dispatch code that moves into the
/// API switches from one to the other. The parity tests ask both the distributor and the
/// <c>/api/work-items</c> endpoints the same questions.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class ApiWorkDistributorTests(ApiWebApplicationFactory factory)
{
    private IWorkDistributor Distributor => Seeded().Services.GetRequiredService<IWorkDistributor>();

    /// <summary>The factory with its default route stored (spec 053): the API routes every work item it creates.</summary>
    private ApiWebApplicationFactory Seeded()
    {
        factory.SeedDefaultRoute();
        return factory;
    }

    private HttpClient Operator()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
        return client;
    }

    private static JobDistributionRequest Request(string? issue = null, string? runId = null, WorkItemTaskType taskType = WorkItemTaskType.Implementation) => new()
    {
        IssueIdentifier = new IssueIdentifier(issue ?? $"dist-{Guid.NewGuid():N}"),
        IssueProviderConfigId = "prov-dist",
        RepoProviderConfigId = "repo-dist",
        InitiatedBy = "test",
        TaskType = taskType,
        AgentSelector = "",
        TimeoutSeconds = 3600,
        RunId = runId,
    };

    private async Task<WorkItemStatus> StatusAsync(Guid id)
    {
        await using var db = factory.CreateDbContext();
        return await db.WorkItems.AsNoTracking().Where(w => w.Id == id).Select(w => w.Status).SingleAsync();
    }

    [Fact]
    public void TheApiRegistersTheInProcessDistributor()
    {
        Distributor.Should().BeOfType<ApiWorkDistributor>();
    }

    [Fact]
    public async Task Distribute_EnqueuesAPendingItem_UnderTheRunId()
    {
        var runId = Guid.NewGuid();

        var result = await Distributor.DistributeAsync(Request(runId: runId.ToString()), CancellationToken.None);

        result.Should().BeEquivalentTo(new DistributionResult(true, runId.ToString(), null, Queued: true));
        (await StatusAsync(runId)).Should().Be(WorkItemStatus.Pending);
    }

    [Fact]
    public async Task Distribute_TheSameRunAgain_IsAnIdempotentRetry()
    {
        var request = Request(runId: Guid.NewGuid().ToString());
        await Distributor.DistributeAsync(request, CancellationToken.None);

        var again = await Distributor.DistributeAsync(request, CancellationToken.None);

        again.Should().BeEquivalentTo(new DistributionResult(true, request.RunId, null, Queued: true));
    }

    [Fact]
    public async Task Distribute_WhileTheIssueHasALiveItem_ReportsAlreadyExists()
    {
        var issue = $"dist-live-{Guid.NewGuid():N}";
        await Distributor.DistributeAsync(Request(issue), CancellationToken.None);

        var second = await Distributor.DistributeAsync(Request(issue), CancellationToken.None);

        second.Should().BeEquivalentTo(new DistributionResult(true, null, null, Queued: true, AlreadyExists: true));
    }

    [Fact]
    public async Task Distribute_WithoutARoute_IsAPermanentFailureWithTheReason()
    {
        // Spec 053 task 4.2: the Scheduler shows the reason and does not retry.
        var distributor = Distributor;
        await using (var db = factory.CreateDbContext())
        {
            db.KeyValueStore.RemoveRange(db.KeyValueStore.Where(kv => kv.Key == CodingAgent.Pipeline.Models.AgentRouting.StorageKey));
            await db.SaveChangesAsync();
        }

        var result = await distributor.DistributeAsync(Request(), CancellationToken.None);

        result.Should().BeEquivalentTo(new DistributionResult(false, null, AgentRouteRefusalReason.NoRouteText, IsPermanentFailure: true));
    }

    [Fact]
    public async Task Cancel_EndsAQueuedItem_AndIsANoOpAfterwards()
    {
        var created = await Distributor.DistributeAsync(Request(), CancellationToken.None);
        var jobId = new JobId(created.WorkItemId!);

        (await Distributor.CancelJobAsync(jobId, CancellationToken.None)).Should().BeTrue();
        (await Distributor.CancelJobAsync(jobId, CancellationToken.None)).Should().BeTrue("an item that already ended counts as cancelled");
        (await Distributor.GetJobStatusAsync(jobId, CancellationToken.None)).Should().Be(JobDistributionStatus.Cancelled);
        (await StatusAsync(Guid.Parse(created.WorkItemId!))).Should().Be(WorkItemStatus.Cancelled);
    }

    [Fact]
    public async Task UnknownOrMalformedJobs_AreUnknown_AndCannotBeCancelled()
    {
        (await Distributor.CancelJobAsync(new JobId(Guid.NewGuid().ToString()), CancellationToken.None)).Should().BeFalse();
        (await Distributor.CancelJobAsync(new JobId("not-a-guid"), CancellationToken.None)).Should().BeFalse();
        (await Distributor.GetJobStatusAsync(new JobId(Guid.NewGuid().ToString()), CancellationToken.None)).Should().Be(JobDistributionStatus.Unknown);
        (await Distributor.GetJobStatusAsync(new JobId("not-a-guid"), CancellationToken.None)).Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task DedupQueries_MatchTheEndpoints()
    {
        var issue = $"dist-dedup-{Guid.NewGuid():N}";
        await Distributor.DistributeAsync(Request(issue, taskType: WorkItemTaskType.Decomposition), CancellationToken.None);
        var client = Operator();

        var isDistributed = await Distributor.IsIssueDistributedAsync(issue, "prov-dist", CancellationToken.None);
        var endpointIsDistributed = await client.GetFromJsonAsync<IsDistributedBody>(
            $"/api/work-items/is-distributed?issueIdentifier={issue}&issueProviderConfigId=prov-dist", PipelineJsonOptions.Default);
        // Through the store: other tests leave rows whose IDs the distributor's value types reject.
        var identifiers = await factory.Services.GetRequiredService<WorkItemStore>().GetActiveIdentifiersAsync(CancellationToken.None);
        var endpointIdentifiers = await client.GetFromJsonAsync<List<IdentifierBody>>("/api/work-items/active-identifiers", PipelineJsonOptions.Default);
        var decompositions = await Distributor.GetActiveDecompositionCountAsync(CancellationToken.None);
        var endpointDecompositions = await client.GetFromJsonAsync<CountBody>("/api/work-items/active-decomposition-count", PipelineJsonOptions.Default);

        isDistributed.Should().BeTrue();
        endpointIsDistributed!.IsDistributed.Should().Be(isDistributed);
        identifiers.Should().Contain((issue, "prov-dist"));
        identifiers.Should().BeEquivalentTo(endpointIdentifiers!.Select(p => (p.IssueIdentifier, p.IssueProviderConfigId)));
        decompositions.Should().BeGreaterThan(0);
        endpointDecompositions!.Count.Should().Be(decompositions);
    }

    private sealed record IsDistributedBody(bool IsDistributed);

    private sealed record IdentifierBody(string IssueIdentifier, string IssueProviderConfigId);

    private sealed record CountBody(int Count);
}
