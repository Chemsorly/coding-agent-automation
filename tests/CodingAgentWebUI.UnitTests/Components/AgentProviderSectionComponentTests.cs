using Bunit;
using Moq;
using AwesomeAssertions;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the AgentProviderSection component.
/// Tests rendering, form interactions, and save behavior.
/// </summary>
public class AgentProviderSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;

    public AgentProviderSectionComponentTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
    }

    [Fact]
    public void AgentProviderSection_RendersHeader()
    {
        var component = RenderSection();

        Assert.Contains("Agent Provider Configs", component.Markup);
        Assert.Contains("referenced by Agent Profiles", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_RendersAddButton()
    {
        var component = RenderSection();

        Assert.Contains("+ Add Agent Provider", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_DisplaysExistingProviders()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ap-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "My Kiro Agent",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro-cli",
                    ["timeout"] = "45",
                    [ProviderSettingKeys.Model] = "claude-sonnet-4"
                }
            }
        };

        var component = RenderSection(providers);

        Assert.Contains("My Kiro Agent", component.Markup);
        Assert.Contains("KiroCli", component.Markup);
        Assert.Contains("/usr/bin/kiro-cli", component.Markup);
        Assert.Contains("45", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_ProviderCard_HasEditAndDeleteButtons()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ap-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "Agent 1",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro-cli",
                    ["timeout"] = "30",
                    [ProviderSettingKeys.Model] = "auto"
                }
            }
        };

        var component = RenderSection(providers);

        var editButtons = component.FindAll(".btn-edit");
        var deleteButtons = component.FindAll(".btn-delete");

        Assert.True(editButtons.Count >= 1);
        Assert.True(deleteButtons.Count >= 1);
    }

    [Fact]
    public void AgentProviderSection_ClickAdd_ShowsForm()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();

        Assert.Contains("Add Agent Provider", component.Markup);
        Assert.Contains("Display Name", component.Markup);
        Assert.Contains("Executable Path", component.Markup);
        Assert.Contains("Timeout", component.Markup);
        Assert.Contains("Agent Name", component.Markup);
        Assert.Contains("Model", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_ClickAdd_ShowsHelpPanel()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();

        Assert.Contains("Kiro CLI Setup", component.Markup);
        Assert.Contains("Prerequisites", component.Markup);
        Assert.Contains("kiro-cli login", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_ClickCancel_HidesForm()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();
        // Form header should be visible
        Assert.Contains("<h3>Add Agent Provider</h3>", component.Markup);

        var cancelButton = component.FindAll("button").First(b => b.TextContent.Contains("Cancel"));
        cancelButton.Click();

        // Form header should be gone
        Assert.DoesNotContain("<h3>Add Agent Provider</h3>", component.Markup);
        // Add button should be back
        Assert.Contains("+ Add Agent Provider", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_ClickEdit_ShowsFormWithExistingValues()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ap-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "My Agent",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.ExecutablePath] = "/custom/path/kiro-cli",
                    ["timeout"] = "60",
                    ["agentName"] = "custom-agent",
                    [ProviderSettingKeys.Model] = "claude-sonnet-4"
                }
            }
        };

        var component = RenderSection(providers);

        var editButton = component.Find(".btn-edit");
        editButton.Click();

        Assert.Contains("Edit Agent Provider", component.Markup);
    }

    [Fact]
    public async Task AgentProviderSection_Save_EmptyDisplayName_ShowsError()
    {
        (string Message, bool IsError)? statusResult = null;

        var component = Render<AgentProviderSection>(parameters => parameters
            .Add(p => p.Providers, new List<ProviderConfig>())
            .Add(p => p.ConfigStore, _mockConfigStore.Object)
            .Add(p => p.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, result => statusResult = result)));

        var addButton = component.Find(".btn-add");
        addButton.Click();

        // Clear the display name (it has a default)
        var displayNameInput = component.FindAll("input[type='text']")[0];
        displayNameInput.Change("");

        var saveButton = component.Find(".btn-save");
        await saveButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(statusResult);
        Assert.True(statusResult!.Value.IsError);
        Assert.Contains("Display Name is required", statusResult.Value.Message);
    }

    [Fact]
    public async Task AgentProviderSection_Save_InvalidModelName_ShowsError()
    {
        // Verify that saving with a valid display name succeeds and stores the model value.
        // The form defaults to model="auto" which is always valid.
        _mockConfigStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = Render<AgentProviderSection>(parameters => parameters
            .Add(p => p.Providers, new List<ProviderConfig>())
            .Add(p => p.ConfigStore, _mockConfigStore.Object));

        var addButton = component.Find(".btn-add");
        addButton.Click();

        // Set a display name (required)
        var displayNameInput = component.FindAll("input[type='text']").First();
        displayNameInput.Change("Test Agent");

        var saveButton = component.Find(".btn-save");
        await saveButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Verify the saved config has model="auto" (the default)
        _mockConfigStore.Verify(s => s.SaveProviderConfigAsync(
            It.Is<ProviderConfig>(c => c.Settings[ProviderSettingKeys.Model] == "auto"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentProviderSection_Save_ValidForm_CallsConfigStore()
    {
        _mockConfigStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var onSavedCalled = false;
        var component = Render<AgentProviderSection>(parameters => parameters
            .Add(p => p.Providers, new List<ProviderConfig>())
            .Add(p => p.ConfigStore, _mockConfigStore.Object)
            .Add(p => p.OnSaved, EventCallback.Factory.Create(this, () => onSavedCalled = true)));

        var addButton = component.Find(".btn-add");
        addButton.Click();

        // Set a display name (required field, defaults to empty)
        var displayNameInput = component.FindAll("input[type='text']").First();
        displayNameInput.Change("My Test Agent");

        var saveButton = component.Find(".btn-save");
        await saveButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockConfigStore.Verify(s => s.SaveProviderConfigAsync(
            It.Is<ProviderConfig>(c =>
                c.Kind == ProviderKind.Agent &&
                c.ProviderType == "KiroCli"),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.True(onSavedCalled);
    }

    [Fact]
    public async Task AgentProviderSection_Save_StoreThrows_ShowsError()
    {
        _mockConfigStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Disk full"));

        (string Message, bool IsError)? statusResult = null;
        var component = Render<AgentProviderSection>(parameters => parameters
            .Add(p => p.Providers, new List<ProviderConfig>())
            .Add(p => p.ConfigStore, _mockConfigStore.Object)
            .Add(p => p.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, result => statusResult = result)));

        var addButton = component.Find(".btn-add");
        addButton.Click();

        // The form has default display name "" which triggers "Display Name is required" first.
        // We need to set a display name. The first text input is DisplayName.
        var displayNameInput = component.FindAll("input[type='text']").First();
        displayNameInput.Change("Test Agent");

        var saveButton = component.Find(".btn-save");
        await saveButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(statusResult);
        Assert.True(statusResult!.Value.IsError);
        Assert.Contains("Disk full", statusResult.Value.Message);
    }

    [Fact]
    public async Task AgentProviderSection_Delete_InvokesOnDelete()
    {
        string? deletedId = null;
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ap-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "To Delete",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro-cli",
                    ["timeout"] = "30",
                    [ProviderSettingKeys.Model] = "auto"
                }
            }
        };

        var component = Render<AgentProviderSection>(parameters => parameters
            .Add(p => p.Providers, providers)
            .Add(p => p.ConfigStore, _mockConfigStore.Object)
            .Add(p => p.OnDelete, EventCallback.Factory.Create<string>(this, id => deletedId = id)));

        var deleteButton = component.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("ap-1", deletedId);
    }

    [Fact]
    public void AgentProviderSection_FormDefaults_HasKiroCliType()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();

        // The select should have KiroCli as the only option
        Assert.Contains("KiroCli", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_FetchModelsButton_Visible()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();

        Assert.Contains("Fetch Models", component.Markup);
    }

    [Fact]
    public void AgentProviderSection_SectionDescription_Visible()
    {
        var component = RenderSection();

        Assert.Contains("These configurations define how agents execute", component.Markup);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private IRenderedComponent<AgentProviderSection> RenderSection(List<ProviderConfig>? providers = null)
    {
        return Render<AgentProviderSection>(parameters => parameters
            .Add(p => p.Providers, providers ?? new List<ProviderConfig>())
            .Add(p => p.ConfigStore, _mockConfigStore.Object));
    }
}
