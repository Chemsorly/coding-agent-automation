using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for RepositoryPage — the per-template page at /pipelines/{id}.
/// </summary>
public class RepositoryPageTests : BunitContext
{
    private const string TestProjectId = "proj-1";
    private const string TestTemplateId = "tmpl-1";

    private static readonly PipelineJobTemplate DefaultTemplate = new()
    {
        Id = TestTemplateId,
        Name = "DotNet Repo",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        Enabled = true,
        ImplementationEnabled = true,
        ReviewEnabled = true,
        DecompositionEnabled = false,
        HousekeepingEnabled = false,
    };

    private static readonly PipelineProject DefaultProject = new()
    {
        Id = TestProjectId,
        Name = "My Project",
        TemplateIds = [TestTemplateId],
        Enabled = true,
    };

    private static UiPipelinesPage MakePage(
        PipelineJobTemplate? template = null,
        PipelineProject? project = null) =>
        new(
            Projects: [project ?? DefaultProject],
            Templates: [template ?? DefaultTemplate],
            IssueProviders: [new ProviderConfig { Id = "ip-1", DisplayName = "GitHub Issues", Kind = ProviderKind.Issue, ProviderType = "GitHub" }],
            RepoProviders: [new ProviderConfig { Id = "rp-1", DisplayName = "GitHub Repo", Kind = ProviderKind.Repository, ProviderType = "GitHub", Settings = new Dictionary<string, string> { ["owner"] = "org", ["repo"] = "myrepo" } }],
            PipelineProviders: [],
            QualityGateConfigs: [],
            ReviewerConfigs: [],
            AgentProfiles: [],
            DefaultRequiredAgentLabels: null,
            MaxRetries: 3);

    private static Mock<IUiConfigClient> SetupConfigClient(UiPipelinesPage page)
    {
        var client = new Mock<IUiConfigClient>();
        client.Setup(c => c.GetPipelinesPageAsync(It.IsAny<CancellationToken>())).ReturnsAsync(page);
        return client;
    }

