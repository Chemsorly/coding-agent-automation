using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

public class ClaudeCodeChatWiringTests
{
    [Theory]
    [InlineData("claude", AgentProviderType.ClaudeCode)]
    [InlineData("Claude", AgentProviderType.ClaudeCode)]
    [InlineData("ClaudeCode", AgentProviderType.ClaudeCode)]
    [InlineData("opencode", AgentProviderType.OpenCode)]
    [InlineData("OpenCode", AgentProviderType.OpenCode)]
    [InlineData("kiro", AgentProviderType.KiroCli)]
    [InlineData("", AgentProviderType.KiroCli)]
    [InlineData(null, AgentProviderType.KiroCli)]
    public void ResolveChatProviderType_MapsTemplateAndProviderNames(string? value, AgentProviderType expected)
    {
        AgentChatModeRegistration.ResolveChatProviderType(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("APIKEY", "apiKey")]
    [InlineData(" subscription ", "subscription")]
    [InlineData("other", "auto")]
    public void AuthModes_Normalize_MapsToTheClosedSet(string? value, string expected)
    {
        ClaudeCodeAuthModes.Normalize(value).Should().Be(expected);
    }

    [Fact]
    public void ClaudeCodeModels_HavePinnedIdsAndAliases()
    {
        ClaudeCodeModels.All.Select(m => m.ModelId).Should()
            .Contain(["claude-opus-5-5", "claude-opus-4-8", "claude-sonnet-5-5", "claude-haiku-4-5", "opus"])
            .And.OnlyHaveUniqueItems();
    }
}
