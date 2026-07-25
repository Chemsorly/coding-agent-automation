using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="OrchestratorRunService"/>.
/// </summary>
public class OrchestratorRunServiceTests
{
    private static OrchestratorRunService CreateService(int bufferCapacity = 100) =>
        new(new Mock<ILogger>().Object, bufferCapacity);

    private static PipelineRun CreateRun(string runId = "run-1", string issueId = "issue-1") => new()
    {
        RunId = runId,
        IssueIdentifier = issueId,
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow
    };

    [Fact]
    public void HasActiveRuns_NoRuns_ReturnsFalse()
    {
        var service = CreateService();
        service.HasActiveRuns.Should().BeFalse();
    }

    [Fact]
    public void HasActiveRuns_WithRun_ReturnsTrue()
    {
        var service = CreateService();
        service.AddRun(CreateRun());
        service.HasActiveRuns.Should().BeTrue();
    }

    [Fact]
    public void ActiveRunCount_ReflectsAddedRuns()
    {
        var service = CreateService();
        service.ActiveRunCount.Should().Be(0);

        service.AddRun(CreateRun("run-1"));
        service.ActiveRunCount.Should().Be(1);

        service.AddRun(CreateRun("run-2"));
        service.ActiveRunCount.Should().Be(2);
    }

    [Fact]
    public void AddRun_DuplicateRunId_DoesNotOverwrite()
    {
        var service = CreateService();
        var run1 = CreateRun("run-1", "issue-A");
        var run2 = CreateRun("run-1", "issue-B");

        service.AddRun(run1);
        service.AddRun(run2); // duplicate — should be ignored

        service.ActiveRunCount.Should().Be(1);
        service.GetRun("run-1")!.IssueIdentifier.Value.Should().Be("issue-A");
    }

    [Fact]
    public void GetRun_ExistingRun_ReturnsRun()
    {
        var service = CreateService();
        var run = CreateRun("run-42");
        service.AddRun(run);

        service.GetRun("run-42").Should().BeSameAs(run);
    }

    [Fact]
    public void GetRun_NonExistentRun_ReturnsNull()
    {
        var service = CreateService();
        service.GetRun("nonexistent").Should().BeNull();
    }

    [Fact]
    public void RemoveRun_ExistingRun_RemovesAndReturns()
    {
        var service = CreateService();
        var run = CreateRun("run-1");
        service.AddRun(run);

        var removed = service.RemoveRun("run-1");

        removed.Should().BeSameAs(run);
        service.HasActiveRuns.Should().BeFalse();
        service.ActiveRunCount.Should().Be(0);
    }

    [Fact]
    public void RemoveRun_NonExistentRun_ReturnsNull()
    {
        var service = CreateService();
        service.RemoveRun("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetActiveRuns_ReturnsAllRuns()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1"));
        service.AddRun(CreateRun("run-2"));
        service.AddRun(CreateRun("run-3"));

