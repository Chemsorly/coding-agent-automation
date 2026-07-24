using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class PipelineRunConcurrencyTests
{
    [Fact]
    // TODO: This test only validates StartedAtOffset atomicity in isolation. It does not verify that
    //       StartedAt and StartedAtOffset are consistent with each other after a concurrent ResetStartedAt call.
    //       A test that concurrently reads both StartedAt and StartedAtOffset and asserts they always refer
    //       to the same logical timestamp would validate the compound-atomicity requirement.
    // TODO: This concurrency test is probabilistic — on x64 with modern JIT, long reads/writes are naturally
    //       aligned and atomic, so this test would likely pass even without Interlocked. It validates safety
    //       but cannot demonstrate failure when the fix is reverted (weak regression guard).
    public void StartedAtOffset_ConcurrentReadWrite_NeverProducesTornRead()
    {
        // Arrange: create a PipelineRun and define a set of known-good timestamps
        var run = PipelineRun.Create(
            runId: "concurrency-test",
            issueIdentifier: "org/repo#1",
            issueTitle: "Concurrency test",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");

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

        writerThread.Join(TimeSpan.FromSeconds(10));
        readerThread1.Join(TimeSpan.FromSeconds(10));
        readerThread2.Join(TimeSpan.FromSeconds(10));

        // Assert
        tornReadDetected.Should().BeFalse("every read should match a value that was actually written");
        impossibleDateDetected.Should().BeFalse("no read should produce a zero-ticks (torn) DateTimeOffset");
    }

    [Fact]
    public void StartedAtOffset_AfterResetStartedAt_MatchesExpectedValue()
    {
        // Verify basic correctness of the Interlocked-backed property
        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");

        var timestamps = Enumerable.Range(1, 100)
            .Select(i => new DateTimeOffset(2026, 6, 15, i % 24, i % 60, 0, TimeSpan.Zero))
            .ToList();

        foreach (var ts in timestamps)
        {
            run.ResetStartedAt(ts);
            run.StartedAtOffset.Should().Be(ts);
        }
    }
}
