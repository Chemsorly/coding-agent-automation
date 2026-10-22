using CodingAgent.Api.Client;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.TestUtilities;

/// <summary>
/// Spec 051 2.7: the Pipelines page reads <see cref="IUiConfigClient.GetPipelinesPageAsync"/> and writes templates
/// through <see cref="IUiSettingsClient"/>. Page tests that arrange their data on an
/// <see cref="IPipelineApiConfigClient"/> mock (the client the page used before) keep doing so: this client answers
/// from that mock. The Pipeline API client has no template writes, so tests verify those on their own
/// <see cref="IUiSettingsClient"/> mock. The project and role filtering is the API's (UiPipelinesEndpointTests).
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
                    [], // agent profiles: the Pipeline API client no longer serves them (spec 053)
                    new Dictionary<string, IReadOnlyList<UiResolvedRoute>>(),
                    settings.MaxRetries);
            });
        return client;
    }
}
