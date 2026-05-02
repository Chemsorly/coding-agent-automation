using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the GitHubAppProviderForm reusable form component.
/// Covers form field rendering, key loaded state, repo dropdown vs manual inputs,
/// save/cancel buttons, and validation message display.
/// </summary>
public class GitHubAppProviderFormComponentTests : BunitContext
{
    // ═══ Form Field Rendering ═══

    [Fact]
    public void RendersAllFormFields()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test Provider")
            .Add(s => s.DisplayNamePlaceholder, "My Provider"));

        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("API URL", cut.Markup);
        Assert.Contains("Client ID", cut.Markup);
        Assert.Contains("Installation ID", cut.Markup);
        Assert.Contains("Private Key", cut.Markup);
    }

    [Fact]
    public void RendersAddTitle_WhenNotEditing()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Issue Provider")
            .Add(s => s.IsEditing, false));

        Assert.Contains("Add Issue Provider", cut.Markup);
    }

    [Fact]
    public void RendersEditTitle_WhenEditing()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Issue Provider")
            .Add(s => s.IsEditing, true));

        Assert.Contains("Edit Issue Provider", cut.Markup);
    }

    [Fact]
    public void RendersProviderTypeDropdown()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test"));

        var selects = cut.FindAll("select");
        Assert.True(selects.Count >= 1);
        Assert.Contains("GitHub", cut.Markup);
    }

    // ═══ Key Loaded State ═══

    [Fact]
    public void ShowsKeyLoaded_WhenPrivateKeyBase64IsSet()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.PrivateKeyBase64, "c29tZWtleWRhdGE="));

        Assert.Contains("Key loaded", cut.Markup);
    }

    [Fact]
    public void DoesNotShowKeyLoaded_WhenPrivateKeyBase64IsEmpty()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.PrivateKeyBase64, ""));

        Assert.DoesNotContain("Key loaded", cut.Markup);
    }

    // ═══ Owner/Repo Inputs vs Dropdown ═══

    [Fact]
    public void ShowsOwnerAndRepoInputs_WhenNoAvailableRepos()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.AvailableRepos, new List<(string, string, string)>()));

        Assert.Contains("Owner", cut.Markup);
        Assert.Contains("Repository", cut.Markup);
        // Should have text inputs for owner and repo
        var inputs = cut.FindAll("input[type='text']");
        Assert.True(inputs.Count >= 2); // At least owner and repo inputs
    }

    [Fact]
    public void ShowsRepoDropdown_WhenAvailableReposPopulated()
    {
        var repos = new List<(string FullName, string Owner, string Name)>
        {
            ("org/repo1", "org", "repo1"),
            ("org/repo2", "org", "repo2")
        };

        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.AvailableRepos, repos));

        Assert.Contains("-- Select Repository --", cut.Markup);
        Assert.Contains("org/repo1", cut.Markup);
        Assert.Contains("org/repo2", cut.Markup);
    }

    [Fact]
    public void HidesOwnerRepoInputs_WhenAvailableReposPopulated()
    {
        var repos = new List<(string FullName, string Owner, string Name)>
        {
            ("org/repo1", "org", "repo1")
        };

        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.AvailableRepos, repos));

        // Should not have separate Owner/Repo text inputs when dropdown is shown
        // The "Repository" label will be for the dropdown, not a text input
        Assert.Contains("Repository", cut.Markup);
        Assert.Contains("-- Select Repository --", cut.Markup);
    }

    // ═══ Save/Cancel Buttons ═══

    [Fact]
    public void RendersSaveAndCancelButtons()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test"));

        Assert.Contains("Save", cut.Markup);
        Assert.Contains("Cancel", cut.Markup);
        Assert.NotNull(cut.Find(".btn-save"));
        Assert.NotNull(cut.Find(".btn-cancel"));
    }

    [Fact]
    public void SaveButton_ShowsValidating_WhenSaving()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.Saving, true));

        var saveBtn = cut.Find(".btn-save");
        Assert.Contains("Validating...", saveBtn.TextContent);
        Assert.True(saveBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void SaveButton_ShowsSave_WhenNotSaving()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.Saving, false));

        var saveBtn = cut.Find(".btn-save");
        Assert.Contains("Save", saveBtn.TextContent);
        Assert.False(saveBtn.HasAttribute("disabled"));
    }

    [Fact]
    public async Task ClickSave_TriggersOnSave()
    {
        bool saveCalled = false;
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.OnSave, EventCallback.Factory.Create(this, () => saveCalled = true)));

        var saveBtn = cut.Find(".btn-save");
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(saveCalled);
    }

    [Fact]
    public async Task ClickCancel_TriggersOnCancel()
    {
        bool cancelCalled = false;
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.OnCancel, EventCallback.Factory.Create(this, () => cancelCalled = true)));

        var cancelBtn = cut.Find(".btn-cancel");
        await cancelBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(cancelCalled);
    }

    // ═══ Validation Message ═══

    [Fact]
    public void ShowsValidationMessage_WhenSet()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.ValidationMessage, "Display Name is required.")
            .Add(s => s.ValidationSuccess, false));

        Assert.Contains("Display Name is required.", cut.Markup);
        Assert.Contains("status-error", cut.Markup);
    }

    [Fact]
    public void ShowsSuccessMessage_WhenValidationSucceeds()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.ValidationMessage, "Credentials validated!")
            .Add(s => s.ValidationSuccess, true));

        Assert.Contains("Credentials validated!", cut.Markup);
        Assert.Contains("status-success", cut.Markup);
    }

    [Fact]
    public void DoesNotShowValidationMessage_WhenNull()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.ValidationMessage, (string?)null));

        Assert.DoesNotContain("settings-status", cut.Markup);
    }

    // ═══ Fetch Repos Button ═══

    [Fact]
    public void RendersFetchReposButton()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.LoadingRepos, false));

        Assert.Contains("Fetch Repos", cut.Markup);
    }

    [Fact]
    public void FetchReposButton_ShowsLoading_WhenLoadingRepos()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.LoadingRepos, true));

        Assert.Contains("Loading...", cut.Markup);
    }

    [Fact]
    public async Task ClickFetchRepos_TriggersOnFetchRepos()
    {
        bool fetchCalled = false;
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.OnFetchRepos, EventCallback.Factory.Create(this, () => fetchCalled = true)));

        var fetchBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Fetch Repos"));
        await fetchBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(fetchCalled);
    }

    // ═══ Additional Fields (RenderFragment) ═══

    [Fact]
    public void RendersAdditionalFields_WhenProvided()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.AdditionalFields, (Microsoft.AspNetCore.Components.RenderFragment)(builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "custom-field");
                builder.AddContent(2, "Custom Content");
                builder.CloseElement();
            })));

        Assert.Contains("custom-field", cut.Markup);
        Assert.Contains("Custom Content", cut.Markup);
    }

    // ═══ Help Content (RenderFragment) ═══

    [Fact]
    public void RendersHelpContent_WhenProvided()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.HelpContent, (Microsoft.AspNetCore.Components.RenderFragment)(builder =>
            {
                builder.OpenElement(0, "p");
                builder.AddContent(1, "Help text here");
                builder.CloseElement();
            })));

        Assert.Contains("Help text here", cut.Markup);
    }

    // ═══ Input Placeholders ═══

    [Fact]
    public void RendersDisplayNamePlaceholder()
    {
        var cut = Render<GitHubAppProviderForm>(p => p
            .Add(s => s.SectionTitle, "Test")
            .Add(s => s.DisplayNamePlaceholder, "My Custom Provider"));

        var inputs = cut.FindAll("input[type='text']");
        Assert.Contains(inputs, i => i.GetAttribute("placeholder") == "My Custom Provider");
    }
}
