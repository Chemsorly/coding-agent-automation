using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for DispatchLifecycleService.GenerateJobName determinism.
/// Previously tested via DispatchService.GenerateJobName; migrated to the Api canonical copy
/// in arch-audit 2026-08-22.
/// **Validates: Requirements 5.13**
/// </summary>
public class DispatchServiceJobNamingPropertyTests
{
    /// <summary>
    /// Property 8: Deterministic K8s Job Naming
    /// For any GUID, GenerateJobName produces "caa-" + guid.ToString("N")[0..8].
    /// </summary>
    [Property(MaxTest = 20)]
    public void GenerateJobName_MatchesDeterministicFormula(Guid workItemId)
    {
        var expected = "caa-" + workItemId.ToString("N")[..8];

        var actual = DispatchLifecycleService.GenerateJobName(workItemId);

        actual.Should().Be(expected);
    }
}
