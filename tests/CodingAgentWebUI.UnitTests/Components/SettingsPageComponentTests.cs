using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Settings page.
/// Renders the actual Blazor component and asserts on markup and interactions.
/// </summary>
public class SettingsPageComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockProviderFactory;

    public SettingsPageComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        SetupDefaults();
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(new CodingAgentWebUI.Infrastructure.GitHub.GitHubValidationService());
        Services.AddSingleton(_mockProviderFactory.Object);
        Services.AddSingleton(new ModelFetchService(
            new AgentRegistryService(Serilog.Log.Logger),
            new Mock<IAgentCommunication>().Object,
            Serilog.Log.Logger));
    }

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void Settings_RendersPageHeader()
    {
        var component = Render<Settings>();

        Assert.Contains("Settings", component.Markup);
        Assert.NotNull(component.Find("h1"));
    }

    [Fact]
    public void Settings_RendersTreeNavAndDefaultSection()
    {
        var component = Render<Settings>();

        // Tree nav contains group headers
        Assert.Contains("Providers", component.Markup);
        Assert.Contains("Pipeline", component.Markup);

        // Default selected node is Issue Providers — its section is visible
        Assert.Contains("Issue Providers", component.Markup);

        // Tree nav is rendered
        Assert.NotNull(component.Find(".settings-tree"));
    }

    [Fact]
    public void Settings_RendersAddButton_ForDefaultSection()
    {
        var component = Render<Settings>();

        // Default section is Issue Providers — its add button is visible
        Assert.Contains("+ Add Issue Provider", component.Markup);
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
        Assert.Contains("Client ID", component.Markup);
        Assert.Contains("Installation ID", component.Markup);
        Assert.Contains("Private Key", component.Markup);
        Assert.Contains("Owner", component.Markup);
        Assert.Contains("Repository", component.Markup);
    }

    [Fact]
    public void Settings_ClickAddRepoProvider_ShowsFormWithBaseBranch()
    {
        var component = Render<Settings>();

        // Navigate to Repository Providers section via tree nav
        var repoNode = component.FindAll(".tree-node").First(n => n.TextContent.Contains("Repository"));
        repoNode.Click();

        var addButton = component.FindAll(".btn-add").First(b => b.TextContent.Contains("Repository"));
        addButton.Click();

        Assert.Contains("Add Repository Provider", component.Markup);
        Assert.Contains("Base Branch", component.Markup);
    }

    [Fact]
    public void Settings_ClickAddAgentProvider_ShowsFormWithExecutablePath()
    {
        var component = Render<Settings>();

        // Navigate to Agent Providers section via tree nav
        var agentNode = component.FindAll(".tree-node").First(n => n.TextContent.Contains("Agent"));
        agentNode.Click();

        var addButton = component.FindAll(".btn-add").First(b => b.TextContent.Contains("Agent"));
        addButton.Click();

        Assert.Contains("Add Agent Provider", component.Markup);
        Assert.Contains("Executable Path", component.Markup);
        Assert.Contains("Timeout", component.Markup);
        Assert.Contains("Agent Name", component.Markup);
    }

    [Fact]
    public void Settings_DefaultLoad_ShowsIssueProviders_NotPipelineConfig()
    {
        var component = Render<Settings>();

        // Default node is Issue Providers — its section is visible
        Assert.Contains("Issue Providers", component.Markup);

        // Pipeline config fields should NOT be visible on default load
        Assert.DoesNotContain("Max Retries", component.Markup);
        Assert.DoesNotContain("Agent Timeout", component.Markup);
        Assert.DoesNotContain("Min Coverage Threshold", component.Markup);
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

    [Fact]
    public void Settings_RelatedProvidersModal_NotVisibleByDefault()
    {
        var component = Render<Settings>();

        Assert.DoesNotContain("Create Related Providers", component.Markup);
        Assert.DoesNotContain("modal-overlay", component.Markup);
    }

    [Fact]
    public void Settings_RelatedProvidersModal_NotVisibleBeforeProviderSave()
    {
        // The modal should not appear until a GitHub provider is successfully saved.
        // Since GitHubValidator is a real instance that will reject invalid credentials,
        // we verify the modal remains hidden when the page loads with existing providers.
        var component = Render<Settings>();

        // Default section (Issue Providers) is visible
        Assert.Contains("Issue Providers", component.Markup);
        // Tree nav contains other provider nodes
        Assert.Contains("Repository", component.Markup);
        Assert.Contains("Pipeline", component.Markup);
        Assert.DoesNotContain("modal-overlay", component.Markup);
        Assert.DoesNotContain("Create Related Providers", component.Markup);
    }

    [Fact]
    public void Settings_RelatedProvidersModal_RendersExistingProviders_InProviderList()
    {
        // Verify that existing providers for the same owner/repo are displayed in the provider list,
        // which is a prerequisite for the modal's existing-provider detection to work correctly.
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "My Issues",
                    Settings = new() { [ProviderSettingKeys.Owner] = "acme", [ProviderSettingKeys.Repo] = "webapp" } }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "My Repo",
                    Settings = new() { [ProviderSettingKeys.Owner] = "acme", [ProviderSettingKeys.Repo] = "webapp", [ProviderSettingKeys.BaseBranch] = "main" } }
            });

        var component = Render<Settings>();

        // Default section is Issue Providers — issue provider is visible
        Assert.Contains("My Issues", component.Markup);

        // Navigate to Repository section to see repo provider
        var repoNode = component.FindAll(".tree-node").First(n => n.TextContent.Contains("Repository"));
        repoNode.Click();
        Assert.Contains("My Repo", component.Markup);
    }

    [Fact]
    public void Settings_ModalMarkup_HasAccessibilityAttributes()
    {
        // Verify the modal markup includes proper ARIA attributes by checking the source file.
        // Use the test assembly location to find the source file relative to the project root.
        var testDir = AppContext.BaseDirectory;
        // Navigate from bin/Debug/net10.0 up to the solution root, then to the source file
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var razorPath = Path.Combine(solutionRoot, "src", "CodingAgentWebUI", "Components", "Pages", "SettingsModals.razor");

        // Skip if source file not available (e.g., in CI with only binaries)
        if (!File.Exists(razorPath))
        {
            Assert.Fail($"Source file not found at {razorPath}. Skipping accessibility attribute check.");
            return;
        }

        var razorContent = File.ReadAllText(razorPath);

        Assert.Contains("role=\"dialog\"", razorContent);
        Assert.Contains("aria-modal=\"true\"", razorContent);
        Assert.Contains("aria-labelledby=\"related-providers-title\"", razorContent);
        Assert.Contains("HandleModalKeyDown", razorContent);
    }

    [Fact]
    public void Settings_IssueProviderCard_DoesNotShowConfigureLabelsButton()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "My Issues",
                    Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org", [ProviderSettingKeys.Repo] = "repo" } }
            });

        var component = Render<Settings>();

        Assert.DoesNotContain("Configure Labels", component.Markup);
    }

    [Fact]
    public void Settings_ConfigureLabelsModal_HasAccessibilityAttributes()
    {
        var razorPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "CodingAgentWebUI", "Components", "Pages", "SettingsModals.razor");
        if (!File.Exists(razorPath))
        {
            Assert.Fail($"Source file not found at {razorPath}. Skipping accessibility attribute check.");
            return;
        }

        var razorContent = File.ReadAllText(razorPath);

        Assert.Contains("aria-labelledby=\"configure-labels-title\"", razorContent);
        Assert.Contains("HandleConfigureLabelsKeyDown", razorContent);
    }
}
