using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Triage;
using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using static CodingAgent.Web.UnitTests.Components.TriageTestData;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>The triage list (spec 050, B1) and the new-triage form (B2).</summary>
public class TriageListAndFormComponentTests : BunitContext
{
    private readonly Mock<IUiTriageClient> _triages = new();
    private readonly Mock<IUiConfigClient> _config = new();
    private readonly CockpitState _state = new();
    private readonly List<TriageListQuery> _queries = [];

    private static readonly PipelineProject Shop = new()
    {
        Id = ShopProjectId, Name = "Shop", Enabled = true, TemplateIds = ["t1", "t2"],
        McpServers = [new McpServerConfig { Name = "grafana" }],
    };
    private static readonly PipelineProject Billing = new() { Id = OtherProjectId, Name = "Billing", Enabled = true };
    private static readonly List<PipelineJobTemplate> ShopTemplates =
    [
        new() { Id = "t1", Name = "storefront-web", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true },
        new() { Id = "t2", Name = "checkout-api", IssueProviderId = "ip-2", RepoProviderId = "rp-2", Enabled = true },
    ];
    private static readonly List<AgentProfile> Profiles =
    [
        new() { Id = "p1", DisplayName = "default", AgentProviderConfigId = "ap-1", Enabled = true, MatchLabels = ["dotnet"],
            McpServers = [new McpServerConfig { Name = "sonarqube" }, new McpServerConfig { Name = "grafana" }] },
    ];

