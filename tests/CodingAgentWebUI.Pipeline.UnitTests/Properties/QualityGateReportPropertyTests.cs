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
    /// For any combination of GateResult values for compilation and tests, AllPassed equals true
    /// if and only if every gate has Passed == true. SecurityScan was retired (Key(4) tombstoned).
    /// **Validates: Requirements 4.3, 4.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void AllPassed_IsConsistentWithIndividualGateResults(
        bool compilationPassed,
        bool testsPassed)
    {
        var compilation = new GateResult { GateName = "Compilation", Passed = compilationPassed };
        var tests = new GateResult { GateName = "Tests", Passed = testsPassed };

        var report = new QualityGateReport
        {
            Compilation = compilation,
            Tests = tests
        };

        var expected = compilationPassed && testsPassed;

        report.AllPassed.Should().Be(expected);
    }
}
