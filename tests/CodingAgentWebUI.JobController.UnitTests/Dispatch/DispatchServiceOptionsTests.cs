using AwesomeAssertions;
using CodingAgentWebUI.Kubernetes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchServiceOptions.ValidateAndClamp"/> and
/// <see cref="DispatchServiceOptionsFactory.Create"/>.
/// </summary>
public sealed class DispatchServiceOptionsTests
{
    // ── ValidateAndClamp ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateAndClamp_ValuesAboveMinimum_Unchanged()
    {
        var opts = new DispatchServiceOptions
        {
            ChatSessionMaxDurationSeconds = 3600,
            ChatPodConnectTimeoutSeconds = 120,
            ChatTerminationGracePeriodSeconds = 120
        };

        opts.ValidateAndClamp();

        opts.ChatSessionMaxDurationSeconds.Should().Be(3600);
        opts.ChatPodConnectTimeoutSeconds.Should().Be(120);
        opts.ChatTerminationGracePeriodSeconds.Should().Be(120);
    }

    [Fact]
    public void ValidateAndClamp_SessionDuration_BelowMinimum_ClampsTo60()
    {
        var opts = new DispatchServiceOptions { ChatSessionMaxDurationSeconds = 10 };
        opts.ValidateAndClamp();
        opts.ChatSessionMaxDurationSeconds.Should().Be(60);
    }

    [Fact]
    public void ValidateAndClamp_ConnectTimeout_BelowMinimum_ClampsTo5()
    {
        var opts = new DispatchServiceOptions { ChatPodConnectTimeoutSeconds = 0 };
        opts.ValidateAndClamp();
        opts.ChatPodConnectTimeoutSeconds.Should().Be(5);
    }

    [Fact]
    public void ValidateAndClamp_TerminationGrace_BelowMinimum_ClampsTo5()
    {
        var opts = new DispatchServiceOptions { ChatTerminationGracePeriodSeconds = 1 };
        opts.ValidateAndClamp();
        opts.ChatTerminationGracePeriodSeconds.Should().Be(5);
    }

    [Fact]
    public void ValidateAndClamp_AtExactMinimum_Unchanged()
    {
        var opts = new DispatchServiceOptions
        {
            ChatSessionMaxDurationSeconds = 60,
            ChatPodConnectTimeoutSeconds = 5,
            ChatTerminationGracePeriodSeconds = 5
        };

        opts.ValidateAndClamp();

        opts.ChatSessionMaxDurationSeconds.Should().Be(60);
        opts.ChatPodConnectTimeoutSeconds.Should().Be(5);
        opts.ChatTerminationGracePeriodSeconds.Should().Be(5);
    }

    [Fact]
    public void ValidateAndClamp_NegativeValues_AllClamped()
    {
        var opts = new DispatchServiceOptions
        {
            ChatSessionMaxDurationSeconds = -100,
            ChatPodConnectTimeoutSeconds = -1,
            ChatTerminationGracePeriodSeconds = -50
        };

        opts.ValidateAndClamp();

        opts.ChatSessionMaxDurationSeconds.Should().Be(60);
        opts.ChatPodConnectTimeoutSeconds.Should().Be(5);
        opts.ChatTerminationGracePeriodSeconds.Should().Be(5);
    }
}

/// <summary>
/// Unit tests for <see cref="DispatchServiceOptionsFactory.Create"/>.
/// </summary>
public sealed class DispatchServiceOptionsFactoryTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    [Fact]
    public void Create_WithAllKeys_MapsCorrectly()
    {
        var config = BuildConfig(new()
        {
            ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "30",
            ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "20",
            ["WorkDistribution:OrchestratorUrl"] = "http://orch:5000",
            ["WorkDistribution:AgentApiKeySecretName"] = "my-secret",
            ["WorkDistribution:AgentServiceAccountName"] = "my-sa",
            ["WorkDistribution:Namespace"] = "production",
            ["WorkDistribution:OpencodeConfigSecretName"] = "oc-secret",
            ["AGENT_API_KEY"] = "master-key"
        });

        var opts = DispatchServiceOptionsFactory.Create(config);

        opts.PollIntervalSeconds.Should().Be(30);
        opts.RateLimitPerSecond.Should().Be(20);
        opts.OrchestratorUrl.Should().Be("http://orch:5000");
        opts.AgentApiKeySecretName.Should().Be("my-secret");
        opts.AgentServiceAccountName.Should().Be("my-sa");
        opts.Namespace.Should().Be("production");
        opts.OpencodeConfigSecretName.Should().Be("oc-secret");
        opts.AgentMasterApiKey.Should().Be("master-key");
    }

    [Fact]
    public void Create_MissingKeys_UsesDefaults()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var opts = DispatchServiceOptionsFactory.Create(config);

        opts.OrchestratorUrl.Should().Be("");
        opts.AgentApiKeySecretName.Should().Be("");
        opts.AgentServiceAccountName.Should().Be("");
        opts.OpencodeConfigSecretName.Should().Be("");
        opts.AgentMasterApiKey.Should().Be("");
        // Namespace falls back to "default" when neither config nor env is set
        opts.Namespace.Should().BeOneOf("default", Environment.GetEnvironmentVariable("POD_NAMESPACE") ?? "default");
    }

    [Fact]
    public void Create_PvcPool_BoundFromSection()
    {
        var config = BuildConfig(new()
        {
            ["WorkDistribution:CredentialPools:Kiro:0"] = "pvc-1",
            ["WorkDistribution:CredentialPools:Kiro:1"] = "pvc-2"
        });

        var opts = DispatchServiceOptionsFactory.Create(config);

        opts.KiroPvcPool.Should().BeEquivalentTo(new[] { "pvc-1", "pvc-2" });
    }

    [Fact]
    public void Create_NoPvcPool_DefaultsToEmpty()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var opts = DispatchServiceOptionsFactory.Create(config);

        opts.KiroPvcPool.Should().BeEmpty();
    }
}
