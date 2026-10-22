using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Spec 053 task 2.2: <see cref="AgentToolCatalog"/> — what each tool accepts (req 2.4, 2.5) and needs (req 1.1, DD5).
/// </summary>
public class AgentToolCatalogTests
{
    [Theory]
    [InlineData("kiro", "kiro")]
    [InlineData("KIRO", "kiro")]
    [InlineData("KiroCli", "kiro")]
    [InlineData("opencode", "opencode")]
    [InlineData("OpenCode", "opencode")]
    [InlineData("claude", "claude")]
    [InlineData("ClaudeCode", "claude")]
    [InlineData(" claude ", "claude")]
    public void Find_KnownProviderType_ReturnsTheTool(string providerType, string expectedId)
        => AgentToolCatalog.Find(providerType)!.Id.Should().Be(expectedId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gemini")]
    public void Find_EmptyOrUnknownProviderType_ReturnsNull(string? providerType)
        => AgentToolCatalog.Find(providerType).Should().BeNull("an unset providerType names no tool (design DD4)");

    [Fact]
    public void ProviderConfigType_IsTheAgentConfigTypeOfEachTool()
    {
        AgentToolCatalog.Kiro.ProviderConfigType.Should().Be(ProviderTypes.KiroCli);
        AgentToolCatalog.OpenCode.ProviderConfigType.Should().Be(ProviderTypes.OpenCode);
        AgentToolCatalog.ClaudeCode.ProviderConfigType.Should().Be(ProviderTypes.ClaudeCode);
    }

    [Fact]
    public void Efforts_PerTool_Req2_5()
    {
        AgentToolCatalog.ClaudeCode.Efforts.Should().Equal("low", "medium", "high", "xhigh", "max");
        AgentToolCatalog.Kiro.Efforts.Should().Equal(["low", "medium", "high", "max"], "kiro-cli has no xhigh");
        AgentToolCatalog.OpenCode.Efforts.Should().BeEmpty("OpenCode has no effort");
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("high", true)]
    [InlineData("xhigh", false)]
    [InlineData("HIGH", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AcceptsEffort_Kiro(string? effort, bool accepted)
        => AgentToolCatalog.AcceptsEffort(AgentToolCatalog.Kiro, effort).Should().Be(accepted);

    [Fact]
    public void AcceptsEffort_OpenCode_OnlyAuto()
    {
        AgentToolCatalog.AcceptsEffort(AgentToolCatalog.OpenCode, "auto").Should().BeTrue();
        AgentToolCatalog.AcceptsEffort(AgentToolCatalog.OpenCode, "low").Should().BeFalse();
    }

    [Fact]
    public void Settings_PerTool_Req1_1()
    {
        AgentToolCatalog.Kiro.Settings.Should().Equal(ProviderSettingKeys.AgentName);
        AgentToolCatalog.OpenCode.Settings.Should().Equal(ProviderSettingKeys.BaseUrl);
        AgentToolCatalog.ClaudeCode.Settings.Should().Equal(ProviderSettingKeys.AuthMode);
    }

    [Fact]
    public void BuiltInModels_OnlyClaudeCodeHasAList_Req2_4()
    {
        AgentToolCatalog.ClaudeCode.BuiltInModels.Should().Equal(ClaudeCodeModels.All.Select(m => m.ModelId));
        AgentToolCatalog.Kiro.BuiltInModels.Should().BeEmpty();
        AgentToolCatalog.OpenCode.BuiltInModels.Should().BeEmpty();
    }

    [Fact]
    public void DefaultMcpConfigPath_PerTool()
    {
        AgentToolCatalog.Kiro.DefaultMcpConfigPath.Should().Be("/home/ubuntu/.kiro/settings/mcp.json");
        AgentToolCatalog.OpenCode.DefaultMcpConfigPath.Should().Be("/home/ubuntu/.opencode/opencode.json");
        AgentToolCatalog.ClaudeCode.DefaultMcpConfigPath.Should().Be(AgentDefaults.ClaudeMcpConfigPath);
    }

    [Fact]
    public void AllEfforts_StartsWithAuto_AndCoversEveryTool()
    {
        AgentToolCatalog.AllEfforts[0].Should().Be(AgentToolCatalog.Auto);
        AgentToolCatalog.AllEfforts.Should().Contain(AgentToolCatalog.All.SelectMany(t => t.Efforts));
    }
}
