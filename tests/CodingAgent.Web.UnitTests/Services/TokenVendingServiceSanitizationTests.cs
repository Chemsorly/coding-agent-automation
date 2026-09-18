using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Regression tests for errorBody sanitization in
/// <see cref="TokenVendingService.GenerateAgentTokenAsync"/>.
/// Verifies that large GitHub API error bodies are truncated to ≤ 500 chars
/// before being logged and before being embedded in the thrown exception message.
/// </summary>
public class TokenVendingServiceSanitizationTests
{
    private readonly Mock<ILogger> _mockLogger;

    public TokenVendingServiceSanitizationTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    // ── Shared helpers ───────────────────────────────────────────────────

    private static string GenerateValidPrivateKeyBase64()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));
    }

    private static ProviderConfig MakeConfig(string privateKeyBase64) => new()
    {
        Id = "rp-test",
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "Test",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.PrivateKeyBase64] = privateKeyBase64,
            [ProviderSettingKeys.ClientId] = "Iv1.abc123",
            [ProviderSettingKeys.InstallationId] = "12345",
            [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
            [ProviderSettingKeys.Owner] = "test-owner",
            [ProviderSettingKeys.Repo] = "test-repo"
        }
    };

    private static HttpClient MakeHttpClientWithBody(System.Net.HttpStatusCode statusCode, string body)
    {
        var handler = new FakeHttpHandler(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body)
        });
        return new HttpClient(handler);
    }

    // ── Tests ────────────────────────────────────────────────────────────

    // TODO [WARNING] (TestQualityReviewer): Missing exact-boundary test: errorBody.Length == 500 should pass through
    // unchanged (no truncation, no ellipsis appended) because the condition is `errorBody.Length > MaxErrorBodyLength`.
    // A test with a 500-char body verifies the off-by-one at the > vs >= boundary.

    /// <summary>
    /// AC1: errorBody longer than 500 chars must be truncated in the thrown exception message.
    /// </summary>
    [Fact]
    public async Task GenerateAgentTokenAsync_NonSuccessResponse_TruncatesErrorBodyInException()
    {
        var longBody = new string('x', 600);
        var httpClient = MakeHttpClientWithBody(System.Net.HttpStatusCode.UnprocessableEntity, longBody);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);
        var config = MakeConfig(GenerateValidPrivateKeyBase64());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        // The exception message should NOT contain the full 600-char body
        ex.Message.Should().NotContain(longBody,
            "raw errorBody must not be embedded in the exception message");

        // The embedded body portion must be ≤ 500 chars (prefix "GitHub token exchange failed (HTTP 422): " is ~41 chars)
        // Extract just the body portion after the prefix
        var prefix = "GitHub token exchange failed (HTTP 422): ";
        ex.Message.Should().StartWith(prefix);
        var embeddedBody = ex.Message[prefix.Length..];
        // TODO [WARNING] (TestQualityReviewer): The bound of 502 is one wider than the maximum possible output of 501
        // (500 content chars + 1 "…" U+2026 char). Tighten to 501. Also, this test does not assert that the
        // embedded body starts with the first 500 characters of the original input — a broken implementation that
        // replaces the body with a fixed short string would still pass. Add:
        //   embeddedBody.Should().StartWith(longBody[..500])
        embeddedBody.Length.Should().BeLessThanOrEqualTo(502, // 500 chars + 1 ellipsis char (…) or "..."
            "errorBody must be capped at 500 chars before embedding in exception message");
    }

    /// <summary>
    /// AC1: errorBody longer than 500 chars must be truncated in the log call.
    /// </summary>
    [Fact]
    public async Task GenerateAgentTokenAsync_NonSuccessResponse_TruncatesErrorBodyInLogCall()
    {
        var longBody = new string('y', 600);
        var httpClient = MakeHttpClientWithBody(System.Net.HttpStatusCode.UnprocessableEntity, longBody);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);
        var config = MakeConfig(GenerateValidPrivateKeyBase64());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        // TODO [WARNING] (DotNetSpecialist, TestQualityReviewer): The Moq Verify call uses It.IsAny<long>() for the
        // installationId parameter. If the runtime type passed to Serilog's Error overload is not exactly long (e.g.
        // it is int, string, or boxed differently), the four-argument overload matcher will not match and
        // Times.AtLeastOnce will throw with a misleading failure. Confirm the exact runtime type of installationId
        // and match accordingly. Also consider using It.IsAny<object>() or capturing via a callback to make
        // the test resilient to Serilog overload resolution. Additionally, the bound 502 should be 501 (500 + 1 "…").
        _mockLogger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<int>(),
            It.Is<string>(s => s.Length <= 502)),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// AC1: errorBody shorter than or equal to 500 chars must NOT be truncated.
    /// </summary>
    [Fact]
    public async Task GenerateAgentTokenAsync_NonSuccessResponse_ShortErrorBodyNotTruncated()
    {
        const string shortBody = "{\"message\":\"Bad credentials\",\"documentation_url\":\"https://docs.github.com\"}";
        var httpClient = MakeHttpClientWithBody(System.Net.HttpStatusCode.Unauthorized, shortBody);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);
        var config = MakeConfig(GenerateValidPrivateKeyBase64());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        // The full short body must appear in the exception message unchanged
        ex.Message.Should().Contain(shortBody,
            "short errorBody (≤500 chars) must not be truncated");
    }

    /// <summary>
    /// AC2: activity?.SetStatus and activity?.AddException must not record raw errorBody on the OTel span.
    /// Verifies that the span status description and exception event message do not contain raw errorBody.
    /// </summary>
    [Fact]
    public async Task GenerateAgentTokenAsync_NonSuccessResponse_SpanDoesNotContainRawErrorBody()
    {
        var rawBody = new string('z', 600);
        var httpClient = MakeHttpClientWithBody(System.Net.HttpStatusCode.UnprocessableEntity, rawBody);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);
        var config = MakeConfig(GenerateValidPrivateKeyBase64());

        Activity? capturedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => capturedActivity = activity
        };
        ActivitySource.AddActivityListener(listener);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        capturedActivity.Should().NotBeNull("an OTel span must be started for the token-vending operation");

        // Status description must not contain any portion of rawBody
        capturedActivity!.StatusDescription.Should().NotContain(rawBody[..10],
            "raw errorBody must not appear in the span status description");

        // The exception event (if present) must not contain raw errorBody in exception.message
        var exceptionEvent = capturedActivity.Events.FirstOrDefault(e => e.Name == "exception");
        if (exceptionEvent.Name == "exception")
        {
            var message = exceptionEvent.Tags
                .FirstOrDefault(t => t.Key == "exception.message").Value as string;
            message?.Should().NotContain(rawBody[..10],
                "raw errorBody must not appear in the span exception.message attribute");
        }
    }

    /// <summary>
    /// Fake HTTP handler that returns a pre-configured response.
    /// </summary>
    private sealed class FakeHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