        var runs = service.GetActiveRuns();
        runs.Should().HaveCount(3);
    }

    [Fact]
    public void IsIssueBeingProcessed_ActiveIssue_ReturnsTrue()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1", "issue-42"));

        service.IsIssueBeingProcessed("issue-42", "ip").Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessed_InactiveIssue_ReturnsFalse()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1", "issue-42"));

        service.IsIssueBeingProcessed("issue-99", "ip").Should().BeFalse();
    }

    [Fact]
    public void IsIssueBeingProcessed_AfterRemoval_ReturnsFalse()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1", "issue-42"));
        service.RemoveRun("run-1");

        service.IsIssueBeingProcessed("issue-42", "ip").Should().BeFalse();
    }

    [Fact]
    public void GetOutputBuffer_CreatesBufferOnAdd()
    {
        var service = CreateService(bufferCapacity: 500);
        service.AddRun(CreateRun("run-1"));

        var buffer = service.GetOutputBuffer("run-1");
        buffer.Should().NotBeNull();
        buffer.Capacity.Should().Be(500);
    }

    [Fact]
    public void GetOutputBuffer_NonExistentRun_CreatesNewBuffer()
    {
        var service = CreateService(bufferCapacity: 200);
        var buffer = service.GetOutputBuffer("new-run");
        buffer.Should().NotBeNull();
        buffer.Capacity.Should().Be(200);
    }

    [Fact]
    public void GetOutputBuffer_SameRunId_ReturnsSameInstance()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1"));

        var buffer1 = service.GetOutputBuffer("run-1");
        var buffer2 = service.GetOutputBuffer("run-1");

        buffer1.Should().BeSameAs(buffer2);
    }

    [Fact]
    public void RemoveRun_CleansUpOutputBuffer()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1"));
        var originalBuffer = service.GetOutputBuffer("run-1");
        originalBuffer.Add("some output");

        service.RemoveRun("run-1");

        // After removal, GetOutputBuffer creates a new empty buffer
        var newBuffer = service.GetOutputBuffer("run-1");
        newBuffer.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new OrchestratorRunService(null!, 100);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_InvalidBufferCapacity_Throws()
    {
        var act = () => new OrchestratorRunService(new Mock<ILogger>().Object, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddRun_NullRun_Throws()
    {
        var service = CreateService();
        var act = () => service.AddRun(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetRun_NullRunId_Throws()
    {
        var service = CreateService();
        var act = () => service.GetRun(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RemoveRun_NullRunId_Throws()
    {
        var service = CreateService();
        var act = () => service.RemoveRun(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IsIssueBeingProcessed_NullIdentifier_Throws()
    {
        var service = CreateService();
        var act = () => service.IsIssueBeingProcessed(null!, "provider-1");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetOutputBuffer_NullRunId_Throws()
    {
        var service = CreateService();
        var act = () => service.GetOutputBuffer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #region Concurrency Tests

    [Fact]
    public async Task AddRun_ConcurrentFromMultipleThreads_AllRunsTrackedWithoutDataLoss()
    {
        var service = CreateService();
        const int threadCount = 50;

        var tasks = Enumerable.Range(0, threadCount)
            .Select(i => Task.Run(() => service.AddRun(CreateRun($"run-{i}", $"issue-{i}"))))
            .ToArray();

        await Task.WhenAll(tasks);

        service.ActiveRunCount.Should().Be(threadCount);
        for (var i = 0; i < threadCount; i++)
        {
            service.GetRun($"run-{i}").Should().NotBeNull();
            service.IsIssueBeingProcessed($"issue-{i}", "ip").Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetActiveRuns_ReturnsConsistentSnapshotDuringConcurrentModifications()
    {
        var service = CreateService();

        // Pre-populate with some runs
        for (var i = 0; i < 20; i++)
            service.AddRun(CreateRun($"run-{i}", $"issue-{i}"));

        // Concurrently add and remove runs while taking snapshots
        var snapshots = new List<IReadOnlyList<PipelineRun>>();
        var snapshotLock = new object();

        var addTasks = Enumerable.Range(20, 30)
            .Select(i => Task.Run(() => service.AddRun(CreateRun($"run-{i}", $"issue-{i}"))))
            .ToArray();

        var removeTasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => service.RemoveRun($"run-{i}")))
            .ToArray();

        var snapshotTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                var snapshot = service.GetActiveRuns();
                lock (snapshotLock)
                {
                    snapshots.Add(snapshot);
                }
            }))
            .ToArray();

        await Task.WhenAll(addTasks.Concat(removeTasks).Concat(snapshotTasks));

        // Each snapshot should be a valid consistent list (no nulls, no duplicates)
        foreach (var snapshot in snapshots)
        {
            snapshot.Should().NotBeNull();
            snapshot.Should().OnlyContain(r => r != null);
            snapshot.Select(r => r.RunId).Should().OnlyHaveUniqueItems();
        }
    }

    #endregion

    #region WasRecentlyCompleted / MarkRecentlyCompleted

    [Fact]
    public void WasRecentlyCompleted_ReturnsFalseForUnknownIssue()
    {
        var service = CreateService();

        service.WasRecentlyCompleted("unknown-issue", "provider-1").Should().BeFalse();
    }

    [Fact]
    public void WasRecentlyCompleted_ReturnsTrueWithinGracePeriod()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new OrchestratorRunService(new Mock<ILogger>().Object, 100, fakeTime);

        service.MarkRecentlyCompleted("issue-42", "provider-1");

        // Immediately after marking, should return true
        service.WasRecentlyCompleted("issue-42", "provider-1").Should().BeTrue();

        // Advance 60s — still within the 120s TTL
        fakeTime.Advance(TimeSpan.FromSeconds(60));
        service.WasRecentlyCompleted("issue-42", "provider-1").Should().BeTrue();
    }

    [Fact]
    public void WasRecentlyCompleted_ReturnsFalseAfterGracePeriodExpires()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new OrchestratorRunService(new Mock<ILogger>().Object, 100, fakeTime);

        service.MarkRecentlyCompleted("issue-42", "provider-1");

        // Advance past the 120s TTL
        fakeTime.Advance(TimeSpan.FromSeconds(121));
        service.WasRecentlyCompleted("issue-42", "provider-1").Should().BeFalse();
    }

    [Fact]
    public void WasRecentlyCompleted_DifferentProviderIds_AreIndependent()
    {
        var service = CreateService();

        service.MarkRecentlyCompleted("issue-1", "provider-A");

        service.WasRecentlyCompleted("issue-1", "provider-A").Should().BeTrue();
        service.WasRecentlyCompleted("issue-1", "provider-B").Should().BeFalse();
    }

    [Fact]
    public void MarkRecentlyCompleted_UpdatesTimestamp_OnSubsequentCalls()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new OrchestratorRunService(new Mock<ILogger>().Object, 100, fakeTime);

        service.MarkRecentlyCompleted("issue-1", "provider-1");

        // Advance 100s (still within TTL)
        fakeTime.Advance(TimeSpan.FromSeconds(100));
        service.WasRecentlyCompleted("issue-1", "provider-1").Should().BeTrue();

        // Mark again — this resets the timestamp
        service.MarkRecentlyCompleted("issue-1", "provider-1");

        // Advance another 100s (would be 200s from first mark, but only 100s from second)
        fakeTime.Advance(TimeSpan.FromSeconds(100));
        service.WasRecentlyCompleted("issue-1", "provider-1").Should().BeTrue();

        // Advance past TTL from second mark
        fakeTime.Advance(TimeSpan.FromSeconds(25));
        service.WasRecentlyCompleted("issue-1", "provider-1").Should().BeFalse();
    }

    #endregion
}
