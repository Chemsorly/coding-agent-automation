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
            ExitCode = 0,
            OutputLines = [],
            Usage = usage,
            Cost = 0.25m,
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

    // ── Tool call accumulation ────────────────────────────────────────────

    [Fact]
    public void AccumulateTokenUsage_ToolCallsRecordedWhenUsageIsNull()
    {
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = null,
            ToolCalls = new Dictionary<string, McpCallCounts>
            {
                ["sonarqube/get_issues"] = new McpCallCounts { Calls = 3 }
            }
        };

        run.AccumulateTokenUsage(result, "codegen");

        run.Metrics.PhaseBreakdown.Should().ContainKey("codegen");
        run.Metrics.PhaseBreakdown["codegen"].ToolCalls.Should().NotBeNull();
        run.Metrics.PhaseBreakdown["codegen"].ToolCalls!["sonarqube/get_issues"].Calls.Should().Be(3);
        run.CacheReadTokens.Should().Be(0, "Usage is null so token accumulation is skipped");
    }

    [Fact]
    public void AccumulateTokenUsage_TwoResultsInOnePhase_ToolCallsSumPerKey()
    {
        var run = CreateRun();
        var first = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = null,
            ToolCalls = new Dictionary<string, McpCallCounts>
            {
                ["sonarqube/get_issues"] = new McpCallCounts { Calls = 2, FailedOther = 1 },
                ["github/list_prs"] = new McpCallCounts { Calls = 1 }
            }
        };
        var second = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = null,
            ToolCalls = new Dictionary<string, McpCallCounts>
            {
                ["sonarqube/get_issues"] = new McpCallCounts { Calls = 3, FailedAuth = 1 }
            }
        };

        run.AccumulateTokenUsage(first, "codegen");
        run.AccumulateTokenUsage(second, "codegen");

        var phase = run.Metrics.PhaseBreakdown["codegen"];
        phase.ToolCalls.Should().NotBeNull();
        // sonarqube/get_issues: 2+3=5 calls, 1 FailedOther + 1 FailedAuth
        phase.ToolCalls!["sonarqube/get_issues"].Calls.Should().Be(5);
        phase.ToolCalls["sonarqube/get_issues"].FailedOther.Should().Be(1);
        phase.ToolCalls["sonarqube/get_issues"].FailedAuth.Should().Be(1);
        // github/list_prs: only in first result
        phase.ToolCalls["github/list_prs"].Calls.Should().Be(1);
    }

    [Fact]
    public void AccumulateTokenUsage_UnavailableServers_MergedNewerValueWins()
    {
        var run = CreateRun();
        var first = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = null,
            UnavailableMcpServers = new Dictionary<string, string>
            {
                ["sonarqube"] = McpServerStatuses.Failed,
                ["grafana"] = McpServerStatuses.NeedsAuth
            }
        };
        var second = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = null,
            UnavailableMcpServers = new Dictionary<string, string>
            {
                // sonarqube gets a newer status — it should win
                ["sonarqube"] = McpServerStatuses.NeedsAuth,
                ["context7"] = McpServerStatuses.Failed
            }
        };

        run.AccumulateTokenUsage(first, "analysis");
        run.AccumulateTokenUsage(second, "analysis");

        var phase = run.Metrics.PhaseBreakdown["analysis"];
        phase.UnavailableMcpServers.Should().NotBeNull();
        phase.UnavailableMcpServers!["sonarqube"].Should().Be(McpServerStatuses.NeedsAuth,
            "newer value wins for duplicate server keys");
        phase.UnavailableMcpServers["grafana"].Should().Be(McpServerStatuses.NeedsAuth);
        phase.UnavailableMcpServers["context7"].Should().Be(McpServerStatuses.Failed);
    }

    [Fact]
    public void AccumulateTokenUsage_NullPhase_RecordsNothing()
    {
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = null,
            ToolCalls = new Dictionary<string, McpCallCounts>
            {
                ["sonarqube/get_issues"] = new McpCallCounts { Calls = 5 }
            }
        };

        run.AccumulateTokenUsage(result, phase: null);

        run.Metrics.PhaseBreakdown.Should().BeEmpty("null phase means tool calls are not recorded to any phase");
    }

    [Fact]
    public void AccumulateTokenUsage_NonNullResultWithToolCalls_RecordsPhaseBreakdown()
    {
        // The pattern-match guard `result is { ToolCalls.Count: > 0 }` at the call site already
        // proves result is non-null inside the if body. The null-forgiving ! operators that were
        // previously on those calls have been removed; this test verifies the branch compiles and
        // functions correctly without them.
        // TODO: This test validates the runtime behaviour of the branch but cannot detect regression
        // to `result!` in PipelineRunExtensions.cs: null-forgiving operators are a compile-time
        // suppressor with no runtime effect, so reverting to result! would leave this test passing
        // unchanged. The canonical guard against that regression is the scopeQuery in the issue
        // (`git grep -n 'result!' -- src/CodingAgent.Pipeline/Models/PipelineRunExtensions.cs`
        // returning zero matches), which is enforced at code-review time rather than in a unit test.
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = null,
            ToolCalls = new Dictionary<string, McpCallCounts>
            {
                ["github/list_issues"] = new McpCallCounts { Calls = 4 }
            }
        };

        run.AccumulateTokenUsage(result, "analysis");

        run.Metrics.PhaseBreakdown.Should().ContainKey("analysis");
        run.Metrics.PhaseBreakdown["analysis"].ToolCalls.Should().NotBeNull();
        run.Metrics.PhaseBreakdown["analysis"].ToolCalls!["github/list_issues"].Calls.Should().Be(4);
    }
}
