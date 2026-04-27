using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class QualityGateOrchestratorLogFormatTests
{
    [Fact]
    public void FormatGateLogValue_Null_ReturnsNA()
    {
        QualityGateOrchestrator.FormatGateLogValue(null).Should().Be("N/A");
    }

    [Fact]
    public void FormatGateLogValue_Passed_ReturnsTrue()
    {
        var gate = new GateResult { GateName = "SecurityScan", Passed = true };
        QualityGateOrchestrator.FormatGateLogValue(gate).Should().Be("True");
    }

    [Fact]
    public void FormatGateLogValue_Failed_ReturnsFalse()
    {
        var gate = new GateResult { GateName = "ExternalCi", Passed = false };
        QualityGateOrchestrator.FormatGateLogValue(gate).Should().Be("False");
    }

    [Fact]
    public void FormatCoverageLogValue_Null_ReturnsNA()
    {
        QualityGateOrchestrator.FormatCoverageLogValue(null).Should().Be("N/A");
    }

    [Fact]
    public void FormatCoverageLogValue_PassedWithPercentage_IncludesPercentage()
    {
        var gate = new GateResult { GateName = "Coverage", Passed = true, CoveragePercent = 56.4 };
        QualityGateOrchestrator.FormatCoverageLogValue(gate).Should().Be("True (56.4%)");
    }

    [Fact]
    public void FormatCoverageLogValue_FailedWithPercentage_IncludesPercentage()
    {
        var gate = new GateResult { GateName = "Coverage", Passed = false, CoveragePercent = 12.3 };
        QualityGateOrchestrator.FormatCoverageLogValue(gate).Should().Be("False (12.3%)");
    }

    [Fact]
    public void FormatCoverageLogValue_WithoutPercentage_ReturnsPassedOnly()
    {
        var gate = new GateResult { GateName = "Coverage", Passed = true };
        QualityGateOrchestrator.FormatCoverageLogValue(gate).Should().Be("True");
    }
}
