using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>Spec 057 req 1.3, 1.4, D14: the stored shape of the UI agent limits.</summary>
public sealed class AgentCapacityLimitsTests
{
    // req 1.3, 1.4
    [Fact]
    public void WhenSerialized_ShouldUseCamelCaseNamesAndKeepTemplateKeysUnchanged()
    {
        var limits = new AgentCapacityLimits
        {
            MaxConcurrentAgents = 3,
            MaxConcurrentByJobTemplate = new Dictionary<string, int>(StringComparer.Ordinal) { ["dotnet,kiro"] = 2, ["Z,a"] = 1 }
        };

        var json = JsonSerializer.Serialize(limits, PipelineJsonOptions.Default);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("maxConcurrentAgents").GetInt32().Should().Be(3);
        var map = doc.RootElement.GetProperty("maxConcurrentByJobTemplate");
        map.GetProperty("dotnet,kiro").GetInt32().Should().Be(2);
        map.GetProperty("Z,a").GetInt32().Should().Be(1);
    }

    // req 6.5
    [Fact]
    public void WhenReadLeniently_ShouldRoundTripAndIgnoreUnknownMembers()
    {
        const string json = """{"maxConcurrentAgents":3,"maxConcurrentByJobTemplate":{"dotnet,kiro":2},"unknown":1}""";

        var limits = JsonSerializer.Deserialize<AgentCapacityLimits>(json, PipelineJsonOptions.Lenient);

        limits.Should().NotBeNull();
        limits!.MaxConcurrentAgents.Should().Be(3);
        limits.MaxConcurrentByJobTemplate.Should().ContainKey("dotnet,kiro").WhoseValue.Should().Be(2);
    }

    [Fact]
    public void Empty_ShouldHaveNoLimits()
    {
        AgentCapacityLimits.Empty.MaxConcurrentAgents.Should().Be(0);
        AgentCapacityLimits.Empty.MaxConcurrentByJobTemplate.Should().BeEmpty();
        AgentCapacityLimits.StorageKey.Should().Be("agent-capacity");
    }

    // req 1.3: the [Range] attribute is the source of the global limit's bounds.
    [Fact]
    public void MaxConcurrentAgents_ShouldDeclareTheRange0To100()
    {
        var range = typeof(AgentCapacityLimits).GetProperty(nameof(AgentCapacityLimits.MaxConcurrentAgents))!
            .GetCustomAttribute<RangeAttribute>();

        range.Should().NotBeNull();
        range!.Minimum.Should().Be(0);
        range.Maximum.Should().Be(100);
    }
}
