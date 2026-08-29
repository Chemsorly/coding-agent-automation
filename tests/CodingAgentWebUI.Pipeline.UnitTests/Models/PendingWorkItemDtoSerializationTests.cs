using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// JSON serialization tests for <see cref="PendingWorkItemDto"/>.
///
/// Regression coverage for the production incident where the orchestrator crashed with:
///   "JSON deserialization for type 'PendingWorkItemDto' was missing required properties
///    including: 'timeoutSeconds'"
///
/// Root cause: <c>TimeoutSeconds</c> was added as <c>required</c> on the DTO but the
/// running API instance pre-deployment did not yet emit the field. STJ enforces
/// <c>required</c> at deserialization time and throws rather than silently defaulting.
///
/// These tests lock in:
/// 1. Correct camelCase wire key (<c>timeoutSeconds</c>, not <c>TimeoutSeconds</c>).
/// 2. Full round-trip fidelity for all required fields, including non-zero TimeoutSeconds.
/// 3. That a response missing <c>timeoutSeconds</c> throws <see cref="JsonException"/>.
/// 4. That a response missing any other required field also throws.
/// </summary>
public sealed class PendingWorkItemDtoSerializationTests
{
    private static readonly JsonSerializerOptions Opts = PipelineJsonOptions.Default;

