using AwesomeAssertions;
using CodingAgent.Agent.ClaudeCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

public class ClaudeCodeCredentialsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => (string?)v.Value);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void Resolve_ApiKeyMode_UsesPipelineApiKey()
    {
        var credential = ClaudeCodeCredentials.Resolve(
            ClaudeCodeAuthModes.ApiKey,
            Env((AgentDefaults.EnvClaudeApiKey, "sk-pipeline"), (AgentDefaults.EnvClaudeOAuthToken, "oauth")));

        credential.Should().Be(new ClaudeCredential("ANTHROPIC_API_KEY", "sk-pipeline", AgentBillingModes.Api));
    }

    [Fact]
    public void Resolve_SubscriptionMode_UsesOAuthToken()
    {
        var credential = ClaudeCodeCredentials.Resolve(
            ClaudeCodeAuthModes.Subscription,
            Env((AgentDefaults.EnvClaudeApiKey, "sk-pipeline"), (AgentDefaults.EnvClaudeOAuthToken, "oauth")));

        credential.Should().Be(new ClaudeCredential("CLAUDE_CODE_OAUTH_TOKEN", "oauth", AgentBillingModes.Subscription));
    }

    [Fact]
    public void Resolve_PipelineVariable_WinsOverCliVariable()
    {
        var credential = ClaudeCodeCredentials.Resolve(
            ClaudeCodeAuthModes.ApiKey,
            Env((AgentDefaults.EnvClaudeApiKey, "from-secret"), ("ANTHROPIC_API_KEY", "from-env")));

        credential!.Value.Should().Be("from-secret");
    }

    [Fact]
    public void Resolve_CliVariable_IsTheFallback()
    {
        var credential = ClaudeCodeCredentials.Resolve(
            ClaudeCodeAuthModes.Subscription, Env(("CLAUDE_CODE_OAUTH_TOKEN", "local-token")));

        credential!.Value.Should().Be("local-token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("nonsense")]
    public void Resolve_AutoMode_PrefersApiKey(string? mode)
    {
        var credential = ClaudeCodeCredentials.Resolve(
            mode, Env((AgentDefaults.EnvClaudeApiKey, "key"), (AgentDefaults.EnvClaudeOAuthToken, "oauth")));

        credential!.BillingMode.Should().Be(AgentBillingModes.Api);
    }

    [Fact]
    public void Resolve_AutoMode_FallsBackToSubscription()
    {
        var credential = ClaudeCodeCredentials.Resolve(
            ClaudeCodeAuthModes.Auto, Env((AgentDefaults.EnvClaudeOAuthToken, "oauth")));

        credential!.BillingMode.Should().Be(AgentBillingModes.Subscription);
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("subscription")]
    [InlineData("auto")]
    public void Resolve_NothingConfigured_ReturnsNull(string mode)
    {
        ClaudeCodeCredentials.Resolve(mode, Env((AgentDefaults.EnvClaudeApiKey, "  "))).Should().BeNull();
    }

    [Fact]
    public void Apply_RemovesEveryCredentialVariable_ThenSetsTheSelectedOne()
    {
        var environment = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = "inherited",
            ["anthropic_auth_token"] = "gateway",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "old",
            [AgentDefaults.EnvClaudeApiKey] = "pipeline",
            [AgentDefaults.EnvClaudeOAuthToken] = "pipeline-oauth",
            ["PATH"] = "/bin"
        };

        ClaudeCodeCredentials.Apply(environment, new ClaudeCredential("CLAUDE_CODE_OAUTH_TOKEN", "selected", AgentBillingModes.Subscription));

        environment.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "selected",
            ["PATH"] = "/bin"
        });
    }

    [Fact]
    public void Apply_NoCredential_LeavesNoCredentialVariable()
    {
        var environment = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "inherited" };

        ClaudeCodeCredentials.Apply(environment, null);

        environment.Should().BeEmpty();
    }

    [Fact]
    public void Credential_ToString_DoesNotRevealTheSecret()
    {
        new ClaudeCredential("ANTHROPIC_API_KEY", "sk-secret", AgentBillingModes.Api).ToString()
            .Should().NotContain("sk-secret");
    }
}