    public TriageListAndFormComponentTests()
    {
        _triages.Setup(t => t.ListAsync(It.IsAny<TriageListQuery>(), It.IsAny<CancellationToken>()))
            .Callback((TriageListQuery q, CancellationToken _) => _queries.Add(q))
            .ReturnsAsync(new TriageListPage());
        _triages.Setup(t => t.FindSimilarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        // Spec 051 2.7: the API builds the preview; here the same builder answers from the arranged data.
        _triages.Setup(t => t.GetPreviewAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string projectId, string? startIn, CancellationToken _) => Task.FromResult(projectId == ShopProjectId
                ? TriagePreviewBuilder.Build(Shop, ShopTemplates, startIn, [], Profiles, new PipelineConfiguration())
                : TriagePreviewBuilder.Build(Billing, [], startIn, [], Profiles, new PipelineConfiguration())));
        _config.Setup(c => c.GetPipelineSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiPipelineSettings(3, new PipelineConfiguration().TriageRetentionDays));
        _config.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new UiProject(ShopProjectId, "Shop", true),
            new UiProject(OtherProjectId, "Billing", true),
        ]);
        _config.Setup(c => c.GetTemplatesAsync(ShopProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ShopTemplates.Select(t => new UiTemplate(t.Id, t.Name, t.Enabled)).ToList());
        _config.Setup(c => c.GetTemplatesAsync(OtherProjectId, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        _state.SetProject(ShopProjectId, "Shop");
        Services.AddSingleton(_triages.Object);
        Services.AddSingleton(_config.Object);
        Services.AddSingleton(_state);
        Services.AddSingleton(Mock.Of<IJSRuntime>());
    }

    private void ListReturns(params TriageListItem[] items) =>
        _triages.Setup(t => t.ListAsync(It.Is<TriageListQuery>(q => q.PageSize > 1), It.IsAny<CancellationToken>()))
            .Callback((TriageListQuery q, CancellationToken _) => _queries.Add(q))
            .ReturnsAsync(new TriageListPage { Items = items, Total = items.Length });

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public void List_ShowsStatusVerdictIssuesAndSource()
    {
        ListReturns(
            ListItem(TriageStatus.NeedsReview, "Orders stuck in Pending", verdict: TriageVerdict.CauseFound, drafts: 3),
            ListItem(TriageStatus.IssuesCreated, "Search ignores umlauts", TriageSource.Issue, TriageVerdict.CauseFound, 1, 1,
                issueUrl: "https://github.com/acme/web/issues/431"));
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));

        var cut = Render<Triage>();

        var rows = cut.FindAll("[data-testid='triage-row']");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("RCA ready").And.Contain("Cause found · high").And.Contain("0 of 3").And.Contain("Operator · anna");
        rows[1].QuerySelector("a[target='_blank']")!.GetAttribute("href").Should().Be("https://github.com/acme/web/issues/431");
        rows[0].QuerySelector("a.triage-row-title")!.GetAttribute("href").Should().StartWith("triage/");
    }

    [Fact]
    public void List_QueriesTheSelectedProjectAndTheChosenTab()
    {
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));
        var cut = Render<Triage>();
        _queries.Clear();

        cut.Find("[data-testid='triage-tab-need-you']").Click();

        _queries.Should().Contain(q => q.Tab == TriageListTab.NeedYou && q.PageSize > 1 && q.ProjectId == ShopProjectId);
    }

    [Fact]
    public void List_AllProjectsScope_QueriesWithoutAProject()
    {
        _state.SetProject("", null);
        Services.AddTestAccess(TestAccess.Global(AccessRole.ReadOnly));

        Render<Triage>();

        _queries.Should().NotBeEmpty().And.OnlyContain(q => q.ProjectId == null);
    }

    [Theory]
    [InlineData(AccessRole.Operator, true)]
    [InlineData(AccessRole.ReadOnly, false)]
    public void List_NewTriageButton_OnlyForOperators(AccessRole role, bool shown)
    {
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, role)));

        var cut = Render<Triage>();

        cut.FindAll("[data-testid='triage-new']").Should().HaveCount(shown ? 1 : 0);
    }

    // ── New ──────────────────────────────────────────────────────────────────

    [Fact]
    public void New_ShowsWhatTheAgentWillUse()
    {
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));

        var cut = Render<TriageNew>();

        var preview = cut.Find("[data-testid='triage-preview']").TextContent;
        preview.Should().Contain("checkout-api").And.Contain("runs the job").And.Contain("storefront-web").And.Contain("read-only");
        preview.Should().Contain("grafana").And.Contain("project").And.Contain("sonarqube").And.Contain("profile");
        cut.FindAll("[data-testid='triage-preview-problem']").Should().BeEmpty();
    }

    [Fact]
    public void New_StartInChangesTheExecutor()
    {
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));
        var cut = Render<TriageNew>();

        cut.Find("[data-testid='triage-start']").Change("t1");

        var first = cut.Find("[data-testid='triage-preview'] .triage-list li").TextContent;
        first.Should().Contain("storefront-web").And.Contain("runs the job");
    }

    [Fact]
    public void New_OnlyProjectsTheUserOperates_AreOffered()
    {
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator), (OtherProjectId, AccessRole.ReadOnly)));

        var cut = Render<TriageNew>();

        cut.FindAll("[data-testid='triage-project'] option").Select(o => o.TextContent).Should().Equal("Shop");
    }

    [Fact]
    public void New_ProjectWithoutAnEnabledTemplate_CannotStart()
    {
        _state.SetProject(OtherProjectId, "Billing");
        Services.AddTestAccess(TestAccess.Scoped((OtherProjectId, AccessRole.Operator)));
        var cut = Render<TriageNew>();

        cut.Find("[data-testid='triage-title']").Input("Invoices missing");
        cut.Find("[data-testid='triage-happened']").Input("No invoice mail");
        cut.Find("[data-testid='triage-expected']").Input("A mail per order");

        cut.Find("[data-testid='triage-preview-problem']").TextContent.Should().Contain("no enabled template");
        cut.Find("[data-testid='triage-start-button']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void New_Start_CreatesTheTriageAndOpensIt()
    {
        var created = Detail(OperatorRecord(ShopProjectId));
        _triages.Setup(t => t.CreateAsync(It.IsAny<CreateTriageRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(created);
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));
        var cut = Render<TriageNew>();

        cut.Find("[data-testid='triage-title']").Input("Checkout returns 502");
        cut.Find("[data-testid='triage-happened']").Input("One in five checkouts fails");
        cut.Find("[data-testid='triage-expected']").Input("Checkout completes");
        cut.Find("[data-testid='triage-start']").Change("t1");
        cut.Find("form").Submit();

        _triages.Verify(t => t.CreateAsync(It.Is<CreateTriageRequest>(r =>
                r.ProjectId == ShopProjectId
                && r.RequestedBy == "tester"
                && r.Request.Title == "Checkout returns 502"
                && r.Request.StartInTemplateId == "t1"
                && r.Request.From != null && r.Request.Until == null),
            It.IsAny<CancellationToken>()), Times.Once);
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"triage/{TriageId}");
    }

    [Fact]
    public void New_TitleShowsSimilarTriages()
    {
        _triages.Setup(t => t.FindSimilarAsync(ShopProjectId, "Checkout returns 502", It.IsAny<CancellationToken>()))
            .ReturnsAsync([ListItem(TriageStatus.Duplicate, "Payment page timeout")]);
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));
        var cut = Render<TriageNew>();

        cut.Find("[data-testid='triage-title']").Input("Checkout returns 502");
        cut.Find("[data-testid='triage-title']").Blur();

        cut.Find("[data-testid='triage-similar']").TextContent.Should().Contain("Payment page timeout").And.Contain("Duplicate");
    }

    [Fact]
    public void New_ApiRefusal_IsShown()
    {
        _triages.Setup(t => t.CreateAsync(It.IsAny<CreateTriageRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("'x' is not an enabled template of the project"));
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));
        var cut = Render<TriageNew>();

        cut.Find("[data-testid='triage-title']").Input("t");
        cut.Find("[data-testid='triage-happened']").Input("w");
        cut.Find("[data-testid='triage-expected']").Input("e");
        cut.Find("form").Submit();

        cut.Find("[data-testid='triage-new-error']").TextContent.Should().Contain("not an enabled template");
    }
}
