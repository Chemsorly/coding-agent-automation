using AwesomeAssertions;
using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.TestUtilities;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for AgentProfileSection (spec 053 req 1.1–1.4): a profile is a job template, the settings of that
/// template's tool and MCP servers; profiles whose job template is missing are flagged.
/// </summary>
public class AgentProfileSectionComponentTests : BunitContext
{
    private const string KiroTemplate = "dotnet,kiro";
    private const string ClaudeTemplate = "claude,dotnet";
    private const string OpenCodeTemplate = "opencode,python";
    private const string TemplateWithoutTool = "notool";

    private static readonly UiJobTemplate[] JobTemplates =
    [
        new(ClaudeTemplate, JobTemplateProviderType.Claude),
        new(KiroTemplate, JobTemplateProviderType.Kiro),
        new(TemplateWithoutTool, ""),
        new(OpenCodeTemplate, JobTemplateProviderType.Opencode),
    ];

    private readonly Mock<IUiSettingsClient> _client = new();
    private AgentProfile? _saved;

    public AgentProfileSectionComponentTests()
    {
        Returns();
        _client.Setup(s => s.SaveAgentProfileAsync(It.IsAny<AgentProfile>(), It.IsAny<CancellationToken>()))
            .Callback<AgentProfile, CancellationToken>((p, _) => _saved = p)
            .Returns(Task.CompletedTask);
        _client.Setup(s => s.DeleteAgentProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void Returns(params AgentProfile[] profiles) =>
        _client.Setup(s => s.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiAgentProfilesPage(profiles, JobTemplates));

    private IRenderedComponent<AgentProfileSection> RenderSection() =>
        Render<AgentProfileSection>(p => p.Add(s => s.ConfigClient, _client.Object));

    private static void OpenAddForm(IRenderedComponent<AgentProfileSection> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Add Agent Profile")).Click();

    [Fact]
    public void WhenNoProfiles_ShowsTheEmptyMessage()
    {
        var cut = RenderSection();

        cut.Markup.Should().Contain("Agent Profiles").And.Contain("No agent profiles configured").And.Contain("+ Add Agent Profile");
    }

    [Fact]
    public void Table_ShowsNameJobTemplateAndTool()
    {
        Returns(AgentRoutingTestData.Profile("p-1", ClaudeTemplate, "Claude dotnet"));

        var cut = RenderSection();

        cut.FindAll("th").Select(h => h.TextContent.Trim()).Should().Equal("Name", "Job template", "MCP", "Enabled", "Actions");
        var row = cut.Find("[data-testid='profile-row-p-1']");
        row.TextContent.Should().Contain("Claude dotnet").And.Contain(ClaudeTemplate);
        row.QuerySelector("[data-testid='profile-tool']")!.TextContent.Should().Be("Claude Code");
        cut.FindAll("[data-testid='profile-missing-template']").Should().BeEmpty();
    }

    [Fact]
    public void AProfileWhoseJobTemplateIsMissing_IsFlagged()
    {
        // Req 1.4: for example after a Helm change.
        Returns(AgentRoutingTestData.Profile("p-1", "java,kiro", "Java agent"));

        var cut = RenderSection();

        cut.Find("[data-testid='profile-row-p-1'] [data-testid='profile-missing-template']").TextContent.Should().Be("Missing job template");
    }

    [Fact]
    public void AProfileOnAJobTemplateWithoutTool_IsFlagged()
    {
        Returns(AgentRoutingTestData.Profile("p-1", TemplateWithoutTool, "Toolless"));

        var cut = RenderSection();

        cut.Find("[data-testid='profile-row-p-1'] [data-testid='profile-no-tool']").TextContent.Should().Be("No tool");
    }

    [Fact]
    public void Form_OffersTheJobTemplatesWithTheirTool()
    {
        var cut = RenderSection();
        OpenAddForm(cut);

        cut.FindAll("[data-testid='profile-job-template'] option").Select(o => o.TextContent.Trim()).Should().Equal(
            "-- Select --", "claude,dotnet (Claude Code)", "dotnet,kiro (Kiro)", "notool (no tool)", "opencode,python (OpenCode)");
        cut.Markup.Should().NotContain("Match Labels").And.NotContain("Agent Provider Config").And.NotContain("Priority");
    }

    [Theory]
    [InlineData(KiroTemplate, "profile-agent-name")]
    [InlineData(OpenCodeTemplate, "profile-base-url")]
    [InlineData(ClaudeTemplate, "profile-auth-mode")]
    [InlineData(TemplateWithoutTool, "profile-no-tool-hint")]
    public void Form_ShowsTheSettingsOfTheChosenTemplatesTool(string jobTemplate, string shown)
    {
        string[] settings = ["profile-agent-name", "profile-base-url", "profile-auth-mode", "profile-no-tool-hint"];
        var cut = RenderSection();
        OpenAddForm(cut);

        cut.Find("[data-testid='profile-job-template']").Change(jobTemplate);

        foreach (var setting in settings)
            cut.FindAll($"[data-testid='{setting}']").Should().HaveCount(setting == shown ? 1 : 0, setting);
    }

    [Fact]
    public void Save_SendsTheJobTemplateAndTheToolsSettings()
    {
        var cut = RenderSection();
        OpenAddForm(cut);
        cut.FindAll("input[type='text']")[0].Change("Claude dotnet");
        cut.Find("[data-testid='profile-job-template']").Change(ClaudeTemplate);
        cut.Find("[data-testid='profile-auth-mode']").Change(ClaudeCodeAuthModes.Subscription);

        cut.Find("button.btn-save").Click();

        _saved.Should().NotBeNull();
        _saved!.DisplayName.Should().Be("Claude dotnet");
        _saved.JobTemplate.Should().Be(ClaudeTemplate);
        _saved.Settings.Should().Equal(new Dictionary<string, string> { [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription });
    }

    [Fact]
    public void Edit_KeepsTheMcpConfigPath_WhichIsNotOnTheForm()
    {
        Returns(AgentRoutingTestData.Profile("p-1", KiroTemplate, "Kiro dotnet", settings: new Dictionary<string, string>
        {
            [ProviderSettingKeys.AgentName] = "reviewer",
            [ProviderSettingKeys.McpConfigPath] = "/home/ubuntu/custom/mcp.json",
        }));
        var cut = RenderSection();

        cut.Find(".btn-edit").Click();
        cut.Find("[data-testid='profile-agent-name']").GetAttribute("value").Should().Be("reviewer");
        cut.Find("button.btn-save").Click();

        _saved!.Id.Should().Be("p-1");
        _saved.Settings.Should().Equal(new Dictionary<string, string>
        {
            [ProviderSettingKeys.AgentName] = "reviewer",
            [ProviderSettingKeys.McpConfigPath] = "/home/ubuntu/custom/mcp.json",
        });
    }

    [Fact]
    public void Save_WithoutAJobTemplate_IsRefusedInTheForm()
    {
        var cut = RenderSection();
        OpenAddForm(cut);
        cut.FindAll("input[type='text']")[0].Change("No template");

        cut.Find("button.btn-save").Click();

        cut.Markup.Should().Contain("Job Template is required.");
        _saved.Should().BeNull();
    }

    [Fact]
    public void ARefusedSave_ShowsTheApisReason()
    {
        _client.Setup(s => s.SaveAgentProfileAsync(It.IsAny<AgentProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Another agent profile is named 'Kiro dotnet'."));
        var cut = RenderSection();
        OpenAddForm(cut);
        cut.FindAll("input[type='text']")[0].Change("Kiro dotnet");
        cut.Find("[data-testid='profile-job-template']").Change(KiroTemplate);

        cut.Find("button.btn-save").Click();

        cut.Find(".status-error").TextContent.Should().Contain("Another agent profile is named 'Kiro dotnet'.");
    }

    [Fact]
    public void Form_ShowsTheMcpServerSection()
    {
        var cut = RenderSection();
        OpenAddForm(cut);

        cut.Markup.Should().Contain("MCP Servers").And.Contain("+ Add MCP Server");
    }

    [Fact]
    public void McpHeadersPlaceholder_ShowsLineBreaks()
    {
        // An "&#10;" entity in an attribute of a rendered element is not decoded, so it would show as text.
        var cut = RenderSection();
        OpenAddForm(cut);
        cut.FindAll("button").First(b => b.TextContent.Contains("+ Add MCP Server")).Click();

        cut.FindAll("select").First(s => s.InnerHtml.Contains("HTTP (URL-based)")).Change("http");

        Assert.Equal("Authorization=Bearer mytoken\nX-Org=myorg", cut.Find("textarea").GetAttribute("placeholder"));
    }

    [Fact]
    public void ClickCancel_HidesTheForm()
    {
        var cut = RenderSection();
        OpenAddForm(cut);

        cut.FindAll("button").First(b => b.TextContent.Contains("Cancel")).Click();

        cut.Markup.Should().NotContain("Display Name *");
    }

    [Fact]
    public void DeleteProfile_ShowsConfirmation()
    {
        Returns(AgentRoutingTestData.Profile("p-1", KiroTemplate, "To Delete"));
        var cut = RenderSection();

        cut.Find(".btn-delete").Click();

        cut.Markup.Should().Contain("Delete profile").And.Contain("To Delete");
    }

    [Fact]
    public void ProfileWithMcpServers_ShowsTheCountOfEnabledOnes()
    {
        Returns(AgentRoutingTestData.Profile("p-1", KiroTemplate, "With MCP", mcpServers:
        [
            new McpServerConfig { Name = "context7", Command = "uvx" },
            new McpServerConfig { Name = "disabled", Command = "x", Disabled = true },
        ]));

        var cut = RenderSection();

        Assert.Contains(cut.FindAll("td"), c => c.TextContent.Trim() == "1");
    }

    /// <summary>Regression test: NullReferenceException when AgentProfile.McpServers is null.</summary>
    [Fact]
    public void ProfileList_WhenMcpServersIsNull_RendersWithoutException()
    {
        Returns(AgentRoutingTestData.Profile("p-1", KiroTemplate, "Legacy Profile") with { McpServers = null! });

        var cut = RenderSection();

        Assert.Contains("Legacy Profile", cut.Markup);
        Assert.Contains(cut.FindAll("td"), c => c.TextContent.Trim() == "0");
    }

    /// <summary>Regression test: NullReferenceException in EditProfile when McpServerConfig.Headers or Env is null.</summary>
    [Fact]
    public void EditProfile_WhenServerHasNullHeadersAndEnv_OpensFormWithoutException()
    {
        var serverWithNullFields = new McpServerConfig { Name = "legacy-server", Type = "stdio", Command = "uvx" } with
        { Headers = null!, Env = null! };
        Returns(AgentRoutingTestData.Profile("p-1", KiroTemplate, "Profile With Legacy Server", mcpServers: [serverWithNullFields]));
        var cut = RenderSection();

        cut.Find(".btn-edit").Click();

        Assert.Contains("Edit", cut.Markup);
    }
}
