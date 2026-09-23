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
    private readonly Mock<ILoopStatusService> _mockLoopStatus = new();

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
        var mockLogger = new Mock<ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        var pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        SetupStoreMocks();
        SetupProjectStoreMocks();
        SetupConfigClientMocks();

        _mockLoopStatus.SetupGet(l => l.ValidationErrors).Returns(Array.Empty<string>());
        _mockLoopStatus.SetupGet(l => l.TemplateStatuses)
            .Returns(new Dictionary<string, ConfigStatusSnapshot>());
        _mockLoopStatus.SetupGet(l => l.IsSchedulerUnreachable).Returns(false);
        _mockLoopStatus.SetupGet(l => l.CurrentCycleTemplateIndex).Returns(0);
        _mockLoopStatus.SetupGet(l => l.CurrentCycleTemplateCount).Returns(1);
        _mockLoopStatus.SetupGet(l => l.ProcessedCount).Returns(0);
        _mockLoopStatus.SetupGet(l => l.FailedCount).Returns(0);

        var mockSchedulerClient = new Mock<ISchedulerApiClient>();
        mockSchedulerClient.Setup(c => c.StartLoopAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoopStartResultDto(true, null));
        mockSchedulerClient.Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Services.AddSingleton(pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<ILoopStatusService>(_mockLoopStatus.Object);
        Services.AddSingleton<ISchedulerApiClient>(mockSchedulerClient.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(_mockProjectStore.Object);
        Services.AddSingleton<IPipelineApiConfigClient>(_mockConfigClient.Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));
        Services.AddSingleton<IDispatchOrchestrationService>(new Mock<IDispatchOrchestrationService>().Object);

        Services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        Services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        Services.AddScoped<IEpicDrawerService, EpicDrawerService>();
        Services.AddScoped<AgentCodingPageService>();
        Services.AddScoped<NotificationService>();
    }

    [Fact]
    public void WhenLoopActive_ToastStackDoesNotContainLoopStatusBar()
    {
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(true);
        _mockLoopStatus.SetupGet(l => l.IsCircuitBroken).Returns(false);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("Running…");

        var cut = Render<AgentCoding>();

        // The toast stack must not render a loop-status-bar element
        var toastStack = cut.Find(".agent-toast-stack");
        toastStack.QuerySelectorAll(".loop-status-bar").Should().BeEmpty(
            "the loop status toast bar must have been removed — loop status is shown in the inline controls section only");
    }

    [Fact]
    public void WhenLoopActive_InlineSectionShowsStopLoopButton()
    {
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(true);
        _mockLoopStatus.SetupGet(l => l.IsCircuitBroken).Returns(false);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("Running…");

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
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(true);
        _mockLoopStatus.SetupGet(l => l.IsCircuitBroken).Returns(true);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("Circuit broken — too many failures");

        var cut = Render<AgentCoding>();

        // The circuit-broken bar is a distinct alert state and must remain in the toast stack
        cut.Markup.Should().Contain("Resume",
            "the circuit-broken bar with Resume button must still render when IsCircuitBroken is true");
    }

    [Fact]
    public void WhenLoopInactive_InlineSectionShowsStartLoopButton()
    {
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(false);
        _mockLoopStatus.SetupGet(l => l.IsCircuitBroken).Returns(false);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("");

        var cut = Render<AgentCoding>();

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
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
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
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.DeleteTemplateAsync(It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupConfigClientMocks()
    {
        _mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        _mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
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
        _mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadAgentProfilesAsync(ct));
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }
}
