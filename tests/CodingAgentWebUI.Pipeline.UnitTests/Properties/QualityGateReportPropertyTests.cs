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
        bool hasSecurity,
        bool securityPassed)
    {
        var compilation = new GateResult { GateName = "Compilation", Passed = compilationPassed };
        var tests = new GateResult { GateName = "Tests", Passed = testsPassed };
        var security = hasSecurity ? new GateResult { GateName = "Security", Passed = securityPassed } : null;

        var report = new QualityGateReport
        {
            Compilation = compilation,
            Tests = tests,
            SecurityScan = security
        };

        var expected = compilationPassed
            && testsPassed
            && (security?.Passed ?? true);

        report.AllPassed.Should().Be(expected);
    }
}
