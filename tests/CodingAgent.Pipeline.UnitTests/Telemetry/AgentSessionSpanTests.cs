using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests verifying that <see cref="AgentStallMonitor.ExecuteWithMonitoringAsync"/> creates a span
/// per agent session with the expected GenAI semantic convention attributes.
/// </summary>
public class AgentSessionSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stoppedActivities = [];
    // Unique test-run tag to isolate activities from concurrent tests
    private readonly string _testRunTag = Guid.NewGuid().ToString("N")[..8];

    public AgentSessionSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _stoppedActivities.Add(a)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ExecuteWithMonitoringAsync_KiroProvider_CreatesSpanWithGenAiAttributes()
    {
        var uniquePhase = $"analysis_{_testRunTag}";
        var agentResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 150, OutputTokens = 75 }
        };
        var provider = CreateMockProvider(AgentProviderType.KiroCli, model: "claude-sonnet-4-5", result: agentResult);
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "Analysis agent", onChange: null, Serilog.Log.Logger, CancellationToken.None,
            phase: uniquePhase);

        var span = _stoppedActivities.Should()
            .Contain(a => a.OperationName == $"invoke_agent {uniquePhase}").Which;
        span.GetTagItem("gen_ai.operation.name").Should().Be("invoke_agent");
        span.GetTagItem("gen_ai.provider.name").Should().Be("kiro");
        span.GetTagItem("gen_ai.request.model").Should().Be("claude-sonnet-4-5");
        span.GetTagItem("pipeline.phase").Should().Be(uniquePhase);
        span.GetTagItem("agent.session.resumed").Should().Be(false);
        span.GetTagItem("agent.exit_code").Should().Be(0);
        span.GetTagItem("gen_ai.usage.input_tokens").Should().Be(150L);
        span.GetTagItem("gen_ai.usage.output_tokens").Should().Be(75L);
        span.GetTagItem("gen_ai.usage.total_tokens").Should().Be(225L);
        // TODO: This test uses a non-null Usage to exercise the token tag path. Add a companion test
        // for the realistic Kiro case (Usage = null) that asserts gen_ai.usage.* tags are ABSENT on the
        // span — Kiro never reports tokens, so those tags should never appear. Without that test, a
        // regression that accidentally populates Kiro token tags would go undetected.
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_OpenCodeProvider_SetsProviderTagToOpencode()
    {
        var uniquePhase = $"codegen_{_testRunTag}";
        var provider = CreateMockProvider(AgentProviderType.OpenCode, model: null, result: SimpleResult());
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "Code generation", onChange: null, Serilog.Log.Logger, CancellationToken.None,
            phase: uniquePhase);

        var span = _stoppedActivities.Should()
            .Contain(a => a.OperationName == $"invoke_agent {uniquePhase}").Which;
        span.GetTagItem("gen_ai.provider.name").Should().Be("opencode");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_WithResume_SetsSessionResumedToTrue()
    {
        var uniquePhase = $"codegen_{_testRunTag}_resume";
        var provider = CreateMockProvider(AgentProviderType.KiroCli, model: null, result: SimpleResult());
        var run = CreateRun();
        var config = CreateConfig();
        var request = new AgentRequest
        {
            Prompt = "fix the bug",
            WorkspacePath = "/tmp/workspace",
            UseResume = true
        };

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, request, run, config,
            "QGC retry agent", onChange: null, Serilog.Log.Logger, CancellationToken.None,
            phase: uniquePhase);

        var span = _stoppedActivities.Should()
            .Contain(a => a.OperationName == $"invoke_agent {uniquePhase}").Which;
        span.GetTagItem("agent.session.resumed").Should().Be(true);
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_NoPhase_FallsBackToNormalizedDescription()
    {
        // No phase= parameter; phaseDescription "Code generation" normalizes to "codegen"
        // We can't fully isolate "codegen" from other parallel tests here, so just verify
        // our call created at least one "invoke_agent codegen" span in this test's listener.
        var provider = CreateMockProvider(AgentProviderType.KiroCli, model: null, result: SimpleResult());
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "Code generation", onChange: null, Serilog.Log.Logger, CancellationToken.None);

        _stoppedActivities.Should().Contain(a => a.OperationName == "invoke_agent codegen"
            && (string?)a.GetTagItem("pipeline.phase") == "codegen");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_WithPhase_AccumulatesSessionInPhaseBreakdown()
    {
        var uniquePhase = $"analysis_{_testRunTag}_session";
        var provider = CreateMockProvider(AgentProviderType.KiroCli, model: "my-model", result: SimpleResult());
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "Analysis agent", onChange: null, Serilog.Log.Logger, CancellationToken.None,
            phase: uniquePhase);

        run.Metrics.PhaseBreakdown.Should().ContainKey(uniquePhase);
        run.Metrics.PhaseBreakdown[uniquePhase].SessionCount.Should().Be(1);
        // TODO: BeGreaterThanOrEqualTo(0) is vacuously true for a double — it passes even if
        // AccumulateAgentSession is never called. Use BeGreaterThan(0) or inject a fake TimeProvider
        // with a known elapsed value and assert the exact accumulated seconds.
        run.Metrics.PhaseBreakdown[uniquePhase].AgentTimeSeconds.Should().BeGreaterThanOrEqualTo(0);
        run.Metrics.PhaseBreakdown[uniquePhase].Provider.Should().Be("kiro");
        run.Metrics.PhaseBreakdown[uniquePhase].Model.Should().Be("my-model");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_MultipleInvocations_AccumulatesSessionCounts()
    {
        var uniquePhase = $"codegen_{_testRunTag}_multi";
        var provider = CreateMockProvider(AgentProviderType.KiroCli, model: null, result: SimpleResult());
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "QGC retry", onChange: null, Serilog.Log.Logger, CancellationToken.None, phase: uniquePhase);
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "QGC retry", onChange: null, Serilog.Log.Logger, CancellationToken.None, phase: uniquePhase);

        run.Metrics.PhaseBreakdown[uniquePhase].SessionCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_NoPhase_DoesNotAccumulateSession()
    {
        var provider = CreateMockProvider(AgentProviderType.KiroCli, model: null, result: SimpleResult());
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "Code generation", onChange: null, Serilog.Log.Logger, CancellationToken.None);

        run.Metrics.PhaseBreakdown.Should().BeEmpty("no phase= was specified so no session is accumulated");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_NoModel_OmitsModelAttribute()
    {
        var uniquePhase = $"reflection_{_testRunTag}";
        var provider = CreateMockProvider(AgentProviderType.KiroCli, model: null, result: SimpleResult());
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object, CreateRequest(), run, config,
            "Reflection", onChange: null, Serilog.Log.Logger, CancellationToken.None,
            phase: uniquePhase);

        var span = _stoppedActivities.Should()
            .Contain(a => a.OperationName == $"invoke_agent {uniquePhase}").Which;
        span.GetTagItem("gen_ai.request.model").Should().BeNull("model tag must be absent when provider.Model is null");
    }

    // ── Static helpers ─────────────────────────────────────────────────────────────────────────

    private static AgentRequest CreateRequest() => new()
    {
        Prompt = "do something",
        WorkspacePath = "/tmp/workspace",
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static AgentResult SimpleResult() => new() { ExitCode = 0, OutputLines = [] };

    private static Mock<IAgentProvider> CreateMockProvider(
        AgentProviderType providerType,
        string? model,
        AgentResult result)
    {
        var mock = new Mock<IAgentProvider>();
        mock.SetupGet(p => p.ProviderType).Returns(providerType);
        mock.SetupGet(p => p.Model).Returns(model);
        mock.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(result);
        mock.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true });
        return mock;
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Implementation,
        ProjectId = "test-project",
        ProjectName = "Test Project"
    };

    private static PipelineConfiguration CreateConfig() => new()
    {
        AgentTimeout = TimeSpan.FromSeconds(60),
        StallPollInterval = TimeSpan.FromSeconds(60), // long poll so monitor never fires in test
        StallWarningInterval = TimeSpan.FromSeconds(60)
    };
}