    private static Mock<IUiSettingsClient> SetupSettingsClient()
    {
        var settings = new Mock<IUiSettingsClient>();
        settings.Setup(s => s.SaveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return settings;
    }

    private static Mock<IUiConsolidationClient> SetupConsolidationClient()
    {
        var client = new Mock<IUiConsolidationClient>();
        return client;
    }

    private void RegisterServices(
        Mock<IUiConfigClient> configClient,
        Mock<IUiSettingsClient> settingsClient,
        Mock<IUiConsolidationClient>? consolidationClient = null)
    {
        Services.AddSingleton(configClient.Object);
        Services.AddSingleton(settingsClient.Object);
        Services.AddSingleton((consolidationClient ?? SetupConsolidationClient()).Object);
        Services.AddSingleton(CodingAgent.Web.UnitTests.TestUtilities.UiAgentClientStub.With().Object);

        // Minimal dispatch drawer mocks
        var issueDrawer = new Mock<IIssueDrawerService>();
        issueDrawer.Setup(d => d.DrawerState).Returns(new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((false, null, null)), false));
        issueDrawer.Setup(d => d.DrawerReadiness).Returns(new Dictionary<string, DependencyCheckResult>());
        var prDrawer = new Mock<IPrReviewDrawerService>();
        prDrawer.Setup(d => d.DrawerState).Returns(new DrawerStateService<PullRequestSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((false, null, null)), false));
        var epicDrawer = new Mock<IEpicDrawerService>();
        epicDrawer.Setup(d => d.DrawerState).Returns(new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<(bool, string?, string?)>((false, null, null)), false));

        Services.AddSingleton(issueDrawer.Object);
        Services.AddSingleton(prDrawer.Object);
        Services.AddSingleton(epicDrawer.Object);
        Services.AddSingleton(sp => new DispatchDrawers(
            sp.GetRequiredService<IIssueDrawerService>(),
            sp.GetRequiredService<IPrReviewDrawerService>(),
            sp.GetRequiredService<IEpicDrawerService>()));
        Services.AddScoped<RepositoryPageService>();
    }

    // NOTE: No test covers PR/Epic button visibility. The issue requires "Browse pull requests" and
    // "Browse epics" to be hidden when ReviewEnabled=false and DecompositionEnabled=false respectively.
    // DefaultTemplate already sets DecompositionEnabled=false, so the epic-button case is trivially
    // testable. Add two facts: one asserting the PR button is hidden when ReviewEnabled=false, one
    // asserting the epic button is hidden when DecompositionEnabled=false (which is the default).
    // (TestQualityReviewer finding, issue #3576)

    [Fact]
    public void GlobalAdmin_CanEditBindingCard()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));
        var configClient = SetupConfigClient(MakePage());
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // Admin sees editable inputs in the Binding card
        var bindingCard = cut.Find("[data-testid='binding-card']");
        Assert.NotNull(bindingCard.QuerySelector("select")); // provider selects
        Assert.NotNull(bindingCard.QuerySelector("button.btn-save")); // Save button
        Assert.DoesNotContain("Managed by the operator", bindingCard.TextContent);
    }

    [Fact]
    public void GlobalAdmin_CanEditBehaviourCard()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));
        var configClient = SetupConfigClient(MakePage());
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // Admin sees editable inputs in the Behaviour card
        var behaviourCard = cut.Find("[data-testid='behaviour-card']");
        Assert.NotNull(behaviourCard.QuerySelector("input[type='text']")); // Name input
        Assert.NotNull(behaviourCard.QuerySelector("button.btn-save")); // Save button
    }

    [Fact]
    public void ProjectOperator_BothCardsReadOnly_CanOpenDispatchButtons()
    {
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.Operator)));
        var configClient = SetupConfigClient(MakePage());
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // Non-admin sees read-only binding card with "Managed by the operator"
        var bindingCard = cut.Find("[data-testid='binding-card']");
        Assert.Contains("Managed by the operator", bindingCard.TextContent);
        Assert.Null(bindingCard.QuerySelector("button.btn-save")); // No Save button

        // NOTE: The Behaviour card is not checked for read-only state here.
        // A project operator who could edit the Behaviour card would violate the permission model
        // ("only global admins edit"). Add assertions that the Behaviour card has no Save button and
        // no editable name input to guard against that regression. (TestQualityReviewer finding, issue #3576)

        // Dispatch buttons are present (operator can dispatch)
        var browseIssuesBtn = cut.Find("[data-testid='browse-issues-btn']");
        Assert.NotNull(browseIssuesBtn);
        Assert.False(browseIssuesBtn.HasAttribute("disabled"), "Browse issues button should be enabled for project operator");
    }

    [Fact]
    public void ReadOnlyUser_CannotDispatch()
    {
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.ReadOnly)));
        var configClient = SetupConfigClient(MakePage());
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // Browse issues button should be disabled (no dispatch permission)
        var browseIssuesBtn = cut.Find("[data-testid='browse-issues-btn']");
        Assert.NotNull(browseIssuesBtn);
        Assert.True(browseIssuesBtn.HasAttribute("disabled"), "Browse issues button should be disabled for read-only user");
    }

    [Fact]
    public void UserWithoutProjectAccess_ShowsNotFoundState()
    {
        // User has access to a different project, not the template's project
        Services.AddTestAccess(TestAccess.Scoped(("other-project", AccessRole.Operator)));
        var configClient = SetupConfigClient(MakePage());
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        Assert.Contains("Template not found", cut.Markup);
    }

    [Fact]
    public void UnknownTemplateId_ShowsNotFoundState()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));
        var configClient = SetupConfigClient(MakePage()); // page has TestTemplateId, not "unknown"
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, "unknown-id"));

        Assert.Contains("Template not found", cut.Markup);
    }

    [Fact]
    public void HousekeepingOptions_HiddenWhenHousekeepingOff()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));
        var template = DefaultTemplate with { HousekeepingEnabled = false };
        var configClient = SetupConfigClient(MakePage(template));
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // Housekeeping limit and cleanup are hidden when HousekeepingEnabled = false
        Assert.Empty(cut.FindAll("[data-testid='housekeeping-limit']"));
        Assert.Empty(cut.FindAll("[data-testid='housekeeping-cleanup']"));
    }

    [Fact]
    public async Task SaveBehaviourCard_ChangesOnlyBehaviourFields()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));
        var template = DefaultTemplate with
        {
            BrainProviderId = "brain-1",
            BrainReadOnly = true,
            RequiredLabels = ["kiro"],
            Enabled = true,
        };
        var configClient = SetupConfigClient(MakePage(template));
        var settingsClient = SetupSettingsClient();
        PipelineJobTemplate? savedTemplate = null;
        settingsClient.Setup(s => s.SaveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectId, PipelineJobTemplate, CancellationToken>((_, t, _) => savedTemplate = t)
            .Returns(Task.CompletedTask);
        RegisterServices(configClient, settingsClient);

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // Click the Behaviour card Save button
        await cut.InvokeAsync(() => cut.Find("[data-testid='behaviour-card'] button.btn-save").Click());

        // Verify binding fields are preserved
        Assert.NotNull(savedTemplate);
        Assert.Equal("brain-1", savedTemplate!.BrainProviderId);
        Assert.True(savedTemplate.BrainReadOnly);
        Assert.Equal(["kiro"], savedTemplate.RequiredLabels);
        Assert.True(savedTemplate.Enabled);
        // NOTE: The name assertion below is tautological — no behaviour field is mutated before Save,
        // so Assert.Equal(template.Name, savedTemplate.Name) passes even if behaviour-field edits are
        // silently dropped by a broken form binding. Improve by pre-populating _behaviourForm.Name
        // with a different value before clicking Save and asserting the new value was persisted.
        // (TestQualityReviewer finding, issue #3576)
        // Behaviour fields applied
        Assert.Equal(template.Name, savedTemplate.Name);
    }

    [Fact]
    public void StoredSecretValues_NeverAppearsInMarkup()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin));
        var template = DefaultTemplate with
        {
            Secrets = new Dictionary<string, string> { ["MY_KEY"] = "super-secret-value-xyz" }
        };
        var configClient = SetupConfigClient(MakePage(template));
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // The secret value must NEVER appear anywhere in the rendered HTML, not even in hidden markup
        Assert.DoesNotContain("super-secret-value-xyz", cut.Markup);
    }

    [Fact]
    public void ProjectAdmin_CanEditBehaviourCard_ButNotBindingCard()
    {
        // A project admin can edit the Behaviour card but not the Binding card.
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.Admin)));
        var configClient = SetupConfigClient(MakePage());
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // Behaviour card: project admin can edit
        var behaviourCard = cut.Find("[data-testid='behaviour-card']");
        Assert.NotNull(behaviourCard.QuerySelector("input[type='text']")); // Name input
        Assert.NotNull(behaviourCard.QuerySelector("button.btn-save")); // Save button

        // Binding card: project admin sees read-only "Managed by the operator"
        var bindingCard = cut.Find("[data-testid='binding-card']");
        Assert.Contains("Managed by the operator", bindingCard.TextContent);
        Assert.Null(bindingCard.QuerySelector("button.btn-save")); // No binding save button
    }

    [Fact]
    public void ProjectOperator_BehaviourCard_IsReadOnly()
    {
        // The operator fixes the NOTE from ProjectOperator_BothCardsReadOnly_CanOpenDispatchButtons:
        // a project operator must NOT be able to edit the Behaviour card.
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.Operator)));
        var configClient = SetupConfigClient(MakePage());
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        var behaviourCard = cut.Find("[data-testid='behaviour-card']");
        Assert.Null(behaviourCard.QuerySelector("button.btn-save")); // No Save button
        Assert.Null(behaviourCard.QuerySelector("input[type='text']")); // No name input
    }

    [Fact]
    public void ProjectOperator_CanStartRefactoringScan()
    {
        // A project operator on the template's project can see the Scan for refactorings button.
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.Operator)));
        var template = DefaultTemplate with { RepoProviderId = "rp-1" };
        var configClient = SetupConfigClient(MakePage(template));
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        // The scan button should be visible for a project operator
        Assert.Contains("Scan for refactorings", cut.Markup);
    }

    [Fact]
    public void ProjectReadOnly_CannotStartRefactoringScan()
    {
        // A project read-only user cannot see the Scan for refactorings button.
        Services.AddTestAccess(TestAccess.Scoped((TestProjectId, AccessRole.ReadOnly)));
        var template = DefaultTemplate with { RepoProviderId = "rp-1" };
        var configClient = SetupConfigClient(MakePage(template));
        RegisterServices(configClient, SetupSettingsClient());

        var cut = Render<RepositoryPage>(p => p.Add(x => x.TemplateId, TestTemplateId));

        Assert.DoesNotContain("Scan for refactorings", cut.Markup);
    }
}
