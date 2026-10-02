using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Moq;
using Moq.Protected;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TokenVendingService.PrepareReadOnlyCloneConfigsAsync"/>: the other project
/// repositories a project epic or a project review clones get none of their secrets or setup steps. A GitHub
/// App repository gets a <c>contents: read</c> token; any other keeps its own token.
/// </summary>
public class TokenVendingServiceReadOnlyCloneTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    private static string GenerateValidPrivateKeyBase64()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));
    }

    private static ProviderConfig GitHubAppConfig(string id = "repo-web") => new()
    {
        Id = id,
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "web",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.PrivateKeyBase64] = GenerateValidPrivateKeyBase64(),
            [ProviderSettingKeys.ClientId] = "Iv1.abc123",
            [ProviderSettingKeys.InstallationId] = "12345678",
            [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
            [ProviderSettingKeys.Owner] = "acme",
            [ProviderSettingKeys.Repo] = "web",
        },
        Secrets = new Dictionary<string, string> { ["NPM_TOKEN"] = "secret" },
        SetupSteps = [new SetupStep { Name = "install", Command = "npm ci" }]
    };

    /// <summary>
    /// A token endpoint that returns a unique token per call and records every request body,
    /// or fails with <paramref name="failWith"/>.
    /// </summary>
    private static (List<string> RequestBodies, HttpClient Client) TokenEndpoint(HttpStatusCode? failWith = null)
    {
        var requestBodies = new List<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, _) =>
            {
                requestBodies.Add(await request.Content!.ReadAsStringAsync());
                if (failWith is not null)
                    return new HttpResponseMessage(failWith.Value) { Content = new StringContent("boom") };

                var body = JsonSerializer.Serialize(new
                {
                    token = $"ghs_token_{requestBodies.Count}",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
                });
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                };
            });
        return (requestBodies, new HttpClient(handler.Object));
    }

    [Fact]
    public async Task PrepareReadOnlyCloneConfigsAsync_GitHubApp_GetsAContentsReadTokenAndNoSecretsOrSetupSteps()
    {
        var (requestBodies, httpClient) = TokenEndpoint();
        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        var result = await service.PrepareReadOnlyCloneConfigsAsync([GitHubAppConfig()], CancellationToken.None);

        var config = result.Should().ContainSingle().Subject;
        config.Settings.Should().NotContainKey(ProviderSettingKeys.PrivateKeyBase64);
        config.Settings[ProviderSettingKeys.Token].Should().StartWith("ghs_token_");
        config.Secrets.Should().BeNull();
        config.SetupSteps.Should().BeNull();

        using var request = JsonDocument.Parse(requestBodies.Single());
        var permissions = request.RootElement.GetProperty("permissions");
        permissions.EnumerateObject().Select(p => (p.Name, p.Value.GetString()))
            .Should().Equal(("contents", "read"));
    }

    [Fact]
    public async Task PrepareReadOnlyCloneConfigsAsync_NoGitHubAppCredentials_KeepsItsOwnTokenAndNothingElse()
    {
        // A GitLab access token cannot be narrowed to read-only. The repository keeps its own token, as the job's own
        // repository does, without its secrets or setup steps; the agent removes the token from the clone and turns
        // pushing off there.
        var (requestBodies, httpClient) = TokenEndpoint();
        var service = new TokenVendingService(_mockLogger.Object, httpClient);
        var gitLabConfig = new ProviderConfig
        {
            Id = "repo-gitlab",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "gitlab",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "glpat-secret",
                ["projectId"] = "42"
            },
            Secrets = new Dictionary<string, string> { ["DB_PASSWORD"] = "hunter2" },
            SetupSteps = [new SetupStep { Command = "make deps", Name = "deps" }]
        };

        var result = await service.PrepareReadOnlyCloneConfigsAsync([gitLabConfig], CancellationToken.None);

        var clone = result.Should().ContainSingle().Subject;
        clone.Id.Should().Be("repo-gitlab");
        clone.Settings.Should().Contain(ProviderSettingKeys.Token, "glpat-secret")
            .And.Contain("projectId", "42")
            .And.NotContainKey(ProviderSettingKeys.AccessToken);
        clone.Secrets.Should().BeNull();
        clone.SetupSteps.Should().BeNull();
        requestBodies.Should().BeEmpty("only a GitHub App gets a token minted");
    }

    [Fact]
    public async Task PrepareReadOnlyCloneConfigsAsync_MintingFails_IsLeftOutWithoutThrowing()
    {
        var (_, httpClient) = TokenEndpoint(failWith: HttpStatusCode.InternalServerError);
        var service = new TokenVendingService(_mockLogger.Object, httpClient);

        var result = await service.PrepareReadOnlyCloneConfigsAsync([GitHubAppConfig()], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnlyAndWriteTokens_ForTheSameRepository_AreCachedSeparately()
    {
        // A cached read-only token must never be handed out where a write token is needed, or the reverse
        var (requestBodies, httpClient) = TokenEndpoint();
        var service = new TokenVendingService(_mockLogger.Object, httpClient);
        var config = GitHubAppConfig();

        var readOnly = await service.PrepareReadOnlyCloneConfigsAsync([config], CancellationToken.None);
        var (writeToken, _) = await service.GenerateAgentTokenAsync(config, CancellationToken.None);

        writeToken.Should().NotBe(readOnly.Single().Settings[ProviderSettingKeys.Token]);
        requestBodies.Should().HaveCount(2);
        using var writeRequest = JsonDocument.Parse(requestBodies[1]);
        writeRequest.RootElement.GetProperty("permissions").GetProperty("contents").GetString().Should().Be("write");
    }
}
