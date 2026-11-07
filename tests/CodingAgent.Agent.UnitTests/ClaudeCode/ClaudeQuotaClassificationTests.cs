using AwesomeAssertions;
using CodingAgent.Agent.ClaudeCode;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

/// <summary>
/// Spec 056 req 5.3, 5.5 (task 3.2): stream-json events of a Claude Code run that ran out of quota, lost its credit or
/// was refused its login, and the transient ones that must not pause the credential.
/// </summary>
public class ClaudeQuotaClassificationTests
{
    private static readonly DateTimeOffset FiveHourReset = DateTimeOffset.FromUnixTimeSeconds(1793600000);

    private static ClaudeStreamState Run(params string[] lines)
    {
        var state = new ClaudeStreamState();
        foreach (var line in lines)
            ClaudeStreamJsonParser.ProcessLine(line, state);
        return state;
    }

    private static string ErrorResult(string text) =>
        $$"""{"type":"result","subtype":"success","is_error":true,"session_id":"s","result":"{{text}}","num_turns":1}""";

    [Fact]
    public void UsageLimitReached_IsQuota_WithTheRejectedWindowsReset()
    {
        var state = Run(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"five_hour","resetsAt":1793600000}}""",
            """{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"error_status":429,"error":"usage_limit_reached"}""",
            ErrorResult("Claude AI usage limit reached|1793600000"));

        var category = state.ClassifyFailure();

        category.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        var failure = state.ProviderFailure(category)!;
        failure.Category.Should().Be(AgentProviderFailure.QuotaExhausted);
        failure.ResetsAt.Should().Be(FiveHourReset);
        failure.Detail.Should().Contain("usage limit reached");
    }

    [Fact]
    public void RejectedFiveHourWindow_IsQuota_WithItsReset()
    {
        var state = Run(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"five_hour","resetsAt":1793600000,"overageStatus":"rejected"}}""",
            ErrorResult("API Error: Rate limit reached"));

        var category = state.ClassifyFailure();

        category.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        state.ProviderFailure(category)!.ResetsAt.Should().Be(FiveHourReset);
    }

    [Fact]
    public void RejectedOverageOnly_WithA429_IsTransient()
    {
        var state = Run(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour","overageStatus":"rejected","overageResetsAt":1793600000}}""",
            """{"type":"system","subtype":"api_retry","attempt":3,"max_retries":10,"error_status":429,"error":"rate_limit"}""",
            ErrorResult("API Error: 429"));

        var category = state.ClassifyFailure();

        category.Should().Be(AgentErrorCategory.ProviderRateLimit);
        state.ProviderFailure(category).Should().BeNull();
    }

    [Fact]
    public void CreditsRequired_IsQuota_WithItsReset()
    {
        var state = Run(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"seven_day","errorCode":"credits_required","resetsAt":1793600000}}""",
            ErrorResult("Extra usage credits required"));

        var category = state.ClassifyFailure();

        category.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        state.ProviderFailure(category)!.ResetsAt.Should().Be(FiveHourReset);
    }

    [Fact]
    public void BillingError_WithRegainAccessDate_IsQuota_ResettingThen()
    {
        var state = Run(
            """{"type":"assistant","error":"billing_error","message":{"content":[{"type":"text","text":"You have reached your specified API usage limits. You will regain access on 2026-12-01 at 00:00 UTC."}]}}""",
            ErrorResult("You have reached your specified API usage limits. You will regain access on 2026-12-01 at 00:00 UTC."));

        var category = state.ClassifyFailure();

        category.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        state.ProviderFailure(category)!.ResetsAt.Should().Be(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Credit balance is too low")]
    [InlineData("This organization has reached its monthly spend limit")]
    [InlineData("enforced_spend_limit_reached")]
    public void QuotaTexts_AreQuota_WithoutAReset(string text)
    {
        var state = Run(
            """{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"error_status":400,"error":"invalid_request"}""",
            ErrorResult(text));

        var category = state.ClassifyFailure();

        category.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        var failure = state.ProviderFailure(category)!;
        failure.ResetsAt.Should().BeNull();
        failure.Detail.Should().Be(text);
    }

    [Fact]
    public void AuthenticationFailed_IsAuth()
    {
        var state = Run(
            """{"type":"assistant","error":"authentication_failed","message":{"content":[{"type":"text","text":"Invalid API key · Please run /login"}]}}""",
            ErrorResult("Invalid API key · Please run /login"));

        var category = state.ClassifyFailure();

        category.Should().Be(AgentErrorCategory.PermanentAuthFailure);
        var failure = state.ProviderFailure(category)!;
        failure.Category.Should().Be(AgentProviderFailure.AuthFailed);
        failure.Detail.Should().Be("Invalid API key · Please run /login");
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"error_status":429,"error":"rate_limit"}""", AgentErrorCategory.ProviderRateLimit)]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"error_status":529,"error":"overloaded"}""", AgentErrorCategory.ProviderOverload)]
    public void TransientErrors_RecordNoProviderFailure(string line, AgentErrorCategory expected)
    {
        var state = Run(line, ErrorResult("API Error"));

        var category = state.ClassifyFailure();

        category.Should().Be(expected);
        state.ProviderFailure(category).Should().BeNull();
    }

    [Theory]
    [InlineData("You will regain access on 2026-12-01 at 00:00 UTC.", "2026-12-01T00:00:00Z")]
    [InlineData("regain access on 2026-11-30T18:00:00+01:00", "2026-11-30T17:00:00Z")]
    [InlineData("no date here", null)]
    [InlineData("You will regain access on someday.", null)]
    public void RegainAccessOn_ReadsTheDate(string text, string? expected) =>
        ClaudeQuotaText.RegainAccessOn(text).Should().Be(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
}
