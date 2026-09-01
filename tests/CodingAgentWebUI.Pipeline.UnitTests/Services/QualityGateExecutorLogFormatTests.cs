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
}
