using AwesomeAssertions;
using CodingAgent.Web.UnitTests.Auth;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using CodingAgent.Web.TestUtilities;
using CodingAgent.Web.Ui;
using CodingAgent.Web.UnitTests.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Spec 051 D11: Start, Stop and Resume on the Pipelines page only record the request, and the page shows
/// it from the loop status the API serves. <see cref="UiLoopStatus.RunRequested"/> changes at once,
/// <see cref="UiLoopStatus.IsLoopActive"/> once the Scheduler follows. These tests replace the old
/// "stop pending" flag (issue #2369): the status itself now says a Stop is on its way.
/// </summary>
public class AgentCodingLoopCommandTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IProviderFactory> _mockFactory = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly FakeUiLoopClient _loop = new() { Status = FakeUiLoopClient.Running };

    private static PipelineJobTemplate DefaultTemplate => new()
    {
        Id = "t-1",
        Name = "DotNet Repo",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        Enabled = true
    };

    public AgentCodingLoopCommandTests()
    {
        Services.AddTestAccess(); // Spec 049: global admin, so every control renders as before
        var mockLogger = new Mock<ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        SetupStoreMocks();
        SetupProjectStoreMocks();
        SetupConfigClientMocks();

        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<IUiLoopClient>(_loop);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(_mockProjectStore.Object);
        Services.AddSingleton<IPipelineApiConfigClient>(_mockConfigClient.Object);
        // Spec 051 2.7: the page reads /api/ui/pipelines, answered from the config mock, and writes templates through Settings.
        Services.AddSingleton(CodingAgent.Web.UnitTests.TestUtilities.ConfigBackedPipelinesClients.UiConfig(_mockConfigClient).Object);
        Services.AddSingleton(new Mock<CodingAgent.Web.Ui.IUiSettingsClient>().Object);
        Services.AddSingleton(new Mock<IUiAgentClient>().Object);
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        var mockApiWorkItemClient = new Mock<IPipelineApiWorkItemClient>();
        mockApiWorkItemClient.Setup(c => c.GetActiveIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(string, string)>());
        mockApiWorkItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());
        Services.AddSingleton<IPipelineApiWorkItemClient>(mockApiWorkItemClient.Object);
        // Spec 051 2.3b: the drawers browse through the API; this page test never opens one.
        Services.AddSingleton(ProviderBackedBrowseClient.Empty().Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));
        Services.AddSingleton(new Mock<IUiDispatchClient>().Object);

        Services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        Services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        Services.AddScoped<IEpicDrawerService, EpicDrawerService>();
        Services.AddScoped<DispatchDrawers>();
        Services.AddScoped<AgentCodingPageService>();
        Services.AddScoped<NotificationService>();
    }

    private static AngleSharp.Dom.IElement LoopStopButton(IRenderedComponent<AgentCoding> cut) =>
        cut.Find("[data-testid='loop-controls'] button.btn-cancel");

    private static string LoopProgress(IRenderedComponent<AgentCoding> cut) =>
        cut.Find("[data-testid='loop-progress']").TextContent;

    private static AngleSharp.Dom.IElement ToastButton(IRenderedComponent<AgentCoding> cut, string text) =>
        cut.FindAll(".agent-toast-stack button").Single(b => b.TextContent == text);

    [Fact]
    public void Running_ShowsProgressAndAnEnabledStop()
    {
        var cut = Render<AgentCoding>();

        LoopProgress(cut).Should().Contain("Polling template 1 of 1");
        LoopStopButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task StopClicked_SendsStop_AndShowsTheRequestUntilTheLoopStops()
    {
        var cut = Render<AgentCoding>();

        await cut.InvokeAsync(() => LoopStopButton(cut).Click());

        _loop.Commands.Should().Equal("stop");
        LoopStopButton(cut).HasAttribute("disabled").Should().BeTrue("a Stop is already on its way");
        LoopProgress(cut).Should().Contain("Stop requested");

        // The Scheduler stops the loop; the page's next read shows Start Loop again.
        _loop.Status = FakeUiLoopClient.Stopped;
        await cut.InvokeAsync(cut.Instance.RefreshLoopAsync);
        cut.Render();

        cut.Markup.Should().Contain("Start Loop").And.NotContain("Stop requested");
    }

    [Fact]
    public async Task CircuitBrokenBarStop_IsDisabledOnceAStopIsRequested()
    {
        _loop.Status = FakeUiLoopClient.Running with { IsCircuitBroken = true, StatusMessage = "Loop paused" };
        var cut = Render<AgentCoding>();

        await cut.InvokeAsync(() => ToastButton(cut, "Stop").Click());

        ToastButton(cut, "Stop").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task StopFails_ShowsTheError_AndKeepsStopEnabled()
    {
        _loop.CommandFailure = new HttpRequestException("API unreachable");
        var cut = Render<AgentCoding>();

        await cut.InvokeAsync(() => LoopStopButton(cut).Click());

        cut.Markup.Should().Contain("Failed to stop loop: API unreachable");
        LoopStopButton(cut).HasAttribute("disabled").Should().BeFalse("nothing was recorded, so Stop can be retried");
    }

    [Fact]
    public void StartRequestedButNotYetApplied_SaysTheSchedulerStartsIt()
    {
        _loop.Status = FakeUiLoopClient.Stopped with { RunRequested = true };

        var cut = Render<AgentCoding>();

        LoopProgress(cut).Should().Contain("Start requested");
    }

    [Fact]
    public async Task StartRefusedByTheScheduler_SaysSo_ShowsWhy_AndStopWithdrawsIt()
    {
        _loop.Status = FakeUiLoopClient.Stopped with { RunRequested = true, ValidationErrors = ["No enabled pipeline job templates configured."] };
        var cut = Render<AgentCoding>();

        LoopProgress(cut).Should().Contain("Start refused");
        cut.Markup.Should().Contain("No enabled pipeline job templates configured.");

        await cut.InvokeAsync(() => LoopStopButton(cut).Click());

        _loop.Commands.Should().Equal("stop");
        cut.Markup.Should().Contain("Start Loop");
    }

    [Fact]
    public void SchedulerNotReporting_ShowsTheApiMessage()
    {
        _loop.Status = UiLoopStatus.NotReported(false, "Waiting for the Scheduler's status");

        var cut = Render<AgentCoding>();

        LoopProgress(cut).Should().Contain("Waiting for the Scheduler's status");
    }

    [Fact]
    public async Task ResumeClicked_SendsResume()
    {
        _loop.Status = FakeUiLoopClient.Running with { IsCircuitBroken = true, StatusMessage = "Loop paused" };
        var cut = Render<AgentCoding>();

        await cut.InvokeAsync(() => ToastButton(cut, "Resume").Click());

        _loop.Commands.Should().Equal("resume");
    }

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
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true, TemplateIds = ["t-1"] }
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
        _mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadAgentProfilesAsync(ct));
    }
}
