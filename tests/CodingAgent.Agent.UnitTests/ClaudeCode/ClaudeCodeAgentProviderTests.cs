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
        // Spec 056: the default auth mode is the subscription; tests that need no credential clear it.
        _env[AgentDefaults.EnvClaudeOAuthToken] = "oauth-test";
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ClaudeCodeAgentProvider CreateProvider(
        string? authMode = ClaudeCodeAuthModes.Subscription,
        string? model = "claude-opus-5-5",
        AgentEffortLevel effort = AgentEffortLevel.High,
        ClaudeExitTimings? timings = null,
        Serilog.ILogger? logger = null) =>
        new(logger ?? new Mock<Serilog.ILogger>().Object,
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
        type = "system",
        subtype = "init",
        session_id = sessionId,
        model = "claude-opus-5-5"
    });

    private static string Text(string text) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        parent_tool_use_id = (string?)null,
        message = new { content = new[] { new { type = "text", text } } }
    });

    private static string Result(
        string sessionId, long input, long output, long thinking, decimal cost, int turns,
        bool isError = false, int? apiErrorStatus = null, long cacheRead = 0, long cacheWrite = 0, long? apiDurationMs = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = isError ? "error_during_execution" : "success",
            ["is_error"] = isError,
            ["session_id"] = sessionId,
            ["result"] = isError ? "boom" : "ok",
            ["total_cost_usd"] = cost,
            ["num_turns"] = turns,
            ["duration_api_ms"] = apiDurationMs ?? turns * 1000,
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

    // Spec 056 D8: the former auto mode (and any unknown value) is the subscription, even when an API key is set.
    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("something")]
    public async Task ExecuteAsync_FormerAutoOrUnknownMode_UsesTheSubscriptionToken(string authMode)
    {
        _env[AgentDefaults.EnvClaudeApiKey] = "sk-key";
        _launcher.Enqueue(Init("s1"), Result("s1", 1, 1, 0, 0.01m, 1));

        var result = await CreateProvider(authMode).ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.UsageDetails!.BillingMode.Should().Be(AgentBillingModes.Subscription);
        _launcher.Started[0].Environment["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be("oauth-test");
        _launcher.Started[0].Environment.Should().NotContainKey("ANTHROPIC_API_KEY");
    }

    [Theory]
    [InlineData(ClaudeCodeAuthModes.ApiKey)]
    [InlineData(ClaudeCodeAuthModes.Subscription)]
    public async Task ExecuteAsync_ExplicitModeWithoutItsCredential_FailsWithoutStartingTheCli(string authMode)
    {
        _env.Clear();
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
            InputTokens = 1000,
            OutputTokens = 250,
            ReasoningTokens = 150,
            CacheReadTokens = 9000,
            CacheWriteTokens = 700
        });
        result.Cost.Should().Be(0.42m);
        var details = result.UsageDetails!;
        details.BillingMode.Should().Be(AgentBillingModes.Subscription);
        details.Turns.Should().Be(6);
        details.ApiDurationSeconds.Should().Be(6);
        details.WebSearchRequests.Should().Be(1);
        details.ModelUsage["claude-opus-5-5"].Should().BeEquivalentTo(new AgentModelUsage
        {
            InputTokens = 1000,
            OutputTokens = 250,
            ReasoningTokens = 150,
            CacheReadTokens = 9000,
            CacheWriteTokens = 700,
            WebSearchRequests = 1,
            CostUsd = 0.42m
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
                Provider = "claude",
                Window = "five_hour",
                Status = "allowed",
                Utilization = 0.3
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
        // A resumed call's result reports the session's running total, so the earlier call's totals are subtracted.
        second.Usage!.InputTokens.Should().Be(60);
        second.Usage.OutputTokens.Should().Be(30);
        second.Cost.Should().Be(0.15m);
        second.UsageDetails!.Turns.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ResumedTwice_ReportsEachCallsOwnShare()
    {
        var provider = CreateProvider();
        _launcher.Enqueue(Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 6.65m, turns: 6));
        _launcher.Enqueue(Init("s1"), Result("s1", input: 160, output: 80, thinking: 0, cost: 7.48m, turns: 12));
        _launcher.Enqueue(Init("s1"), Result("s1", input: 250, output: 120, thinking: 0, cost: 9.30m, turns: 44));

        var first = await provider.ExecuteAsync(Request(), CancellationToken.None);
        var second = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);
        var third = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        first.Cost.Should().Be(6.65m);
        second.Cost.Should().Be(0.83m);
        third.Cost.Should().Be(1.82m);
        first.Usage!.InputTokens.Should().Be(100);
        second.Usage!.InputTokens.Should().Be(60);
        third.Usage!.InputTokens.Should().Be(90);
        first.UsageDetails!.Turns.Should().Be(6);
        second.UsageDetails!.Turns.Should().Be(6);
        third.UsageDetails!.Turns.Should().Be(32);
        // The model's own figures are the call's share too, not the running total.
        third.UsageDetails.ModelUsage["claude-opus-5-5"].InputTokens.Should().Be(90);
        third.UsageDetails.ModelUsage["claude-opus-5-5"].CostUsd.Should().Be(1.82m);
    }

    [Fact]
    public async Task ExecuteAsync_TotalsGoingBackwards_AreTakenAsTheyAre()
    {
        var logger = new Mock<Serilog.ILogger>();
        var provider = CreateProvider(logger: logger.Object);
        _launcher.Enqueue(Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 0.25m, turns: 5));
        _launcher.Enqueue(Init("s1"), Result("s1", input: 20, output: 10, thinking: 0, cost: 0.05m, turns: 1));
        _launcher.Enqueue(Init("s1"), Result("s1", input: 60, output: 30, thinking: 0, cost: 0.20m, turns: 3));

        await provider.ExecuteAsync(Request(), CancellationToken.None);
        var second = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);
        var third = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        second.Cost.Should().Be(0.05m);
        second.Usage!.InputTokens.Should().Be(20);
        second.UsageDetails!.Turns.Should().Be(1);
        // The smaller totals are the baseline now: the next call is its share on top of them.
        third.Cost.Should().Be(0.15m);
        third.Usage!.InputTokens.Should().Be(40);
        third.UsageDetails!.Turns.Should().Be(2);
        logger.Verify(l => l.Warning(
            "Claude Code session {SessionId} reported smaller totals than its previous call; using them as they are",
            "s1"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentSessions_AreNotSubtractedFromEachOther()
    {
        var provider = CreateProvider();
        _launcher.Enqueue(Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 0.10m, turns: 2));
        // Every figure of s2 is larger than s1's, so subtracting s1 from s2 would not look like a total going backwards.
        _launcher.Enqueue(Init("s2"), Result("s2", input: 170, output: 80, thinking: 0, cost: 0.17m, turns: 4));

        await provider.ExecuteAsync(Request(), CancellationToken.None);
        var second = await provider.ExecuteAsync(Request(useResume: false), CancellationToken.None);

        second.Usage!.InputTokens.Should().Be(170);
        second.Usage.OutputTokens.Should().Be(80);
        second.Cost.Should().Be(0.17m);
        second.UsageDetails!.Turns.Should().Be(4);
    }

    [Fact]
    public void ReplaceSessionTotals_ReturnsTheTotalsItReplaced_PerSession()
    {
        var provider = CreateProvider();
        var first = new ClaudeUsageTotals { InputTokens = 1 };
        var second = new ClaudeUsageTotals { InputTokens = 2 };

        provider.ReplaceSessionTotals("s1", first).Should().BeNull();
        provider.ReplaceSessionTotals("s2", new ClaudeUsageTotals { InputTokens = 9 }).Should().BeNull();
        provider.ReplaceSessionTotals("s1", second).Should().BeSameAs(first);
        provider.ReplaceSessionTotals("s1", new ClaudeUsageTotals { InputTokens = 3 }).Should().BeSameAs(second);
    }

    [Fact]
    public async Task ExecuteAsync_ResumedCallWithFewerTurnsThanBefore_StillSubtractsTheCost()
    {
        // The turn counts and running totals of widow's run a5a27313 (issue #4293). A call with fewer
        // turns than an earlier one is no total going backwards, so its cost is still its share. Only
        // the fourth call is asserted: how the turns of the calls before it are reported is open
        // (see SubtractTurns).
        var logger = new Mock<Serilog.ILogger>();
        var provider = CreateProvider(logger: logger.Object);
        _launcher.Enqueue(Init("s1"), Result("s1", 100, 50, 0, cost: 6.65m, turns: 6, apiDurationMs: 1_679_000));
        _launcher.Enqueue(Init("s1"), Result("s1", 160, 80, 0, cost: 7.48m, turns: 6, apiDurationMs: 1_810_000));
        _launcher.Enqueue(Init("s1"), Result("s1", 250, 120, 0, cost: 9.30m, turns: 32, apiDurationMs: 2_113_000));
        _launcher.Enqueue(Init("s1"), Result("s1", 300, 140, 0, cost: 10.09m, turns: 3, apiDurationMs: 2_141_000));

        await provider.ExecuteAsync(Request(), CancellationToken.None);
        await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);
        await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);
        var fourth = await provider.ExecuteAsync(Request(useResume: true), CancellationToken.None);

        fourth.Cost.Should().Be(0.79m);
        fourth.Usage!.InputTokens.Should().Be(50);
        fourth.UsageDetails!.Turns.Should().Be(3);
        fourth.UsageDetails.ApiDurationSeconds.Should().Be(28);
        logger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Subtract_SubtractsEveryFieldAndEachModel_KeepsTheContextWindow()
    {
        var previous = new ClaudeUsageTotals
        {
            InputTokens = 100,
            OutputTokens = 50,
            ThinkingTokens = 10,
            CacheReadTokens = 1_000,
            CacheWriteTokens = 200,
            WebSearchRequests = 1,
            CostUsd = 0.40m,
            Turns = 4,
            ApiDurationMs = 5_000,
            ContextWindowTokens = 200_000,
            Models = new Dictionary<string, ClaudeModelTotals>
            {
                ["opus"] = new()
                {
                    InputTokens = 100,
                    OutputTokens = 50,
                    ThinkingTokens = 10,
                    CacheReadTokens = 1_000,
                    CacheWriteTokens = 200,
                    WebSearchRequests = 1,
                    CostUsd = 0.40m
                }
            }
        };
        var current = new ClaudeUsageTotals
        {
            InputTokens = 160,
            OutputTokens = 90,
            ThinkingTokens = 25,
            CacheReadTokens = 1_800,
            CacheWriteTokens = 260,
            WebSearchRequests = 3,
            CostUsd = 0.95m,
            Turns = 9,
            ApiDurationMs = 12_500,
            ContextWindowTokens = 1_000_000,
            Models = new Dictionary<string, ClaudeModelTotals>
            {
                ["opus"] = new()
                {
                    InputTokens = 130,
                    OutputTokens = 70,
                    ThinkingTokens = 20,
                    CacheReadTokens = 1_500,
                    CacheWriteTokens = 230,
                    WebSearchRequests = 2,
                    CostUsd = 0.80m
                },
                ["haiku"] = new()
                {
                    InputTokens = 30,
                    OutputTokens = 20,
                    ThinkingTokens = 5,
                    CacheReadTokens = 300,
                    CacheWriteTokens = 30,
                    WebSearchRequests = 1,
                    CostUsd = 0.15m
                }
            }
        };

        var result = ClaudeCodeAgentProvider.Subtract(current, previous);

        result.InputTokens.Should().Be(60);
        result.OutputTokens.Should().Be(40);
        result.ThinkingTokens.Should().Be(15);
        result.CacheReadTokens.Should().Be(800);
        result.CacheWriteTokens.Should().Be(60);
        result.WebSearchRequests.Should().Be(2);
        result.CostUsd.Should().Be(0.55m);
        result.Turns.Should().Be(5);
        result.ApiDurationMs.Should().Be(7_500);
        // The context window is a property of the model, not a counter: it is taken as it is.
        result.ContextWindowTokens.Should().Be(1_000_000);
        result.Models.Should().HaveCount(2);
        result.Models["opus"].Should().Be(new ClaudeModelTotals
        {
            InputTokens = 30,
            OutputTokens = 20,
            ThinkingTokens = 10,
            CacheReadTokens = 500,
            CacheWriteTokens = 30,
            WebSearchRequests = 1,
            CostUsd = 0.40m
        });
        // A model the earlier totals do not have is taken as it is.
        result.Models["haiku"].Should().Be(current.Models["haiku"]);
    }

    [Fact]
    public void Subtract_FewerTurnsThanTheStoredOnes_TakesTheCurrentTurns()
    {
        var previous = new ClaudeUsageTotals { Turns = 32 };

        ClaudeCodeAgentProvider.Subtract(new ClaudeUsageTotals { Turns = 3 }, previous).Turns.Should().Be(3);
        ClaudeCodeAgentProvider.Subtract(new ClaudeUsageTotals { Turns = 33 }, previous).Turns.Should().Be(1);
    }

    [Fact]
    public void Subtract_CostMissingOnEitherSide_KeepsTheCurrentCost()
    {
        var known = new ClaudeUsageTotals { CostUsd = 0.40m };
        var unknown = new ClaudeUsageTotals { CostUsd = null };

        ClaudeCodeAgentProvider.Subtract(known, unknown).CostUsd.Should().Be(0.40m);
        ClaudeCodeAgentProvider.Subtract(unknown, known).CostUsd.Should().BeNull();
        ClaudeCodeAgentProvider.Subtract(unknown, unknown).CostUsd.Should().BeNull();
    }

    private static ClaudeUsageTotals RunningTotal() => new()
    {
        InputTokens = 10,
        OutputTokens = 10,
        ThinkingTokens = 10,
        CacheReadTokens = 10,
        CacheWriteTokens = 10,
        WebSearchRequests = 10,
        CostUsd = 1m,
        Turns = 10,
        ApiDurationMs = 10,
        ContextWindowTokens = 10,
        Models = new Dictionary<string, ClaudeModelTotals>
        {
            ["opus"] = new()
            {
                InputTokens = 10,
                OutputTokens = 10,
                ThinkingTokens = 10,
                CacheReadTokens = 10,
                CacheWriteTokens = 10,
                WebSearchRequests = 10,
                CostUsd = 1m
            }
        }
    };

    private static ClaudeUsageTotals WithOpus(ClaudeUsageTotals totals, Func<ClaudeModelTotals, ClaudeModelTotals> change) =>
        totals with { Models = new Dictionary<string, ClaudeModelTotals> { ["opus"] = change(totals.Models["opus"]) } };

    private static readonly (string Figure, Func<ClaudeUsageTotals, ClaudeUsageTotals> MakeSmaller)[] SmallerFigures =
    [
        ("InputTokens", t => t with { InputTokens = 9 }),
        ("OutputTokens", t => t with { OutputTokens = 9 }),
        ("ThinkingTokens", t => t with { ThinkingTokens = 9 }),
        ("CacheReadTokens", t => t with { CacheReadTokens = 9 }),
        ("CacheWriteTokens", t => t with { CacheWriteTokens = 9 }),
        ("WebSearchRequests", t => t with { WebSearchRequests = 9 }),
        ("CostUsd", t => t with { CostUsd = 0.5m }),
        ("ApiDurationMs", t => t with { ApiDurationMs = 9 }),
        ("Model.InputTokens", t => WithOpus(t, m => m with { InputTokens = 9 })),
        ("Model.OutputTokens", t => WithOpus(t, m => m with { OutputTokens = 9 })),
        ("Model.ThinkingTokens", t => WithOpus(t, m => m with { ThinkingTokens = 9 })),
        ("Model.CacheReadTokens", t => WithOpus(t, m => m with { CacheReadTokens = 9 })),
        ("Model.CacheWriteTokens", t => WithOpus(t, m => m with { CacheWriteTokens = 9 })),
        ("Model.WebSearchRequests", t => WithOpus(t, m => m with { WebSearchRequests = 9 })),
        ("Model.CostUsd", t => WithOpus(t, m => m with { CostUsd = 0.5m }))
    ];

    [Fact]
    public void WentBackwards_AnyFigureSmallerThanTheStoredOne_IsTrue()
    {
        var previous = RunningTotal();

        foreach (var (figure, makeSmaller) in SmallerFigures)
            ClaudeCodeAgentProvider.WentBackwards(makeSmaller(previous), previous).Should().BeTrue(figure);
    }

    [Fact]
    public void WentBackwards_EqualOrLargerFigures_IsFalse()
    {
        var previous = RunningTotal();
        var larger = previous with
        {
            InputTokens = 11,
            OutputTokens = 11,
            ThinkingTokens = 11,
            CacheReadTokens = 11,
            CacheWriteTokens = 11,
            WebSearchRequests = 11,
            CostUsd = 1.5m,
            Turns = 11,
            ApiDurationMs = 11,
            Models = new Dictionary<string, ClaudeModelTotals>
            {
                ["opus"] = previous.Models["opus"] with { InputTokens = 11, CostUsd = 1.5m },
                ["haiku"] = new() { InputTokens = 1 }
            }
        };

        ClaudeCodeAgentProvider.WentBackwards(previous, previous).Should().BeFalse();
        ClaudeCodeAgentProvider.WentBackwards(larger, previous).Should().BeFalse();
    }

    [Fact]
    public void WentBackwards_FewerTurns_IsNotATotalGoingBackwards()
    {
        var previous = RunningTotal();

        ClaudeCodeAgentProvider.WentBackwards(previous with { Turns = 3 }, previous).Should().BeFalse();
    }

    [Fact]
    public void WentBackwards_IgnoresTheContextWindow_AndACostMissingOnEitherSide()
    {
        var previous = RunningTotal();

        // A smaller context window is no counter going backwards, and a missing cost is not comparable.
        ClaudeCodeAgentProvider.WentBackwards(previous with { ContextWindowTokens = 1 }, previous).Should().BeFalse();
        ClaudeCodeAgentProvider.WentBackwards(previous with { CostUsd = null }, previous).Should().BeFalse();
        ClaudeCodeAgentProvider.WentBackwards(previous, previous with { CostUsd = null }).Should().BeFalse();
        ClaudeCodeAgentProvider.WentBackwards(
            WithOpus(previous, m => m with { CostUsd = null }), previous).Should().BeFalse();
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
        // The result reports that call alone.
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
    public async Task ExecuteAsync_CliThatIgnoresSigterm_IsKilled_AndTheResultStands()
    {
        _launcher.Enqueue(new FakeClaudeRun(
            [Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 0.10m, turns: 1)],
            KeepRunningAfterOutput: true, IgnoresTerminate: true));

        var result = await CreateProvider(timings: FastExit()).ExecuteAsync(Request(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Usage!.InputTokens.Should().Be(100);
        _launcher.Processes[0].Terminated.Should().BeTrue();
        _launcher.Processes[0].Killed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutWhileLingeringAfterItsResult_KeepsTheResult()
    {
        // The result came in, then the request timeout ran out within the stop grace.
        _launcher.Enqueue(new FakeClaudeRun(
            [Init("s1"), Result("s1", input: 100, output: 50, thinking: 0, cost: 0.10m, turns: 1)],
            KeepRunningAfterOutput: true));
        var request = new AgentRequest { Prompt = "p", WorkspacePath = _workspace, Timeout = TimeSpan.FromMilliseconds(200) };

        var result = await CreateProvider(timings: FastExit(graceMs: 10_000)).ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Usage!.InputTokens.Should().Be(100);
        _launcher.Processes[0].Killed.Should().BeTrue();
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

    // Spec 056 DD16: typed, so the provider failure decorator records auth_failed and pauses that credential.
    [Theory]
    [InlineData(ClaudeCodeAuthModes.ApiKey, AgentCredentials.ClaudeApiKey, "*claude-api-key*")]
    [InlineData(ClaudeCodeAuthModes.Subscription, AgentCredentials.ClaudeSubscription, "*claude-oauth-token*")]
    public async Task ValidateAsync_ModeWithoutItsCredential_ThrowsProviderCredentialMissing(string authMode, string credential, string message)
    {
        _env.Clear();

        (await CreateProvider(authMode).Invoking(p => p.ValidateAsync(CancellationToken.None))
                .Should().ThrowAsync<ProviderCredentialMissingException>().WithMessage(message))
            .Which.Credential.Should().Be(credential);
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

    // ── MCP tool call counting ───────────────────────────────────────────

    private (ClaudeCodeAgentProvider Provider, Mock<Serilog.ILogger> LoggerMock) CreateProviderWithMock(
        string? authMode = ClaudeCodeAuthModes.Subscription)
    {
        var loggerMock = new Mock<Serilog.ILogger>();
        var provider = new ClaudeCodeAgentProvider(
            loggerMock.Object,
            new ClaudeCodeSettings("claude-opus-5-5", "/opt/claude", AgentEffortLevel.High, authMode, _mcpConfigPath),
            _launcher, name => _env.GetValueOrDefault(name));
        return (provider, loggerMock);
    }

    [Fact]
    public async Task ExecuteAsync_McpToolCalls_AreSetFromStream()
    {
        var (provider, _) = CreateProviderWithMock();
        _launcher.Enqueue(new FakeClaudeRun([
            """{"type":"system","subtype":"init","session_id":"s1","model":"m","mcp_servers":[{"name":"brokenserver","status":"failed"}]}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__sonarqube__get_issues","input":{}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"404 Not Found"}]}}""",
            Result("s1", 10, 5, 0, 0.01m, 1)
        ]));

        var result = await provider.ExecuteAsync(Request(), CancellationToken.None);

        result.ToolCalls.Should().NotBeNull();
        result.ToolCalls!["sonarqube/get_issues"].Calls.Should().Be(1);
        result.ToolCalls["sonarqube/get_issues"].FailedOther.Should().Be(1);
        result.UnavailableMcpServers.Should().NotBeNull();
        result.UnavailableMcpServers!["brokenserver"].Should().Be("failed");
    }

    [Fact]
    public async Task ExecuteAsync_NoMcpToolCalls_ToolCallsAndUnavailableAreNull()
    {
        var (provider, _) = CreateProviderWithMock();
        _launcher.Enqueue(new FakeClaudeRun([
            Init("s1"),
            Text("just text"),
            Result("s1", 10, 5, 0, 0.01m, 1)
        ]));

        var result = await provider.ExecuteAsync(Request(), CancellationToken.None);

        result.ToolCalls.Should().BeNull();
        result.UnavailableMcpServers.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_FailedMcpCall_LogsOneWarning()
    {
        var (provider, loggerMock) = CreateProviderWithMock();
        _launcher.Enqueue(new FakeClaudeRun([
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__sonarqube__get_issues","input":{}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"Request failed"}]}}""",
            Result("s1", 10, 5, 0, 0.01m, 1)
        ]));

        await provider.ExecuteAsync(Request(), CancellationToken.None);

        loggerMock.Verify(
            l => l.Warning(It.Is<string>(s => s.Contains("MCP call failed")), It.IsAny<object?[]>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FailedMcpCall_SecretInErrorTextIsMasked()
    {
        var (provider, loggerMock) = CreateProviderWithMock();
        _launcher.Enqueue(new FakeClaudeRun([
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__sonarqube__get_issues","input":{}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"secretValue123 was rejected"}]}}""",
            Result("s1", 10, 5, 0, 0.01m, 1)
        ]));

        object?[]? capturedArgs = null;
        loggerMock
            .Setup(l => l.Warning(It.Is<string>(s => s.Contains("MCP call failed")), It.IsAny<object?[]>()))
            .Callback<string, object?[]?>((_, args) => capturedArgs = args);

        var request = new AgentRequest
        {
            Prompt = "do it",
            WorkspacePath = _workspace,
            Timeout = TimeSpan.FromMinutes(1),
            EnvironmentVariables = new Dictionary<string, string> { ["MY_TOKEN"] = "secretValue123" }
        };
        await provider.ExecuteAsync(request, CancellationToken.None);

        capturedArgs.Should().NotBeNull();
        // Index 3 = error text (server, tool, category, error)
        capturedArgs![3].As<string>().Should().NotContain("secretValue123");
        capturedArgs[3].As<string>().Should().Contain("***");
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
