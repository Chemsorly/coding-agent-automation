using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Verifies that TokenVendingService logs at Error level before throwing exceptions
/// for missing config settings and HTTP failures.
/// </summary>
public class TokenVendingServiceLoggingTests
{
    private readonly Mock<ILogger> _mockLogger;

    public TokenVendingServiceLoggingTests()
    {
        _mockLogger = new Mock<ILogger>();
        // Serilog's fluent API requires ForContext to return the same logger for Write calls
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_MissingPrivateKey_LogsErrorBeforeThrowing()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var config = new ProviderConfig
        {
            Id = "rp-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ClientId] = "123",
                [ProviderSettingKeys.InstallationId] = "456"
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_MissingClientId_LogsErrorBeforeThrowing()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var config = new ProviderConfig
        {
            Id = "rp-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.InstallationId] = "456"
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_MissingInstallationId_LogsErrorBeforeThrowing()
    {
        var service = new TokenVendingService(_mockLogger.Object, new HttpClient());
        var config = new ProviderConfig
        {
            Id = "rp-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "123"
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_HttpFailure_LogsErrorBeforeThrowing()
    {
        // Create an HttpClient that returns a 401 response
        var handler = new FakeHttpHandler(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"message\":\"Bad credentials\"}")
        });
        var httpClient = new HttpClient(handler);

        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        // Use a valid RSA key to get past config validation and JWT generation
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKey();
        var pemBase64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                "-----BEGIN RSA PRIVATE KEY-----\n" +
                Convert.ToBase64String(pem) +
                "\n-----END RSA PRIVATE KEY-----"));

        var config = new ProviderConfig
        {
            Id = "rp-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = pemBase64,
                [ProviderSettingKeys.ClientId] = "123456",
                [ProviderSettingKeys.InstallationId] = "789"
            }
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<int>(),
            It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GenerateAgentTokenAsync_NullTokenResponse_LogsErrorBeforeThrowing()
    {
        // Create an HttpClient that returns 200 with "null" body (deserializes to null)
        var handler = new FakeHttpHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("null")
        });
        var httpClient = new HttpClient(handler);

        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKey();
        var pemBase64 = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                "-----BEGIN RSA PRIVATE KEY-----\n" +
                Convert.ToBase64String(pem) +
                "\n-----END RSA PRIVATE KEY-----"));

        var config = new ProviderConfig
        {
            Id = "rp-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = pemBase64,
                [ProviderSettingKeys.ClientId] = "123456",
                [ProviderSettingKeys.InstallationId] = "789"
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAgentTokenAsync(config, CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.IsAny<long>()), Times.AtLeastOnce);
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
