using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for DispatchService.GenerateJobName determinism.
/// **Validates: Requirements 5.13**
/// </summary>
public class DispatchServiceJobNamingPropertyTests
{
    /// <summary>
    /// Property 8: Deterministic K8s Job Naming
    /// For any GUID, GenerateJobName produces "caa-" + guid.ToString("N")[0..8].
    /// </summary>
    [Property]
    public void GenerateJobName_MatchesDeterministicFormula(Guid workItemId)
    {
        var expected = "caa-" + workItemId.ToString("N")[..8];

        var actual = DispatchService.GenerateJobName(workItemId);

        actual.Should().Be(expected);
    }
}
