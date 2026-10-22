using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// Spec 053 task 4.6, DD10: during a rolling deploy a previous-release API replica stores a current-release request
/// without an agent selector or route. Before the dispatcher dispatches such a Pending item, <see cref="RolloutRouting"/>
/// routes it (source <c>dispatch</c>) and writes the selector and the payload; a refusal fails the item with the reason
/// and answers <c>no_route</c>. It never answers 500, which would abort the Scheduler's dispatch cycle.
/// </summary>
public sealed class RolloutRoutingTests
{
    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());

    private RolloutRouting Rollout(WorkItemRouter? router = null) => new(
        _db,
        router ?? AgentRoutingTestSupport.Router(_db),
        new WorkItemStatusTransitionService(
            new WorkItemTransitionService(_db, NullLogger<WorkItemTransitionService>.Instance),
            new Mock<IRunLifecycleManager>().Object, NullLogger<WorkItemStatusTransitionService>.Instance, _db));

    private Guid Seed(string selector = "", WorkItemStatus status = WorkItemStatus.Pending, string? payload = null)
    {
        using var db = _db.CreateDbContext();
        var id = Guid.NewGuid();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"rollout-{id:N}",
            IssueProviderConfigId = "ip-1",
            Status = status,
            AgentSelector = selector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = payload ?? JsonSerializer.Serialize(new JobDistributionRequest
            {
                IssueIdentifier = new IssueIdentifier($"rollout-{id:N}"),
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "repo-1",
                InitiatedBy = "test",
                TaskType = WorkItemTaskType.Implementation,
                TimeoutSeconds = 3600,
                RunId = id.ToString(),
                PayloadSchemaVersion = 1,
            }, PipelineJsonOptions.Default),
        });
        db.SaveChanges();
        return id;
    }

    private WorkItemEntity Read(Guid id)
    {
        using var db = _db.CreateDbContext();
        return db.WorkItems.AsNoTracking().Single(w => w.Id == id);
    }

    [Fact]
    public async Task ASelectorLessItem_IsRouted_AndItsSelectorAndPayloadAreWritten()
    {
        AgentRoutingTestSupport.SeedRouting(_db, AgentRoutingTestData.Routing(model: "opus", effort: "high"));
        var id = Seed();

        var (selector, early) = await Rollout().RouteAsync(id, CancellationToken.None);

        early.Should().BeNull("the dispatcher goes on with the routed selector");
        selector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        var row = Read(id);
        row.AgentSelector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        var payload = JsonSerializer.Deserialize<JobDistributionRequest>(row.Payload!, PipelineJsonOptions.Default)!;
        payload.ResolvedProfileId.Should().Be("profile-kiro");
        payload.AgentSelector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        payload.Route.Should().Be(new AgentRouteSnapshot(
            "profile-kiro", "profile-kiro", JobTemplateProviderType.Kiro, "opus", "high", AgentRouteSource.Dispatch));
        row.Status.Should().Be(WorkItemStatus.Pending);
    }

    [Fact]
    public async Task ASelectorLessItemWithoutARoute_IsFailedWithTheReason_AndAnswersNoRoute()
    {
        var id = Seed();

        var (selector, early) = await Rollout().RouteAsync(id, CancellationToken.None);

        selector.Should().BeNull();
        early.Should().BeOfType<Ok<DispatchPendingResponse>>()
            .Which.Value.Should().Be(new DispatchPendingResponse(false, "no_route"));
        var row = Read(id);
        row.Status.Should().Be(WorkItemStatus.Failed);
        row.ErrorMessage.Should().Be(AgentRouteRefusalReason.NoRouteText);
        row.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
    }

    [Fact]
    public async Task AnItemWithASelector_IsNotRoutedAgain()
    {
        AgentRoutingTestSupport.SeedRouting(_db);
        var id = Seed(selector: "claude,dotnet");
        var before = Read(id).Payload;

        var (selector, early) = await Rollout().RouteAsync(id, CancellationToken.None);

        (selector, early).Should().Be(("claude,dotnet", (IResult?)null));
        Read(id).Payload.Should().Be(before);
    }

    [Fact]
    public async Task AnItemThatIsNoLongerPending_AnswersNotPending()
    {
        var id = Seed(status: WorkItemStatus.Dispatched);

        var (_, early) = await Rollout().RouteAsync(id, CancellationToken.None);

        early.Should().BeOfType<Ok<DispatchPendingResponse>>()
            .Which.Value.Should().Be(new DispatchPendingResponse(false, "not_pending"));
    }

    [Fact]
    public async Task AnItemWithoutARoutablePayload_IsLeftForTheNoTemplateDeferral()
    {
        var id = Seed(payload: "{}");

        var (selector, early) = await Rollout().RouteAsync(id, CancellationToken.None);

        (selector, early).Should().Be(("", (IResult?)null));
        Read(id).Status.Should().Be(WorkItemStatus.Pending);
    }

    [Fact]
    public async Task AnUnexpectedError_Answers503_NeverA500()
    {
        // V9: any status but 200 and 503 aborts the Scheduler's whole dispatch cycle.
        var id = Seed();
        var profiles = new Mock<IAgentProfileStore>();
        profiles.Setup(p => p.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("DB timeout"));
        var projects = new Mock<IProjectStore>();
        projects.Setup(p => p.LoadAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var router = new WorkItemRouter(
            profiles.Object, new AgentRoutingStore(_db), projects.Object,
            AgentRoutingTestData.JobTemplates(), NullLogger<WorkItemRouter>.Instance);

        var (_, early) = await Rollout(router).RouteAsync(id, CancellationToken.None);

        early.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Read(id).Status.Should().Be(WorkItemStatus.Pending);
    }
}
