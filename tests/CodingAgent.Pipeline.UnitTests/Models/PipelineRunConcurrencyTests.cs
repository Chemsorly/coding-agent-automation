using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class PipelineRunConcurrencyTests
{
    [Fact]
    public void StartedAt_BackingField_UsesInterlockedRead_NotPlainAutoProperty()
    {
        // Walk up from the test assembly to the repo root (same approach as SonarGatePreservationTests).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("*.sln").Length == 0)
            dir = dir.Parent;
        var repoRoot = dir?.FullName ?? throw new InvalidOperationException(
            $"Could not locate repo root (*.sln) starting from {AppContext.BaseDirectory}");

        var sourceFile = Path.Combine(repoRoot, "src", "CodingAgent.Pipeline", "Models", "PipelineRun.cs");
        var source = File.ReadAllText(sourceFile);

        // Must contain Interlocked.Read on the backing field
        source.Should().Contain("Interlocked.Read(ref _startedAtTicks",
            "StartedAt getter must use Interlocked.Read on its backing field, not a plain auto-property");

        // Must NOT be a plain auto-property pattern
        source.Should().NotContain("public DateTime StartedAt { get; internal set; }",
            "StartedAt must not be a plain auto-property — it requires Interlocked semantics matching StartedAtOffset");
    }

    [Fact]
    // TODO: This concurrency test is probabilistic — on x64 with modern JIT, long reads/writes are naturally
    //       aligned and atomic, so this test would likely pass even without Interlocked. It validates safety
    //       but cannot demonstrate failure when the fix is reverted (weak regression guard).
    public void StartedAtOffset_ConcurrentReadWrite_NeverProducesTornRead()
    {
        // Arrange: create a PipelineRun and define a set of known-good timestamps
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "concurrency-test",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Concurrency test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        });

        var writtenValues = new DateTimeOffset[1000];
        for (int i = 0; i < writtenValues.Length; i++)
        {
            writtenValues[i] = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i + 1);
        }

        var validTicks = new HashSet<long>(writtenValues.Select(v => v.UtcTicks))
        {
            run.StartedAtOffset.UtcTicks // initial value is also valid
        };

        var tornReadDetected = false;
        var impossibleDateDetected = false;
        var writerDone = false;

        // Act: spin up concurrent writer and readers
        var writerThread = new Thread(() =>
        {
            for (int i = 0; i < writtenValues.Length; i++)
            {
                run.ResetStartedAt(writtenValues[i]);
            }
            Volatile.Write(ref writerDone, true);
        });

        var readerThread1 = new Thread(() =>
        {
            while (!Volatile.Read(ref writerDone))
            {
                var read = run.StartedAtOffset;

                // Torn reads on x64 produce ticks == 0 or impossible dates
                if (read.Ticks == 0)
                {
                    Volatile.Write(ref impossibleDateDetected, true);
                    break;
                }

                if (!validTicks.Contains(read.UtcTicks))
                {
                    Volatile.Write(ref tornReadDetected, true);
                    break;
                }
            }
        });

        var readerThread2 = new Thread(() =>
        {
            while (!Volatile.Read(ref writerDone))
            {
                var read = run.StartedAtOffset;

                if (read.Ticks == 0)
                {
                    Volatile.Write(ref impossibleDateDetected, true);
                    break;
                }

                if (!validTicks.Contains(read.UtcTicks))
                {
                    Volatile.Write(ref tornReadDetected, true);
                    break;
                }
            }
        });

        writerThread.Start();
        readerThread1.Start();
        readerThread2.Start();

        var writerJoined = writerThread.Join(TimeSpan.FromSeconds(10));
        var reader1Joined = readerThread1.Join(TimeSpan.FromSeconds(10));
        var reader2Joined = readerThread2.Join(TimeSpan.FromSeconds(10));

        writerJoined.Should().BeTrue("writer thread must terminate within the timeout");
        reader1Joined.Should().BeTrue("reader thread 1 must terminate within the timeout");
        reader2Joined.Should().BeTrue("reader thread 2 must terminate within the timeout");

        // Assert
        tornReadDetected.Should().BeFalse("every read should match a value that was actually written");
        impossibleDateDetected.Should().BeFalse("no read should produce a zero-ticks (torn) DateTimeOffset");
    }

    [Fact]
    public void MarkCompleted_ConcurrentCalls_BothFieldsAreConsistentAfterRace()
    {
        // Arrange: two threads will race into MarkCompleted(DateTimeOffset) simultaneously with
        // DISTINCT, pre-assigned timestamps. Using distinct timestamps guarantees that a torn write
        // (CompletedAt from T1, CompletedAtOffset from T2) produces a detectable field mismatch
        // regardless of clock resolution. If we used the parameterless MarkCompleted() instead,
        // both threads could capture the same UtcNow value on coarse-resolution clocks (e.g.
        // the default ~15 ms Windows timer), causing a torn write to pass the assertion silently.
        var t1Timestamp = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var t2Timestamp = new DateTimeOffset(2026, 6, 1, 12, 0, 1, TimeSpan.Zero); // 1 s later — guaranteed distinct

        // We run multiple iterations to increase the probability of catching an interleave.
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = $"concurrent-complete-{iteration}",
                IssueIdentifier = "org/repo#1",
                IssueTitle = "Concurrent MarkCompleted test",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp"
            });

            using var barrier = new Barrier(2);

            var t1 = new Thread(() =>
            {
                barrier.SignalAndWait();
                run.MarkCompleted(t1Timestamp);
            });
            var t2 = new Thread(() =>
            {
                barrier.SignalAndWait();
                run.MarkCompleted(t2Timestamp);
            });

            t1.Start();
            t2.Start();
            var t1Joined = t1.Join(TimeSpan.FromSeconds(10));
            var t2Joined = t2.Join(TimeSpan.FromSeconds(10));

            t1Joined.Should().BeTrue("thread 1 must terminate within the timeout (iteration {0})", iteration);
            t2Joined.Should().BeTrue("thread 2 must terminate within the timeout (iteration {0})", iteration);

            // After both threads have completed, exactly one timestamp must have won and both fields
            // must agree on the same instant. With distinct timestamps, a torn write (CompletedAt
            // from one thread and CompletedAtOffset from the other) produces a disagreement here.
            run.CompletedAtOffset.HasValue.Should().BeTrue(
                "at least one MarkCompleted call must have written a value (iteration {0})", iteration);
            run.CompletedAt.Should().Be(run.CompletedAtOffset!.Value.UtcDateTime,
                "CompletedAt and CompletedAtOffset must represent the same instant — " +
                "a torn write would leave them disagreeing (iteration {0})", iteration);
        }
    }

    [Fact]
    public void StartedAtOffset_AfterResetStartedAt_MatchesExpectedValue()
    {
        // Verify basic correctness of the Interlocked-backed property
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        });

        var timestamps = Enumerable.Range(1, 100)
            .Select(i => new DateTimeOffset(2026, 6, 15, i % 24, i % 60, 0, TimeSpan.Zero))
            .ToList();

        foreach (var ts in timestamps)
        {
            run.ResetStartedAt(ts);
            run.StartedAtOffset.Should().Be(ts);
        }
    }

    [Fact]
    // TODO: This concurrency test is probabilistic — on x64 with modern JIT, long reads/writes are naturally
    //       aligned and atomic, so this test would likely pass even without Interlocked. It validates safety
    //       but cannot demonstrate failure when the fix is reverted (weak regression guard).
    public void StartedAt_ConcurrentReadWrite_NeverProducesTornRead()
    {
        // Arrange: create a PipelineRun and define a set of known-good timestamps
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "concurrency-test-started-at",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Concurrency test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        });

        var writtenValues = new DateTimeOffset[1000];
        for (int i = 0; i < writtenValues.Length; i++)
        {
            writtenValues[i] = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i + 1);
        }

