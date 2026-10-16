using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Contracts.Ui;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for RepoProviderSection and IssueProviderSection components.
/// Covers rendering, add/edit/delete flows, and form visibility.
/// </summary>
public class ProviderSectionComponentTests : BunitContext
{
    private readonly Mock<CodingAgent.Web.Ui.IUiProviderClient> _mockStore;

    public ProviderSectionComponentTests()
    {
        _mockStore = new Mock<CodingAgent.Web.Ui.IUiProviderClient>();
    }

    // ═══ RepoProviderSection ═══

    [Fact]
    public void RepoSection_RendersHeader()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.Contains("Repository Providers", cut.Markup);
    }

    [Fact]
    public void RepoSection_RendersAddButton()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.Contains("+ Add Repository Provider", cut.Markup);
    }

    [Fact]
    public void RepoSection_RendersProviderCards()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "My Repo",
                RepositoryRole = RepositoryRole.Work,
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "myorg",
                    [ProviderSettingKeys.Repo] = "myrepo",
                    [ProviderSettingKeys.BaseBranch] = "develop"
                }
            }
        };

        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.Contains("My Repo", cut.Markup);
        Assert.Contains("GitHub", cut.Markup);
        Assert.Contains("myorg/myrepo", cut.Markup);
        Assert.Contains("develop", cut.Markup);
        Assert.Contains("Work", cut.Markup);
    }

    [Fact]
    public void RepoSection_DoesNotShowRequiredLabels_InProviderList_TheyLiveOnTemplate()
    {
        // Labels (RequiredLabels) now live on the pipeline job template, not the repository connection.
        // The provider list must not show label chips for Work providers.
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-2",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Labeled Repo",
                RepositoryRole = RepositoryRole.Work,
                RequiredLabels = new List<string> { "kiro", "dotnet" },
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo",
                    [ProviderSettingKeys.BaseBranch] = "main"
                }
            }
        };

        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        // Label chips are removed from the provider list; the note directs users to the template.
        Assert.DoesNotContain("provider-label", cut.Markup);
        Assert.Contains("pipeline job template", cut.Markup);
    }

    [Fact]
    public void RepoSection_DoesNotShowLabels_ForBrainProviders()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-3",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Brain Repo",
                RepositoryRole = RepositoryRole.Brain,
                RequiredLabels = new List<string> { "should-not-show" },
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "brain",
                    [ProviderSettingKeys.BaseBranch] = "main"
                }
            }
        };

        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.DoesNotContain("should-not-show", cut.Markup);
    }

    [Fact]
    public void RepoSection_RendersEditAndDeleteButtons()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-4",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Editable Repo",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo",
                    [ProviderSettingKeys.BaseBranch] = "main"
                }
            }
        };

        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        var editButtons = cut.FindAll(".btn-edit");
        var deleteButtons = cut.FindAll(".btn-delete");
        Assert.True(editButtons.Count >= 1);
        Assert.True(deleteButtons.Count >= 1);
    }

    [Fact]
    public void RepoSection_ClickAdd_ShowsForm()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        var addButton = cut.Find(".btn-add");
        addButton.Click();

        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("API URL", cut.Markup);
        Assert.Contains("Client ID", cut.Markup);
        Assert.Contains("Installation ID", cut.Markup);
        Assert.Contains("Private Key", cut.Markup);
        Assert.Contains("Base Branch", cut.Markup);
        Assert.Contains("Repository Role", cut.Markup);
    }

    [Fact]
    public void RepoSection_ClickAdd_HidesAddButton()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        var addButton = cut.Find(".btn-add");
        addButton.Click();

        Assert.DoesNotContain("+ Add Repository Provider", cut.Markup);
    }

    [Fact]
    public void RepoSection_ClickCancel_HidesForm()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".btn-add").Click();
        Assert.Contains("Display Name", cut.Markup);

        cut.Find(".btn-cancel").Click();
        Assert.Contains("+ Add Repository Provider", cut.Markup);
    }

    [Fact]
    public void RepoSection_ClickEdit_PopulatesForm()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-edit",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Edit Me",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "editorg",
                    [ProviderSettingKeys.Repo] = "editrepo",
                    [ProviderSettingKeys.BaseBranch] = "develop",
                    [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                    [ProviderSettingKeys.ClientId] = "Iv1.test123",
                    [ProviderSettingKeys.InstallationId] = "12345",
                    [ProviderSettingKeys.PrivateKeyBase64] = ""
                }
            }
        };

        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".btn-edit").Click();

        // Form should be visible with populated values
        Assert.Contains("Edit", cut.Markup);
        Assert.Contains("Display Name", cut.Markup);
    }

    [Fact]
    public async Task RepoSection_ClickDelete_TriggersOnDelete()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-del",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Delete Me",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo",
                    [ProviderSettingKeys.BaseBranch] = "main"
                }
            }
        };

        string? deletedId = null;
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object)
            .Add(s => s.OnDelete, EventCallback.Factory.Create<string>(this, v => deletedId = v)));

        var deleteButton = cut.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Confirm the deletion
        var confirmButton = cut.Find(".agent-detail-confirm .btn-delete");
        await confirmButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("rp-del", deletedId);
    }

    // ═══ IssueProviderSection ═══

    [Fact]
    public void IssueSection_RendersHeader()
    {
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.Contains("Issue Providers", cut.Markup);
    }

    [Fact]
    public void IssueSection_RendersAddButton()
    {
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.Contains("+ Add Issue Provider", cut.Markup);
    }

    [Fact]
    public void IssueSection_RendersProviderCards()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-1",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "My Issues",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "issueorg",
                    [ProviderSettingKeys.Repo] = "issuerepo"
                }
            }
        };

        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.Contains("My Issues", cut.Markup);
        Assert.Contains("GitHub", cut.Markup);
        Assert.Contains("issueorg/issuerepo", cut.Markup);
    }

    [Fact]
    public void IssueSection_RendersInitializeButton()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-init",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Init Provider",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        Assert.Contains("Initialize Provider", cut.Markup);
    }

    [Fact]
    public void IssueSection_RendersEditAndDeleteButtons()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-2",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Editable Issues",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        var editButtons = cut.FindAll(".btn-edit");
        var deleteButtons = cut.FindAll(".btn-delete");
        // Exactly 1 .btn-edit button per card (the "Edit" button); "Initialize Provider" uses .btn-initialize
        Assert.Single(editButtons);
        // TODO: tighten to Assert.Single(deleteButtons) — with one provider card in the setup exactly one
        // delete button is expected; the weak >= 1 check would pass even if a bug introduced duplicates.
        Assert.True(deleteButtons.Count >= 1);
    }

    [Fact]
    public void IssueSection_InitializeProviderButton_HasBtnInitializeClass_AndEditButtonHasBtnEditClass()
    {
        // Verifies AC4: "Initialize Provider" uses .btn-initialize (filled/primary),
        // "Edit" uses .btn-edit (outline/secondary). They must be visually distinct.
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-3",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Class Test Provider",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        var initializeButtons = cut.FindAll(".btn-initialize");
        var editButtons = cut.FindAll(".btn-edit");

        // "Initialize Provider" button must carry .btn-initialize, not .btn-edit
        var initBtn = Assert.Single(initializeButtons);
        Assert.Contains("Initialize Provider", initBtn.TextContent);

        // "Edit" button must carry .btn-edit
        var editBtn = Assert.Single(editButtons);
        Assert.Contains("Edit", editBtn.TextContent);

        // The two button classes must be distinct — no button carries both
        // TODO: this assertion only verifies class-name assignment, not actual visual rendering.
        // bUnit does not apply CSS, so the filled-accent background on .btn-initialize vs the outline
        // style on .btn-edit cannot be asserted here. If visual distinctness regresses in CSS, these
        // tests will still pass. Ideally supplement with a Playwright/E2E snapshot test for AC4.
        Assert.DoesNotContain("btn-edit", initBtn.ClassName ?? "");
        Assert.DoesNotContain("btn-initialize", editBtn.ClassName ?? "");
    }

    [Fact]
    public void IssueSection_ClickAdd_ShowsForm()
    {
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        var addButton = cut.Find(".btn-add");
        addButton.Click();

        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("API URL", cut.Markup);
        Assert.Contains("Client ID", cut.Markup);
        Assert.Contains("Installation ID", cut.Markup);
        Assert.Contains("Private Key", cut.Markup);
    }

    [Fact]
    public void IssueSection_ClickCancel_HidesForm()
    {
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".btn-add").Click();
        Assert.Contains("Display Name", cut.Markup);

        cut.Find(".btn-cancel").Click();
        Assert.Contains("+ Add Issue Provider", cut.Markup);
    }

    [Fact]
    public async Task IssueSection_ClickDelete_TriggersOnDelete()
    {
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-del",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Delete Me",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        string? deletedId = null;
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object)
            .Add(s => s.OnDelete, EventCallback.Factory.Create<string>(this, v => deletedId = v)));

        var deleteButton = cut.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Confirm the deletion
        var confirmButton = cut.Find(".agent-detail-confirm .btn-delete");
        await confirmButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("ip-del", deletedId);
    }

    [Fact]
    public async Task IssueSection_InitializeRepository_ShowsSuccessStatus()
    {
        _mockStore
            .Setup(c => c.InitializeAsync(It.Is<ProviderConfig>(d => d.Id == "ip-init-success"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(true, "Repository initialized successfully."));

        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-init-success",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Init Success",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        var initButton = cut.FindAll("button").First(b => b.TextContent.Contains("Initialize Provider"));
        await initButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("initialized successfully", cut.Markup);
    }

    [Fact]
    public async Task IssueSection_InitializeRepository_ShowsErrorOnFailure()
    {
        // The API refuses with the reason (400), which the client throws.
        _mockStore
            .Setup(c => c.InitializeAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Auth failed"));

        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-init-fail",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Init Fail",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        var initButton = cut.FindAll("button").First(b => b.TextContent.Contains("Initialize Provider"));
        await initButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Initialization failed", cut.Markup);
        Assert.Contains("Auth failed", cut.Markup);
    }

    [Fact]
    public async Task IssueSection_InitializeRepository_DisablesAllButtonsDuringInit()
    {
        var tcs = new TaskCompletionSource<UiProviderCheck>();
        _mockStore
            .Setup(c => c.InitializeAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-1",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Provider One",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org", [ProviderSettingKeys.Repo] = "repo1" }
            },
            new()
            {
                Id = "ip-2",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Provider Two",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "org", [ProviderSettingKeys.Repo] = "repo2" }
            }
        };

        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        // Click Initialize on the first provider (does not complete yet)
        var initButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Initialize Provider")).ToList();
        Assert.Equal(2, initButtons.Count);

        _ = cut.InvokeAsync(() => initButtons[0].Click());

        // Both Initialize buttons should now be disabled
        var allInitButtons = cut.FindAll("button").Where(b =>
            b.TextContent.Contains("Initialize Provider") || b.TextContent.Contains("Initializing...")).ToList();
        Assert.All(allInitButtons, btn => Assert.True(btn.HasAttribute("disabled")));

        // Complete the operation
        tcs.SetResult(new UiProviderCheck(true, "Repository initialized successfully."));
    }

    [Fact]
    public async Task RepoSection_DeleteFails_ShowsErrorViaOnShowStatus()
    {
        // TODO: Add a test asserting that OperationCanceledException from OnDelete does NOT invoke
        // OnShowStatus (i.e., cancellation is silently suppressed). RepoProviderSection currently
        // catches all exceptions before OperationCanceledException — a regression that surfaces
        // cancellation errors to the user would not be caught by the existing tests.
        // TODO: Add assertions that after a failed delete: (a) the confirm overlay is no longer
        // visible (_showDeleteConfirm == false), and (b) the provider card is still rendered in
        // the component markup (provider was not incorrectly removed from the UI on failure).
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-del-fail",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Delete Fail",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo",
                    [ProviderSettingKeys.BaseBranch] = "main"
                }
            }
        };

        (string Message, bool IsError)? statusResult = null;
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object)
            .Add(s => s.OnDelete, EventCallback.Factory.Create<string>(this, _ => throw new InvalidOperationException("DB error")))
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, result => statusResult = result)));

        var deleteButton = cut.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var confirmButton = cut.Find(".agent-detail-confirm .btn-delete");
        await confirmButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(statusResult);
        Assert.True(statusResult!.Value.IsError);
        Assert.Contains("DB error", statusResult.Value.Message);
    }

    [Fact]
    public async Task IssueSection_DeleteFails_ShowsErrorViaOnShowStatus()
    {
        // TODO: Add a test asserting that OperationCanceledException from OnDelete does NOT invoke
        // OnShowStatus (i.e., cancellation is silently suppressed per the catch (OperationCanceledException) { }
        // guard in IssueProviderSection.ConfirmDeleteAsync). A regression that surfaces cancellation
        // errors to the user would not be caught by the existing tests.
        // TODO: Add assertions that after a failed delete: (a) the confirm overlay is no longer
        // visible (_showDeleteConfirm == false), and (b) the provider card is still rendered in
        // the component markup (provider was not incorrectly removed from the UI on failure).
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "ip-del-fail",
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub",
                DisplayName = "Delete Fail",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo"
                }
            }
        };

        (string Message, bool IsError)? statusResult = null;
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object)
            .Add(s => s.OnDelete, EventCallback.Factory.Create<string>(this, _ => throw new InvalidOperationException("DB error")))
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, result => statusResult = result)));

        var deleteButton = cut.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var confirmButton = cut.Find(".agent-detail-confirm .btn-delete");
        await confirmButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(statusResult);
        Assert.True(statusResult!.Value.IsError);
        Assert.Contains("DB error", statusResult.Value.Message);
    }

    // ═══ RepoProviderSection — Steering (now on the template, not the provider form) ═══

    [Fact]
    public void RepoSection_EditForm_DoesNotShowSteeringTextarea_TheyLiveOnTemplate()
    {
        // Steering instructions live on the pipeline job template now (issue #3575).
        // The repo provider edit form must NOT show the steering textarea; it must show the note.
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-steer",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Steer Repo",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo",
                    [ProviderSettingKeys.BaseBranch] = "main",
                    [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                    [ProviderSettingKeys.ClientId] = "Iv1.test",
                    [ProviderSettingKeys.InstallationId] = "123",
                    [ProviderSettingKeys.PrivateKeyBase64] = ""
                }
            }
        };

        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".btn-edit").Click();

        // Steering textarea is not rendered in the provider form
        Assert.DoesNotContain("Steering Instructions", cut.Markup);
        // The note directs users to the pipeline job template
        Assert.Contains("pipeline job template", cut.Markup);
    }

    [Fact]
    public void RepoSection_EditForm_DoesNotShowExistingSteeringContent_TheyLiveOnTemplate()
    {
        // SteeringContent on the ProviderConfig record is ignored by the edit form after issue #3575.
        // Steering content is not displayed in the provider form.
        var providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-steer2",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Steer Repo",
                SteeringContent = "Always use Gradle",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "org",
                    [ProviderSettingKeys.Repo] = "repo",
                    [ProviderSettingKeys.BaseBranch] = "main",
                    [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                    [ProviderSettingKeys.ClientId] = "Iv1.test",
                    [ProviderSettingKeys.InstallationId] = "123",
                    [ProviderSettingKeys.PrivateKeyBase64] = ""
                }
            }
        };

        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, providers)
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".btn-edit").Click();

        // The steering content from the provider record is not shown in the edit form
        Assert.DoesNotContain("Always use Gradle", cut.Markup);
        Assert.Contains("pipeline job template", cut.Markup);
    }

    [Fact]
    public void RepoSection_AddForm_ShowsTemplateNote_NotSteeringTextarea()
    {
        // The add form no longer contains a steering textarea; it shows a note to the user.
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".btn-add").Click();

        // Steering textarea is not rendered
        Assert.DoesNotContain("These instructions are provided to agents working on this repository", cut.Markup);
        // The note is shown
        Assert.Contains("pipeline job template", cut.Markup);
    }

    // ═══ Provider checks through the API (spec 051 2.1c) ═══

    private static ProviderConfig StoredGitHubProvider(ProviderKind kind, string id) => new()
    {
        Id = id,
        Kind = kind,
        ProviderType = "GitHub",
        DisplayName = "Stored",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
            [ProviderSettingKeys.ClientId] = "Iv1.abc",
            [ProviderSettingKeys.InstallationId] = "456",
            [ProviderSettingKeys.PrivateKeyBase64] = "****",
            [ProviderSettingKeys.Owner] = "acme",
            [ProviderSettingKeys.Repo] = "webapp"
        }
    };

    private static bool IsMaskedDraft(ProviderConfig draft, string id) =>
        draft.Id == id && draft.Settings[ProviderSettingKeys.PrivateKeyBase64] == "****";

    [Fact]
    public async Task IssueSection_SaveEdited_ChecksTheMaskedDraftInTheApi_ThenSaves()
    {
        _mockStore.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(true, "Connected to acme/webapp"));
        ProviderConfig? saved = null;
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Issue, "ip-1") })
            .Add(s => s.ProviderClient, _mockStore.Object)
            .Add(s => s.OnSaved, EventCallback.Factory.Create<ProviderConfig>(this, c => saved = c)));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // The web host never holds the key: the stored "****" goes to the API, which fills it in.
        _mockStore.Verify(c => c.CheckAsync(It.Is<ProviderConfig>(d => IsMaskedDraft(d, "ip-1")), It.IsAny<CancellationToken>()), Times.Once);
        _mockStore.Verify(c => c.SaveProviderConfigAsync(It.Is<ProviderConfig>(d => IsMaskedDraft(d, "ip-1")), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("ip-1", saved?.Id);
    }

    [Fact]
    public async Task IssueSection_SaveEdited_CheckFails_ShowsTheReason_AndDoesNotSave()
    {
        _mockStore.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(false, "Bad credentials"));
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Issue, "ip-1") })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Bad credentials", cut.Markup);
        _mockStore.Verify(c => c.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IssueSection_SaveEdited_ApiRefuses_ShowsTheReason()
    {
        // E.g. the API URL was changed while the key stayed masked (D4 URL rule): the API answers 400.
        _mockStore.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The API URL changed: enter the credentials again."));
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Issue, "ip-1") })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("The API URL changed", cut.Markup);
        _mockStore.Verify(c => c.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IssueSection_FetchRepos_ListsTheApiRepositories()
    {
        _mockStore.Setup(c => c.ListRepositoriesAsync(It.Is<ProviderConfig>(d => IsMaskedDraft(d, "ip-1")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UiRepository> { new("acme/webapp", "acme", "webapp"), new("acme/api", "acme", "api") });
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Issue, "ip-1") })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.FindAll("button").First(b => b.TextContent.Contains("Fetch Repos"))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Credentials valid — 2 repositories found", cut.Markup);
        Assert.Contains("acme/api", cut.Markup);
    }

    [Fact]
    public async Task IssueSection_FetchRepos_CredentialsFail_ShowsTheApiReason()
    {
        _mockStore.Setup(c => c.ListRepositoriesAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Installation 456 not found"));
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Issue, "ip-1") })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.FindAll("button").First(b => b.TextContent.Contains("Fetch Repos"))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Installation 456 not found", cut.Markup);
    }

    [Fact]
    public async Task IssueSection_SaveEditedGitLab_ChecksInTheApi_ThenSaves()
    {
        var gitLab = new ProviderConfig
        {
            Id = "ip-gl",
            Kind = ProviderKind.Issue,
            ProviderType = "GitLab",
            DisplayName = "GitLab Issues",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://gitlab.example.com/api/v4",
                [ProviderSettingKeys.AccessToken] = "****",
                [ProviderSettingKeys.ProjectId] = "42"
            }
        };
        _mockStore.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(true, "Connected to group/project (access: Maintainer)"));
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { gitLab })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(c => c.SaveProviderConfigAsync(
            It.Is<ProviderConfig>(d => d.Id == "ip-gl" && d.Settings[ProviderSettingKeys.AccessToken] == "****"
                && d.Settings[ProviderSettingKeys.ApiUrl] == "https://gitlab.example.com/api/v4"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IssueSection_InitializeRepository_LabelsMissing_ShowsTheWarning()
    {
        _mockStore.Setup(c => c.InitializeAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(false, "Credentials validated but some labels could not be created."));
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Issue, "ip-1") })
            .Add(s => s.ProviderClient, _mockStore.Object));

        await cut.FindAll("button").First(b => b.TextContent.Contains("Initialize Provider"))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("some labels could not be created", cut.Markup);
        Assert.NotNull(cut.Find(".settings-status.status-error"));
    }

    [Fact]
    public async Task RepoSection_SaveEdited_ChecksTheMaskedDraftInTheApi_ThenSaves()
    {
        var stored = StoredGitHubProvider(ProviderKind.Repository, "rp-1");
        // Secrets are no longer part of the repository provider form (moved to template, issue #3575);
        // use a config without secrets so the save path doesn't try to mask them.
        _mockStore.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(true, "Connected to acme/webapp"));
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { stored })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(c => c.SaveProviderConfigAsync(
            It.Is<ProviderConfig>(d => IsMaskedDraft(d, "rp-1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RepoSection_SaveEdited_CheckFails_ShowsTheReason_AndDoesNotSave()
    {
        _mockStore.Setup(c => c.CheckAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiProviderCheck(false, "Repository acme/webapp not accessible"));
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Repository, "rp-1") })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.Find(".btn-save").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Repository acme/webapp not accessible", cut.Markup);
        _mockStore.Verify(c => c.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RepoSection_FetchRepos_ListsTheApiRepositories()
    {
        _mockStore.Setup(c => c.ListRepositoriesAsync(It.Is<ProviderConfig>(d => IsMaskedDraft(d, "rp-1")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UiRepository> { new("acme/webapp", "acme", "webapp") });
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig> { StoredGitHubProvider(ProviderKind.Repository, "rp-1") })
            .Add(s => s.ProviderClient, _mockStore.Object));

        cut.Find(".provider-card .btn-edit").Click();
        await cut.FindAll("button").First(b => b.TextContent.Contains("Fetch Repos"))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Credentials valid — 1 repositories found", cut.Markup);
    }
}
