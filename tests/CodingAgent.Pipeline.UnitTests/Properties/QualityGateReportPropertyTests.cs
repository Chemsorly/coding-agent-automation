using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for QualityGateReport.AllPassed consistency.
/// </summary>
public class QualityGateReportPropertyTests
{
    /// <summary>
    /// Property 3: QualityGateReport.AllPassed is consistent with individual gate results.
    /// For any combination of GateResult values for compilation, tests, and external CI
    /// (where external CI may be absent), AllPassed equals true if and only if
    /// every present gate has Passed == true.
    /// **Validates: Requirements 4.3, 4.5**
    /// </summary>
    // TODO: This property test only exercises the legacy path (QgcResults.Count == 0) with an optional
    // ExternalCi gate. It does not cover the case where QgcResults is empty and ExternalCi is also null —
    // the exact expression modified by issue #2400 (SecurityScan?.Passed removed). A variant with
    // QgcResults = [] and ExternalCi = null would lock in the bare legacy branch against regression.
    [Property(MaxTest = 20)]
    public void AllPassed_IsConsistentWithIndividualGateResults(
        bool compilationPassed,
        bool testsPassed,
        bool hasExternalCi,
        bool externalCiPassed)
    {
        var compilation = new GateResult { GateName = "Compilation", Passed = compilationPassed };
        var tests = new GateResult { GateName = "Tests", Passed = testsPassed };
        var externalCi = hasExternalCi ? new GateResult { GateName = "ExternalCi", Passed = externalCiPassed } : null;

        var report = new QualityGateReport
        {
            Compilation = compilation,
            Tests = tests,
            ExternalCi = externalCi
        };

        var expected = compilationPassed
            && testsPassed
            && (externalCi?.Passed ?? true);

        report.AllPassed.Should().Be(expected);
    }
}
