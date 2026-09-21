using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="WorkItemPayload.TryDeserialize"/>.
/// These tests run without any database setup — the helper is pure JSON deserialization logic.
/// Access to the internal class is granted via InternalsVisibleTo("CodingAgent.Api.IntegrationTests")
/// in CodingAgent.Api.csproj.
/// </summary>
public sealed class WorkItemPayloadTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Minimal valid camelCase payload matching PipelineJsonOptions.Default serialization.</summary>
    private static string MakeCamelCasePayload() =>
        JsonSerializer.Serialize(new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("owner/repo#1"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600
        }, PipelineJsonOptions.Default);

    // ── TryDeserialize tests ──────────────────────────────────────────────────────

    [Fact]
    public void TryDeserialize_ValidCamelCaseJson_ReturnsTrueWithRequest()
    {
        // Arrange
        var payload = MakeCamelCasePayload();

        // Act
        var result = WorkItemPayload.TryDeserialize(payload, out var req);

        // Assert
        result.Should().BeTrue("a well-formed camelCase payload must be deserialized successfully");
        req.Should().NotBeNull();
        req!.IssueProviderConfigId.Should().Be("prov-1");
        req.RepoProviderConfigId.Should().Be("repo-1");
        req.InitiatedBy.Should().Be("test");
    }

    [Fact]
    public void TryDeserialize_ValidPascalCaseJson_ReturnsTrueWithRequest()
    {
        // Arrange: hand-craft a PascalCase JSON payload (as would have been written by an older
        // serializer config that did not enforce camelCase). This is the regression guard that
        // ensures PipelineJsonOptions.Lenient (PropertyNameCaseInsensitive=true) is used.
        var payload = """
            {
                "IssueIdentifier": "owner/repo#1",
                "IssueProviderConfigId": "prov-pascal",
                "RepoProviderConfigId": "repo-pascal",
                "InitiatedBy": "legacy-loop",
                "TaskType": "Implementation",
                "AgentSelector": "dotnet",
                "TimeoutSeconds": 3600
            }
            """;

        // Act
        var result = WorkItemPayload.TryDeserialize(payload, out var req);

        // Assert: Lenient (PropertyNameCaseInsensitive=true) must parse PascalCase keys
        result.Should().BeTrue("PascalCase payload must parse successfully with Lenient options");
        req.Should().NotBeNull();
        req!.IssueProviderConfigId.Should().Be("prov-pascal");
        req.RepoProviderConfigId.Should().Be("repo-pascal");
        req.InitiatedBy.Should().Be("legacy-loop");
    }

    [Fact]
    public void TryDeserialize_MalformedJson_ReturnsFalse()
    {
        // Arrange: malformed JSON that cannot be parsed
        const string malformedPayload = "{not-valid-json";

        // Act
        var result = WorkItemPayload.TryDeserialize(malformedPayload, out var req);

        // Assert: JsonException must be caught — no exception propagates, method returns false
        result.Should().BeFalse("a malformed payload must return false without throwing");
        req.Should().BeNull();
    }

    [Fact]
    public void TryDeserialize_NullPayload_ReturnsFalse()
    {
        // Act
        var result = WorkItemPayload.TryDeserialize(null, out var req);

        // Assert
        result.Should().BeFalse("null payload must return false immediately");
        req.Should().BeNull();
    }

    [Fact]
    public void TryDeserialize_JsonLiteralNull_ReturnsFalse()
    {
        // Arrange: "null" as a JSON literal deserializes to null
        const string nullLiteral = "null";

        // Act
        var result = WorkItemPayload.TryDeserialize(nullLiteral, out var req);

        // Assert
        result.Should().BeFalse("JSON literal 'null' must return false (deserializes to null object)");
        req.Should().BeNull();
    }
}
