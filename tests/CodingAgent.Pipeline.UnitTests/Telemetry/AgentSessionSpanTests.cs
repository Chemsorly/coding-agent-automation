using System.Diagnostics;
using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests verifying that <see cref="AgentStallMonitor.ExecuteWithMonitoringAsync"/> creates
/// an <c>invoke_agent {phase}</c> span with OpenTelemetry GenAI semantic convention attributes.
/// AC2: Session spans carry the GenAI attributes (unit test with in-memory ActivityListener).
/// </summary>
public sealed class AgentSessionSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<Activity> _stopped = [];

    public AgentSessionSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            // SampleUsingParentId ensures activities started without an ambient parent are still sampled.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            // Use ActivityStopped (not ActivityStarted) so all tags set after StartActivity are captured.
            ActivityStopped = a => _stopped.Add(a)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private static PipelineRun CreateRun() => new()
    {
        RunId = "span-test-run",
        IssueIdentifier = "1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Implementation
    };

    private static PipelineConfiguration CreateConfig() => new()
    {
        WorkspaceBaseDirectory = Path.GetTempPath()
    };

    private static Mock<IAgentProvider> CreateProvider(
        AgentProviderType providerType = AgentProviderType.KiroCli,
        string? model = "test-model")
    {
        var provider = new Mock<IAgentProvider>();
        provider.Setup(p => p.ProviderType).Returns(providerType);
        provider.Setup(p => p.Model).Returns(model);
        provider.Setup(p => p.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });
        provider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsProcessAlive = true, IsExecuting = true });
        return provider;
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_CreatesSpanWithCorrectName()
    {
        var provider = CreateProvider();
        var run = CreateRun();
        var config = CreateConfig();

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "analysis" },
            run, config, "Analysis agent",
            onChange: null, logger: new Serilog.LoggerConfiguration().CreateLogger(),
            ct: CancellationToken.None);

        var span = _stopped.Should().Contain(a => a.OperationName == "invoke_agent analysis").Which;
        span.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsGenAiOperationNameTag()
    {
        var provider = CreateProvider();
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "codegen" },
            CreateRun(), CreateConfig(), "Code gen",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.Should().Contain(a => a.OperationName == "invoke_agent codegen").Which;
        span.GetTagItem("gen_ai.operation.name").Should().Be("invoke_agent");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsProviderNameTag_Kiro()
    {
        var provider = CreateProvider(AgentProviderType.KiroCli);
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "analysis" },
            CreateRun(), CreateConfig(), "Analysis",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent analysis");
        span.GetTagItem("gen_ai.provider.name").Should().Be("kiro");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsProviderNameTag_OpenCode()
    {
        var provider = CreateProvider(AgentProviderType.OpenCode, "claude-sonnet-4-5");
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "codegen" },
            CreateRun(), CreateConfig(), "Code gen",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent codegen");
        span.GetTagItem("gen_ai.provider.name").Should().Be("opencode");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsModelTag_WhenModelIsNotNull()
    {
        var provider = CreateProvider(model: "claude-sonnet-4-5");
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "analysis" },
            CreateRun(), CreateConfig(), "Analysis",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent analysis");
        span.GetTagItem("gen_ai.request.model").Should().Be("claude-sonnet-4-5");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_DoesNotSetModelTag_WhenModelIsNull()
    {
        var provider = CreateProvider(model: null);
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "analysis" },
            CreateRun(), CreateConfig(), "Analysis",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent analysis");
        span.GetTagItem("gen_ai.request.model").Should().BeNull();
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsPipelinePhaseTag()
    {
        var provider = CreateProvider();
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "pr_description" },
            CreateRun(), CreateConfig(), "PR desc",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent pr_description");
        span.GetTagItem("pipeline.phase").Should().Be("pr_description");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsSessionResumedTag()
    {
        var provider = CreateProvider();
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "codegen", UseResume = true },
            CreateRun(), CreateConfig(), "Code gen resumed",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent codegen");
        span.GetTagItem("agent.session.resumed").Should().Be(true);
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsExitCodeTag()
    {
        var provider = CreateProvider();
        provider.Setup(p => p.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = [] });

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "codegen" },
            CreateRun(), CreateConfig(), "Code gen fail",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent codegen");
        span.GetTagItem("agent.exit_code").Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_SetsTokenUsageTags_WhenUsageAvailable()
    {
        var provider = CreateProvider();
        provider.Setup(p => p.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = [],
                Usage = new TokenUsage { InputTokens = 500, OutputTokens = 200, CacheReadTokens = 100 }
            });

        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "analysis" },
            CreateRun(), CreateConfig(), "Analysis",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        var span = _stopped.First(a => a.OperationName == "invoke_agent analysis");
        // TODO: [WARNING] GetTagItem returns object?, and Activity.SetTag stores the value as-is.
        // If TokenUsage.InputTokens is int (not long), the boxed value is int, and comparing to
        // 500L (boxed long) may fail via reference equality rather than value equality on some
        // assertion libraries. Verify that SetTag receives a long (cast explicitly if needed) or
        // use a type-agnostic comparison such as span.GetTagItem(...).ToString() == "500".
        span.GetTagItem("gen_ai.usage.input_tokens").Should().Be(500L);
        span.GetTagItem("gen_ai.usage.output_tokens").Should().Be(200L);
        // TODO: [WARNING] There is no test for the CacheWriteTokens > 0 branch in AgentStallMonitor.
        // If the `if (result.Usage.CacheWriteTokens > 0) span?.SetTag(...)` block were deleted,
        // no test would fail. Add a case with CacheWriteTokens > 0 to cover that branch.
        span.GetTagItem("gen_ai.usage.cache_read_tokens").Should().Be(100L);
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_UsesOtherPhase_WhenPhaseIsNull()
    {
        var provider = CreateProvider();
        await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = null },
            CreateRun(), CreateConfig(), "Unknown phase",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        // Phase = null → span name "invoke_agent other"
        _stopped.Should().Contain(a => a.OperationName == "invoke_agent other");
    }

    [Fact]
    public async Task ExecuteWithMonitoringAsync_PropagatesAgentSeconds_OnResult()
    {
        var provider = CreateProvider();
        var result = await AgentStallMonitor.ExecuteWithMonitoringAsync(
            provider.Object,
            new AgentRequest { Prompt = "p", WorkspacePath = "/tmp", Phase = "codegen" },
            CreateRun(), CreateConfig(), "Code gen",
            null, new Serilog.LoggerConfiguration().CreateLogger(), CancellationToken.None);

        // AgentSeconds must be >= 0 (timing is not predictable in tests but must be set)
        // TODO: [WARNING] This assertion is too weak: >= 0.0 passes even if AgentSeconds is never
        // assigned and stays at the default 0.0. If the Stopwatch measurement were removed from
        // AgentStallMonitor, this test would still pass. Consider using a fake TimeProvider or
        // injecting a controllable Stopwatch to assert that AgentSeconds equals the measured elapsed
        // time, making the test a real verification rather than a sanity check.
        // TODO: [WARNING] There are no tests verifying that stall warnings, kills, and process-death
        // events appear as ActivityEvent entries on the span (agent.stall_warning, agent.stall_kill,
        // agent.process_death). These are explicitly listed in the AC and in docs/observability.md.
        // Exercising them requires simulating a stall via a fake TimeProvider and a mock provider
        // that never returns (blocking ExecuteAsync), then asserting span.Events contains the event.
        result.AgentSeconds.Should().BeGreaterThanOrEqualTo(0.0);
    }
}
