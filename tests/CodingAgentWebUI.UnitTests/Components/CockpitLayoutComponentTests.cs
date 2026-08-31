using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// Smoke tests for the cockpit shell — the app's default layout since the legacy MainLayout was retired.
/// Verifies the shell renders its brand, primary nav, project switcher and theme toggle without throwing.
/// </summary>
public class CockpitLayoutComponentTests : BunitContext
{
    public CockpitLayoutComponentTests()
    {
        var mockLogger = new Mock<ILogger>();

        // Config client: projects (scope switcher) + the keys FirstRunBanner reads.
        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());
        mockConfigClient.Setup(s => s.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        mockConfigClient.Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Services.AddSingleton(mockConfigClient.Object);

        // Run-history client: the top-bar attention-count query.
        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = Array.Empty<PipelineRunSummary>(),
                Page = 1,
                PageSize = 100,
                HasMore = false
            });
        Services.AddSingleton(mockRunHistory.Object);

        Services.AddSingleton(new CockpitState());

        // Faro frontend observability — no-op mocks (ported into the cockpit shell from MainLayout).
        Services.AddSingleton<IFaroService>(Mock.Of<IFaroService>());
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton(sp => new NotificationFaroBridge(
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<IFaroService>()));

        // SidebarHealthIndicators dependencies.
        var emptyConfig = new ConfigurationBuilder().Build();
        var emptyServiceProvider = new ServiceCollection().BuildServiceProvider();
        Services.AddSingleton(new InfrastructureHealthService(
            emptyServiceProvider, emptyConfig, Mock.Of<IPipelineApiHealthClient>()));
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(mockLogger.Object));

        // JS runtime — loose mock; theme sync + global-keyboard registration are no-ops in tests.
        Services.AddSingleton(Mock.Of<IJSRuntime>());
    }

    [Fact]
    public void Renders_BrandAndPrimaryNav()
    {
        var cut = Render<CockpitLayout>();

        Assert.Contains("Coding Agent", cut.Markup);
        Assert.NotNull(cut.Find("a[href='overview']"));
        Assert.NotNull(cut.Find("a[href='fleet']"));
        Assert.NotNull(cut.Find("a[href='pipelines']"));
        // About migrated into the cockpit nav when the legacy shell was retired.
        Assert.NotNull(cut.Find("a[href='about']"));
    }

    [Fact]
    public void Renders_ProjectSwitcherAndThemeToggle()
    {
        var cut = Render<CockpitLayout>();

        Assert.NotNull(cut.Find(".cockpit-project-switcher"));
        Assert.NotNull(cut.Find(".cockpit-theme-toggle"));
    }
}
