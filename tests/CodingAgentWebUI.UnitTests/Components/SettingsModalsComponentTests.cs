using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the SettingsModals component.
/// Tests the Related Providers modal and Configure Labels modal behavior.
/// </summary>
public class SettingsModalsComponentTests : BunitContext
{
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IConfigurationStore> _mockConfigStore;

    public SettingsModalsComponentTests()
    {
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockConfigStore = new Mock<IConfigurationStore>();
    }

    [Fact]
    public void SettingsModals_InitialRender_NoModalsVisible()
    {
        var component = RenderSettingsModals();

        Assert.DoesNotContain("modal-overlay", component.Markup);
        Assert.DoesNotContain("Create Related Providers", component.Markup);
        Assert.DoesNotContain("Repository Setup", component.Markup);
    }

    [Fact]
    public void SettingsModals_ShowRelatedProviders_NonGitHub_DoesNotShowModal()
    {
        var component = RenderSettingsModals();

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "Jira", // Not GitHub
            DisplayName = "Jira Provider",
            Settings = new Dictionary<string, string> { ["owner"] = "org", ["repo"] = "repo" }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.DoesNotContain("modal-overlay", component.Markup);
    }

    [Fact]
    public void SettingsModals_ShowRelatedProviders_GitHub_ShowsModal()
    {
        var component = RenderSettingsModals();

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["clientId"] = "123",
                ["installationId"] = "456",
                ["privateKeyBase64"] = "key",
                ["owner"] = "acme",
                ["repo"] = "webapp"
            }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.Contains("modal-overlay", component.Markup);
        Assert.Contains("Create Related Providers", component.Markup);
        Assert.Contains("acme/webapp", component.Markup);
    }

    [Fact]
    public void SettingsModals_ShowRelatedProviders_ExcludesSourceKind()
    {
        var component = RenderSettingsModals();

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string>
            {
                ["owner"] = "acme",
                ["repo"] = "webapp"
            }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.Contains("Repository", component.Markup);
        Assert.Contains("Pipeline", component.Markup);
    }

    [Fact]
    public void SettingsModals_ShowRelatedProviders_ExistingProviderForSameOwnerRepo_NotOffered()
    {
        var existingRepoProviders = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Existing Repo",
                Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
            }
        };

        var component = RenderSettingsModals(repoProviders: existingRepoProviders);

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.Contains("Pipeline", component.Markup);
    }

    [Fact]
    public void SettingsModals_ShowRelatedProviders_AllExist_DoesNotShowModal()
    {
        var existingRepoProviders = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub",
                DisplayName = "Repo", Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
            }
        };
        var existingPipelineProviders = new List<ProviderConfig>
        {
            new()
            {
                Id = "pp-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHub",
                DisplayName = "Pipeline", Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
            }
        };

        var component = RenderSettingsModals(repoProviders: existingRepoProviders, pipelineProviders: existingPipelineProviders);

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.DoesNotContain("modal-overlay", component.Markup);
    }

    [Fact]
    public void SettingsModals_RelatedProvidersModal_HasAccessibilityAttributes()
    {
        var component = RenderSettingsModals();

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.Contains("role=\"dialog\"", component.Markup);
        Assert.Contains("aria-modal=\"true\"", component.Markup);
        Assert.Contains("aria-labelledby=\"related-providers-title\"", component.Markup);
    }

    [Fact]
    public void SettingsModals_RelatedProvidersModal_HasSkipButton()
    {
        var component = RenderSettingsModals();

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.Contains("Skip", component.Markup);
    }

    [Fact]
    public void SettingsModals_RelatedProvidersModal_HasCreateSelectedButton()
    {
        var component = RenderSettingsModals();

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        Assert.Contains("Create Selected", component.Markup);
    }

    [Fact]
    public async Task SettingsModals_ConfirmRelatedProviders_SavesConfigs()
    {
        _mockConfigStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var component = RenderSettingsModals();

        var savedConfig = new ProviderConfig
        {
            Id = "p-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string>
            {
                ["owner"] = "acme",
                ["repo"] = "webapp",
                ["apiUrl"] = "https://api.github.com",
                ["clientId"] = "123",
                ["installationId"] = "456",
                ["privateKeyBase64"] = "key"
            }
        };

        component.InvokeAsync(() => component.Instance.ShowRelatedProviders(savedConfig));

        // Click "Create Selected"
        var saveButton = component.Find(".btn-save");
        await saveButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockConfigStore.Verify(s => s.SaveProviderConfigAsync(
            It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public void SettingsModals_ShowConfigureLabels_ShowsModal()
    {
        var component = RenderSettingsModals();

        var config = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string> { ["owner"] = "acme", ["repo"] = "webapp" }
        };

        component.InvokeAsync(() => component.Instance.ShowConfigureLabels(config));

        Assert.Contains("Repository Setup", component.Markup);
        Assert.Contains("modal-overlay", component.Markup);
        Assert.Contains("Yes, configure", component.Markup);
        Assert.Contains("Skip", component.Markup);
    }

    [Fact]
    public void SettingsModals_ConfigureLabelsModal_HasAccessibilityAttributes()
    {
        var component = RenderSettingsModals();

        var config = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string>()
        };

        component.InvokeAsync(() => component.Instance.ShowConfigureLabels(config));

        Assert.Contains("role=\"dialog\"", component.Markup);
        Assert.Contains("aria-modal=\"true\"", component.Markup);
        Assert.Contains("aria-labelledby=\"configure-labels-title\"", component.Markup);
    }

    [Fact]
    public void SettingsModals_DismissConfigureLabels_HidesModal()
    {
        var component = RenderSettingsModals();

        var config = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My Issues",
            Settings = new Dictionary<string, string>()
        };

        component.InvokeAsync(() => component.Instance.ShowConfigureLabels(config));
        Assert.Contains("Repository Setup", component.Markup);

        // Click Skip to dismiss
        var skipButton = component.Find(".btn-cancel");
        skipButton.Click();

        Assert.DoesNotContain("Repository Setup", component.Markup);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private IRenderedComponent<SettingsModals> RenderSettingsModals(
        List<ProviderConfig>? issueProviders = null,
        List<ProviderConfig>? repoProviders = null,
        List<ProviderConfig>? pipelineProviders = null)
    {
        return Render<SettingsModals>(parameters => parameters
            .Add(p => p.ProviderFactory, _mockProviderFactory.Object)
            .Add(p => p.ConfigStore, _mockConfigStore.Object)
            .Add(p => p.IssueProviders, issueProviders ?? new List<ProviderConfig>())
            .Add(p => p.RepoProviders, repoProviders ?? new List<ProviderConfig>())
            .Add(p => p.PipelineProviders, pipelineProviders ?? new List<ProviderConfig>()));
    }
}
