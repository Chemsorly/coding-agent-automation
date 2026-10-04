using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests;

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

        run.AccumulateTokenUsage((AgentResult?)null);

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

    [Fact]
    public void AccumulateTokenUsage_WithUsageDetails_AccumulatesThePhaseBreakdownDetail()
    {
        var run = CreateRun();
        var details = new AgentUsageDetails
        {
            BillingMode = AgentBillingModes.Subscription,
            Turns = 4,
            WebSearchRequests = 1,
            RateLimits = [new AgentRateLimitObservation { Provider = "claude", Window = "five_hour", Status = "allowed", Utilization = 0.2 }]
        };
        var usage = new TokenUsage { InputTokens = 10, OutputTokens = 20, ReasoningTokens = 5, CacheReadTokens = 100, CacheWriteTokens = 7 };

        run.AccumulateTokenUsage(new AgentResult { ExitCode = 0, OutputLines = [], Usage = usage, Cost = 0.5m, UsageDetails = details }, "codegen");
        run.AccumulateTokenUsage(new AgentResult
        {
            ExitCode = 0, OutputLines = [], Usage = usage, Cost = 0.25m,
            UsageDetails = details with
            {
                RateLimits = [new AgentRateLimitObservation { Provider = "claude", Window = "five_hour", Status = "allowed_warning", Utilization = 0.9 }]
            }
        }, "codegen");
        run.AccumulateAgentSession("codegen", 12.5, "claude", "claude-opus-5-5");

        var phase = run.Metrics.PhaseBreakdown["codegen"];
        phase.Tokens.Should().Be(70);
        phase.Cost.Should().Be(0.75m);
        phase.InputTokens.Should().Be(20);
        phase.OutputTokens.Should().Be(40);
        phase.ReasoningTokens.Should().Be(10);
        phase.CacheReadTokens.Should().Be(200);
        phase.CacheWriteTokens.Should().Be(14);
        phase.Turns.Should().Be(8);
        phase.WebSearchRequests.Should().Be(2);
        phase.BillingMode.Should().Be(AgentBillingModes.Subscription);
        phase.SessionCount.Should().Be(1);
        phase.Provider.Should().Be("claude");
        run.Metrics.RateLimits["five_hour"].Status.Should().Be("allowed_warning", "the latest reading per window wins");
        run.Metrics.RateLimits["five_hour"].Utilization.Should().Be(0.9);
    }

    [Fact]
    public void AccumulateAgentSession_ThenTokenUsage_KeepsBothSides()
    {
        var run = CreateRun();

        run.AccumulateAgentSession("analysis", 3, "kiro", "auto");
        run.AccumulateTokenUsage(new TokenUsage { InputTokens = 1, OutputTokens = 2 }, "analysis");

        var phase = run.Metrics.PhaseBreakdown["analysis"];
        phase.SessionCount.Should().Be(1);
        phase.AgentTimeSeconds.Should().Be(3);
        phase.Provider.Should().Be("kiro");
        phase.Tokens.Should().Be(3);
        phase.InputTokens.Should().Be(1);
        phase.Cost.Should().BeNull();
    }
}
