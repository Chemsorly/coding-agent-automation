using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodingAgent.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Spec 056 DD11 step 1, req 6.4 (task 6.5): the HTTP status and the hub completion of one run race on two API replicas.
/// The row version makes exactly one compare-and-swap win, so the credential is paused and the work queued once. The API
/// tests use the in-memory provider, which has no row version; this needs PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProviderFailureCompareAndSwapTests : IClassFixture<WorkItemPostgresFixture>
{
    private const string Job = "agent-job-1";
    private const int Rounds = 5;
    private readonly WorkItemPostgresFixture _fixture;

    public ProviderFailureCompareAndSwapTests(WorkItemPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // The handler's predicate: only the agent that runs the item, only while it runs.
    private static bool RunBy(WorkItemEntity w, string reporter) =>
        w.Status is WorkItemStatus.Dispatched or WorkItemStatus.Running
        && (string.Equals(w.K8sJobName, reporter, StringComparison.Ordinal)
            || string.Equals(w.AssignedAgentId, reporter, StringComparison.Ordinal));

    [Fact]
    public async Task WhenFourReportsOfOneRunRaceOnTwoReplicas_ExactlyOneShouldWin()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");

        for (var round = 1; round <= Rounds; round++)
        {
            await _fixture.ResetAsync();
            var id = Guid.NewGuid();
            await using (var db = _fixture.CreateDbContext())
            {
                db.WorkItems.Add(new WorkItemEntity
                {
                    Id = id,
                    IssueIdentifier = "org/repo#1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Running,
                    TaskType = WorkItemTaskType.Implementation,
                    K8sJobName = Job,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var replicas = Enumerable.Range(0, 2)
                .Select(_ => new WorkItemTransitionService(_fixture.CreateReplica().Factory, NullLogger<WorkItemTransitionService>.Instance))
                .ToArray();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
            {
                await start.Task;
                return await replicas[i % 2].TransitionIfAsync(id, w => RunBy(w, Job), WorkItemStatus.Failed,
                    w => w.FailureReason = FailureReason.ProviderUnavailable);
            })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(attempts);

            results.Count(won => won).Should().Be(1, $"round {round}: exactly one report ends the item");
            await using var check = _fixture.CreateDbContext();
            var stored = await check.WorkItems.AsNoTracking().SingleAsync(w => w.Id == id);
            stored.Status.Should().Be(WorkItemStatus.Failed);
            stored.FailureReason.Should().Be(FailureReason.ProviderUnavailable);
        }
    }

    [Fact]
    public async Task WhenAnotherAgentReports_TheCompareAndSwapShouldFail()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull();
        await _fixture.ResetAsync();
        var id = Guid.NewGuid();
        await using (var db = _fixture.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = id, IssueIdentifier = "org/repo#2", IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running, TaskType = WorkItemTaskType.Implementation,
                K8sJobName = Job, AgentSelector = "kiro", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var transitions = new WorkItemTransitionService(_fixture.CreateReplica().Factory, NullLogger<WorkItemTransitionService>.Instance);
        var won = await transitions.TransitionIfAsync(id, w => RunBy(w, "other-job"), WorkItemStatus.Failed);

        won.Should().BeFalse();
    }
}
