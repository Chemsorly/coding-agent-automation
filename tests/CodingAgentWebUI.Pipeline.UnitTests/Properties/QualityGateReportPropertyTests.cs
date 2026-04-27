using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for QualityGateReport.AllPassed consistency.
/// </summary>
public class QualityGateReportPropertyTests
{
    /// <summary>
    /// Property 3: QualityGateReport.AllPassed is consistent with individual gate results.
    /// For any combination of GateResult values for compilation, tests, coverage, and security scan
    /// (where coverage and security scan may be absent), AllPassed equals true if and only if
    /// every present gate has Passed == true.
    /// **Validates: Requirements 4.3, 4.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void AllPassed_IsConsistentWithIndividualGateResults(
        bool compilationPassed,
        bool testsPassed,
        bool hasCoverage,
        bool coveragePassed,
        bool hasSecurity,
        bool securityPassed)
    {
        var compilation = new GateResult { GateName = "Compilation", Passed = compilationPassed };
        var tests = new GateResult { GateName = "Tests", Passed = testsPassed };
        var coverage = hasCoverage ? new GateResult { GateName = "Coverage", Passed = coveragePassed } : null;
        var security = hasSecurity ? new GateResult { GateName = "Security", Passed = securityPassed } : null;

        var report = new QualityGateReport
        {
            Compilation = compilation,
            Tests = tests,
            Coverage = coverage,
            SecurityScan = security
        };

        var expected = compilationPassed
            && testsPassed
            && (coverage?.Passed ?? true)
            && (security?.Passed ?? true);

        report.AllPassed.Should().Be(expected);
    }
}
