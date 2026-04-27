using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class PipelineFormattingTests
{
    [Fact]
    public void FormatQualityGateSummary_AllPassed_ContainsCheckmarks()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK", TestsPassed = 42, TestsFailed = 0 }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().StartWith("🏗️ Quality gates:");
        result.Should().Contain("Compilation ✅");
        result.Should().Contain("Tests ✅ (42 passed, 0 failed)");
    }

    [Fact]
    public void FormatQualityGateSummary_CompilationFailed_ContainsCross()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "2 errors" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("Compilation ❌");
    }

    [Fact]
    public void FormatQualityGateSummary_WithCoverage_IncludesCoverageDetails()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" },
            Coverage = new GateResult { GateName = "Coverage", Passed = false, Details = "26.7% below threshold 40.0%" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("Coverage ❌ (26.7% below threshold 40.0%)");
    }

    [Fact]
    public void FormatQualityGateSummary_WithExternalCi_IncludesCiStatus()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" },
            ExternalCi = new GateResult { GateName = "External CI", Passed = true, Details = "CI passed" }
        };

        var result = PipelineFormatting.FormatQualityGateSummary(report);

        result.Should().Contain("External CI ✅");
    }
}