    private static readonly Guid SampleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static PendingWorkItemDto MakeDto(int timeoutSeconds = 3600) => new()
    {
        Id = SampleId,
        IssueIdentifier = "owner/repo#42",
        IssueProviderConfigId = "github-main",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
        AgentSelector = "kiro",
        RetryCount = 2,
        TimeoutSeconds = timeoutSeconds,
        IssueTitle = "Fix the thing",
        InitiatedBy = "loop",
        ProjectName = "my-project",
        ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

    // ── Serialization key name ────────────────────────────────────────────

    [Fact]
    public void Serialize_TimeoutSeconds_EmitsCamelCaseKey()
    {
        var json = JsonSerializer.Serialize(MakeDto(), Opts);

        json.Should().Contain("\"timeoutSeconds\"",
            "PipelineJsonOptions.Default uses camelCase naming — the wire key must be timeoutSeconds, " +
            "not TimeoutSeconds. A PascalCase key would silently be ignored by the client deserializer " +
            "because PropertyNameCaseInsensitive is false, causing the required-property exception.");
        json.Should().NotContain("\"TimeoutSeconds\"");
    }

    [Fact]
    public void Serialize_TimeoutSeconds_EmitsCorrectValue()
    {
        var json = JsonSerializer.Serialize(MakeDto(timeoutSeconds: 7200), Opts);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("timeoutSeconds").GetInt32().Should().Be(7200);
    }

    // ── Round-trip fidelity ───────────────────────────────────────────────

    [Fact]
    public void RoundTrip_AllRequiredFields_PreservesValues()
    {
        var original = MakeDto(timeoutSeconds: 1800);

        var json = JsonSerializer.Serialize(original, Opts);
        var restored = JsonSerializer.Deserialize<PendingWorkItemDto>(json, Opts);

        restored.Should().NotBeNull();
        restored!.Id.Should().Be(original.Id);
        restored.IssueIdentifier.Should().Be(original.IssueIdentifier);
        restored.IssueProviderConfigId.Should().Be(original.IssueProviderConfigId);
        restored.TaskType.Should().Be(original.TaskType);
        restored.CreatedAt.Should().Be(original.CreatedAt);
        restored.AgentSelector.Should().Be(original.AgentSelector);
        restored.RetryCount.Should().Be(original.RetryCount);
        restored.TimeoutSeconds.Should().Be(1800,
            "TimeoutSeconds must survive the serialization round-trip with its actual value, " +
            "not silently default to 0. The Job Controller uses this value to compute " +
            "activeDeadlineSeconds on the K8s Job.");
    }

    [Fact]
    public void RoundTrip_OptionalDisplayFields_PreservesNullsAndValues()
    {
        var withDisplay = MakeDto();
        var withoutDisplay = withDisplay with
        {
            IssueTitle = null,
            InitiatedBy = null,
            ProjectName = null,
            ProjectId = null
        };

        var jsonWith = JsonSerializer.Serialize(withDisplay, Opts);
        var jsonWithout = JsonSerializer.Serialize(withoutDisplay, Opts);

        var restoredWith = JsonSerializer.Deserialize<PendingWorkItemDto>(jsonWith, Opts)!;
        var restoredWithout = JsonSerializer.Deserialize<PendingWorkItemDto>(jsonWithout, Opts)!;

        restoredWith.IssueTitle.Should().Be("Fix the thing");
        restoredWith.InitiatedBy.Should().Be("loop");
        restoredWith.ProjectName.Should().Be("my-project");
        restoredWith.ProjectId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        restoredWithout.IssueTitle.Should().BeNull();
        restoredWithout.InitiatedBy.Should().BeNull();
        restoredWithout.ProjectName.Should().BeNull();
        restoredWithout.ProjectId.Should().BeNull();
    }

    // ── Missing required field → JsonException ────────────────────────────

    [Fact]
    public void Deserialize_MissingTimeoutSeconds_ThrowsJsonException()
    {
        // Reproduces the exact production failure: the pre-deployment API returned a response
        // that did not include timeoutSeconds. The client threw JsonException on deserialization
        // because the property is marked required on the DTO record.
        const string jsonMissingTimeout = """
            [{
                "id": "11111111-1111-1111-1111-111111111111",
                "issueIdentifier": "owner/repo#1",
                "issueProviderConfigId": "github",
                "taskType": 0,
                "createdAt": "2026-08-29T12:00:00+00:00",
                "agentSelector": "kiro",
                "retryCount": 0
            }]
            """;

        var act = () => JsonSerializer.Deserialize<List<PendingWorkItemDto>>(jsonMissingTimeout, Opts);

        act.Should().Throw<JsonException>(
            "timeoutSeconds is a required property on PendingWorkItemDto. " +
            "A server response that omits the field (e.g., an old API deployment) must throw " +
            "rather than silently deserializing with TimeoutSeconds=0, which would cause the " +
            "Job Controller to compute a zero activeDeadlineSeconds on the K8s Job.");
    }

    [Fact]
    public void Deserialize_MissingId_ThrowsJsonException()
    {
        const string json = """
            [{
                "issueIdentifier": "owner/repo#1",
                "issueProviderConfigId": "github",
                "taskType": 0,
                "createdAt": "2026-08-29T12:00:00+00:00",
                "agentSelector": "kiro",
                "retryCount": 0,
                "timeoutSeconds": 300
            }]
            """;

        var act = () => JsonSerializer.Deserialize<List<PendingWorkItemDto>>(json, Opts);

        act.Should().Throw<JsonException>("id is a required property");
    }

    [Fact]
    public void Deserialize_MissingAgentSelector_ThrowsJsonException()
    {
        const string json = """
            [{
                "id": "11111111-1111-1111-1111-111111111111",
                "issueIdentifier": "owner/repo#1",
                "issueProviderConfigId": "github",
                "taskType": 0,
                "createdAt": "2026-08-29T12:00:00+00:00",
                "retryCount": 0,
                "timeoutSeconds": 300
            }]
            """;

        var act = () => JsonSerializer.Deserialize<List<PendingWorkItemDto>>(json, Opts);

        act.Should().Throw<JsonException>("agentSelector is a required property");
    }

    // ── PascalCase key is NOT accepted ────────────────────────────────────

    [Fact]
    public void Deserialize_PascalCaseTimeoutSeconds_ThrowsJsonException()
    {
        // PipelineJsonOptions.Default does NOT set PropertyNameCaseInsensitive=true.
        // A server that serializes with the wrong naming policy would silently omit
        // the field from the client's perspective, triggering the required-property check.
        const string jsonPascalCase = """
            [{
                "Id": "11111111-1111-1111-1111-111111111111",
                "IssueIdentifier": "owner/repo#1",
                "IssueProviderConfigId": "github",
                "TaskType": 0,
                "CreatedAt": "2026-08-29T12:00:00+00:00",
                "AgentSelector": "kiro",
                "RetryCount": 0,
                "TimeoutSeconds": 300
            }]
            """;

        var act = () => JsonSerializer.Deserialize<List<PendingWorkItemDto>>(jsonPascalCase, Opts);

        act.Should().Throw<JsonException>(
            "PipelineJsonOptions.Default uses camelCase and is case-sensitive. " +
            "PascalCase keys are unrecognized and required properties will be reported as missing.");
    }
}
