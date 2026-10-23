using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.TestUtilities;

/// <summary>
/// #3577: a project admin's project page reads the templates and issue trackers from the Pipelines page
/// (<see cref="IUiConfigClient.GetPipelinesPageAsync"/>) instead of the Settings lists.
/// </summary>
public static class ProjectPageClients
{
    public static Mock<IUiConfigClient> UiConfig(IReadOnlyList<PipelineJobTemplate>? templates = null, IReadOnlyList<ProviderConfig>? issueProviders = null)
    {
        var client = new Mock<IUiConfigClient>();
        client.Setup(c => c.GetPipelinesPageAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiPipelinesPage([], templates ?? [], issueProviders ?? [], [], [], [], [], [], new Dictionary<string, IReadOnlyList<UiResolvedRoute>>(), 3));
        return client;
    }
}
