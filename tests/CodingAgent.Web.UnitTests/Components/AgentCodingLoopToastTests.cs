using CodingAgent.Web.UnitTests.Auth;
using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for loop duplicate control removal (issue #2939).
/// The loop toast bar must be removed; the inline controls section must remain.
/// The circuit-broken bar is a distinct state and must stay.
/// </summary>
public class AgentCodingLoopToastTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IProviderFactory> _mockFactory = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly CodingAgent.Web.UnitTests.TestUtilities.FakeUiLoopClient _loop = new();

    private static readonly string[] DefaultTemplateIds = ["t-1"];

    private static PipelineJobTemplate DefaultTemplate => new()
    {
        Id = "t-1",
        Name = "DotNet Repo",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        Enabled = true
    };

    public AgentCodingLoopToastTests()
    {
        Services.AddTestAccess(); // Spec 049: global admin, so every control renders as before
        var mockLogger = new Mock<ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        SetupStoreMocks();
        SetupProjectStoreMocks();
        SetupConfigClientMocks();

        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<CodingAgent.Web.Ui.IUiLoopClient>(_loop);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(_mockProjectStore.Object);
        Services.AddSingleton<IPipelineApiConfigClient>(_mockConfigClient.Object);
        // Spec 051 2.7: the page reads /api/ui/pipelines, answered from the config mock, and writes templates through Settings.
        Services.AddSingleton(CodingAgent.Web.UnitTests.TestUtilities.ConfigBackedPipelinesClients.UiConfig(_mockConfigClient).Object);
        Services.AddSingleton(new Mock<CodingAgent.Web.Ui.IUiSettingsClient>().Object);

        Services.AddSingleton(new Mock<CodingAgent.Web.Ui.IUiAgentClient>().Object);
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        // IssueDrawerService now requires IPipelineApiWorkItemClient for status-aware active-issue tracking.
        var mockApiWorkItemClient = new Mock<IPipelineApiWorkItemClient>();
        mockApiWorkItemClient.Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(string, string)>());
        mockApiWorkItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());
        Services.AddSingleton<IPipelineApiWorkItemClient>(mockApiWorkItemClient.Object);
        // Spec 051 2.3b: the drawers browse through the API; this page test never opens one.
        Services.AddSingleton(CodingAgent.Web.UnitTests.TestUtilities.ProviderBackedBrowseClient.Empty().Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));
        Services.AddSingleton(new Mock<CodingAgent.Web.Ui.IUiDispatchClient>().Object);

        Services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        Services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        Services.AddScoped<IEpicDrawerService, EpicDrawerService>();
        Services.AddScoped<DispatchDrawers>();
        Services.AddScoped<AgentCodingPageService>();
        Services.AddScoped<NotificationService>();
    }

    [Fact]
    public void WhenLoopActive_ToastStackDoesNotContainLoopStatusBar()
    {
        _loop.Status = CodingAgent.Web.UnitTests.TestUtilities.FakeUiLoopClient.Running with { StatusMessage = "Running…" };

        var cut = Render<AgentCoding>();

        // The toast stack must not render a loop-status-bar element
        var toastStack = cut.Find(".agent-toast-stack");
        toastStack.QuerySelectorAll(".loop-status-bar").Should().BeEmpty(
            "the loop status toast bar must have been removed — loop status is shown in the inline controls section only");
    }

    [Fact]
    public void WhenLoopActive_InlineSectionShowsStopLoopButton()
    {
        _loop.Status = CodingAgent.Web.UnitTests.TestUtilities.FakeUiLoopClient.Running with { StatusMessage = "Running…" };

        var cut = Render<AgentCoding>();

        // The inline settings section must still show a Stop Loop button
        // TODO: [WARNING] Button search is scoped to all buttons on the page via FindAll("button").
        // If the inline loop-controls section is removed (regression), buttons from elsewhere on the page
        // (e.g. template table) could satisfy the predicate and the assertion would pass against wrong elements.
        // Scope the search to the loop-controls container element (e.g. by section CSS class) to ensure only
        // the intended region is queried.
        var stopButtons = cut.FindAll("button")
            .Where(b => b.TextContent.Contains("Stop Loop"))
            .ToList();
        stopButtons.Should().NotBeEmpty(
            "the inline Loop Controls section must still render a Stop Loop button when the loop is active");
    }

    [Fact]
    public void WhenCircuitBroken_CircuitBrokenBarStillRendered()
    {
        _loop.Status = CodingAgent.Web.UnitTests.TestUtilities.FakeUiLoopClient.Running with { IsCircuitBroken = true, StatusMessage = "Circuit broken — too many failures" };

        var cut = Render<AgentCoding>();

        // The circuit-broken bar is a distinct alert state and must remain in the toast stack
        // TODO: [WARNING] cut.Markup.Should().Contain("Resume") is a substring match against the entire page.
        // If any other element on the page ever contains "Resume" (e.g. a template status badge), this test
        // would pass even if the circuit-broken bar were removed. Assert on the specific circuit-broken bar
        // element (e.g. a data-testid or the .agent-summary-bar with known error-state styles) instead.
        cut.Markup.Should().Contain("Resume",
            "the circuit-broken bar with Resume button must still render when IsCircuitBroken is true");
    }

    [Fact]
    public void WhenLoopInactive_InlineSectionShowsStartLoopButton()
    {
        _loop.Status = CodingAgent.Web.UnitTests.TestUtilities.FakeUiLoopClient.Stopped;

        var cut = Render<AgentCoding>();

        // TODO: [WARNING] FindAll("button") searches all buttons on the page. If the inline loop-controls
        // section is removed (regression), buttons from the template table or elsewhere that contain "Start Loop"
        // text could satisfy the predicate, producing a false positive. Scope the search to the loop-controls
        // container element (e.g. by a specific CSS class on that section) to ensure only the intended region
        // is queried.
        var startButtons = cut.FindAll("button")
            .Where(b => b.TextContent.Contains("Start Loop"))
            .ToList();
        startButtons.Should().NotBeEmpty(
            "the inline Loop Controls section must render a Start Loop button when the loop is inactive");
    }

    // ── Setup helpers (mirrored from AgentCodingStopPendingTests pattern) ────

    private void SetupStoreMocks()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupProjectStoreMocks()
    {
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true, TemplateIds = DefaultTemplateIds }
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { DefaultTemplate });
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.DeleteTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupConfigClientMocks()
    {
        _mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        _mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadPipelineConfigAsync(ct));
        _mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadAllTemplatesAsync(ct));
        _mockConfigClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadProjectsAsync(ct));
        _mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadQualityGateConfigsAsync(ct));
        _mockConfigClient.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadReviewerConfigsAsync(ct));
    }
}
