using System.Text.Json;
using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline;

namespace CodingAgent.Api.Loop;

/// <summary>A loop status the Scheduler leader pushed, and when it arrived.</summary>
internal sealed record LoopStatusReport(LoopStatusDto Status, DateTimeOffset ReportedAt);

/// <summary>
/// Keeps the last loop status the Scheduler leader pushed (spec 051 D11): in Redis when the API runs
/// with it, so every replica serves the same status, otherwise in memory. The leader pushes on every
/// change and at least every <see cref="RefreshInterval"/>; a report older than <see cref="MaxAge"/>
/// is treated as missing, because the Scheduler has stopped reporting.
/// </summary>
internal sealed class LoopStatusStore(IRedisStore? redis, TimeProvider time)
{
    /// <summary>How often the leader re-sends an unchanged status.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>The age after which a report no longer counts.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private const string Key = "loop-status";

    private LoopStatusReport? _local;

    public async Task SaveAsync(LoopStatusDto status)
    {
        var report = new LoopStatusReport(status, time.GetUtcNow());
        if (redis is not null)
        {
            await redis.SetAsync(Key, JsonSerializer.Serialize(report, PipelineJsonOptions.Default), MaxAge);
            return;
        }
        Volatile.Write(ref _local, report);
    }

    /// <summary>The last report, or null when there is none younger than <see cref="MaxAge"/>.</summary>
    public async Task<LoopStatusReport?> FindAsync()
    {
        LoopStatusReport? report;
        if (redis is not null)
        {
            var json = await redis.GetAsync(Key);
            report = json is null ? null : JsonSerializer.Deserialize<LoopStatusReport>(json, PipelineJsonOptions.Lenient);
        }
        else
        {
            report = Volatile.Read(ref _local);
        }
        return report is not null && time.GetUtcNow() - report.ReportedAt < MaxAge ? report : null;
    }
}
