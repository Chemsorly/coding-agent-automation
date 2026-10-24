using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>Unit tests for <see cref="RunUsage.From(PipelineRunSummary)"/> and <see cref="RunUsage.From(PhaseUsage)"/>.</summary>
public class RunUsageTests
{
    private static PipelineRunSummary MakeRun(
        long tokens = 0,
        decimal? cost = null,
        long cacheRead = 0,
        long cacheWrite = 0,
        string? model = null,
        IReadOnlyDictionary<string, PhaseUsage>? phaseBreakdown = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "1",
        IssueTitle = "Test",
        FinalStep = PipelineStep.Completed,
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
        TotalTokens = tokens,
        TotalCost = cost,
        CacheReadTokens = cacheRead,
        CacheWriteTokens = cacheWrite,
        ModelName = model,
        PhaseBreakdown = phaseBreakdown,
        InitiatedBy = "manual",
    };

    [Fact]
    public void From_Run_SumsTheTokenSplitOverPhases()
    {
        var run = MakeRun(tokens: 500, phaseBreakdown: new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = new PhaseUsage(200, 0.01m) { InputTokens = 50, OutputTokens = 100, ReasoningTokens = 50 },
            ["codegen"]  = new PhaseUsage(300, 0.02m) { InputTokens = 100, OutputTokens = 150, ReasoningTokens = 50 },
        });

        var usage = RunUsage.From(run);

        usage.Tokens.Should().Be(500);
        usage.InputTokens.Should().Be(150);
        usage.OutputTokens.Should().Be(250);
        usage.ReasoningTokens.Should().Be(100);
    }

    [Fact]
    public void From_Run_WithoutPhaseBreakdown_HasZeroSplit()
    {
        var run = MakeRun(tokens: 1000, cost: 0.05m, phaseBreakdown: null);

        var usage = RunUsage.From(run);

        usage.Tokens.Should().Be(1000);
        usage.InputTokens.Should().Be(0);
        usage.OutputTokens.Should().Be(0);
        usage.ReasoningTokens.Should().Be(0);
        usage.BillingMode.Should().BeNull();
    }

    [Fact]
    public void From_Run_CopiesCacheReadAndWriteTokens()
    {
        var run = MakeRun(cacheRead: 5_000, cacheWrite: 2_000);

        var usage = RunUsage.From(run);

        usage.CacheReadTokens.Should().Be(5_000);
        usage.CacheWriteTokens.Should().Be(2_000);
    }

    [Fact]
    public void From_Run_AllPhasesSubscription_IsSubscription()
    {
        var run = MakeRun(phaseBreakdown: new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = new PhaseUsage(100, 0.01m) { BillingMode = AgentBillingModes.Subscription },
            ["codegen"]  = new PhaseUsage(200, 0.02m) { BillingMode = AgentBillingModes.Subscription },
        });

        var usage = RunUsage.From(run);

        usage.BillingMode.Should().Be(AgentBillingModes.Subscription);
    }

    [Fact]
    public void From_Run_ApiAndSubscriptionPhases_IsApi()
    {
        var run = MakeRun(phaseBreakdown: new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = new PhaseUsage(100, 0.01m) { BillingMode = AgentBillingModes.Subscription },
            ["codegen"]  = new PhaseUsage(200, 0.02m) { BillingMode = AgentBillingModes.Api },
        });

        var usage = RunUsage.From(run);

        usage.BillingMode.Should().Be(AgentBillingModes.Api);
    }

    [Fact]
    public void From_Run_NoKnownBillingMode_IsNull()
    {
        var run = MakeRun(phaseBreakdown: new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = new PhaseUsage(100, 0.01m) { BillingMode = AgentBillingModes.Unknown },
            ["codegen"]  = new PhaseUsage(200, 0.02m) { BillingMode = null },
        });

        var usage = RunUsage.From(run);

        usage.BillingMode.Should().BeNull();
    }

    [Fact]
    public void From_Phase_CopiesFieldsAndDropsUnknownBillingMode()
    {
        var phase = new PhaseUsage(5000, 0.03m, Model: "claude-opus-5-5")
        {
            InputTokens = 1000,
            OutputTokens = 2000,
            ReasoningTokens = 2000,
            CacheReadTokens = 10_000,
            CacheWriteTokens = 500,
            BillingMode = AgentBillingModes.Unknown,
        };

        var usage = RunUsage.From(phase);

        usage.Cost.Should().Be(0.03m);
        usage.Tokens.Should().Be(5000);
        usage.InputTokens.Should().Be(1000);
        usage.OutputTokens.Should().Be(2000);
        usage.ReasoningTokens.Should().Be(2000);
        usage.CacheReadTokens.Should().Be(10_000);
        usage.CacheWriteTokens.Should().Be(500);
        usage.Model.Should().Be("claude-opus-5-5");
        usage.BillingMode.Should().BeNull("unknown billing mode should be dropped");
    }

    [Fact]
    public void From_Phase_PreservesApiAndSubscriptionBillingModes()
    {
        var apiPhase = new PhaseUsage(100, 0.01m) { BillingMode = AgentBillingModes.Api };
        var subPhase = new PhaseUsage(100, 0.01m) { BillingMode = AgentBillingModes.Subscription };

        RunUsage.From(apiPhase).BillingMode.Should().Be(AgentBillingModes.Api,
            "Api billing mode must pass through unchanged");
        RunUsage.From(subPhase).BillingMode.Should().Be(AgentBillingModes.Subscription,
            "Subscription billing mode must pass through unchanged");
    }
}
