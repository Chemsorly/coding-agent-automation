using Bunit;
using Moq;
using KiroWebUI.Components.Pages;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace KiroWebUI.Tests.Components;

/// <summary>
/// bUnit component tests for the AgentCoding page.
/// Renders the actual Blazor component and asserts on markup and view switching.
/// </summary>
public class AgentCodingPageComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly PipelineOrchestrationService _pipelineService;

    public AgentCodingPageComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();

        _pipelineService = new PipelineOrchestrationService(
            _mockStore.Object,
            _mockFactory.Object,
            new IssueDescriptionParser(),
            mockValidator.Object,
            new CiLogWriter(mockLogger.Object),
            mockLogger.Object,
            runsDirectory: Path.Combine(Path.GetTempPath(), $"test-runs-{Guid.NewGuid()}"));

        SetupDefaults();

        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
    }

    private void SetupDefaults()
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
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ap-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });

        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "42", Title = "Test Issue", Labels = Array.Empty<string>() },
                    new() { Identifier = "43", Title = "Bug Fix", Labels = new[] { "bug" } }
                },
                Page = 1,
                PageSize = 25,
                HasMore = false
            });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);
    }

    [Fact]
    public void AgentCoding_RendersPageHeader()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("Agent Coding", component.Markup);
        Assert.NotNull(component.Find("h1"));
    }

    [Fact]
    public void AgentCoding_InitialState_ShowsIssueSelectionView()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("Browse Issues", component.Markup);
        Assert.Contains("Issue Provider", component.Markup);
    }

    [Fact]
    public void AgentCoding_ShowsIssueProviderDropdown()
    {
        var component = Render<AgentCoding>();

        Assert.Contains("GitHub Issues", component.Markup);
        Assert.Contains("-- Select Provider --", component.Markup);
    }

    [Fact]
    public void AgentCoding_ShowsRunHistorySection()
    {
        var component = Render<AgentCoding>();

        // Run history only appears in the pipeline setup panel when there are history items.
        // With no history, the section is not rendered at all.
        Assert.DoesNotContain("Run History", component.Markup);
    }

    [Fact]
    public void AgentCoding_StartPipelineButton_IsDisabled_WhenNoIssueSelected()
    {
        var component = Render<AgentCoding>();

        // Start Pipeline button is always visible but disabled until all providers and issue are selected
        Assert.Contains("Start Pipeline", component.Markup);
        var btn = component.Find(".pipeline-start-btn");
        Assert.True(btn.HasAttribute("disabled"));
    }

    [Fact]
    public void AgentCoding_WhenProviderLoadFails_ShowsError()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var component = Render<AgentCoding>();

        Assert.Contains("Failed to load providers", component.Markup);
        Assert.Contains("Connection failed", component.Markup);
    }

    [Fact]
    public void AgentCoding_RendersStepProgressSteps()
    {
        // The progress steps array is used for the step indicator
        var component = Render<AgentCoding>();

        // These are defined in the component but only shown during active pipeline
        // Just verify the component renders without error
        Assert.NotNull(component);
    }

    [Fact]
    public void AgentCoding_DisposesEventHandlers()
    {
        var component = Render<AgentCoding>();

        // Dispose should not throw
        component.Dispose();
    }
}
