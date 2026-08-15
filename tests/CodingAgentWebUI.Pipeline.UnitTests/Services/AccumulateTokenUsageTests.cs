using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for the <see cref="PipelineRunExtensions.AccumulateTokenUsage"/> extension method,
/// specifically verifying that cache token fields (<see cref="PipelineRun.CacheReadTokens"/> and
/// <see cref="PipelineRun.CacheWriteTokens"/>) are accumulated correctly.
/// </summary>
public class AccumulateTokenUsageTests
{
    private static PipelineRun CreateRun() => new()
    {
        RunId = "r1",
        IssueIdentifier = "42",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow
    };

    [Fact]
    public void AccumulateTokenUsage_WithCacheTokens_AccumulatesCacheReadAndWriteOnRun()
    {
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 50,
                CacheReadTokens = 500,
                CacheWriteTokens = 200
            }
        };

        run.AccumulateTokenUsage(result);

        run.CacheReadTokens.Should().Be(500);
        run.CacheWriteTokens.Should().Be(200);
    }

    [Fact]
    public void AccumulateTokenUsage_CalledMultipleTimes_SumsCacheTokensCorrectly()
    {
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 50,
                CacheReadTokens = 500,
                CacheWriteTokens = 200
            }
        };

        run.AccumulateTokenUsage(result);
        run.AccumulateTokenUsage(result);

        run.CacheReadTokens.Should().Be(1000);
        run.CacheWriteTokens.Should().Be(400);
    }

    // TODO: These two null-guard tests are near-duplicates — both hit the same `if (result?.Usage is null) return;`
    // guard via slightly different paths (null AgentResult vs AgentResult with null Usage). Consider combining
    // into a single [Theory] with both cases to avoid silent drift if the guard is ever refactored.
    [Fact]
    public void AccumulateTokenUsage_WithNullUsage_LeavesCacheTokensAtZero()
    {
        var run = CreateRun();

        run.AccumulateTokenUsage(null);

        run.CacheReadTokens.Should().Be(0);
        run.CacheWriteTokens.Should().Be(0);
    }

    [Fact]
    public void AccumulateTokenUsage_WithNullResult_LeavesCacheTokensAtZero()
    {
        var run = CreateRun();
        var result = new AgentResult { ExitCode = 0, OutputLines = [], Usage = null };

        run.AccumulateTokenUsage(result);

        run.CacheReadTokens.Should().Be(0);
        run.CacheWriteTokens.Should().Be(0);
    }

    // TODO: This test only asserts zero-in → zero-out, which is trivially satisfied by the default field value.
    // If the accumulation lines for CacheReadTokens/CacheWriteTokens were deleted from PipelineRunExtensions,
    // this test would still pass. It does not detect a regression. Consider replacing with a test that uses
    // non-zero values for one field and zero for the other to confirm independent accumulation.
    [Fact]
    public void AccumulateTokenUsage_WithZeroCacheTokens_LeavesCacheTokensAtZero()
    {
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 50,
                CacheReadTokens = 0,
                CacheWriteTokens = 0
            }
        };

        run.AccumulateTokenUsage(result);

        run.CacheReadTokens.Should().Be(0);
        run.CacheWriteTokens.Should().Be(0);
    }

    [Fact]
    public void AccumulateTokenUsage_WithCacheTokens_AlsoAccumulatesTotalTokens()
    {
        // Verify that adding cache token accumulation did not break the existing TotalTokens accumulation
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 50,
                CacheReadTokens = 500,
                CacheWriteTokens = 200
            }
        };

        run.AccumulateTokenUsage(result);

        run.TotalTokens.Should().Be(150); // InputTokens + OutputTokens (TotalTokens = Input + Output + Reasoning)
        run.CacheReadTokens.Should().Be(500);
        run.CacheWriteTokens.Should().Be(200);
    }
}
