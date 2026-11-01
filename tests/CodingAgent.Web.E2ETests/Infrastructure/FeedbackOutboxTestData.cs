using System.Text.Json;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Test helpers for seeding and inspecting the FeedbackCommentOutbox table,
/// and for triggering relay sweeps deterministically.
/// </summary>
internal static class FeedbackOutboxTestData
{
    /// <summary>
    /// Removes every row from db.FeedbackCommentOutbox.
    /// <see cref="E2EWebApplicationFactory.ResetAll"/> does not clear this table,
    /// so rows from earlier tests survive and can be picked up by later sweeps.
    /// Call this at the start of every test that sweeps the relay.
    /// </summary>
    public static Task ClearAsync(E2EFixture fixture)
    {
        using var db = fixture.DbContextFactory.CreateDbContext();
        db.FeedbackCommentOutbox.RemoveRange(db.FeedbackCommentOutbox);
        db.SaveChanges();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues a feedback comment outbox row via <see cref="IFeedbackCommentOutbox.EnqueueAsync"/>
    /// with IssueProviderConfigId "issue-e2e", RepoProviderConfigId "repo-e2e", no pull request.
    /// Returns the row's Id, read back by RunId.
    /// </summary>
    public static async Task<Guid> EnqueueAsync(
        E2EFixture fixture,
        string runId,
        string issueIdentifier,
        string description)
    {
        var outbox = fixture.ApiServices.GetRequiredService<IFeedbackCommentOutbox>();
        var entry = new FeedbackCommentOutboxEntry
        {
            RunId = runId,
            IssueProviderConfigId = "issue-e2e",
            IssueIdentifier = issueIdentifier,
            RepoProviderConfigId = "repo-e2e",
            FeedbackJson = JsonSerializer.Serialize(
                new IssueFeedback { Category = "vague", Description = description },
                PipelineJsonOptions.Default)
        };
        await outbox.EnqueueAsync(entry, CancellationToken.None);
        var row = await ReadAsync(fixture, runId);
        return row.Id;
    }

    /// <summary>
    /// Sets CreatedAt = UtcNow - age on the row, through a tracked entity.
    /// Does not change Status.
    /// </summary>
    public static async Task BackdateAsync(E2EFixture fixture, string runId, TimeSpan age)
    {
        await using var db = fixture.DbContextFactory.CreateDbContext();
        var entity = await db.FeedbackCommentOutbox.SingleAsync(f => f.RunId == runId);
        entity.CreatedAt = DateTimeOffset.UtcNow - age;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Reads the row with AsNoTracking.
    /// </summary>
    public static async Task<FeedbackCommentOutboxEntity> ReadAsync(E2EFixture fixture, string runId)
    {
        await using var db = fixture.DbContextFactory.CreateDbContext();
        return await db.FeedbackCommentOutbox
            .AsNoTracking()
            .SingleAsync(f => f.RunId == runId);
    }

    /// <summary>
    /// Runs a single relay sweep by calling
    /// <see cref="FeedbackCommentRelayService.SweepOnceForTestAsync"/>.
    /// </summary>
    public static Task SweepAsync(E2EFixture fixture) =>
        fixture.SchedulerFactory.Services
            .GetRequiredService<FeedbackCommentRelayService>()
            .SweepOnceForTestAsync(CancellationToken.None);
}
