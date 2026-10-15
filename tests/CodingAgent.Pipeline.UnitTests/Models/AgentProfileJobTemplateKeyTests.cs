using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="AgentProfile.JobTemplateKey"/>.
/// Covers: sorting and normalisation, fallback to MatchLabels, and serialisation round-trips.
/// </summary>
public sealed class AgentProfileJobTemplateKeyTests
{
    // ── JobTemplateKey normalisation ──────────────────────────────────────

    [Fact]
    public void JobTemplateKey_WithSpacesAndMixedOrder_ReturnsSortedTrimmedKey()
    {
        // " dotnet10, kiro,dotnet" → sorted ordinal: "dotnet,dotnet10,kiro"
        var profile = MakeProfile(jobTemplate: " dotnet10, kiro,dotnet");

        profile.JobTemplateKey().Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public void JobTemplateKey_NullJobTemplate_FallsBackToMatchLabels()
    {
        var profile = MakeProfile(matchLabels: ["kiro", "dotnet"], jobTemplate: null);

        profile.JobTemplateKey().Should().Be("dotnet,kiro");
    }

    [Fact]
    public void JobTemplateKey_EmptyJobTemplate_FallsBackToMatchLabels()
    {
        var profile = MakeProfile(matchLabels: ["kiro", "dotnet"], jobTemplate: "");

        profile.JobTemplateKey().Should().Be("dotnet,kiro");
    }

    [Fact]
    public void JobTemplateKey_WhitespaceOnlyJobTemplate_FallsBackToMatchLabels()
    {
        var profile = MakeProfile(matchLabels: ["kiro", "dotnet"], jobTemplate: "   ");

        profile.JobTemplateKey().Should().Be("dotnet,kiro");
    }

    [Fact]
    public void JobTemplateKey_JobTemplateWithEmptyEntries_SkipsEmptyEntries()
    {
        // Leading/trailing commas produce empty entries that are removed.
        var profile = MakeProfile(jobTemplate: ",kiro,,dotnet,");

        profile.JobTemplateKey().Should().Be("dotnet,kiro");
    }

    [Fact]
    public void JobTemplateKey_NoMatchLabelsNoJobTemplate_ReturnsEmptyString()
    {
        var profile = MakeProfile(matchLabels: [], jobTemplate: null);

        profile.JobTemplateKey().Should().BeEmpty();
    }

    [Fact]
    public void JobTemplateKey_JobTemplateDiffersFromMatchLabels_ReturnsTemplateKey()
    {
        // Profile has extra match label (sonnet) not in the job template.
        var profile = MakeProfile(matchLabels: ["kiro", "dotnet", "sonnet"], jobTemplate: "kiro,dotnet");

        profile.JobTemplateKey().Should().Be("dotnet,kiro",
            "JobTemplateKey() should use JobTemplate, not MatchLabels, when JobTemplate is set");
    }

    // ── MessagePack round-trip ────────────────────────────────────────────

    [Fact]
    public void MessagePackRoundTrip_PreservesJobTemplate()
    {
        var original = MakeProfile(matchLabels: ["kiro", "dotnet"], jobTemplate: "kiro,dotnet");

        var bytes = MessagePackSerializer.Serialize(original, MessagePackSerializerOptions.Standard);
        var restored = MessagePackSerializer.Deserialize<AgentProfile>(bytes, MessagePackSerializerOptions.Standard);

        restored.JobTemplate.Should().Be("kiro,dotnet");
        restored.JobTemplateKey().Should().Be(original.JobTemplateKey());
    }

    [Fact]
    public void MessagePackRoundTrip_NullJobTemplate_PreservesNull()
    {
        var original = MakeProfile(matchLabels: ["kiro", "dotnet"], jobTemplate: null);

        var bytes = MessagePackSerializer.Serialize(original, MessagePackSerializerOptions.Standard);
        var restored = MessagePackSerializer.Deserialize<AgentProfile>(bytes, MessagePackSerializerOptions.Standard);

        restored.JobTemplate.Should().BeNull();
        restored.JobTemplateKey().Should().Be(original.JobTemplateKey());
    }

    // ── JSON round-trip ───────────────────────────────────────────────────

    [Fact]
    public void JsonRoundTrip_PreservesJobTemplate()
    {
        var original = MakeProfile(matchLabels: ["kiro", "dotnet"], jobTemplate: "kiro,dotnet");

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AgentProfile>(json);

        restored!.JobTemplate.Should().Be("kiro,dotnet");
        restored.JobTemplateKey().Should().Be(original.JobTemplateKey());
    }

    [Fact]
    public void JsonRoundTrip_LegacyProfileWithoutJobTemplate_DefaultsToNull()
    {
        // Profiles stored before this change lack the jobTemplate field.
        // JSON deserialisation should produce a null JobTemplate (fallback to MatchLabels).
        var json = """{"id":"p1","displayName":"Test","matchLabels":["kiro","dotnet"],"agentProviderConfigId":"ap-1","enabled":true,"priority":0,"mcpServers":[]}""";

        var profile = JsonSerializer.Deserialize<AgentProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        profile!.JobTemplate.Should().BeNull(
            "legacy profiles without jobTemplate field must fall back to MatchLabels");
        profile.JobTemplateKey().Should().Be("dotnet,kiro",
            "legacy profiles use MatchLabels as the selector key");
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private static AgentProfile MakeProfile(
        string[]? matchLabels = null,
        string? jobTemplate = null) => new()
        {
            DisplayName = "Test",
            AgentProviderConfigId = "ap-1",
            MatchLabels = matchLabels ?? [],
            JobTemplate = jobTemplate
        };
}
