using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog.Events;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline.UnitTests.Helpers;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Tests for the provider-busy retry in <see cref="AgentStallMonitor.ExecuteWithMonitoringAsync"/>:
/// a call that ends with <see cref="AgentErrorCategory.ProviderRateLimit"/> or
/// <see cref="AgentErrorCategory.ProviderOverload"/> is sent again after <c>TransientRetryDelay</c>.
///
/// Every test passes a <see cref="FakeTimeProvider"/> and, unless it is about the wait itself,
/// <c>TransientRetryDelay = TimeSpan.Zero</c>, so nothing waits on the wall clock.
/// </summary>
public class AgentStallMonitorProviderRetryTests : IDisposable
{
    private const string OriginalPrompt = "implement the feature";

    private readonly Mock<IAgentProvider> _mockAgent = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly PipelineRun _run = new()
    {
        RunId = "test-run",
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp"
    };

    // ActivityStopped runs on the thread that stops the span, so a ConcurrentBag is needed (as in AgentSessionSpanTests).
    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<Activity> _stoppedActivities = [];
    private readonly string _testRunTag = Guid.NewGuid().ToString("N")[..8];

    public AgentStallMonitorProviderRetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _stoppedActivities.Add(a)
        };
        ActivitySource.AddActivityListener(_listener);

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                LastOutputTime = _fakeTime.GetUtcNow().UtcDateTime
            });
    }

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Retried results ────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimitedCall_IsRetried_UntilItSucceeds()
    {
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Busy(AgentErrorCategory.ProviderRateLimit), Success());

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.ExitCode.Should().Be(0);
        VerifyExecuteCalls(Times.Exactly(3));
    }

    [Fact]
    public async Task OverloadedCall_IsRetried()
    {
        SetupResults(Busy(AgentErrorCategory.ProviderOverload), Success());

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Success.Should().BeTrue();
        VerifyExecuteCalls(Times.Exactly(2));
    }

    [Fact]
    public async Task ProviderStillBusy_AfterTenRetries_ReturnsTheLastResult()
    {
        var lastResult = Busy(AgentErrorCategory.ProviderRateLimit);
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(lastResult);

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderRateLimit);
        result.Should().BeSameAs(lastResult);
        VerifyExecuteCalls(Times.Exactly(AgentStallMonitor.MaxProviderRetries + 1));
    }

    [Fact]
    public async Task MixedBusyCategories_ShareOneRetryBudget()
    {
        SetupResults(
            Busy(AgentErrorCategory.ProviderRateLimit), Busy(AgentErrorCategory.ProviderOverload), Success());

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Success.Should().BeTrue();
        VerifyExecuteCalls(Times.Exactly(3));
    }

    // ── Progress between bursts ────────────────────────────────────────────────

    [Fact]
    public async Task ProgressBetweenBursts_ResetsTheCount()
    {
        var busy = Busy(AgentErrorCategory.ProviderRateLimit);
        SetupResults(
            [.. Enumerable.Repeat(busy, 9),
             BusyAfterProgress(AgentErrorCategory.ProviderRateLimit),
             .. Enumerable.Repeat(busy, 9),
             Success()]);

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Success.Should().BeTrue();
        VerifyExecuteCalls(Times.Exactly(20));
    }

    [Fact]
    public async Task AfterAProgressReset_TenMoreBurstsWithoutProgress_EndTheCall()
    {
        var busy = Busy(AgentErrorCategory.ProviderRateLimit);
        var lastBusy = Busy(AgentErrorCategory.ProviderRateLimit);
        SetupResults(
            [.. Enumerable.Repeat(busy, AgentStallMonitor.MaxProviderRetries - 1),
             BusyAfterProgress(AgentErrorCategory.ProviderRateLimit),
             .. Enumerable.Repeat(busy, AgentStallMonitor.MaxProviderRetries - 1),
             lastBusy]);

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Should().BeSameAs(lastBusy, "the count restarts at 0 after progress, not at a partial value");
        VerifyExecuteCalls(Times.Exactly(2 * AgentStallMonitor.MaxProviderRetries));
    }

    [Fact]
    public async Task TenBurstsWithoutProgress_StillEndTheCall()
    {
        var busy = Busy(AgentErrorCategory.ProviderRateLimit);
        SetupResults([.. Enumerable.Repeat(busy, AgentStallMonitor.MaxProviderRetries + 1)]);

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Should().BeSameAs(busy);
        VerifyExecuteCalls(Times.Exactly(AgentStallMonitor.MaxProviderRetries + 1));
    }

    [Fact]
    public async Task ProgressOnTheTenthBurst_StillRetries()
    {
        var busy = Busy(AgentErrorCategory.ProviderOverload);
        SetupResults(
            [.. Enumerable.Repeat(busy, AgentStallMonitor.MaxProviderRetries),
             BusyAfterProgress(AgentErrorCategory.ProviderOverload),
             Success()]);

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Success.Should().BeTrue("progress resets the count before the limit is checked");
        VerifyExecuteCalls(Times.Exactly(AgentStallMonitor.MaxProviderRetries + 2));
    }

    [Fact]
    public async Task EveryAttemptMakingProgress_StopsAtThePerCallCap()
    {
        var busy = BusyAfterProgress(AgentErrorCategory.ProviderRateLimit);
        SetupResults([.. Enumerable.Repeat(busy, AgentStallMonitor.MaxProviderRetriesPerCall + 1)]);

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Should().BeSameAs(busy);
        VerifyExecuteCalls(Times.Exactly(AgentStallMonitor.MaxProviderRetriesPerCall + 1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(500, 0)]
    public async Task BusyAttemptWithoutOutputTokens_IsNotProgress(long inputTokens, long outputTokens)
    {
        var busy = new AgentResult
        {
            ExitCode = 1,
            OutputLines = ["provider is busy"],
            ErrorCategory = AgentErrorCategory.ProviderRateLimit,
            Usage = new TokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
        SetupResults([.. Enumerable.Repeat(busy, AgentStallMonitor.MaxProviderRetries + 1)]);

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Should().BeSameAs(busy);
        VerifyExecuteCalls(Times.Exactly(AgentStallMonitor.MaxProviderRetries + 1));
    }

    // ── Results that are not retried ───────────────────────────────────────────

    [Theory]
    [InlineData(AgentErrorCategory.ProviderQuotaExhausted)]
    [InlineData(AgentErrorCategory.PermanentAuthFailure)]
    [InlineData(AgentErrorCategory.ProviderMisconfigured)]
    [InlineData(AgentErrorCategory.None)]
    public async Task OtherFailures_AreNotRetried(AgentErrorCategory category)
    {
        var failure = new AgentResult { ExitCode = 1, OutputLines = ["failed"], ErrorCategory = category };
        SetupResults(failure, Success());

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Should().BeSameAs(failure);
        VerifyExecuteCalls(Times.Once());
    }

    [Fact]
    public async Task SuccessfulResult_IsNotRetried_EvenWithABusyCategory()
    {
        var succeeded = new AgentResult { ExitCode = 0, OutputLines = [], ErrorCategory = AgentErrorCategory.ProviderRateLimit };
        SetupResults(succeeded, Success());

        var result = await RunAsync(FreshRequest(), CreateConfig());

        result.Should().BeSameAs(succeeded);
        VerifyExecuteCalls(Times.Once());
    }

    [Fact]
    public async Task ThrownException_IsNotRetried()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("provider crashed"));

        var act = () => RunAsync(FreshRequest(), CreateConfig());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider crashed");
        VerifyExecuteCalls(Times.Once());
    }

    // ── The retried request ────────────────────────────────────────────────────

    [Fact]
    public async Task ResumedCall_GetsTheRetryNoteOnce()
    {
        var original = new AgentRequest { Prompt = OriginalPrompt, WorkspacePath = "/ws", UseResume = true };
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Busy(AgentErrorCategory.ProviderRateLimit), Success());

        await RunAsync(original, CreateConfig());

        var prompts = SentRequests().Select(r => r.Prompt).ToList();
        prompts.Should().HaveCount(3);
        prompts[0].Should().Be(OriginalPrompt);
        prompts[1].Should().Be(AgentStallMonitor.ProviderRetryNote + OriginalPrompt);
        prompts[2].Should().Be(AgentStallMonitor.ProviderRetryNote + OriginalPrompt);
        original.Prompt.Should().Be(OriginalPrompt, "the caller's request is never changed");
    }

    [Fact]
    public async Task CallWithAResumeSessionId_GetsTheRetryNote()
    {
        var original = new AgentRequest { Prompt = OriginalPrompt, WorkspacePath = "/ws", UseResume = false, ResumeSessionId = "session-1" };
        SetupResults(Busy(AgentErrorCategory.ProviderOverload), Success());

        await RunAsync(original, CreateConfig());

        var prompts = SentRequests().Select(r => r.Prompt).ToList();
        prompts.Should().Equal(OriginalPrompt, AgentStallMonitor.ProviderRetryNote + OriginalPrompt);
    }

    [Fact]
    public async Task FreshCall_IsSentAgainUnchanged()
    {
        var original = new AgentRequest { Prompt = OriginalPrompt, WorkspacePath = "/ws", UseResume = false, ResumeSessionId = null };
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Success());

        await RunAsync(original, CreateConfig());

        var prompts = SentRequests().Select(r => r.Prompt).ToList();
        prompts.Should().Equal(OriginalPrompt, OriginalPrompt);
    }

    [Fact]
    public async Task RetriedRequest_CopiesEveryOtherPropertyOfTheOriginal()
    {
        var original = new AgentRequest
        {
            Prompt = OriginalPrompt,
            WorkspacePath = "/ws-copy",
            Timeout = TimeSpan.FromMinutes(7),
            UseResume = true,
            ResumeSessionId = "session-9",
            ImagePaths = ["/images/a.png", "/images/b.png"],
            EnvironmentVariables = new Dictionary<string, string> { ["TOKEN_NAME"] = "value" },
            EffortOverride = AgentEffortLevel.High
        };
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Success());

        await RunAsync(original, CreateConfig());

        var retried = SentRequests()[1];
        retried.Should().NotBeSameAs(original);
        retried.WorkspacePath.Should().Be("/ws-copy");
        retried.Timeout.Should().Be(TimeSpan.FromMinutes(7));
        retried.UseResume.Should().BeTrue();
        retried.ResumeSessionId.Should().Be("session-9");
        retried.ImagePaths!.Should().Equal("/images/a.png", "/images/b.png");
        retried.EnvironmentVariables!.Should().BeEquivalentTo(new Dictionary<string, string> { ["TOKEN_NAME"] = "value" });
        retried.EffortOverride.Should().Be(AgentEffortLevel.High);
    }

    // ── What the operator sees ─────────────────────────────────────────────────

    [Fact]
    public async Task Retry_EmitsTheBusyOutputLine()
    {
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Success());
        var lines = new ConcurrentQueue<string>();

        await RunAsync(FreshRequest(), CreateConfig(), onOutputLine: lines.Enqueue);

        lines.Where(l => l.StartsWith("⏳ Model provider busy (ProviderRateLimit), retrying in 0 s (1/10)", StringComparison.Ordinal))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task EveryRetry_EmitsItsOwnNumberedLine()
    {
        SetupResults(
            Busy(AgentErrorCategory.ProviderRateLimit), Busy(AgentErrorCategory.ProviderOverload), Success());
        var lines = new ConcurrentQueue<string>();

        await RunAsync(FreshRequest(), CreateConfig(), onOutputLine: lines.Enqueue);

        lines.Should().Equal(
            "⏳ Model provider busy (ProviderRateLimit), retrying in 0 s (1/10)",
            "⏳ Model provider busy (ProviderOverload), retrying in 0 s (2/10)");
    }

    [Fact]
    public async Task OutputLine_ShowsTheConsecutiveCount()
    {
        SetupResults(
            Busy(AgentErrorCategory.ProviderRateLimit),
            BusyAfterProgress(AgentErrorCategory.ProviderRateLimit),
            Busy(AgentErrorCategory.ProviderRateLimit),
            Success());
        var lines = new ConcurrentQueue<string>();

        await RunAsync(FreshRequest(), CreateConfig(), onOutputLine: lines.Enqueue);

        lines.Should().HaveCount(3);
        lines.Should().AllSatisfy(l => l.StartsWith("⏳ Model provider busy (ProviderRateLimit), retrying in 0 s (", StringComparison.Ordinal));
        lines.Select(l => l[l.LastIndexOf('(')..]).Should().Equal("(1/10)", "(1/10)", "(2/10)");
    }

    [Fact]
    public async Task Retry_LogsTheConsecutiveAndTheTotalCount()
    {
        SetupResults(
            Busy(AgentErrorCategory.ProviderRateLimit),
            BusyAfterProgress(AgentErrorCategory.ProviderRateLimit),
            Busy(AgentErrorCategory.ProviderRateLimit),
            Success());
        var sink = new LifecycleCaptureSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

        await RunAsync(FreshRequest(), CreateConfig(), logger: logger);

        var warnings = sink.Events.Where(e => e.MessageTemplate.Text.Contains("model provider busy")).ToList();
        warnings.Select(w => ScalarValueOf(w, "Attempt")).Should().Equal(1, 1, 2);
        warnings.Select(w => ScalarValueOf(w, "TotalRetries")).Should().Equal(1, 2, 3);
        warnings.Should().OnlyContain(w => (int)ScalarValueOf(w, "MaxRetries")! == AgentStallMonitor.MaxProviderRetries);
    }

    [Fact]
    public async Task Retry_WithoutAnOutputCallback_StillRetries()
    {
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Success());

        var result = await RunAsync(FreshRequest(), CreateConfig(), onOutputLine: null);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Retry_LogsAWarningWithTheRunAndPhase()
    {
        SetupResults(Busy(AgentErrorCategory.ProviderOverload), Success());
        var sink = new LifecycleCaptureSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

        await RunAsync(FreshRequest(), CreateConfig(), logger: logger);

        var warning = sink.Events.Should().ContainSingle(e => e.MessageTemplate.Text.Contains("model provider busy")).Which;
        warning.Level.Should().Be(LogEventLevel.Warning);
        ScalarValueOf(warning, "RunId").Should().Be("test-run");
        ScalarValueOf(warning, "Phase").Should().Be("Fast agent");
        ScalarValueOf(warning, "ErrorCategory").Should().Be(AgentErrorCategory.ProviderOverload);
        ScalarValueOf(warning, "Attempt").Should().Be(1);
        ScalarValueOf(warning, "MaxRetries").Should().Be(AgentStallMonitor.MaxProviderRetries);
        ScalarValueOf(warning, "DelaySeconds").Should().Be(0);
    }

    [Fact]
    public async Task Retry_ShowsTheDelayInWholeSeconds_AndWaitsForIt()
    {
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Success());
        var busyLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Longer than BackgroundWait.Timeout: a wait on the wall clock would outlast the test's safety bound,
        // so the call can only finish in time when the wait runs on the injected TimeProvider.
        var delay = TimeSpan.FromMinutes(5);

        var call = RunAsync(
            FreshRequest(), CreateConfig(delay),
            onOutputLine: line => busyLine.TrySetResult(line));

        (await busyLine.Task.WaitAsync(BackgroundWait.Timeout))
            .Should().Be("⏳ Model provider busy (ProviderRateLimit), retrying in 300 s (1/10)");

        // One second short of the delay on the fake clock: the call is still waiting and has not been sent again.
        _fakeTime.Advance(delay - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        call.IsCompleted.Should().BeFalse();
        VerifyExecuteCalls(Times.Once());

        // The last second ends the wait. The busy line is written just before the wait starts, so keep
        // advancing until the call has registered its timer and finished.
        var deadline = DateTime.UtcNow + BackgroundWait.Timeout;
        while (!call.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            _fakeTime.Advance(TimeSpan.FromSeconds(1));
        }

        (await call.WaitAsync(BackgroundWait.Timeout)).Success.Should().BeTrue();
        VerifyExecuteCalls(Times.Exactly(2));
    }

    // ── Cancellation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelDuringTheWait_Throws()
    {
        using var cts = new CancellationTokenSource();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(Busy(AgentErrorCategory.ProviderRateLimit));

        var act = () => RunAsync(FreshRequest(), CreateConfig(TimeSpan.FromMinutes(1)), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        VerifyExecuteCalls(Times.Once());
    }

    // ── One monitored call per attempt ─────────────────────────────────────────

    [Fact]
    public async Task EveryAttempt_IsAccumulatedAsOneSession()
    {
        var phase = $"codegen_{_testRunTag}_sessions";
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Busy(AgentErrorCategory.ProviderRateLimit), Success());

        await RunAsync(FreshRequest(), CreateConfig(), phase: phase);

        _run.Metrics.PhaseBreakdown[phase].SessionCount.Should().Be(3);
    }

    [Fact]
    public async Task OnlyRetries_CarryTheProviderRetryTag()
    {
        var phase = $"codegen_{_testRunTag}_tags";
        SetupResults(Busy(AgentErrorCategory.ProviderRateLimit), Busy(AgentErrorCategory.ProviderOverload), Success());

        await RunAsync(FreshRequest(), CreateConfig(), phase: phase);

        var spans = _stoppedActivities
            .Where(a => (string?)a.GetTagItem("pipeline.phase_key") == phase)
            .ToList();
        spans.Should().HaveCount(3, "each attempt is its own invoke_agent span");
        spans.Count(a => a.GetTagItem("agent.provider_retry") is null).Should().Be(1, "the first attempt carries no retry tag");
        spans.Select(a => a.GetTagItem("agent.provider_retry")).Where(tag => tag is not null)
            .Select(tag => Convert.ToInt32(tag))
            .Should().BeEquivalentTo(new[] { 1, 2 });
        spans.Single(a => a.GetTagItem("agent.provider_retry") is null)
            .GetTagItem("agent.error_category").Should().Be("ProviderRateLimit");
    }

    [Fact]
    public async Task ProviderRetryTag_KeepsCountingTheWholeCall_AcrossAProgressReset()
    {
        var phase = $"codegen_{_testRunTag}_reset";
        SetupResults(
            Busy(AgentErrorCategory.ProviderRateLimit),
            BusyAfterProgress(AgentErrorCategory.ProviderRateLimit),
            Busy(AgentErrorCategory.ProviderOverload),
            Success());

        await RunAsync(FreshRequest(), CreateConfig(), phase: phase);

        _stoppedActivities
            .Where(a => (string?)a.GetTagItem("pipeline.phase_key") == phase)
            .Select(a => a.GetTagItem("agent.provider_retry"))
            .Where(tag => tag is not null)
            .Select(tag => Convert.ToInt32(tag))
            .Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static AgentResult Busy(AgentErrorCategory category) =>
        new() { ExitCode = 1, OutputLines = ["provider is busy"], ErrorCategory = category };

    /// <summary>A busy result of an attempt that did work before the provider turned it down: it produced output tokens.</summary>
    private static AgentResult BusyAfterProgress(AgentErrorCategory category) =>
        new()
        {
            ExitCode = 1,
            OutputLines = ["provider is busy"],
            ErrorCategory = category,
            Usage = new TokenUsage { OutputTokens = 100 }
        };

    private static AgentResult Success() => new() { ExitCode = 0, OutputLines = [] };

    private static AgentRequest FreshRequest() => new() { Prompt = OriginalPrompt, WorkspacePath = "/ws" };

    private static PipelineConfiguration CreateConfig(TimeSpan? retryDelay = null) => new()
    {
        StallPollInterval = TimeSpan.FromMinutes(1),
        StallWarningInterval = TimeSpan.FromHours(1),
        AgentTimeout = TimeSpan.FromHours(1),
        TransientRetryDelay = retryDelay ?? TimeSpan.Zero
    };

    private void SetupResults(params AgentResult[] results)
    {
        var sequence = _mockAgent.SetupSequence(a =>
            a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()));
        foreach (var result in results)
            sequence.ReturnsAsync(result);
    }

    private Task<AgentResult> RunAsync(
        AgentRequest request,
        PipelineConfiguration config,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default,
        string? phase = null,
        Serilog.ILogger? logger = null) =>
        AgentStallMonitor.ExecuteWithMonitoringAsync(
            new AgentMonitorContext(_mockAgent.Object, _run, config, "Fast agent", null, logger ?? _mockLogger.Object),
            request,
            ct,
            onOutputLine,
            timeProvider: _fakeTime,
            phase: phase);

    private void VerifyExecuteCalls(Times times) =>
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            times);

    /// <summary>The requests the provider received, in call order (Moq also records property and health calls).</summary>
    private List<AgentRequest> SentRequests() =>
        _mockAgent.Invocations
            .Where(i => i.Method.Name == nameof(IAgentProvider.ExecuteAsync))
            .Select(i => (AgentRequest)i.Arguments[0])
            .ToList();

    private static object? ScalarValueOf(LogEvent logEvent, string propertyName) =>
        ((ScalarValue)logEvent.Properties[propertyName]).Value;
}
