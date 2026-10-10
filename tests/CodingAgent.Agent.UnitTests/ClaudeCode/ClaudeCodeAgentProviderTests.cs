using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Agent.ClaudeCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Moq;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

public class ClaudeCodeAgentProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspace;
    private readonly string _mcpConfigPath;
    private readonly FakeClaudeProcessLauncher _launcher = new();
    private readonly Dictionary<string, string?> _env = new();

    public ClaudeCodeAgentProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claude-provider-{Guid.NewGuid():N}");
        _workspace = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspace);
        _mcpConfigPath = Path.Combine(_tempDir, "pipeline-mcp.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ClaudeCodeAgentProvider CreateProvider(
        string? authMode = ClaudeCodeAuthModes.Auto,
        string? model = "claude-opus-5-5",
        AgentEffortLevel effort = AgentEffortLevel.High,
        ClaudeExitTimings? timings = null) =>
        new(new Mock<Serilog.ILogger>().Object,
            new ClaudeCodeSettings(model, "/opt/claude", effort, authMode, _mcpConfigPath),
            _launcher, name => _env.GetValueOrDefault(name), timings);

    private AgentRequest Request(string prompt = "do it", bool useResume = false, string? resumeSessionId = null) => new()
    {
        Prompt = prompt,
        WorkspacePath = _workspace,
        UseResume = useResume,
        ResumeSessionId = resumeSessionId,
        Timeout = TimeSpan.FromMinutes(1)
    };

    private static string Init(string sessionId) => JsonSerializer.Serialize(new
    {
        type = "system", subtype = "init", session_id = sessionId, model = "claude-opus-5-5"
    });

    private static string Text(string text) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        parent_tool_use_id = (string?)null,
        message = new { content = new[] { new { type = "text", text } } }
    });

    private static string Result(
        string sessionId, long input, long output, long thinking, decimal cost, int turns,
        bool isError = false, int? apiErrorStatus = null, long cacheRead = 0, long cacheWrite = 0) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = isError ? "error_during_execution" : "success",
            ["is_error"] = isError,
            ["session_id"] = sessionId,
            ["result"] = isError ? "boom" : "ok",
            ["total_cost_usd"] = cost,
            ["num_turns"] = turns,
            ["duration_api_ms"] = turns * 1000,
            ["api_error_status"] = apiErrorStatus,
            ["modelUsage"] = new Dictionary<string, object>
            {
                ["claude-opus-5-5"] = new
                {
                    inputTokens = input,
                    outputTokens = output,
                    thinkingTokens = thinking,
                    cacheReadInputTokens = cacheRead,
                    cacheCreationInputTokens = cacheWrite,
                    webSearchRequests = 1,
                    costUSD = cost
                }
            }
        });

    // ── Process start ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_StartsHeadlessCliInWorkspace_WithPromptOnStdin()
    {
        _env[AgentDefaults.EnvClaudeApiKey] = "sk-key";
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));

        await CreateProvider().ExecuteAsync(Request("fix the bug"), CancellationToken.None);

        var psi = _launcher.Started.Should().ContainSingle().Subject;
        psi.FileName.Should().Be("/opt/claude");
        psi.WorkingDirectory.Should().Be(Path.GetFullPath(_workspace));
        psi.ArgumentList.Should().Equal(
            "-p", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions",
            "--model", "claude-opus-5-5", "--effort", "high");
        _launcher.Stdin.Should().Equal("fix the bug");
    }

    [Fact]
    public async Task ExecuteAsync_HandsOnlyTheSelectedCredentialToTheCli()
    {
        _env[AgentDefaults.EnvClaudeApiKey] = "sk-key";
        _env[AgentDefaults.EnvClaudeOAuthToken] = "oauth";
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));

        await CreateProvider(ClaudeCodeAuthModes.Subscription).ExecuteAsync(Request(), CancellationToken.None);

        var environment = _launcher.Started[0].Environment;
        environment["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("oauth");
        environment.Should().NotContainKeys(
            "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", AgentDefaults.EnvClaudeApiKey, AgentDefaults.EnvClaudeOAuthToken);
        environment["DISABLE_AUTOUPDATER"].Should().Be("1");
        environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"].Should().Be("1");
    }

    [Fact]
    public async Task ExecuteAsync_PassesProjectSecrets_ButNeverOneThatWouldReplaceTheCredential()
    {
        _env[AgentDefaults.EnvClaudeApiKey] = "sk-agent";
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));
        var request = new AgentRequest
        {
            Prompt = "p",
            WorkspacePath = _workspace,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["DATABASE_URL"] = "postgres://x",
                ["ANTHROPIC_API_KEY"] = "sk-project"
            }
        };

        await CreateProvider(ClaudeCodeAuthModes.ApiKey).ExecuteAsync(request, CancellationToken.None);

        var environment = _launcher.Started[0].Environment;
        environment["DATABASE_URL"].Should().Be("postgres://x");
        environment["ANTHROPIC_API_KEY"].Should().Be("sk-agent");
    }

    [Fact]
    public async Task ExecuteAsync_AutoModeWithoutCredential_StillRunsTheCli()
    {
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));

        var result = await CreateProvider(ClaudeCodeAuthModes.Auto).ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.UsageDetails!.BillingMode.Should().Be(AgentBillingModes.Unknown);
    }

    [Theory]
    [InlineData(ClaudeCodeAuthModes.ApiKey)]
    [InlineData(ClaudeCodeAuthModes.Subscription)]
    public async Task ExecuteAsync_ExplicitModeWithoutItsCredential_FailsWithoutStartingTheCli(string authMode)
    {
        var result = await CreateProvider(authMode).ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.ErrorCategory.Should().Be(AgentErrorCategory.PermanentAuthFailure);
        result.OutputLines.Should().ContainSingle().Which.Should().Contain(authMode);
        _launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ImagePaths_AreListedAfterThePrompt()
    {
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));
        var request = new AgentRequest
        {
            Prompt = "look",
            WorkspacePath = _workspace,
            ImagePaths = ["/tmp/a.png", "/tmp/b.png"]
        };

        await CreateProvider().ExecuteAsync(request, CancellationToken.None);

        _launcher.Stdin[0].Should().StartWith("look").And.Contain("- /tmp/a.png").And.Contain("- /tmp/b.png");
    }

    [Fact]
    public async Task ExecuteAsync_McpConfigFileExists_PassesIt()
    {
        await File.WriteAllTextAsync(_mcpConfigPath, """{"mcpServers":{}}""");
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));

        await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        _launcher.Started[0].ArgumentList.Should().EndWith(["--mcp-config", _mcpConfigPath]);
    }

    [Fact]
    public async Task ExecuteAsync_NoMcpConfigFile_PassesNoMcpConfig()
    {
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));

        await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        _launcher.Started[0].ArgumentList.Should().NotContain("--mcp-config");
    }

    // ── Output and usage ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_StreamsReadableLines_AndReturnsThem()
    {
        _launcher.Enqueue(Init("s1"), Text("hello"), Result("s1", 1, 1, 0, 0.01m, 1));
        var streamed = new List<string>();

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None, streamed.Add);

        result.OutputLines.Should().Equal("hello");
        streamed.Should().Equal("hello");
    }

    [Fact]
    public async Task ExecuteAsync_ReportsTokensCostAndDetails()
    {
        _env[AgentDefaults.EnvClaudeOAuthToken] = "oauth";
        _launcher.Enqueue(Init("s1"), Result("s1", input: 1000, output: 400, thinking: 150, cost: 0.42m, turns: 6, cacheRead: 9000, cacheWrite: 700));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.Usage.Should().BeEquivalentTo(new TokenUsage
        {
            InputTokens = 1000, OutputTokens = 250, ReasoningTokens = 150, CacheReadTokens = 9000, CacheWriteTokens = 700
        });
        result.Cost.Should().Be(0.42m);
        var details = result.UsageDetails!;
        details.BillingMode.Should().Be(AgentBillingModes.Subscription);
        details.Turns.Should().Be(6);
        details.ApiDurationSeconds.Should().Be(6);
        details.WebSearchRequests.Should().Be(1);
        details.ModelUsage["claude-opus-5-5"].Should().BeEquivalentTo(new AgentModelUsage
        {
            InputTokens = 1000, OutputTokens = 250, ReasoningTokens = 150, CacheReadTokens = 9000,
            CacheWriteTokens = 700, WebSearchRequests = 1, CostUsd = 0.42m
        });
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitEvents_AreReported()
    {
        _launcher.Enqueue(
            Init("s1"),
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour","utilization":0.3}}""",
            Result("s1", 1, 1, 0, 0.01m, 1));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.UsageDetails!.RateLimits.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AgentRateLimitObservation
            {
                Provider = "claude", Window = "five_hour", Status = "allowed", Utilization = 0.3
            });
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitWarning_IsShownAndReported()
    {
        _launcher.Enqueue(
            Init("s1"),
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"seven_day","utilization":0.91}}""",
            Result("s1", 1, 1, 0, 0.01m, 1));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.OutputLines.Should().ContainSingle(l => l.Contains("seven_day") && l.Contains("allowed_warning"));
        result.UsageDetails!.RateLimits.Should().ContainSingle()
            .Which.Utilization.Should().Be(0.91);
    }

    // ── Sessions ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UseResume_ResumesTheWorkspaceSession_AndReportsOnlyThisCallsUsage()
    {
        var provider = CreateProvider();
        _launcher.Enqueue(Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 0.10m, turns: 2));
        _launcher.Enqueue(Init("s1"), Result("s1", input: 160, output: 80, thinking: 0, cost: 0.25m, turns: 5));

        await provider.ExecuteAsync(Request(), CancellationToken.None);
        var second = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        _launcher.Started[0].ArgumentList.Should().NotContain("--resume");
        _launcher.Started[1].ArgumentList.Should().ContainInOrder("--resume", "s1");
        second.Usage!.InputTokens.Should().Be(60);
        second.Usage.OutputTokens.Should().Be(30);
        second.Cost.Should().Be(0.15m);
        second.UsageDetails!.Turns.Should().Be(5); // num_turns is per call, not cumulative
    }

    [Fact]
    public async Task ExecuteAsync_UseResumeWithoutEarlierSession_StartsAFreshSession()
    {
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));

        await CreateProvider().ExecuteAsync(Request(useResume: true), CancellationToken.None);

        _launcher.Started[0].ArgumentList.Should().NotContain("--resume");
    }

    [Fact]
    public async Task ExecuteAsync_ResumeSessionId_TargetsThatSession_AndKeepsTheWorkspaceSession()
    {
        var provider = CreateProvider();
        _launcher.Enqueue(Init("gen"), Result("gen", 1, 1, 0, 0.01m, 1));
        _launcher.Enqueue(Init("other"), Result("other", input: 70, output: 30, thinking: 0, cost: 0.07m, turns: 4));

        await provider.ExecuteAsync(Request(), CancellationToken.None);
        var resumed = await provider.ExecuteAsync(Request(resumeSessionId: "other"), CancellationToken.None);

        _launcher.Started[1].ArgumentList.Should().ContainInOrder("--resume", "other");
        (await provider.GetLatestSessionIdAsync(_workspace, CancellationToken.None)).Should().Be("gen");
        // Nothing was seen of "other" before, so its totals are reported in full.
        resumed.Usage!.InputTokens.Should().Be(70);
        resumed.Cost.Should().Be(0.07m);
        resumed.UsageDetails!.Turns.Should().Be(4);
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_ReturnsTheWorkspacesMainSession_WithoutStartingTheCli()
    {
        var provider = CreateProvider();
        (await provider.GetLatestSessionIdAsync(_workspace, CancellationToken.None)).Should().BeNull();

        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));
        _launcher.Enqueue(Init("s2"), Result("s2", 1, 1, 0, 0.01m, 1));
        await provider.ExecuteAsync(Request(), CancellationToken.None);
        await provider.ExecuteAsync(Request(), CancellationToken.None);

        (await provider.GetLatestSessionIdAsync(_workspace, CancellationToken.None)).Should().Be("s1");
        _launcher.Started.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExecuteAsync_IsolatedCallAfterTheMainSession_DoesNotBecomeWhatUseResumeContinues()
    {
        // Analysis starts the main conversation, an isolated reviewer runs, then codegen resumes.
        var provider = CreateProvider();
        _launcher.Enqueue(Init("analysis"), Result("analysis", 1, 1, 0, 0.01m, 1));
        _launcher.Enqueue(Init("reviewer"), Result("reviewer", 1, 1, 0, 0.01m, 1));
        _launcher.Enqueue(Init("analysis"), Result("analysis", 2, 2, 0, 0.02m, 1));

        await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);
        await provider.ExecuteAsync(Request(useResume: false), CancellationToken.None);
        await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        _launcher.Started[2].ArgumentList.Should().ContainInOrder("--resume", "analysis");
    }

    [Fact]
    public async Task ExecuteAsync_ResumeAfterAnIsolatedCallSaved_CountsTheResumedCallFromZero()
    {
        // The CLI restores saved totals only for the session it saved last in the workspace, which
        // after the reviewer is the reviewer's: codegen's totals are then its own.
        var provider = CreateProvider();
        _launcher.Enqueue(Init("analysis"), Result("analysis", input: 100, output: 50, thinking: 0, cost: 1m, turns: 1));
        _launcher.Enqueue(Init("reviewer"), Result("reviewer", input: 10, output: 5, thinking: 0, cost: 0.1m, turns: 1));
        _launcher.Enqueue(Init("analysis"), Result("analysis", input: 900, output: 400, thinking: 0, cost: 8m, turns: 1));

        await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);
        await provider.ExecuteAsync(Request(useResume: false), CancellationToken.None);
        var codegen = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        codegen.Usage!.InputTokens.Should().Be(900);
        codegen.Cost.Should().Be(8m);
    }

    [Fact]
    public async Task EnsureSessionAsync_IsANoOp()
    {
        await CreateProvider().EnsureSessionAsync(_workspace, CancellationToken.None);

        _launcher.Started.Should().BeEmpty();
    }

    // ── Failures ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ErrorResultWithExitZero_IsAFailure_ClassifiedFromTheStream()
    {
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1, isError: true, apiErrorStatus: 429));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderRateLimit);
        result.OutputLines.Should().Contain(l => l.Contains("boom"));
    }

    [Fact]
    public async Task ExecuteAsync_SuccessAfterRetries_HasNoErrorCategory()
    {
        _launcher.Enqueue(
            Init("s1"),
            """{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"error_status":529,"error":"overloaded"}""",
            Result("s1", 1, 1, 0, 0.01m, 1));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.ErrorCategory.Should().Be(AgentErrorCategory.None);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedOverageOnly_DoesNotMakeAFailureARateLimit()
    {
        // Accounts without extra usage report overageStatus "rejected" while the window is allowed,
        // as in this real event.
        _launcher.Enqueue(
            Init("s1"),
            ClaudeStreamJsonParserTests.RealRateLimitEvent,
            Result("s1", 1, 1, 0, 0.01m, 1, isError: true, apiErrorStatus: 400));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.ErrorCategory.Should().Be(AgentErrorCategory.None);
    }

    [Fact]
    public async Task ExecuteAsync_RecoveredRetryAndSubagentErrors_DoNotClassifyALaterFailure()
    {
        _launcher.Enqueue(
            Init("s1"),
            """{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"error_status":529,"error":"overloaded"}""",
            Text("recovered"),
            """{"type":"assistant","parent_tool_use_id":"tool_1","error":"rate_limit","message":{"content":[]}}""",
            Result("s1", 1, 1, 0, 0.01m, 1, isError: true));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.ErrorCategory.Should().Be(AgentErrorCategory.None);
    }

    [Fact]
    public async Task ExecuteAsync_FailureWithoutResult_AddsStderrToTheOutput()
    {
        _launcher.Enqueue(new FakeClaudeRun([], ExitCode: 2, Stderr: ["error: unknown option '--bogus'"]));

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(2);
        result.OutputLines.Should().Contain("error: unknown option '--bogus'");
        result.Usage.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_KillsTheProcessAndReturnsTimeout()
    {
        _launcher.Enqueue(new FakeClaudeRun([], Hang: true));
        var request = new AgentRequest { Prompt = "p", WorkspacePath = _workspace, Timeout = TimeSpan.FromMilliseconds(100) };

        var result = await CreateProvider().ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Timeout);
        _launcher.Processes[0].Killed.Should().BeTrue();
        _launcher.Processes[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_KillsTheProcessAndThrows()
    {
        _launcher.Enqueue(new FakeClaudeRun([], Hang: true));
        using var cts = new CancellationTokenSource();
        var provider = CreateProvider();

        var execution = provider.ExecuteAsync(Request(), cts.Token);
        await _launcher.ProcessesStarted(1);
        await cts.CancelAsync();

        await execution.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        _launcher.Processes[0].Killed.Should().BeTrue();
    }

    private static ClaudeExitTimings FastExit(int graceMs = 50, int stopMs = 200) =>
        new(TimeSpan.FromMilliseconds(graceMs), TimeSpan.FromMilliseconds(stopMs));

    [Theory]
    [InlineData(false, ExitCodes.Success)]
    [InlineData(true, ExitCodes.GeneralFailure)]
    public async Task ExecuteAsync_CliStillRunningAfterItsResult_IsAskedToExit_AndTheResultDecides(bool isError, int expectedExitCode)
    {
        // CLI 2.1.292+ keeps running after its result while background commands are still alive.
        _launcher.Enqueue(new FakeClaudeRun(
            [Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1, isError: isError)], KeepRunningAfterOutput: true));

        var result = await CreateProvider(timings: FastExit()).ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(expectedExitCode);
        result.Usage.Should().NotBeNull();
        _launcher.Processes[0].Terminated.Should().BeTrue();
        _launcher.Processes[0].Killed.Should().BeFalse();
        _launcher.Processes[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_FollowUpTurnAfterTheResult_RunsToItsOwnResult()
    {
        // A background command finishes 100 ms after the first result and starts a second turn,
        // which ends 300 ms later: past the 200 ms grace counted from the first result.
        _launcher.Enqueue(new FakeClaudeRun(
            [Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1)],
            KeepRunningAfterOutput: true,
            LaterStdout:
            [
                (TimeSpan.FromMilliseconds(100), [Init("s1"), Text("follow-up turn")]),
                (TimeSpan.FromMilliseconds(300), [Text("follow-up done"), Result("s1", 2, 2, 0, 0.02m, 1, isError: true)])
            ]));

        var result = await CreateProvider(timings: FastExit(graceMs: 200)).ExecuteAsync(Request(), CancellationToken.None);

        result.OutputLines.Should().Contain("follow-up done");
        result.ExitCode.Should().Be(ExitCodes.GeneralFailure, "the last turn's result decides");
        _launcher.Processes[0].Terminated.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CliThatIgnoresSigterm_IsKilled_AndTheNextResumeIsMeasuredAgainstSavedTotals()
    {
        // Killed before it could save its totals, the CLI restores none on the next resume, so that
        // call's cumulative totals are all its own.
        var provider = CreateProvider(timings: FastExit());
        _launcher.Enqueue(new FakeClaudeRun(
            [Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 0.10m, turns: 1)],
            KeepRunningAfterOutput: true, IgnoresTerminate: true));
        _launcher.Enqueue(Init("s1"), Result("s1", input: 160, output: 80, thinking: 0, cost: 0.25m, turns: 1));

        var first = await provider.ExecuteAsync(Request(), CancellationToken.None);
        var second = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        first.ExitCode.Should().Be(ExitCodes.Success);
        _launcher.Processes[0].Terminated.Should().BeTrue();
        _launcher.Processes[0].Killed.Should().BeTrue();
        second.Usage!.InputTokens.Should().Be(160);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutWhileLingeringAfterItsResult_KeepsTheResult_AndCountsTheKill()
    {
        // The result came in, then the request timeout ran out within the stop grace.
        var provider = CreateProvider(timings: FastExit(graceMs: 10_000));
        _launcher.Enqueue(new FakeClaudeRun(
            [Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 0.10m, turns: 1)],
            KeepRunningAfterOutput: true));
        _launcher.Enqueue(Init("s1"), Result("s1", input: 160, output: 80, thinking: 0, cost: 0.25m, turns: 1));
        var request = new AgentRequest { Prompt = "p", WorkspacePath = _workspace, Timeout = TimeSpan.FromMilliseconds(200) };

        var first = await provider.ExecuteAsync(request, CancellationToken.None);
        var second = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        first.ExitCode.Should().Be(ExitCodes.Success);
        _launcher.Processes[0].Killed.Should().BeTrue();
        second.Usage!.InputTokens.Should().Be(160, "the killed CLI saved nothing to restore");
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutDuringAFollowUpTurn_IsATimeout()
    {
        _launcher.Enqueue(new FakeClaudeRun(
            [Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1)],
            KeepRunningAfterOutput: true,
            LaterStdout: [(TimeSpan.FromMilliseconds(50), [Init("s1"), Text("follow-up turn")])]));
        var request = new AgentRequest { Prompt = "p", WorkspacePath = _workspace, Timeout = TimeSpan.FromMilliseconds(300) };

        var result = await CreateProvider(timings: FastExit(graceMs: 10_000)).ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Timeout);
    }

    [Fact]
    public async Task ExecuteAsync_CliStillRunningWithoutAResult_IsNotStoppedEarly()
    {
        _launcher.Enqueue(new FakeClaudeRun([Init("s1"), Text("working")], KeepRunningAfterOutput: true));
        var request = new AgentRequest { Prompt = "p", WorkspacePath = _workspace, Timeout = TimeSpan.FromMilliseconds(300) };

        var result = await CreateProvider(timings: FastExit(graceMs: 10)).ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Timeout);
        _launcher.Processes[0].Killed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CliCannotStart_ReturnsGeneralFailure()
    {
        _launcher.StartException = new System.ComponentModel.Win32Exception("No such file or directory");

        var result = await CreateProvider().ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.OutputLines.Should().ContainSingle().Which.Should().Contain("/opt/claude");
    }

    // ── Health and kill ────────────────────────────────────────────────

    [Fact]
    public async Task GetHealthStatus_AndKillAsync_CoverTheRunningProcess()
    {
        _launcher.Enqueue(new FakeClaudeRun([], Hang: true));
        var provider = CreateProvider();
        provider.GetHealthStatus().IsExecuting.Should().BeFalse();

        var execution = provider.ExecuteAsync(
            new AgentRequest { Prompt = "p", WorkspacePath = _workspace, Timeout = TimeSpan.FromSeconds(30) },
            CancellationToken.None);
        await _launcher.ProcessesStarted(1);

        var health = provider.GetHealthStatus();
        health.IsExecuting.Should().BeTrue();
        health.ProcessId.Should().Be(4242);
        health.IsProcessAlive.Should().BeTrue();

        await provider.KillAsync();
        _launcher.Processes[0].Killed.Should().BeTrue();

        var result = await execution;
        result.ExitCode.Should().Be(FakeClaudeProcess.KilledExitCode);
        provider.GetHealthStatus().IsExecuting.Should().BeFalse();
    }

    // ── Validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_RunsVersionCheck()
    {
        _launcher.Enqueue("2.1.296 (Claude Code)");

        await CreateProvider().ValidateAsync(CancellationToken.None);

        _launcher.Started.Should().ContainSingle().Which.ArgumentList.Should().Equal("--version");
    }

    [Fact]
    public async Task ValidateAsync_VersionCheckFails_Throws()
    {
        _launcher.Enqueue(new FakeClaudeRun(["boom"], ExitCode: 1));

        await CreateProvider().Invoking(p => p.ValidateAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*exited with code 1*");
    }

    [Fact]
    public async Task ValidateAsync_ExplicitModeWithoutCredential_Throws()
    {
        await CreateProvider(ClaudeCodeAuthModes.ApiKey).Invoking(p => p.ValidateAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*claude-api-key*");
        _launcher.Started.Should().BeEmpty();
    }

    // ── Arguments and properties ───────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void BuildArguments_NoExplicitModel_LeavesModelToTheCli(string? model)
    {
        ClaudeCodeAgentProvider.BuildArguments(model, AgentEffortLevel.Auto, null, null)
            .Should().NotContain("--model").And.NotContain("--effort");
    }

    [Fact]
    public void BuildArguments_AllOptions_InOrder()
    {
        ClaudeCodeAgentProvider.BuildArguments("claude-opus-4-8", AgentEffortLevel.XHigh, "sess", "/m.json")
            .Should().Equal(
                "-p", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions",
                "--model", "claude-opus-4-8", "--effort", "xhigh", "--resume", "sess", "--mcp-config", "/m.json");
    }

    [Fact]
    public void Properties_DescribeTheProvider()
    {
        var provider = CreateProvider(authMode: "subscription", model: "opus");

        provider.ProviderType.Should().Be(AgentProviderType.ClaudeCode);
        provider.Model.Should().Be("opus");
        provider.AuthMode.Should().Be(ClaudeCodeAuthModes.Subscription);
        provider.SupportsParallelExecution.Should().BeTrue();
        provider.SupportsVisionInput.Should().BeTrue();
        provider.McpConfigPath.Should().Be(_mcpConfigPath);
        provider.PipelineInjectedPaths.Should().Equal("CLAUDE.local.md", ".claude/settings.local.json");
    }

    [Fact]
    public void Constructor_DefaultMcpConfigPath_IsInTheUsersClaudeDirectory()
    {
        var provider = new ClaudeCodeAgentProvider();

        provider.McpConfigPath.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "pipeline-mcp.json"));
    }
}

internal static class FakeClaudeProcessLauncherExtensions
{
    /// <summary>Waits until <paramref name="count"/> processes were started and are waiting to exit.</summary>
    public static async Task ProcessesStarted(this FakeClaudeProcessLauncher launcher, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (launcher.Processes.Count < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {count} started process(es), saw {launcher.Processes.Count}.");
            await Task.Delay(10);
        }
        await launcher.Processes[count - 1].Running.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
