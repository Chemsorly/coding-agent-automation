using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="McpCallCounts"/> (Add, Failed) and <see cref="PhaseUsage"/>
/// JSON serialisation with <see cref="McpCallCounts"/> values.
/// </summary>
public class McpToolUsageTests
{
    // ── McpCallCounts.Add ────────────────────────────────────────────────

    [Fact]
    public void Add_SumsAllFields()
    {
        var a = new McpCallCounts { Calls = 1, FailedConnection = 2, FailedAuth = 3, FailedOther = 4, FailedUnknown = 5 };
        var b = new McpCallCounts { Calls = 10, FailedConnection = 20, FailedAuth = 30, FailedOther = 40, FailedUnknown = 50 };

        var result = a.Add(b);

        result.Calls.Should().Be(11);
        result.FailedConnection.Should().Be(22);
        result.FailedAuth.Should().Be(33);
        result.FailedOther.Should().Be(44);
        result.FailedUnknown.Should().Be(55);
    }

    [Fact]
    public void Add_WithZeroValues_ReturnsSameValues()
    {
        var a = new McpCallCounts { Calls = 5, FailedOther = 2 };
        var zero = new McpCallCounts();

        var result = a.Add(zero);

        result.Calls.Should().Be(5);
        result.FailedOther.Should().Be(2);
        result.FailedConnection.Should().Be(0);
        result.FailedAuth.Should().Be(0);
        result.FailedUnknown.Should().Be(0);
    }

    // ── McpCallCounts.Failed computed property ───────────────────────────

    [Fact]
    public void Failed_IsSumOfAllFailureCategories()
    {
        var counts = new McpCallCounts
        {
            Calls = 20,
            FailedConnection = 1,
            FailedAuth = 2,
            FailedOther = 3,
            FailedUnknown = 4
        };

        counts.Failed.Should().Be(10, "Failed = FailedConnection + FailedAuth + FailedOther + FailedUnknown");
    }

    [Fact]
    public void Failed_AllZero_ReturnsZero()
    {
        new McpCallCounts { Calls = 5 }.Failed.Should().Be(0);
    }

    // ── PhaseUsage JSON roundtrip ────────────────────────────────────────

    [Fact]
    public void PhaseUsage_WithToolCalls_JsonRoundtrip_PreservesToolCalls()
    {
        var phase = new PhaseUsage(Tokens: 100, Cost: 0.5m)
        {
            ToolCalls = new Dictionary<string, McpCallCounts>
            {
                ["sonarqube/get_issues"] = new McpCallCounts { Calls = 5, FailedOther = 1 }
            }
        };

        var json = JsonSerializer.Serialize(phase, PipelineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<PhaseUsage>(json, PipelineJsonOptions.Default);

        restored.Should().NotBeNull();
        restored!.ToolCalls.Should().NotBeNull();
        restored.ToolCalls!.Should().ContainKey("sonarqube/get_issues");
        restored.ToolCalls["sonarqube/get_issues"].Calls.Should().Be(5);
        restored.ToolCalls["sonarqube/get_issues"].FailedOther.Should().Be(1);
    }

    [Fact]
    public void PhaseUsage_WithUnavailableMcpServers_JsonRoundtrip_Preserved()
    {
        var phase = new PhaseUsage(Tokens: 0, Cost: null)
        {
            UnavailableMcpServers = new Dictionary<string, string>
            {
                ["grafana"] = McpServerStatuses.NeedsAuth,
                ["context7"] = McpServerStatuses.Failed
            }
        };

        var json = JsonSerializer.Serialize(phase, PipelineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<PhaseUsage>(json, PipelineJsonOptions.Default);

        restored!.UnavailableMcpServers.Should().NotBeNull();
        restored.UnavailableMcpServers!["grafana"].Should().Be(McpServerStatuses.NeedsAuth);
        restored.UnavailableMcpServers["context7"].Should().Be(McpServerStatuses.Failed);
    }

    [Fact]
    public void McpCallCounts_JsonSerialization_DoesNotIncludeFailedProperty()
    {
        var counts = new McpCallCounts { Calls = 5, FailedConnection = 1, FailedAuth = 2, FailedOther = 3, FailedUnknown = 4 };

        var json = JsonSerializer.Serialize(counts, PipelineJsonOptions.Default);

        // PipelineJsonOptions uses camelCase naming (JsonNamingPolicy.CamelCase)
        // The [JsonIgnore] computed property must not appear in the JSON output
        json.Should().NotContain("\"failed\"", "Failed is a computed property with [JsonIgnore]");
        // But all storage fields must be present (camelCase)
        json.Should().Contain("\"calls\"");
        json.Should().Contain("\"failedConnection\"");
        json.Should().Contain("\"failedAuth\"");
        json.Should().Contain("\"failedOther\"");
        json.Should().Contain("\"failedUnknown\"");
    }

    [Fact]
    public void McpCallCounts_JsonRoundtrip_PreservesAllFields()
    {
        var original = new McpCallCounts
        {
            Calls = 7,
            FailedConnection = 1,
            FailedAuth = 2,
            FailedOther = 3,
            FailedUnknown = 1
        };

        var json = JsonSerializer.Serialize(original, PipelineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<McpCallCounts>(json, PipelineJsonOptions.Default);

        restored!.Calls.Should().Be(7);
        restored.FailedConnection.Should().Be(1);
        restored.FailedAuth.Should().Be(2);
        restored.FailedOther.Should().Be(3);
        restored.FailedUnknown.Should().Be(1);
        // Computed property is reconstructed correctly after round-trip
        restored.Failed.Should().Be(7);
    }
}
