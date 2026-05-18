using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for RepoProviderSection and IssueProviderSection components.
/// Covers rendering, add/edit/delete flows, and form visibility.
/// </summary>
public class ProviderSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly GitHubValidationService _gitHubValidator;

    public ProviderSectionComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _gitHubValidator = new GitHubValidationService();
    }

    // ═══ RepoProviderSection ═══

    [Fact]
    public void RepoSection_RendersHeader()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

        Assert.Contains("Repository Providers", cut.Markup);
    }

    [Fact]
    public void RepoSection_RendersAddButton()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

        Assert.Contains("My Repo", cut.Markup);
        Assert.Contains("GitHub", cut.Markup);
        Assert.Contains("myorg/myrepo", cut.Markup);
        Assert.Contains("develop", cut.Markup);
        Assert.Contains("Work", cut.Markup);
    }

    [Fact]
    public void RepoSection_ShowsRequiredLabels_ForWorkProviders()
    {
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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

        Assert.Contains("kiro", cut.Markup);
        Assert.Contains("dotnet", cut.Markup);
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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

        var addButton = cut.Find(".btn-add");
        addButton.Click();

        Assert.DoesNotContain("+ Add Repository Provider", cut.Markup);
    }

    [Fact]
    public void RepoSection_ClickCancel_HidesForm()
    {
        var cut = Render<RepoProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.OnDelete, EventCallback.Factory.Create<string>(this, v => deletedId = v)));

        var deleteButton = cut.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("rp-del", deletedId);
    }

    // ═══ IssueProviderSection ═══

    [Fact]
    public void IssueSection_RendersHeader()
    {
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        Assert.Contains("Issue Providers", cut.Markup);
    }

    [Fact]
    public void IssueSection_RendersAddButton()
    {
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        var editButtons = cut.FindAll(".btn-edit");
        var deleteButtons = cut.FindAll(".btn-delete");
        // At least 1 edit button (Initialize + Edit = 2 .btn-edit buttons per card)
        Assert.True(editButtons.Count >= 1);
        Assert.True(deleteButtons.Count >= 1);
    }

    [Fact]
    public void IssueSection_ClickAdd_ShowsForm()
    {
        var cut = Render<IssueProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object)
            .Add(s => s.OnDelete, EventCallback.Factory.Create<string>(this, v => deletedId = v)));

        var deleteButton = cut.Find(".btn-delete");
        await deleteButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("ip-del", deletedId);
    }

    [Fact]
    public async Task IssueSection_InitializeRepository_ShowsSuccessStatus()
    {
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        var initButton = cut.FindAll("button").First(b => b.TextContent.Contains("Initialize Provider"));
        await initButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("initialized successfully", cut.Markup);
    }

    [Fact]
    public async Task IssueSection_InitializeRepository_ShowsErrorOnFailure()
    {
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Auth failed"));
        mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        var initButton = cut.FindAll("button").First(b => b.TextContent.Contains("Initialize Provider"));
        await initButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Initialization failed", cut.Markup);
        Assert.Contains("Auth failed", cut.Markup);
    }

    [Fact]
    public async Task IssueSection_InitializeRepository_DisablesAllButtonsDuringInit()
    {
        var tcs = new TaskCompletionSource<bool>();
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);
        mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

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
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.GitHubValidator, _gitHubValidator)
            .Add(s => s.ProviderFactory, _mockProviderFactory.Object));

        // Click Initialize on the first provider (does not complete yet)
        var initButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Initialize Provider")).ToList();
        Assert.Equal(2, initButtons.Count);

        _ = cut.InvokeAsync(() => initButtons[0].Click());

        // Both Initialize buttons should now be disabled
        var allInitButtons = cut.FindAll("button").Where(b =>
            b.TextContent.Contains("Initialize Provider") || b.TextContent.Contains("Initializing...")).ToList();
        Assert.All(allInitButtons, btn => Assert.True(btn.HasAttribute("disabled")));

        // Complete the operation
        tcs.SetResult(true);
    }
}
