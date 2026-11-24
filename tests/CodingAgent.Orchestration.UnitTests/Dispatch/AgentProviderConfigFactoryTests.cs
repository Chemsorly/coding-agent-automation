using AwesomeAssertions;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Spec 053 DD5, req 4.7: <see cref="AgentProviderConfigFactory"/> builds today's agent config shape from a profile and
/// a route, so agents built before spec 053 run it unchanged. The comparison with what agents received before the
/// move is the golden case in the API integration tests (task 4.7).
/// </summary>
public class AgentProviderConfigFactoryTests
{
    private static readonly AgentProfile Profile = AgentRoutingTestData.Profile(
        "profile-claude", "claude,dotnet", "Claude",
        settings: new Dictionary<string, string>
        {
            [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription,
            [ProviderSettingKeys.McpConfigPath] = "/home/ubuntu/custom-mcp.json",
        });

    [Fact]
    public void Build_SettingsAreTheProfilesToolSettings_PlusModelAndEffort()
    {
        var config = AgentProviderConfigFactory.Build(Profile, "claude", "opus", "xhigh");

        config.Settings.Should().Equal(new Dictionary<string, string>
        {
            [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription,
            [ProviderSettingKeys.McpConfigPath] = "/home/ubuntu/custom-mcp.json",
            [ProviderSettingKeys.Model] = "opus",
            [ProviderSettingKeys.Effort] = "xhigh",
        });
    }

    [Fact]
    public void Build_LightValues_AreInTheSettings()
    {
        var config = AgentProviderConfigFactory.Build(Profile, "claude", "opus", "xhigh", "haiku", "low");

        config.Settings.Should().Equal(new Dictionary<string, string>
        {
            [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription,
            [ProviderSettingKeys.McpConfigPath] = "/home/ubuntu/custom-mcp.json",
            [ProviderSettingKeys.Model] = "opus",
            [ProviderSettingKeys.Effort] = "xhigh",
            [ProviderSettingKeys.LightModel] = "haiku",
            [ProviderSettingKeys.LightEffort] = "low",
        });
    }

    [Fact]
    public void Build_OnlyOneLightValue_LeavesTheOtherOut()
    {
        var modelOnly = AgentProviderConfigFactory.Build(Profile, "opencode", "openai/gpt-5", "auto", lightModel: "openai/gpt-5-mini");
        var effortOnly = AgentProviderConfigFactory.Build(Profile, "claude", "opus", "high", lightEffort: "low");

        modelOnly.Settings[ProviderSettingKeys.LightModel].Should().Be("openai/gpt-5-mini");
        modelOnly.Settings.Should().NotContainKey(ProviderSettingKeys.LightEffort);
        effortOnly.Settings[ProviderSettingKeys.LightEffort].Should().Be("low");
        effortOnly.Settings.Should().NotContainKey(ProviderSettingKeys.LightModel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_NoLightValues_RemovesThemFromTheProfileSettings(string? blank)
    {
        var stale = Profile with
        {
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription,
                [ProviderSettingKeys.LightModel] = "left-in-profile",
                [ProviderSettingKeys.LightEffort] = "low",
            },
        };

        var config = AgentProviderConfigFactory.Build(stale, "claude", "opus", "high", blank, blank);

        config.Settings.Should().NotContainKey(ProviderSettingKeys.LightModel)
            .And.NotContainKey(ProviderSettingKeys.LightEffort);
        config.Settings.Should().ContainKey(ProviderSettingKeys.AuthMode, "only the light keys are removed");
    }

    [Fact]
    public void Build_TheRoutesLightValues_WinOverOnesLeftInTheProfileSettings()
    {
        var stale = Profile with
        {
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.LightModel] = "left-in-profile",
                [ProviderSettingKeys.LightEffort] = "max",
            },
        };

        var config = AgentProviderConfigFactory.Build(stale, "claude", "opus", "high", "haiku", "low");

        config.Settings[ProviderSettingKeys.LightModel].Should().Be("haiku");
        config.Settings[ProviderSettingKeys.LightEffort].Should().Be("low");
    }

    [Fact]
    public void Build_IdIsTheProfileId_AndKindIsAgent()
    {
        var config = AgentProviderConfigFactory.Build(Profile, "claude", "auto", "auto");

        config.Id.Should().Be("profile-claude");
        config.DisplayName.Should().Be("Claude");
        config.Kind.Should().Be(ProviderKind.Agent);
    }

    [Theory]
    [InlineData("kiro", ProviderTypes.KiroCli)]
    [InlineData("opencode", ProviderTypes.OpenCode)]
    [InlineData("claude", ProviderTypes.ClaudeCode)]
    [InlineData("KiroCli", ProviderTypes.KiroCli)]
    public void Build_ProviderTypeIsTheTools(string tool, string providerType)
        => AgentProviderConfigFactory.Build(Profile, tool, "auto", "auto").ProviderType.Should().Be(providerType);

    [Fact]
    public void Build_TheRouteWins_OverAModelOrEffortLeftInTheProfileSettings()
    {
        var stale = Profile with
        {
            Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "old", [ProviderSettingKeys.Effort] = "low" },
        };

        var config = AgentProviderConfigFactory.Build(stale, "claude", "opus", "high");

        config.Settings[ProviderSettingKeys.Model].Should().Be("opus");
        config.Settings[ProviderSettingKeys.Effort].Should().Be("high");
    }

    [Fact]
    public void Build_ProfileWithoutSettings_GetsModelAndEffortOnly()
    {
        var bare = Profile with { Settings = null };

        AgentProviderConfigFactory.Build(bare, "kiro", "auto", "auto").Settings.Keys
            .Should().BeEquivalentTo(ProviderSettingKeys.Model, ProviderSettingKeys.Effort);
    }

    [Fact]
    public void Build_DoesNotChangeTheProfile()
    {
        AgentProviderConfigFactory.Build(Profile, "claude", "opus", "high");

        Profile.Settings.Should().NotContainKey(ProviderSettingKeys.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gemini")]
    public void Build_UnknownTool_Throws(string tool)
    {
        var act = () => AgentProviderConfigFactory.Build(Profile, tool, "auto", "auto");

        act.Should().Throw<ArgumentException>();
    }
}
