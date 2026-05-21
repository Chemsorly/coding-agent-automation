using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class QualityGateExecutorLogFormatTests
{
    [Fact]
    public void FormatGateLogValue_Null_ReturnsNA()
    {
        QualityGateExecutor.FormatGateLogValue(null).Should().Be("N/A");
    }

    [Fact]
    public void FormatGateLogValue_Passed_ReturnsTrue()
    {
        var gate = new GateResult { GateName = "SecurityScan", Passed = true };
        QualityGateExecutor.FormatGateLogValue(gate).Should().Be("True");
    }

    [Fact]
    public void FormatGateLogValue_Failed_ReturnsFalse()
    {
        var gate = new GateResult { GateName = "ExternalCi", Passed = false };
        QualityGateExecutor.FormatGateLogValue(gate).Should().Be("False");
    }

    [Fact]
    public void FormatCoverageLogValue_Null_ReturnsNA()
    {
        QualityGateExecutor.FormatCoverageLogValue(null).Should().Be("N/A");
    }

    [Fact]
    public void FormatCoverageLogValue_PassedWithPercentage_IncludesPercentage()
    {
        var gate = new GateResult { GateName = "Coverage", Passed = true, CoveragePercent = 56.4 };
        QualityGateExecutor.FormatCoverageLogValue(gate).Should().Be("True (56.4%)");
    }

    [Fact]
    public void FormatCoverageLogValue_FailedWithPercentage_IncludesPercentage()
    {
        var gate = new GateResult { GateName = "Coverage", Passed = false, CoveragePercent = 12.3 };
        QualityGateExecutor.FormatCoverageLogValue(gate).Should().Be("False (12.3%)");
    }

    [Fact]
    public void FormatCoverageLogValue_WithoutPercentage_ReturnsPassedOnly()
    {
        var gate = new GateResult { GateName = "Coverage", Passed = true };
        QualityGateExecutor.FormatCoverageLogValue(gate).Should().Be("True");
    }
}