#pragma warning disable CS0618 // StartedAt is Obsolete but tested deliberately here
        var validTicks = new HashSet<long>(writtenValues.Select(v => v.UtcTicks))
        {
            run.StartedAt.Ticks // initial value (set by CreateCore to UtcNow — always non-zero)
        };

        var tornReadDetected = false;
        var writerDone = false;

        // Act: spin up concurrent writer and readers
        var writerThread = new Thread(() =>
        {
            for (int i = 0; i < writtenValues.Length; i++)
            {
                run.ResetStartedAt(writtenValues[i]);
            }
            Volatile.Write(ref writerDone, true);
        });

        var readerThread1 = new Thread(() =>
        {
            while (!Volatile.Read(ref writerDone))
            {
                var read = run.StartedAt;

                // A torn read produces ticks not matching any value that was actually written
                // (including the initial construction value). Zero-ticks is also caught here
                // because 0L is not in validTicks.
                if (!validTicks.Contains(read.Ticks))
                {
                    Volatile.Write(ref tornReadDetected, true);
                    break;
                }
            }
        });

        var readerThread2 = new Thread(() =>
        {
            while (!Volatile.Read(ref writerDone))
            {
                var read = run.StartedAt;

                if (!validTicks.Contains(read.Ticks))
                {
                    Volatile.Write(ref tornReadDetected, true);
                    break;
                }
            }
        });

        writerThread.Start();
        readerThread1.Start();
        readerThread2.Start();

        var writerJoined = writerThread.Join(TimeSpan.FromSeconds(10));
        var reader1Joined = readerThread1.Join(TimeSpan.FromSeconds(10));
        var reader2Joined = readerThread2.Join(TimeSpan.FromSeconds(10));
#pragma warning restore CS0618

        writerJoined.Should().BeTrue("writer thread must terminate within the timeout");
        reader1Joined.Should().BeTrue("reader thread 1 must terminate within the timeout");
        reader2Joined.Should().BeTrue("reader thread 2 must terminate within the timeout");

        // Assert
        tornReadDetected.Should().BeFalse("every StartedAt read should match a value that was actually written");
    }
}
