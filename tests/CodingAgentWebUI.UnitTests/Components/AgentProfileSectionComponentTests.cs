using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for AgentProfileSection.
/// </summary>
public class AgentProfileSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly List<ProviderConfig> _agentProviders;

    public AgentProfileSectionComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _agentProviders = new List<ProviderConfig>
        {
            new() { Id = "ap-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent" }
        };

        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>());
        _mockStore.Setup(s => s.SaveAgentProfileAsync(It.IsAny<AgentProfile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockStore.Setup(s => s.DeleteAgentProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void RendersHeader()
    {
        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));
        Assert.Contains("Agent Profiles", cut.Markup);
    }

    [Fact]
    public void WhenNoProfiles_ShowsEmptyMessage()
    {
        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));
        Assert.Contains("No agent profiles configured", cut.Markup);
    }

    [Fact]
    public void WhenProfilesExist_ShowsTable()
    {
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new() { Id = "p-1", DisplayName = "DotNet Profile", AgentProviderConfigId = "ap-1", MatchLabels = new[] { "dotnet" } }
            });

        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        Assert.Contains("DotNet Profile", cut.Markup);
        Assert.Contains("dotnet", cut.Markup);
        Assert.DoesNotContain("No agent profiles configured", cut.Markup);
    }

    [Fact]
    public void ProfileTable_ShowsColumns()
    {
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new() { Id = "p-1", DisplayName = "Test", AgentProviderConfigId = "ap-1" }
            });

        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("Match Labels", cut.Markup);
        Assert.Contains("Agent Provider", cut.Markup);
        Assert.Contains("Enabled", cut.Markup);
        Assert.Contains("Priority", cut.Markup);
        Assert.Contains("MCP", cut.Markup);
    }

    [Fact]
    public void ProfileWithEmptyLabels_ShowsDefaultBadge()
    {
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new() { Id = "p-1", DisplayName = "Default", AgentProviderConfigId = "ap-1", MatchLabels = [] }
            });

        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        Assert.Contains("DEFAULT", cut.Markup);
        Assert.Contains("(matches all)", cut.Markup);
    }

    [Fact]
    public void RendersAddButton()
    {
        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));
        Assert.Contains("+ Add Agent Profile", cut.Markup);
    }

    [Fact]
    public void ClickAdd_ShowsForm()
    {
        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Agent Profile"));
        addBtn.Click();

        Assert.Contains("Create", cut.Markup);
        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("Match Labels", cut.Markup);
        Assert.Contains("Agent Provider Config", cut.Markup);
    }

    [Fact]
    public void Form_ShowsAgentProviderDropdown()
    {
        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Agent Profile"));
        addBtn.Click();

        Assert.Contains("Kiro Agent", cut.Markup);
    }

    [Fact]
    public void Form_ShowsMcpServerSection()
    {
        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Agent Profile"));
        addBtn.Click();

        Assert.Contains("MCP Servers", cut.Markup);
        Assert.Contains("+ Add MCP Server", cut.Markup);
    }

    [Fact]
    public void ClickCancel_HidesForm()
    {
        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Agent Profile"));
        addBtn.Click();
        Assert.Contains("Create", cut.Markup);

        var cancelBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Cancel"));
        cancelBtn.Click();

        Assert.DoesNotContain("Display Name *", cut.Markup);
    }

    [Fact]
    public void EditProfile_PopulatesForm()
    {
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new() { Id = "p-1", DisplayName = "My Profile", AgentProviderConfigId = "ap-1", MatchLabels = new[] { "kiro", "dotnet" }, Priority = 5 }
            });

        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        var editBtn = cut.Find(".btn-edit");
        editBtn.Click();

        Assert.Contains("Edit", cut.Markup);
    }

    [Fact]
    public void DeleteProfile_ShowsConfirmation()
    {
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new() { Id = "p-1", DisplayName = "To Delete", AgentProviderConfigId = "ap-1" }
            });

        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        var deleteBtn = cut.Find(".btn-delete");
        deleteBtn.Click();

        Assert.Contains("Delete profile", cut.Markup);
        Assert.Contains("To Delete", cut.Markup);
    }

    [Fact]
    public void ProfileWithMcpServers_ShowsCount()
    {
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "p-1", DisplayName = "With MCP", AgentProviderConfigId = "ap-1",
                    McpServers = new[]
                    {
                        new McpServerConfig { Name = "context7", Command = "uvx" },
                        new McpServerConfig { Name = "disabled", Command = "x", Disabled = true }
                    }
                }
            });

        var cut = Render<AgentProfileSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.AgentProviders, _agentProviders));

        // Should show count of enabled MCP servers (1 enabled out of 2)
        var cells = cut.FindAll("td");
        Assert.Contains(cells, c => c.TextContent.Trim() == "1");
    }
}
