using Bunit;
using Moq;
using KiroWebUI.Components.Pages;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KiroWebUI.Tests.Components;

/// <summary>
/// bUnit component tests for the Settings page.
/// Renders the actual Blazor component and asserts on markup and interactions.
/// </summary>
public class SettingsPageComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public SettingsPageComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        SetupDefaults();
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(new KiroWebUI.Pipeline.Services.GitHubValidationService());
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
    }

    [Fact]
    public void Settings_RendersPageHeader()
    {
        var component = Render<Settings>();

        Assert.Contains("Settings", component.Markup);
        Assert.NotNull(component.Find("h1"));
    }

    [Fact]
    public void Settings_RendersAllProviderSections()
    {
        var component = Render<Settings>();

        Assert.Contains("Issue Providers", component.Markup);
        Assert.Contains("Repository Providers", component.Markup);
        Assert.Contains("Agent Providers", component.Markup);
        Assert.Contains("Pipeline Configuration", component.Markup);
    }

    [Fact]
    public void Settings_RendersAddButtons_WhenNoProviders()
    {
        var component = Render<Settings>();

        Assert.Contains("+ Add Issue Provider", component.Markup);
        Assert.Contains("+ Add Repository Provider", component.Markup);
        Assert.Contains("+ Add Agent Provider", component.Markup);
    }

    [Fact]
    public void Settings_DisplaysExistingProviders()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "My GitHub" }
            });

        var component = Render<Settings>();

        Assert.Contains("My GitHub", component.Markup);
        Assert.Contains("GitHub", component.Markup);
    }

    [Fact]
    public void Settings_DisplaysEditAndDeleteButtons_ForExistingProvider()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test Provider" }
            });

        var component = Render<Settings>();

        var editButtons = component.FindAll(".btn-edit");
        var deleteButtons = component.FindAll(".btn-delete");

        Assert.True(editButtons.Count >= 1);
        Assert.True(deleteButtons.Count >= 1);
    }

    [Fact]
    public void Settings_ClickAddIssueProvider_ShowsForm()
    {
        var component = Render<Settings>();

        // Click the "Add Issue Provider" button
        var addButton = component.FindAll(".btn-add").First(b => b.TextContent.Contains("Issue"));
        addButton.Click();

        // Form should now be visible
        Assert.Contains("Add Issue Provider", component.Markup);
        Assert.Contains("Display Name", component.Markup);
        Assert.Contains("API URL", component.Markup);
        Assert.Contains("Token", component.Markup);
        Assert.Contains("Owner", component.Markup);
        Assert.Contains("Repository", component.Markup);
    }

    [Fact]
    public void Settings_ClickAddRepoProvider_ShowsFormWithBaseBranch()
    {
        var component = Render<Settings>();

        var addButton = component.FindAll(".btn-add").First(b => b.TextContent.Contains("Repository"));
        addButton.Click();

        Assert.Contains("Add Repository Provider", component.Markup);
        Assert.Contains("Base Branch", component.Markup);
    }

    [Fact]
    public void Settings_ClickAddAgentProvider_ShowsFormWithExecutablePath()
    {
        var component = Render<Settings>();

        var addButton = component.FindAll(".btn-add").First(b => b.TextContent.Contains("Agent"));
        addButton.Click();

        Assert.Contains("Add Agent Provider", component.Markup);
        Assert.Contains("Executable Path", component.Markup);
        Assert.Contains("Timeout", component.Markup);
        Assert.Contains("Agent Name", component.Markup);
    }

    [Fact]
    public void Settings_PipelineConfigSection_ShowsDefaultValues()
    {
        var component = Render<Settings>();

        Assert.Contains("Max Retries", component.Markup);
        Assert.Contains("Agent Timeout", component.Markup);
        Assert.Contains("Min Coverage Threshold", component.Markup);
        Assert.Contains("Security Scan Enabled", component.Markup);
        Assert.Contains("Save Pipeline Configuration", component.Markup);
    }

    [Fact]
    public void Settings_ClickCancelOnForm_HidesForm()
    {
        var component = Render<Settings>();

        // Open the form
        var addButton = component.FindAll(".btn-add").First(b => b.TextContent.Contains("Issue"));
        addButton.Click();
        Assert.Contains("Add Issue Provider", component.Markup);

        // Click cancel
        var cancelButton = component.Find(".btn-cancel");
        cancelButton.Click();

        // Form should be hidden, add button visible again
        Assert.Contains("+ Add Issue Provider", component.Markup);
    }

    [Fact]
    public async Task Settings_DeleteProvider_CallsStoreAndReloads()
    {
        var providers = new List<ProviderConfig>
        {
            new() { Id = "del-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "To Delete" }
        };
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(providers);
        _mockStore.Setup(s => s.DeleteProviderConfigAsync("del-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .Callback(() => providers.Clear())
            .Returns(Task.CompletedTask);

        var component = Render<Settings>();
        Assert.Contains("To Delete", component.Markup);

        // Click delete
        var deleteButton = component.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.DeleteProviderConfigAsync("del-1", ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Settings_WhenStoreThrows_ShowsErrorStatus()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var component = Render<Settings>();

        Assert.Contains("Failed to load settings", component.Markup);
        Assert.Contains("Store unavailable", component.Markup);
    }
}
