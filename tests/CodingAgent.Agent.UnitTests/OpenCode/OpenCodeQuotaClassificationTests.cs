using System.Net;
using AwesomeAssertions;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// Spec 056 req 5.4, 5.5 (task 3.3): OpenCode errors that mean the provider account's quota or credit is used up, read
/// from the message and the response body; a login rejection; and transient errors that must not pause.
/// </summary>
public class OpenCodeQuotaClassificationTests
{
    private const string Session = "sess-quota";

    private static async Task<AgentResult> TurnFailingWith(string error)
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, Session);
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.OK,
            $$"""{"info":{"id":"msg_a","role":"assistant","error":{{error}}},"parts":[]}""");
        return await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None);
    }

    [Fact]
    public async Task QuotaExceededMessageWithoutStatus_IsQuota()
    {
        // What OpenCode makes of a stream insufficient_quota (anomalyco/opencode src/provider/error.ts).
        var result = await TurnFailingWith(
            """{"name":"APIError","data":{"message":"Quota exceeded. Check your plan and billing details.","isRetryable":false}}""");

        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        result.ProviderFailure!.Category.Should().Be(AgentProviderFailure.QuotaExhausted);
        result.ProviderFailure.ResetsAt.Should().BeNull();
        result.ProviderFailure.Detail.Should().Contain("Quota exceeded. Check your plan and billing details.");
    }

    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("credit_balance_exhausted")]
    [InlineData("organization_spend_limit_exceeded")]
    [InlineData("project_spend_limit_exceeded")]
    [InlineData("organization_usage_limit_exceeded")]
    [InlineData("enforced_spend_limit_reached")]
    public async Task QuotaCodeInTheResponseBody_IsQuota_EvenWithA429(string code)
    {
        var body = $$$"""{\"error\":{\"type\":\"{{{code}}}\",\"message\":\"limit\"}}""";
        var result = await TurnFailingWith(
            $$$"""{"name":"APIError","data":{"message":"Request failed","statusCode":429,"isRetryable":false,"responseBody":"{{{body}}}"}}""");

        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        result.ProviderFailure!.Detail.Should().Contain(code);
    }

    [Fact]
    public async Task CreditBalanceTooLow_IsQuota()
    {
        var result = await TurnFailingWith(
            """{"name":"APIError","data":{"message":"Your credit balance is too low to access the Anthropic API.","statusCode":400}}""");

        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
    }

    [Fact]
    public async Task ProviderAuthError_IsAuth()
    {
        var result = await TurnFailingWith("""{"name":"ProviderAuthError","data":{"providerID":"anthropic","message":"invalid x-api-key"}}""");

        result.ErrorCategory.Should().Be(AgentErrorCategory.PermanentAuthFailure);
        result.ProviderFailure!.Category.Should().Be(AgentProviderFailure.AuthFailed);
    }

    [Theory]
    [InlineData("""{"name":"APIError","data":{"message":"rate limited","statusCode":429,"isRetryable":true}}""", AgentErrorCategory.ProviderRateLimit)]
    [InlineData("""{"name":"APIError","data":{"message":"overloaded","statusCode":503,"isRetryable":true}}""", AgentErrorCategory.ProviderOverload)]
    public async Task TransientErrors_RecordNoProviderFailure(string error, AgentErrorCategory expected)
    {
        var result = await TurnFailingWith(error);

        result.ErrorCategory.Should().Be(expected);
        result.ProviderFailure.Should().BeNull();
    }

    [Fact]
    public async Task HttpErrorOfTheMessageRequest_WithAQuotaBody_IsQuota()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, Session);
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.TooManyRequests,
            """{"error":{"type":"insufficient_quota","message":"You exceeded your current quota"}}""");

        var result = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderQuotaExhausted);
        result.ProviderFailure!.Detail.Should().StartWith("HTTP 429:");
    }
}
