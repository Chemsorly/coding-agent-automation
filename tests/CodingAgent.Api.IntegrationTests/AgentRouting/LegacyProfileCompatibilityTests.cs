using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// Spec 053 task 3.8, design "Rollout window": a previous-release API cannot read a profile of the new shape, so its
/// dispatches and claims fail instead of routing with half a profile. The rollout plan rests on this side effect of
/// <c>required</c> (V1).
/// </summary>
public sealed class LegacyProfileCompatibilityTests
{
    [Fact]
    public void PreviousReleaseProfileRecord_RefusesAProfileOfTheNewShape()
    {
        // What the configuration move writes, and what the release writes once the old members are gone (task 6.1).
        var newShape = JsonNode.Parse(JsonSerializer.Serialize(
            AgentRoutingTestData.Profile("p", "dotnet,kiro", "Kiro"), PipelineJsonOptions.Default))!.AsObject();
        newShape.Remove("agentProviderConfigId");
        newShape.Remove("matchLabels");
        newShape.Remove("priority");

        var read = () => JsonSerializer.Deserialize<PreviousReleaseAgentProfile>(newShape.ToJsonString(), PipelineJsonOptions.Lenient);

        read.Should().Throw<JsonException>().WithMessage("*AgentProviderConfigId*");
    }

    [Fact]
    public void PreviousReleaseProfileRecord_ReadsALegacyProfile()
    {
        var json = AgentRoutingLegacyFixture.LegacyProfileJson("p", "Kiro", ["dotnet", "kiro"], "agent-config");

        JsonSerializer.Deserialize<PreviousReleaseAgentProfile>(json, PipelineJsonOptions.Lenient)!
            .AgentProviderConfigId.Should().Be("agent-config");
    }

    /// <summary>A frozen copy of <c>AgentProfile</c> as the release before spec 053 declares it (JSON members only).</summary>
    private sealed record PreviousReleaseAgentProfile
    {
        public string Id { get; init; } = Guid.NewGuid().ToString();
        public required string DisplayName { get; init; }
        public IReadOnlyList<string> MatchLabels { get; init; } = [];
        public required string AgentProviderConfigId { get; init; }
        public bool Enabled { get; init; } = true;
        public int Priority { get; init; }
        public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];
        public string? JobTemplate { get; init; }
    }
}
