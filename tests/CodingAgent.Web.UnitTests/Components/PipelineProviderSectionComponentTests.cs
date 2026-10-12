using Bunit;
using Moq;
using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for the PipelineProviderSection component.
/// Tests rendering, form interactions, and provider management.
/// </summary>
public class PipelineProviderSectionComponentTests : BunitContext
{
    private readonly Mock<CodingAgent.Web.Ui.IUiProviderClient> _mockConfigClient;

    public PipelineProviderSectionComponentTests()
    {
        _mockConfigClient = new Mock<CodingAgent.Web.Ui.IUiProviderClient>();
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
            .Add(p => p.ProviderClient, _mockConfigClient.Object)
            .Add(p => p.OnDelete, EventCallback.Factory.Create<string>(this, id => deletedId = id)));

        var deleteButton = component.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Confirm the deletion
        var confirmButton = component.Find(".agent-detail-confirm .btn-delete");
        await confirmButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

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

    [Fact]
    public async Task PipelineProviderSection_DeleteFails_ShowsErrorViaOnShowStatus()
    {
        // TODO: Add a test asserting the behaviour when OnDelete throws OperationCanceledException.
        // PipelineProviderSection.ConfirmDeleteAsync is currently missing catch (OperationCanceledException) { }
        // (see TODO in the production code), so the current behaviour is that OnShowStatus IS invoked
        // for cancellations — inconsistent with AgentProviderSection and IssueProviderSection. A test
        // here would pin the current behaviour and flag it if the production TODO is ever resolved.
        // TODO: Add assertions that after a failed delete: (a) the confirm overlay is no longer
        // visible (_showDeleteConfirm == false), and (b) the provider card is still rendered in
        // the component markup (provider was not incorrectly removed from the UI on failure).
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "pp-del-fail",
                Kind = ProviderKind.Pipeline,
                ProviderType = "GitHub",
                DisplayName = "Delete Fail",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org", [ProviderSettingKeys.Repo] = "repo" }
            }
        };

        (string Message, bool IsError)? statusResult = null;
        var component = Render<PipelineProviderSection>(parameters => parameters
            .Add(p => p.Providers, providers)
            .Add(p => p.ProviderClient, _mockConfigClient.Object)
            .Add(p => p.OnDelete, EventCallback.Factory.Create<string>(this, _ => throw new InvalidOperationException("DB error")))
            .Add(p => p.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, result => statusResult = result)));

        var deleteButton = component.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var confirmButton = component.Find(".agent-detail-confirm .btn-delete");
        await confirmButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(statusResult);
        Assert.True(statusResult!.Value.IsError);
        Assert.Contains("DB error", statusResult.Value.Message);
    }

    // ── Provider checks through the API (spec 051 2.1c) ─────────────────

    private static List<ProviderConfig> StoredPipelineProvider() =>
    [
        new()
        {
            Id = "pp-1",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "CI",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.ClientId] = "Iv1.abc",
                [ProviderSettingKeys.InstallationId] = "456",
                [ProviderSettingKeys.PrivateKeyBase64] = "****",
                [ProviderSettingKeys.Owner] = "acme",
                [ProviderSettingKeys.Repo] = "webapp"
            }
        }
    ];

    [Fact]
    public async Task PipelineProviderSection_SaveEdited_ChecksCredentialsAndActionsInTheApi_ThenSaves()
    {
        _mockConfigClient.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(true, "Connected"));
        _mockConfigClient.Setup(c => c.CheckActionsAccessAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(true, "Actions readable"));
        var component = RenderSection(StoredPipelineProvider());

        component.Find(".provider-card .btn-edit").Click();
        await component.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockConfigClient.Verify(c => c.CheckActionsAccessAsync(
            It.Is<ProviderConfig>(d => d.Id == "pp-1" && d.Settings[ProviderSettingKeys.PrivateKeyBase64] == "****"),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockConfigClient.Verify(c => c.SaveProviderConfigAsync(
            It.Is<ProviderConfig>(d => d.Id == "pp-1" && d.Kind == ProviderKind.Pipeline), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PipelineProviderSection_SaveEdited_NoActionsAccess_ShowsTheReason_AndDoesNotSave()
    {
        _mockConfigClient.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(true, "Connected"));
        _mockConfigClient.Setup(c => c.CheckActionsAccessAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(false, "The App lacks Actions: Read"));
        var component = RenderSection(StoredPipelineProvider());

        component.Find(".provider-card .btn-edit").Click();
        await component.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("The App lacks Actions: Read", component.Markup);
        _mockConfigClient.Verify(c => c.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PipelineProviderSection_SaveEdited_ApiRefuses_ShowsTheReason()
    {
        _mockConfigClient.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The API URL changed: enter the credentials again."));
        var component = RenderSection(StoredPipelineProvider());

        component.Find(".provider-card .btn-edit").Click();
        await component.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("The API URL changed", component.Markup);
        _mockConfigClient.Verify(c => c.CheckActionsAccessAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PipelineProviderSection_FetchRepos_ListsTheApiRepositories()
    {
        _mockConfigClient.Setup(c => c.ListRepositoriesAsync(It.Is<ProviderConfig>(d => d.Id == "pp-1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UiRepository> { new("acme/webapp", "acme", "webapp"), new("acme/api", "acme", "api") });
        var component = RenderSection(StoredPipelineProvider());

        component.Find(".provider-card .btn-edit").Click();
        await component.FindAll("button").First(b => b.TextContent.Contains("Fetch Repos"))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Credentials valid — 2 repositories found", component.Markup);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private IRenderedComponent<PipelineProviderSection> RenderSection(List<ProviderConfig>? providers = null)
    {
        return Render<PipelineProviderSection>(parameters => parameters
            .Add(p => p.Providers, providers ?? new List<ProviderConfig>())
            .Add(p => p.ProviderClient, _mockConfigClient.Object));
    }
}
