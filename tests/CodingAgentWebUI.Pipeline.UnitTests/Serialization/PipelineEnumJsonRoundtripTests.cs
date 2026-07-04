using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Validates that ALL enum values in the Pipeline domain survive JSON roundtrip serialization.
/// Guards against: renamed enum members losing their string representation, missing JsonStringEnumConverter,
/// and new enum values that serialize to their numeric form instead of string form.
/// 
/// These tests exercise PipelineJsonOptions.Default (which includes JsonStringEnumConverter).
/// If any enum value fails roundtrip, the serialized config files on disk become unreadable
/// after a code change — a silent data corruption bug.
/// </summary>
public class PipelineEnumJsonRoundtripTests
{
    private static readonly JsonSerializerOptions Options = PipelineJsonOptions.Default;

    [Theory]
    [MemberData(nameof(AllPipelineStepValues))]
    public void PipelineStep_AllValues_RoundtripAsString(PipelineStep value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllAnalysisGateResultValues))]
    public void AnalysisGateResult_AllValues_RoundtripAsString(AnalysisGateResult value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllWorkItemStatusValues))]
    public void WorkItemStatus_AllValues_RoundtripAsString(WorkItemStatus value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllWorkItemTaskTypeValues))]
    public void WorkItemTaskType_AllValues_RoundtripAsString(WorkItemTaskType value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllFailureReasonValues))]
    public void FailureReason_AllValues_RoundtripAsString(FailureReason value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllPipelineRunStateValues))]
    public void PipelineRunState_AllValues_RoundtripAsString(PipelineRunState value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllReviewIsolationValues))]
    public void ReviewIsolation_AllValues_RoundtripAsString(ReviewIsolation value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllAgentStatusValues))]
    public void AgentStatus_AllValues_RoundtripAsString(AgentStatus value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllConsolidationRunStatusValues))]
    public void ConsolidationRunStatus_AllValues_RoundtripAsString(ConsolidationRunStatus value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllConsolidationRunTypeValues))]
    public void ConsolidationRunType_AllValues_RoundtripAsString(ConsolidationRunType value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllJobDistributionStatusValues))]
    public void JobDistributionStatus_AllValues_RoundtripAsString(JobDistributionStatus value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllAgentEffortLevelValues))]
    public void AgentEffortLevel_AllValues_RoundtripAsString(AgentEffortLevel value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllPipelineRunTypeValues))]
    public void PipelineRunType_AllValues_RoundtripAsString(PipelineRunType value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllCriterionStatusValues))]
    public void CriterionStatus_AllValues_RoundtripAsString(CriterionStatus value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllProviderKindValues))]
    public void ProviderKind_AllValues_RoundtripAsString(ProviderKind value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    [Theory]
    [MemberData(nameof(AllChatRoleValues))]
    public void ChatRole_AllValues_RoundtripAsString(ChatRole value)
    {
        AssertEnumRoundtrip(value);
        AssertSerializesAsString(value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void AssertEnumRoundtrip<T>(T value) where T : struct, Enum
    {
        var json = JsonSerializer.Serialize(value, Options);
        var deserialized = JsonSerializer.Deserialize<T>(json, Options);
        deserialized.Should().Be(value, $"roundtrip failed for {typeof(T).Name}.{value}");
    }

    private static void AssertSerializesAsString<T>(T value) where T : struct, Enum
    {
        var json = JsonSerializer.Serialize(value, Options);
        // Should serialize as a quoted string, not a bare number
        json.Should().StartWith("\"", $"{typeof(T).Name}.{value} should serialize as string, got: {json}");
    }

    // ── MemberData sources ───────────────────────────────────────────────

    public static IEnumerable<object[]> AllPipelineStepValues() => EnumValues<PipelineStep>();
    public static IEnumerable<object[]> AllAnalysisGateResultValues() => EnumValues<AnalysisGateResult>();
    public static IEnumerable<object[]> AllWorkItemStatusValues() => EnumValues<WorkItemStatus>();
    public static IEnumerable<object[]> AllWorkItemTaskTypeValues() => EnumValues<WorkItemTaskType>();
    public static IEnumerable<object[]> AllFailureReasonValues() => EnumValues<FailureReason>();
    public static IEnumerable<object[]> AllPipelineRunStateValues() => EnumValues<PipelineRunState>();
    public static IEnumerable<object[]> AllReviewIsolationValues() => EnumValues<ReviewIsolation>();
    public static IEnumerable<object[]> AllAgentStatusValues() => EnumValues<AgentStatus>();
    public static IEnumerable<object[]> AllConsolidationRunStatusValues() => EnumValues<ConsolidationRunStatus>();
    public static IEnumerable<object[]> AllConsolidationRunTypeValues() => EnumValues<ConsolidationRunType>();
    public static IEnumerable<object[]> AllJobDistributionStatusValues() => EnumValues<JobDistributionStatus>();
    public static IEnumerable<object[]> AllAgentEffortLevelValues() => EnumValues<AgentEffortLevel>();
    public static IEnumerable<object[]> AllPipelineRunTypeValues() => EnumValues<PipelineRunType>();
    public static IEnumerable<object[]> AllCriterionStatusValues() => EnumValues<CriterionStatus>();
    public static IEnumerable<object[]> AllProviderKindValues() => EnumValues<ProviderKind>();
    public static IEnumerable<object[]> AllChatRoleValues() => EnumValues<ChatRole>();

    private static IEnumerable<object[]> EnumValues<T>() where T : struct, Enum
        => Enum.GetValues<T>().Select(v => new object[] { v });
}
