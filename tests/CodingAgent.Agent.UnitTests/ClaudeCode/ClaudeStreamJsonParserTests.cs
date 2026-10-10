using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Agent.ClaudeCode;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

public class ClaudeStreamJsonParserTests
{
    private readonly ClaudeStreamState _state = new();

    [Fact]
    public void ProcessLine_SystemInit_CapturesSessionAndModel_EmitsNothing()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"system","subtype":"init","session_id":"sess-1","model":"claude-opus-5-5","tools":["Bash"]}""",
            _state);

        lines.Should().BeEmpty();
        _state.SessionId.Should().Be("sess-1");
        _state.Model.Should().Be("claude-opus-5-5");
    }

    [Fact]
    public void ProcessLine_AssistantText_SplitsIntoLines()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"assistant","session_id":"s","parent_tool_use_id":null,"message":{"content":[{"type":"text","text":"first\nsecond"}]}}""",
            _state);

        lines.Should().Equal("first", "second");
    }

    [Fact]
    public void ProcessLine_AssistantToolUse_SummarizesWithTheToolsKeyField()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet test\n--no-build","description":"run tests"}},{"type":"tool_use","name":"Edit","input":{"file_path":"src/a.cs","old_string":"x"}}]}}""",
            _state);

        lines.Should().Equal("▶ Bash: dotnet test", "▶ Edit: src/a.cs");
    }

    [Fact]
    public void ProcessLine_SubagentMessage_IsIndented()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"assistant","parent_tool_use_id":"toolu_1","message":{"content":[{"type":"text","text":"sub"},{"type":"tool_use","name":"Grep","input":{"pattern":"TODO"}}]}}""",
            _state);

        lines.Should().Equal("  sub", "  ▶ Grep: TODO");
    }

    [Fact]
    public void ProcessLine_ThinkingBlock_IsNotEmitted()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":""}]}}""",
            _state);

        lines.Should().BeEmpty();
    }

    [Fact]
    public void ProcessLine_AssistantError_ClassifiesTheFailure()
    {
        ClaudeStreamJsonParser.ProcessLine(
            """{"type":"assistant","error":"rate_limit","message":{"content":[]}}""",
            _state);

        _state.LastErrorCategory.Should().Be("rate_limit");
        _state.ClassifyFailure().Should().Be(AgentErrorCategory.ProviderRateLimit);
    }

    [Fact]
    public void ProcessLine_ApiRetry_RecordsErrorAndEmitsWarning()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":500,"error_status":529,"error":"overloaded"}""",
            _state);

        lines.Should().ContainSingle().Which.Should().Contain("overloaded").And.Contain("HTTP 529");
        _state.LastErrorCategory.Should().Be("overloaded");
        _state.LastErrorStatus.Should().Be(529);
    }

    [Fact]
    public void ProcessLine_Result_ParsesModelUsageTotals()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """
            {"type":"result","subtype":"success","is_error":false,"session_id":"sess-2","result":"done",
             "total_cost_usd":0.4321,"num_turns":7,"duration_api_ms":12500,
             "usage":{"input_tokens":1,"output_tokens":1},
             "modelUsage":{
               "claude-opus-5-5":{"inputTokens":1000,"outputTokens":400,"thinkingTokens":150,"cacheReadInputTokens":5000,"cacheCreationInputTokens":800,"webSearchRequests":2,"costUSD":0.4},
               "claude-haiku-4-5":{"inputTokens":200,"outputTokens":50,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"webSearchRequests":0,"costUSD":0.0321}}}
            """.ReplaceLineEndings(" "),
            _state);

        lines.Should().BeEmpty();
        _state.ResultSeen.Should().BeTrue();
        _state.ResultIsError.Should().BeFalse();
        _state.SessionId.Should().Be("sess-2");
        var totals = _state.Totals!;
        totals.InputTokens.Should().Be(1200);
        totals.OutputTokens.Should().Be(450);
        totals.ThinkingTokens.Should().Be(150);
        totals.CacheReadTokens.Should().Be(5000);
        totals.CacheWriteTokens.Should().Be(800);
        totals.WebSearchRequests.Should().Be(2);
        totals.CostUsd.Should().Be(0.4321m);
        totals.Turns.Should().Be(7);
        totals.ApiDurationMs.Should().Be(12500);
        totals.Models.Should().ContainKeys("claude-opus-5-5", "claude-haiku-4-5");
        totals.Models["claude-haiku-4-5"].CostUsd.Should().Be(0.0321m);
    }

    [Fact]
    public void ProcessLine_ResultWithoutModelUsage_FallsBackToUsage()
    {
        ClaudeStreamJsonParser.ProcessLine(
            """{"type":"result","subtype":"success","is_error":false,"session_id":"s","total_cost_usd":0.01,"num_turns":1,"usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":30,"cache_creation_input_tokens":40,"server_tool_use":{"web_search_requests":3}}}""",
            _state);

        var totals = _state.Totals!;
        totals.InputTokens.Should().Be(10);
        totals.OutputTokens.Should().Be(20);
        totals.CacheReadTokens.Should().Be(30);
        totals.CacheWriteTokens.Should().Be(40);
        totals.WebSearchRequests.Should().Be(3);
        totals.Models.Should().BeEmpty();
    }

    [Fact]
    public void ProcessLine_ModelUsageWithoutTokens_FallsBackToUsage_KeepsPerModelCost()
    {
        ClaudeStreamJsonParser.ProcessLine(
            """{"type":"result","subtype":"success","is_error":false,"session_id":"s","usage":{"input_tokens":11,"output_tokens":22},"modelUsage":{"claude-opus-5-5":{"inputTokens":0,"outputTokens":0,"costUSD":0.02}}}""",
            _state);

        var totals = _state.Totals!;
        totals.InputTokens.Should().Be(11);
        totals.OutputTokens.Should().Be(22);
        totals.Models["claude-opus-5-5"].CostUsd.Should().Be(0.02m);
    }

    [Fact]
    public void ProcessLine_ErrorResult_EmitsFailureLineAndRecordsStatus()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s","result":"Invalid API key","api_error_status":401}""",
            _state);

        lines.Should().ContainSingle().Which.Should().Contain("Invalid API key");
        _state.ResultIsError.Should().BeTrue();
        _state.ApiErrorStatus.Should().Be(401);
    }

    [Fact]
    public void ProcessLine_ErrorResultWithoutText_UsesErrorsArray()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"result","subtype":"error_max_turns","is_error":true,"session_id":"s","errors":["max turns reached"]}""",
            _state);

        lines.Should().ContainSingle().Which.Should().Contain("max turns reached");
    }

    [Fact]
    public void ProcessLine_RateLimitEvent_RecordsObservation_WarnsWhenNotAllowed()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"rate_limit_event","session_id":"s","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","utilization":0.82,"resetsAt":1790000000,"overageStatus":"rejected"}}""",
            _state);

        lines.Should().ContainSingle().Which.Should().Contain("five_hour").And.Contain("allowed_warning");
        var fiveHour = _state.RateLimits["five_hour"];
        fiveHour.Provider.Should().Be("claude");
        fiveHour.Status.Should().Be("allowed_warning");
        fiveHour.Utilization.Should().Be(0.82);
        fiveHour.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790000000));
        _state.RateLimits["overage"].Status.Should().Be("rejected");
    }

    /// <summary>A real event from a subscription account without extra usage (Claude Code 2.1.268).</summary>
    internal const string RealRateLimitEvent =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1791669600,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.73,"resetsAt":1791669600},"seven_day":{"utilization":0.46,"resetsAt":1792069200}}},"uuid":"c07f9eb2-a43e-4e2e-9fa0-6debf6fba10c","session_id":"4d5a10ea-9735-4e0c-a351-b38316e58280"}""";

    [Fact]
    public void ProcessLine_RealRateLimitEvent_ReadsEachWindowsUseFromUnifiedWindows()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(RealRateLimitEvent, _state);

        lines.Should().BeEmpty();
        _state.RateLimits["five_hour"].Should().BeEquivalentTo(new
        {
            Status = "allowed", Utilization = 0.73, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1791669600)
        });
        _state.RateLimits["seven_day"].Should().BeEquivalentTo(new
        {
            Status = "allowed", Utilization = 0.46, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1792069200)
        });
        _state.RateLimits["overage"].Status.Should().Be("rejected");
    }

    [Fact]
    public void ProcessLine_RateLimitEventNotAllowed_LeavesTheOtherWindowsUnreported()
    {
        // Only the reported window's status is known once requests are no longer simply allowed.
        ClaudeStreamJsonParser.ProcessLine(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":0.91},"seven_day":{"utilization":0.5}}}}""",
            _state);

        _state.RateLimits["five_hour"].Utilization.Should().Be(0.91);
        _state.RateLimits.Should().NotContainKey("seven_day");
    }

    [Fact]
    public void ProcessLine_RateLimitEvent_SnakeCaseAndMillisecondTimestamp_AreAccepted()
    {
        var lines = ClaudeStreamJsonParser.ProcessLine(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rate_limit_type":"seven_day","resets_at":1790000000000}}""",
            _state);

        lines.Should().BeEmpty();
        _state.RateLimits["seven_day"].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790000000000));
    }

    [Theory]
    [InlineData("plain text line")]
    [InlineData("{not json")]
    public void ProcessLine_NonJson_IsReturnedAsIs(string line)
    {
        ClaudeStreamJsonParser.ProcessLine(line, _state).Should().Equal(line);
    }

    [Fact]
    public void ProcessLine_UnknownEventType_IsIgnored()
    {
        ClaudeStreamJsonParser.ProcessLine("""{"type":"user","message":{"content":[]}}""", _state).Should().BeEmpty();
        ClaudeStreamJsonParser.ProcessLine("", _state).Should().BeEmpty();
    }

    [Fact]
    public void SummarizeToolInput_UnknownTool_UsesFirstStringAndTruncates()
    {
        using var doc = JsonDocument.Parse($$"""{"count":3,"note":"{{new string('x', 300)}}"}""");

        var summary = ClaudeStreamJsonParser.SummarizeToolInput("Custom", doc.RootElement);

        summary.Should().HaveLength(201).And.EndWith("…");
    }

    [Theory]
    [InlineData("authentication_failed", null, AgentErrorCategory.PermanentAuthFailure)]
    [InlineData("billing_error", null, AgentErrorCategory.PermanentAuthFailure)]
    [InlineData(null, 403, AgentErrorCategory.PermanentAuthFailure)]
    [InlineData("rate_limit", null, AgentErrorCategory.ProviderRateLimit)]
    [InlineData(null, 429, AgentErrorCategory.ProviderRateLimit)]
    [InlineData("overloaded", null, AgentErrorCategory.ProviderOverload)]
    [InlineData(null, 529, AgentErrorCategory.ProviderOverload)]
    [InlineData("invalid_request", 400, AgentErrorCategory.None)]
    [InlineData(null, null, AgentErrorCategory.None)]
    public void ClassifyFailure_MapsCliErrors(string? category, int? status, AgentErrorCategory expected)
    {
        var state = new ClaudeStreamState { LastErrorCategory = category, ApiErrorStatus = status };

        state.ClassifyFailure().Should().Be(expected);
    }

    [Fact]
    public void ClassifyFailure_RejectedRateLimit_IsProviderRateLimit()
    {
        var state = new ClaudeStreamState();
        state.RateLimits["five_hour"] = new AgentRateLimitObservation { Window = "five_hour", Status = "rejected" };

        state.ClassifyFailure().Should().Be(AgentErrorCategory.ProviderRateLimit);
    }
}

