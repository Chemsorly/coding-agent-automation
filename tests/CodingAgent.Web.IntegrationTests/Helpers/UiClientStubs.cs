using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace CodingAgent.Web.IntegrationTests.Helpers;

/// <summary>
/// Spec 051: the <c>/api/ui</c> clients call the Pipeline API as the signed-in user. The test hosts
/// have no API, so the clients answer with empty data, as the master-key clients' mocks do.
/// </summary>
public static class UiClientStubs
{
    public static void Replace(IServiceCollection services)
    {
        var config = new Mock<IUiConfigClient>();
        config.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<UiProject>());
        config.Setup(c => c.GetTemplatesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<UiTemplate>());
        config.Setup(c => c.GetPipelineSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UiPipelineSettings(3, 30));
        services.RemoveAll<IUiConfigClient>();
        services.AddSingleton(config.Object);

        var settings = new Mock<IUiSettingsClient>();
        settings.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());
        settings.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineProject>());
        settings.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        settings.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AgentProfile>());
        settings.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        settings.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        settings.Setup(c => c.GetFirstRunStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UiFirstRunState(true, false));
        services.RemoveAll<IUiSettingsClient>();
        services.AddSingleton(settings.Object);
    }
}
