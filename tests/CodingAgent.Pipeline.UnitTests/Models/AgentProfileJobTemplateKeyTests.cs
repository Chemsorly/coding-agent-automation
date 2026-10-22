using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="AgentProfile.JobTemplateKey"/> (spec 053): the key is the profile's job template,
/// normalized as the job template store normalizes the Helm keys, with no fallback; and serialisation round-trips.
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void JobTemplateKey_WithoutJobTemplate_IsEmpty(string? jobTemplate)
    {
        MakeProfile(jobTemplate: jobTemplate).JobTemplateKey().Should().BeEmpty();
    }

    [Fact]
    public void JobTemplateKey_JobTemplateWithEmptyEntries_SkipsEmptyEntries()
    {
        // Leading/trailing commas produce empty entries that are removed.
        var profile = MakeProfile(jobTemplate: ",kiro,,dotnet,");

        profile.JobTemplateKey().Should().Be("dotnet,kiro");
    }

    /// <summary>
    /// Review-3 n2: the key must equal the job template store's key for the same text, or a profile would name a job
    /// template the store does not find. Contracts cannot call the store, so the two are kept equal by this test.
    /// </summary>
    [Theory]
    [InlineData("kiro,dotnet")]
    [InlineData(" dotnet10 , kiro ,dotnet ")]
    [InlineData(",kiro,,dotnet,")]
    [InlineData("Kiro,dotnet,kiro")]
    [InlineData("b,a,B,A")]
    [InlineData("claude")]
    [InlineData(" , ")]
    public void JobTemplateKey_EqualsTheJobTemplateStoresNormalization(string jobTemplate)
    {
        MakeProfile(jobTemplate: jobTemplate).JobTemplateKey().Should().Be(JobTemplateStore.NormalizeLabels(jobTemplate));
    }

    // ── MessagePack round-trip ────────────────────────────────────────────

    [Fact]
    public void MessagePackRoundTrip_PreservesJobTemplate()
    {
        var original = MakeProfile(jobTemplate: "kiro,dotnet");

        var bytes = MessagePackSerializer.Serialize(original, MessagePackSerializerOptions.Standard);
        var restored = MessagePackSerializer.Deserialize<AgentProfile>(bytes, MessagePackSerializerOptions.Standard);

        restored.JobTemplate.Should().Be("kiro,dotnet");
        restored.JobTemplateKey().Should().Be(original.JobTemplateKey());
    }

    [Fact]
    public void MessagePackRoundTrip_NullJobTemplate_PreservesNull()
    {
        var original = MakeProfile(jobTemplate: null);

        var bytes = MessagePackSerializer.Serialize(original, MessagePackSerializerOptions.Standard);
        var restored = MessagePackSerializer.Deserialize<AgentProfile>(bytes, MessagePackSerializerOptions.Standard);

        restored.JobTemplate.Should().BeNull();
        restored.JobTemplateKey().Should().BeEmpty();
    }

    // ── JSON round-trip ───────────────────────────────────────────────────

    [Fact]
    public void JsonRoundTrip_PreservesJobTemplate()
    {
        var original = MakeProfile(jobTemplate: "kiro,dotnet");

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AgentProfile>(json);

        restored!.JobTemplate.Should().Be("kiro,dotnet");
        restored.JobTemplateKey().Should().Be(original.JobTemplateKey());
    }

    [Fact]
    public void Json_AProfileFromBeforeSpec053_Deserializes_WithoutUsingItsLabels()
    {
        // A row the configuration move has not converted yet still reads; its labels name no job template any more.
        var json = """{"id":"p1","displayName":"Test","matchLabels":["kiro","dotnet"],"agentProviderConfigId":"ap-1","enabled":true,"priority":0,"mcpServers":[]}""";

        var profile = JsonSerializer.Deserialize<AgentProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        profile!.JobTemplate.Should().BeNull();
        profile.JobTemplateKey().Should().BeEmpty();
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private static AgentProfile MakeProfile(string? jobTemplate) => new()
    {
        DisplayName = "Test",
        JobTemplate = jobTemplate
    };
}
