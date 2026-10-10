using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
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
    private readonly Mock<IPipelineApiTriageClient> _triages = new();
    private readonly Mock<IPipelineApiConfigClient> _config = new();
    private readonly CockpitState _state = new();
    private readonly List<TriageListQuery> _queries = [];

    public TriageListAndFormComponentTests()
    {
        _triages.Setup(t => t.ListAsync(It.IsAny<TriageListQuery>(), It.IsAny<CancellationToken>()))
            .Callback((TriageListQuery q, CancellationToken _) => _queries.Add(q))
            .ReturnsAsync(new TriageListPage());
        _triages.Setup(t => t.FindSimilarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _config.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());
        _config.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new PipelineProject { Id = ShopProjectId, Name = "Shop", Enabled = true, TemplateIds = ["t1", "t2"],
                McpServers = [new McpServerConfig { Name = "grafana" }] },
            new PipelineProject { Id = OtherProjectId, Name = "Billing", Enabled = true },
        ]);
        _config.Setup(c => c.GetProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _config.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new AgentProfile { Id = "p1", DisplayName = "default", AgentProviderConfigId = "ap-1", Enabled = true, MatchLabels = ["dotnet"],
                McpServers = [new McpServerConfig { Name = "sonarqube" }, new McpServerConfig { Name = "grafana" }] },
        ]);
        _config.Setup(c => c.GetTemplatesForProjectAsync(ShopProjectId, It.IsAny<CancellationToken>())).ReturnsAsync([
            new PipelineJobTemplate { Id = "t1", Name = "storefront-web", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true },
            new PipelineJobTemplate { Id = "t2", Name = "checkout-api", IssueProviderId = "ip-2", RepoProviderId = "rp-2", Enabled = true },
        ]);
        _config.Setup(c => c.GetTemplatesForProjectAsync(OtherProjectId, It.IsAny<CancellationToken>())).ReturnsAsync([]);

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

        cut.Find("[data-testid='triage-title']").Change("Invoices missing");
        cut.Find("[data-testid='triage-happened']").Change("No invoice mail");
        cut.Find("[data-testid='triage-expected']").Change("A mail per order");

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

        cut.Find("[data-testid='triage-title']").Change("Checkout returns 502");
        cut.Find("[data-testid='triage-happened']").Change("One in five checkouts fails");
        cut.Find("[data-testid='triage-expected']").Change("Checkout completes");
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

        cut.Find("[data-testid='triage-title']").Change("Checkout returns 502");

        cut.Find("[data-testid='triage-similar']").TextContent.Should().Contain("Payment page timeout").And.Contain("Duplicate");
    }

    [Fact]
    public void New_ApiRefusal_IsShown()
    {
        _triages.Setup(t => t.CreateAsync(It.IsAny<CreateTriageRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TriageApiException(400, "'x' is not an enabled template of the project"));
        Services.AddTestAccess(TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));
        var cut = Render<TriageNew>();

        cut.Find("[data-testid='triage-title']").Change("t");
        cut.Find("[data-testid='triage-happened']").Change("w");
        cut.Find("[data-testid='triage-expected']").Change("e");
        cut.Find("form").Submit();

        cut.Find("[data-testid='triage-new-error']").TextContent.Should().Contain("not an enabled template");
    }
}
