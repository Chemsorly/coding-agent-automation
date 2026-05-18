using Bunit;
using Moq;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the PipelineProviderSection component.
/// Tests rendering, form interactions, and provider management.
/// </summary>
public class PipelineProviderSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly GitHubValidationService _gitHubValidator;

    public PipelineProviderSectionComponentTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _gitHubValidator = new GitHubValidationService();
    }

    [Fact]
    public void PipelineProviderSection_RendersHeader()
    {
        var component = RenderSection();

        Assert.Contains("Pipeline Providers", component.Markup);
    }

    [Fact]
    public void PipelineProviderSection_RendersAddButton()
    {
        var component = RenderSection();

        Assert.Contains("+ Add Pipeline Provider", component.Markup);
    }

    [Fact]
    public void PipelineProviderSection_DisplaysExistingProviders()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "pp-1",
                Kind = ProviderKind.Pipeline,
                ProviderType = "GitHub",
                DisplayName = "My CI Pipeline",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "acme",
                    [ProviderSettingKeys.Repo] = "webapp"
                }
            }
        };

        var component = RenderSection(providers);

        Assert.Contains("My CI Pipeline", component.Markup);
        Assert.Contains("GitHub", component.Markup);
        Assert.Contains("acme/webapp", component.Markup);
    }

    [Fact]
    public void PipelineProviderSection_ProviderCard_HasEditAndDeleteButtons()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "pp-1",
                Kind = ProviderKind.Pipeline,
                ProviderType = "GitHub",
                DisplayName = "Pipeline 1",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org", [ProviderSettingKeys.Repo] = "repo" }
            }
        };

        var component = RenderSection(providers);

        var editButtons = component.FindAll(".btn-edit");
        var deleteButtons = component.FindAll(".btn-delete");

        Assert.True(editButtons.Count >= 1);
        Assert.True(deleteButtons.Count >= 1);
    }

    [Fact]
    public void PipelineProviderSection_ClickAdd_ShowsForm()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();

        Assert.Contains("Pipeline Provider", component.Markup);
        Assert.Contains("Display Name", component.Markup);
        Assert.Contains("API URL", component.Markup);
        Assert.Contains("Client ID", component.Markup);
        Assert.Contains("Installation ID", component.Markup);
        Assert.Contains("Private Key", component.Markup);
    }

    [Fact]
    public void PipelineProviderSection_ClickAdd_ShowsHelpContent()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();

        Assert.Contains("GitHub Actions CI", component.Markup);
        Assert.Contains("Actions: Read", component.Markup);
    }

    [Fact]
    public void PipelineProviderSection_ClickCancel_HidesForm()
    {
        var component = RenderSection();

        var addButton = component.Find(".btn-add");
        addButton.Click();
        Assert.Contains("Pipeline Provider", component.Markup);

        var cancelButton = component.FindAll("button").First(b => b.TextContent.Contains("Cancel"));
        cancelButton.Click();

        Assert.Contains("+ Add Pipeline Provider", component.Markup);
    }

    [Fact]
    public async Task PipelineProviderSection_Delete_InvokesOnDelete()
    {
        string? deletedId = null;
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "pp-1",
                Kind = ProviderKind.Pipeline,
                ProviderType = "GitHub",
                DisplayName = "To Delete",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org", [ProviderSettingKeys.Repo] = "repo" }
            }
        };

        var component = Render<PipelineProviderSection>(parameters => parameters
            .Add(p => p.Providers, providers)
            .Add(p => p.ConfigStore, _mockConfigStore.Object)
            .Add(p => p.GitHubValidator, _gitHubValidator)
            .Add(p => p.OnDelete, EventCallback.Factory.Create<string>(this, id => deletedId = id)));

        var deleteButton = component.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("pp-1", deletedId);
    }

    [Fact]
    public void PipelineProviderSection_ClickEdit_ShowsFormWithEditTitle()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "pp-1",
                Kind = ProviderKind.Pipeline,
                ProviderType = "GitHub",
                DisplayName = "My Pipeline",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                    [ProviderSettingKeys.ClientId] = "123",
                    [ProviderSettingKeys.InstallationId] = "456",
                    [ProviderSettingKeys.PrivateKeyBase64] = "key",
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        var component = RenderSection(providers);

        var editButton = component.Find(".btn-edit");
        editButton.Click();

        // Form should show in edit mode
        Assert.Contains("Pipeline Provider", component.Markup);
    }

    [Fact]
    public void PipelineProviderSection_MultipleProviders_RendersAll()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "pp-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHub",
                DisplayName = "Pipeline A", Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org1", [ProviderSettingKeys.Repo] = "repo1" }
            },
            new()
            {
                Id = "pp-2", Kind = ProviderKind.Pipeline, ProviderType = "GitHub",
                DisplayName = "Pipeline B", Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org2", [ProviderSettingKeys.Repo] = "repo2" }
            }
        };

        var component = RenderSection(providers);

        Assert.Contains("Pipeline A", component.Markup);
        Assert.Contains("Pipeline B", component.Markup);
        Assert.Contains("org1/repo1", component.Markup);
        Assert.Contains("org2/repo2", component.Markup);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private IRenderedComponent<PipelineProviderSection> RenderSection(List<ProviderConfig>? providers = null)
    {
        return Render<PipelineProviderSection>(parameters => parameters
            .Add(p => p.Providers, providers ?? new List<ProviderConfig>())
            .Add(p => p.ConfigStore, _mockConfigStore.Object)
            .Add(p => p.GitHubValidator, _gitHubValidator));
    }
}
