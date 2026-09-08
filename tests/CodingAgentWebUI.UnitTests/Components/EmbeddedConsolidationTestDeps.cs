using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// The Pipelines page (AgentCoding) folds in the &lt;Consolidation /&gt; section, so any bUnit test that
/// renders AgentCoding must also satisfy Consolidation's dependencies. This registers benign no-op
/// versions (empty history, no suggestions, no last runs) so those tests render without wiring the
/// full consolidation surface.
/// </summary>
internal static class EmbeddedConsolidationTestDeps
{
    public static void AddEmbeddedConsolidationDeps(this IServiceCollection services)
    {
        var mock = new Mock<IConsolidationService>();
        mock.Setup(c => c.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());
        mock.Setup(c => c.GetHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessSuggestions?)null);
        mock.Setup(c => c.GetLastRunAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        services.AddSingleton(mock.Object);
        services.AddSingleton(new ConsolidationBadgeService());
    }
}
