using CodingAgent.Api.Client;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.TestUtilities;

/// <summary>
/// Spec 051 2.7: the Pipelines page reads <see cref="IUiConfigClient.GetPipelinesPageAsync"/> and writes templates
/// through <see cref="IUiSettingsClient"/>. Page tests that arrange their data on an
/// <see cref="IPipelineApiConfigClient"/> mock (the client the page used before) keep doing so: these clients answer
/// from that mock and pass template writes on to it, so the tests' verifications still hold. The project and
/// role filtering is the API's (UiPipelinesEndpointTests).
/// </summary>
public static class ConfigBackedPipelinesClients
{
    public static Mock<IUiConfigClient> UiConfig(Mock<IPipelineApiConfigClient> config)
    {
        var client = new Mock<IUiConfigClient>();
        client.Setup(c => c.GetPipelinesPageAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                var settings = await config.Object.GetPipelineConfigAsync(ct) ?? new PipelineConfiguration();
                return new UiPipelinesPage(
                    await config.Object.GetProjectsAsync(ct) ?? [],
                    await config.Object.GetAllTemplatesAsync(ct) ?? [],
                    await config.Object.GetProviderConfigsAsync(ProviderKind.Issue, ct) ?? [],
                    await config.Object.GetProviderConfigsAsync(ProviderKind.Repository, ct) ?? [],
                    await config.Object.GetProviderConfigsAsync(ProviderKind.Pipeline, ct) ?? [],
                    await config.Object.GetQualityGateConfigsAsync(ct) ?? [],
                    await config.Object.GetReviewerConfigsAsync(ct) ?? [],
                    await config.Object.GetAgentProfilesAsync(ct) ?? [],
                    settings.DefaultRequiredAgentLabels,
                    settings.MaxRetries);
            });
        return client;
    }

    public static Mock<IUiSettingsClient> UiSettings(Mock<IPipelineApiConfigClient> config)
    {
        var client = new Mock<IUiSettingsClient>();
        client.Setup(c => c.SaveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns((ProjectId projectId, PipelineJobTemplate template, CancellationToken ct) => config.Object.SaveTemplateAsync(projectId.Value, template, ct));
        client.Setup(c => c.DeleteTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .Returns((ProjectId projectId, TemplateId templateId, CancellationToken ct) => config.Object.DeleteTemplateAsync(projectId.Value, templateId.Value, ct));
        client.Setup(c => c.MoveTemplateAsync(It.IsAny<ProjectId>(), It.IsAny<ProjectId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((ProjectId source, ProjectId target, string templateId, CancellationToken ct) => config.Object.MoveTemplateAsync(source, target, templateId, ct));
        return client;
    }
}
