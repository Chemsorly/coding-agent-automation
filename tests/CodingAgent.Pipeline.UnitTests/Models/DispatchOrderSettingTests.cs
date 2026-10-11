using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using static CodingAgent.Pipeline.Models.WorkItemTaskType;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// <see cref="PipelineConfiguration.DispatchOrder"/>: the default order, and a stored order that is incomplete or holds
/// unknown values still loads as a complete order.
/// </summary>
public class DispatchOrderSettingTests
{
    [Fact]
    public void Default_IsTriageReviewDecompositionImplementationConsolidation()
    {
        new PipelineConfiguration().DispatchOrder.Should().Equal(Triage, Review, Decomposition, Implementation, Consolidation);
    }

    [Fact]
    public void CompleteOrder_IsKept()
    {
        var config = new PipelineConfiguration { DispatchOrder = [Implementation, Consolidation, Review, Triage, Decomposition] };

        config.DispatchOrder.Should().Equal(Implementation, Consolidation, Review, Triage, Decomposition);
    }

    [Fact]
    public void IncompleteOrder_GetsTheMissingTypesInTheirDefaultOrder_AndDuplicatesDropped()
    {
        var config = new PipelineConfiguration { DispatchOrder = [Implementation, Implementation, Review] };

        config.DispatchOrder.Should().Equal(Implementation, Review, Triage, Decomposition, Consolidation);
    }

    [Fact]
    public void UnknownValues_AreDropped()
    {
        var config = new PipelineConfiguration { DispatchOrder = [(WorkItemTaskType)99, Consolidation] };

        config.DispatchOrder.Should().Equal(Consolidation, Triage, Review, Decomposition, Implementation);
    }

    [Fact]
    public void Null_IsTheDefault()
    {
        var config = new PipelineConfiguration { DispatchOrder = null! };

        config.DispatchOrder.Should().Equal(Triage, Review, Decomposition, Implementation, Consolidation);
    }

    [Fact]
    public void Json_StoresTypeNames_AndKeepsTheOrder()
    {
        var config = new PipelineConfiguration { DispatchOrder = [Implementation, Triage, Review, Decomposition, Consolidation] };

        var json = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        var loaded = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default)!;

        json.Should().Contain("\"dispatchOrder\"").And.Contain("\"Implementation\"");
        loaded.DispatchOrder.Should().Equal(Implementation, Triage, Review, Decomposition, Consolidation);
    }

    [Fact]
    public void Json_StoredBeforeTheSettingExisted_LoadsTheDefault()
    {
        var loaded = JsonSerializer.Deserialize<PipelineConfiguration>("{}", PipelineJsonOptions.Default)!;

        loaded.DispatchOrder.Should().Equal(Triage, Review, Decomposition, Implementation, Consolidation);
    }

    [Fact]
    public void MessagePack_KeepsTheOrder()
    {
        var options = MessagePack.Resolvers.ContractlessStandardResolverAllowPrivate.Options;
        var config = new PipelineConfiguration { DispatchOrder = [Review, Implementation, Triage, Decomposition, Consolidation] };

        var bytes = MessagePack.MessagePackSerializer.Serialize(config, options);
        var loaded = MessagePack.MessagePackSerializer.Deserialize<PipelineConfiguration>(bytes, options);

        loaded.DispatchOrder.Should().Equal(Review, Implementation, Triage, Decomposition, Consolidation);
    }
}
