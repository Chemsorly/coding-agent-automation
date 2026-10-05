using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Layout;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 049 Req 5.9, 7.5, 8.1–8.3: what the cockpit layout renders for each kind of principal.</summary>
public class CockpitLayoutAccessTests : BunitContext
{
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";
    private const string BillingId = "6f1c2a9e-0000-0000-0000-00000000000b";
    private const string AlphaId = "6f1c2a9e-0000-0000-0000-00000000000c";

    private readonly Mock<IPipelineApiConfigClient> _configClient = new();
    private readonly Mock<IPipelineApiRunHistoryClient> _runHistory = new();
    private readonly Mock<IJSRuntime> _js = new();
    private readonly CockpitState _state = new();
    private readonly List<string?> _attentionQueries = [];

    public CockpitLayoutAccessTests()
    {
        _configClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = PaymentsId, Name = "payments" },
                new() { Id = BillingId, Name = "billing" },
                new() { Id = AlphaId, Name = "alpha" },
            });
        _configClient.Setup(s => s.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _configClient.Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _runHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .Callback((RunHistoryQuery query, CancellationToken _) =>
                _attentionQueries.Add(query.ProjectId))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = [], Page = 1, PageSize = 100, HasMore = false });

        Services.AddSingleton(_configClient.Object);
        Services.AddSingleton(_runHistory.Object);
        Services.AddSingleton(_state);
        Services.AddSingleton<IFaroService>(Mock.Of<IFaroService>());
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton(sp => new NotificationFaroBridge(sp.GetRequiredService<NotificationService>(), sp.GetRequiredService<IFaroService>()));
        Services.AddSingleton(new InfrastructureHealthService(
            new ServiceCollection().BuildServiceProvider(), new ConfigurationBuilder().Build(), Mock.Of<IPipelineApiHealthClient>()));
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(new Mock<ILogger>().Object));
        Services.AddSingleton(_js.Object);
    }

    private IRenderedComponent<CockpitLayout> RenderLayout(AccessGrant grant, string? storedProjectId = null)
    {
        Services.AddTestAccess(grant);
        _js.Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>())).ReturnsAsync(storedProjectId);
        return Render<CockpitLayout>(p => p.Add(x => x.Body, b =>
        {
            b.OpenComponent<ProjectProbe>(0);
            b.CloseComponent();
        }));
    }

    private static string[] NavLabels(IRenderedComponent<CockpitLayout> cut) =>
        cut.FindAll(".cockpit-nav-link").Select(a => a.GetAttribute("aria-label")!).ToArray();

    private static string[] SwitcherValues(IRenderedComponent<CockpitLayout> cut) =>
        cut.FindAll(".cockpit-project-switcher option").Select(o => o.GetAttribute("value")!).ToArray();

    [Fact]
    public void ScopedUser_FirstPageQueryCarriesAnAllowedProject()
    {
        var cut = RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.ReadOnly)));

        cut.WaitForAssertion(() => cut.FindComponent<ProjectProbe>().Instance.ProjectAtInit.Should().Be(PaymentsId));
        _attentionQueries.Should().NotBeEmpty().And.AllSatisfy(q => q.Should().Be(PaymentsId));
    }

    [Fact]
    public void ScopedUser_DefaultsToFirstAllowedProjectByName()
    {
        var cut = RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.ReadOnly), (AlphaId, AccessRole.Operator)));

        cut.WaitForAssertion(() => cut.FindComponent<ProjectProbe>().Instance.ProjectAtInit.Should().Be(AlphaId));
    }

    [Fact]
    public void ScopedUser_SwitcherListsOnlyAllowedProjects_WithoutAllProjects()
    {
        var cut = RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.ReadOnly), (AlphaId, AccessRole.ReadOnly)));

        cut.WaitForAssertion(() => SwitcherValues(cut).Should().BeEquivalentTo([PaymentsId, AlphaId]));
    }

    [Fact]
    public void GlobalUser_SwitcherKeepsAllProjectsOption()
    {
        var cut = RenderLayout(TestAccess.Global(AccessRole.ReadOnly));

        cut.WaitForAssertion(() => SwitcherValues(cut).Should().BeEquivalentTo(["", PaymentsId, BillingId, AlphaId]));
        _state.SelectedProjectId.Should().BeEmpty();
    }

    [Fact]
    public void ScopedUser_StoredDisallowedProject_IsIgnored()
    {
        RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.ReadOnly)), storedProjectId: BillingId);

        _state.SelectedProjectId.Should().Be(PaymentsId);
    }

    [Fact]
    public void ScopedUser_StoredAllProjects_IsIgnored()
    {
        var cut = RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.ReadOnly)), storedProjectId: "");

        cut.WaitForAssertion(() => _js.Verify(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()), Times.Once));
        _state.SelectedProjectId.Should().Be(PaymentsId);
    }

    [Fact]
    public void ScopedUser_SwitcherValueOfDisallowedProject_IsIgnored()
    {
        var cut = RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.ReadOnly)));
        cut.WaitForAssertion(() => _state.SelectedProjectId.Should().Be(PaymentsId));

        cut.Find(".cockpit-project-switcher select").Change(BillingId);
        cut.Find(".cockpit-project-switcher select").Change("");

        _state.SelectedProjectId.Should().Be(PaymentsId);
    }

    [Fact]
    public void ScopedUser_WithoutReadableProject_RendersNoPage()
    {
        var cut = RenderLayout(TestAccess.Scoped(("6f1c2a9e-0000-0000-0000-0000000000ff", AccessRole.Operator)));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=no-readable-project]").Should().ContainSingle());
        cut.FindComponents<ProjectProbe>().Should().BeEmpty();
        _attentionQueries.Should().BeEmpty();
    }

    [Fact]
    public void NoAccessUser_SeesNoAccessPanel_NoNavigation_NoQueries()
    {
        var cut = RenderLayout(AccessGrant.None);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("You have no access yet"));
        cut.FindComponents<ProjectProbe>().Should().BeEmpty();
        NavLabels(cut).Should().BeEmpty();
        cut.FindAll("[data-testid=topbar-attention-badge]").Should().BeEmpty();
        cut.FindAll(".cockpit-project-switcher").Should().BeEmpty();
        cut.FindAll("[data-testid=user-menu]").Should().ContainSingle("a no-access user still reaches the profile and logout");
        _attentionQueries.Should().BeEmpty();
        _configClient.Verify(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AccessRole.Admin, new[] { "Overview", "Work", "Runs", "Fleet", "Attention", "Insights", "Pipelines", "Consolidation", "Settings", "Knowledge", "Agent Chat", "About" })]
    [InlineData(AccessRole.Operator, new[] { "Overview", "Work", "Runs", "Fleet", "Attention", "Insights", "Pipelines", "Consolidation", "Knowledge", "Agent Chat", "About" })]
    [InlineData(AccessRole.ReadOnly, new[] { "Overview", "Work", "Runs", "Fleet", "Attention", "Insights", "Pipelines", "Consolidation", "Knowledge", "About" })]
    public void GlobalRole_NavigationFollowsPagePolicies(AccessRole role, string[] expected)
    {
        var cut = RenderLayout(TestAccess.Global(role));

        cut.WaitForAssertion(() => NavLabels(cut).Should().Equal(expected));
    }

    [Fact]
    public void ScopedOperator_NavigationHasNoGlobalPages_ButAgentChat()
    {
        var cut = RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.Operator)));

        cut.WaitForAssertion(() => NavLabels(cut).Should().Equal(
            "Overview", "Work", "Runs", "Attention", "Insights", "Pipelines", "Knowledge", "Agent Chat", "About"));
    }

    [Fact]
    public void ScopedUser_HasNoHealthIndicatorsAndNoFirstRunBanner()
    {
        var cut = RenderLayout(TestAccess.Scoped((PaymentsId, AccessRole.Operator)));

        cut.WaitForAssertion(() => cut.FindComponents<ProjectProbe>().Should().ContainSingle());
        cut.FindComponents<SidebarHealthIndicators>().Should().BeEmpty();
        cut.FindComponents<CodingAgent.Web.Components.Shared.FirstRunBanner>().Should().BeEmpty();
    }

    [Fact]
    public void UserMenu_ShowsUsername_AndLogoutFormWithAntiforgeryToken()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.ReadOnly), TestAccess.User("alice"));
        _js.Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>())).ReturnsAsync((string?)null);
        var cut = Render<CockpitLayout>();

        cut.Find("[data-testid=user-menu-name]").TextContent.Should().Be("alice");
        var form = cut.Find("[data-testid=logout-form]");
        form.GetAttribute("method").Should().Be("post");
        form.GetAttribute("action").Should().Be("auth/logout");
        form.InnerHtml.Should().Contain("__RequestVerificationToken");
    }

    /// <summary>Stands in for a page: records the project scope when it first initializes.</summary>
    private sealed class ProjectProbe : ComponentBase
    {
        [Inject] private CockpitState State { get; set; } = default!;

        public string? ProjectAtInit { get; private set; }

        protected override void OnInitialized() => ProjectAtInit = State.SelectedProjectId;
    }
}