public class ClaudeUsageTotalsTests
{
    [Fact]
    public void Minus_NoPrevious_ReturnsSameTotals()
    {
        var totals = new ClaudeUsageTotals { InputTokens = 5, CostUsd = 1m };

        totals.Minus(null).Should().BeSameAs(totals);
    }

    [Fact]
    public void Minus_CumulativeTotals_ReturnsThisCallsShare()
    {
        var previous = new ClaudeUsageTotals
        {
            InputTokens = 100, OutputTokens = 50, ThinkingTokens = 10, CacheReadTokens = 1000, CacheWriteTokens = 200,
            WebSearchRequests = 1, CostUsd = 0.10m, Turns = 3, ApiDurationMs = 4000,
            Models = new Dictionary<string, ClaudeModelTotals> { ["m"] = new() { InputTokens = 100, CostUsd = 0.10m } }
        };
        var current = new ClaudeUsageTotals
        {
            InputTokens = 160, OutputTokens = 80, ThinkingTokens = 15, CacheReadTokens = 2500, CacheWriteTokens = 260,
            WebSearchRequests = 1, CostUsd = 0.25m, Turns = 5, ApiDurationMs = 9000,
            Models = new Dictionary<string, ClaudeModelTotals>
            {
                ["m"] = new() { InputTokens = 160, CostUsd = 0.25m },
                ["new"] = new() { InputTokens = 7 }
            }
        };

        var delta = current.Minus(previous);

        delta.InputTokens.Should().Be(60);
        delta.OutputTokens.Should().Be(30);
        delta.ThinkingTokens.Should().Be(5);
        delta.CacheReadTokens.Should().Be(1500);
        delta.CacheWriteTokens.Should().Be(60);
        delta.WebSearchRequests.Should().Be(0);
        delta.CostUsd.Should().Be(0.15m);
        delta.Turns.Should().Be(5); // num_turns is per call, so it is not subtracted
        delta.ApiDurationMs.Should().Be(5000);
        delta.Models["m"].InputTokens.Should().Be(60);
        delta.Models["m"].CostUsd.Should().Be(0.15m);
        delta.Models["new"].InputTokens.Should().Be(7);
    }

    [Fact]
    public void Minus_FieldSmallerThanBefore_KeepsCurrentValue()
    {
        // A field the CLI reported for this call only must not be subtracted into a negative.
        var previous = new ClaudeUsageTotals { ApiDurationMs = 9000, CostUsd = 0.5m };
        var current = new ClaudeUsageTotals { ApiDurationMs = 2000, CostUsd = 0.1m };

        var delta = current.Minus(previous);

        delta.ApiDurationMs.Should().Be(2000);
        delta.CostUsd.Should().Be(0.1m);
    }
}
