using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests for RunMetrics sub-state group and its delegation from PipelineRun (MAINT-13).
/// </summary>
public class RunMetricsTests
{
    [Fact]
    public void DefaultValues_AreZeroOrNull()
    {
        var metrics = new RunMetrics();

        metrics.RetryCount.Should().Be(0);
        metrics.InfrastructureRetryCount.Should().Be(0);
        metrics.FilesChangedCount.Should().Be(0);
        metrics.LinesAdded.Should().Be(0);
        metrics.LinesRemoved.Should().Be(0);
        metrics.TotalTokens.Should().Be(0L);
        metrics.TotalCost.Should().BeNull();
        metrics.CacheReadTokens.Should().Be(0L);
        metrics.CacheWriteTokens.Should().Be(0L);
    }

    [Fact]
    public void DelegatingProperties_ReadFromMetrics()
    {
        var run = CreateRun();
        run.Metrics.RetryCount = 3;
        run.Metrics.InfrastructureRetryCount = 1;
        run.Metrics.FilesChangedCount = 5;
        run.Metrics.LinesAdded = 100;
        run.Metrics.LinesRemoved = 20;
        run.Metrics.TotalTokens = 50000L;
        run.Metrics.TotalCost = 1.23m;
        run.Metrics.CacheReadTokens = 500L;
        run.Metrics.CacheWriteTokens = 200L;

        run.RetryCount.Should().Be(3);
        run.InfrastructureRetryCount.Should().Be(1);
        run.FilesChangedCount.Should().Be(5);
        run.LinesAdded.Should().Be(100);
        run.LinesRemoved.Should().Be(20);
        run.TotalTokens.Should().Be(50000L);
        run.TotalCost.Should().Be(1.23m);
        run.CacheReadTokens.Should().Be(500L);
        run.CacheWriteTokens.Should().Be(200L);
    }

    [Fact]
    public void DelegatingProperties_WriteToMetrics()
    {
        var run = CreateRun();
        run.RetryCount = 2;
        run.InfrastructureRetryCount = 4;
        run.FilesChangedCount = 7;
        run.LinesAdded = 200;
        run.LinesRemoved = 50;
        run.TotalTokens = 99000L;
        run.TotalCost = 4.56m;
        run.CacheReadTokens = 750L;
        run.CacheWriteTokens = 300L;

        run.Metrics.RetryCount.Should().Be(2);
        run.Metrics.InfrastructureRetryCount.Should().Be(4);
        run.Metrics.FilesChangedCount.Should().Be(7);
        run.Metrics.LinesAdded.Should().Be(200);
        run.Metrics.LinesRemoved.Should().Be(50);
        run.Metrics.TotalTokens.Should().Be(99000L);
        run.Metrics.TotalCost.Should().Be(4.56m);
        run.Metrics.CacheReadTokens.Should().Be(750L);
        run.Metrics.CacheWriteTokens.Should().Be(300L);
    }

    [Fact]
    public void ToSummary_MapsMetricsThroughDelegation()
    {
        var run = CreateRun();
        run.RetryCount = 2;
        run.TotalTokens = 75000L;
        run.TotalCost = 3.21m;
        run.CacheReadTokens = 1000L;
        run.CacheWriteTokens = 400L;

        var summary = run.ToSummary();

        summary.RetryCount.Should().Be(2);
        summary.TotalTokens.Should().Be(75000L);
        summary.TotalCost.Should().Be(3.21m);
        summary.CacheReadTokens.Should().Be(1000L);
        summary.CacheWriteTokens.Should().Be(400L);
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = "r1",
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow
    };
}
