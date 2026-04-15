using AwesomeAssertions;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

public class QualityGateValidatorTests
{
    [Fact]
    public void AllPassed_WhenAllGatesPass_ReturnsTrue()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true },
            Coverage = new GateResult { GateName = "Coverage", Passed = true },
            SecurityScan = new GateResult { GateName = "Security", Passed = true }
        };

        report.AllPassed.Should().BeTrue();
    }

    [Fact]
    public void AllPassed_WhenCompilationFails_ReturnsFalse()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error" },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };

        report.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void AllPassed_WhenTestsFail_ReturnsFalse()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false, TestsFailed = 3 }
        };

        report.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void AllPassed_WithNullOptionalGates_ReturnsTrue()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true },
            Coverage = null,
            SecurityScan = null
        };

        report.AllPassed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Passed:  10, Failed:   2, Skipped:   1", 10, 2, 1)]
    [InlineData("Passed: 0, Failed: 0, Skipped: 0", 0, 0, 0)]
    [InlineData("No test results here", 0, 0, 0)]
    [InlineData("Passed: 42", 42, 0, 0)]
    public void ParseTestCounts_ExtractsCorrectValues(string output, int expectedPassed, int expectedFailed, int expectedSkipped)
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCounts(output);

        passed.Should().Be(expectedPassed);
        failed.Should().Be(expectedFailed);
        skipped.Should().Be(expectedSkipped);
    }
}
