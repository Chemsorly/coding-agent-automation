using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

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

    // --- TRX Parsing Tests ---

    [Fact]
    public void ParseTestCountsFromTrx_WithValidTrxFile_ExtractsCorrectCounts()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "results.trx", passed: 10, failed: 2, notExecuted: 1);

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(10);
            failed.Should().Be(2);
            skipped.Should().Be(1);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithMultipleTrxFiles_SumsAcrossAssemblies()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "assembly1.trx", passed: 10, failed: 0, notExecuted: 1);
            WriteTrxFile(dir, "assembly2.trx", passed: 25, failed: 3, notExecuted: 0);

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(35);
            failed.Should().Be(3);
            skipped.Should().Be(1);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithErrorAttribute_CountsAsFailure()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "results.trx", passed: 8, failed: 1, notExecuted: 0, error: 2);

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(8);
            failed.Should().Be(3); // 1 failed + 2 error
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithNoDirectory_ReturnsZeros()
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx("/nonexistent/path");

        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithEmptyDirectory_ReturnsZeros()
    {
        var dir = CreateTempDir();
        try
        {
            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(0);
            failed.Should().Be(0);
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithMalformedXml_SkipsAndReturnsZeros()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.trx"), "not xml at all");

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(0);
            failed.Should().Be(0);
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithMixedValidAndMalformed_SumsValidOnly()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "good.trx", passed: 10, failed: 1, notExecuted: 0);
            File.WriteAllText(Path.Combine(dir, "bad.trx"), "not xml");

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(10);
            failed.Should().Be(1);
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Cobertura Coverage Parsing Tests ---

    [Fact]
    public void ParseCoverageFromCobertura_WithSingleFile_ReturnsCorrectPercentage()
    {
        var dir = CreateTempDir();
        try
        {
            var file = WriteCoberturaFile(dir, "coverage.cobertura.xml", lineRate: 0.85, linesValid: 200);

            var coverage = QualityGateValidator.ParseCoverageFromCobertura([file]);

            coverage.Should().BeApproximately(85.0, 0.5);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromCobertura_WithMultipleFiles_ReturnsWeightedAverage()
    {
        var dir = CreateTempDir();
        try
        {
            var file1 = WriteCoberturaFile(dir, "cov1.xml", lineRate: 0.9, linesValid: 100);
            var file2 = WriteCoberturaFile(dir, "cov2.xml", lineRate: 0.6, linesValid: 300);

            // Weighted: (90 + 180) / 400 = 67.5%
            var coverage = QualityGateValidator.ParseCoverageFromCobertura([file1, file2]);

            coverage.Should().BeApproximately(67.5, 0.5);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromCobertura_WithNoFiles_ReturnsZero()
    {
        var coverage = QualityGateValidator.ParseCoverageFromCobertura([]);

        coverage.Should().Be(0.0);
    }

    // --- Stdout Fallback Parsing Tests ---

    [Theory]
    [InlineData("Passed:  10, Failed:   2, Skipped:   1", 10, 2, 1)]
    [InlineData("Passed: 0, Failed: 0, Skipped: 0", 0, 0, 0)]
    [InlineData("No test results here", 0, 0, 0)]
    [InlineData("", 0, 0, 0)]
    public void ParseTestCountsFromStdout_PerAssemblyFormat_ExtractsCorrectValues(
        string output, int expectedPassed, int expectedFailed, int expectedSkipped)
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);

        passed.Should().Be(expectedPassed);
        failed.Should().Be(expectedFailed);
        skipped.Should().Be(expectedSkipped);
    }

    // --- Build Error Count Parsing Tests ---

    [Theory]
    [InlineData("Build FAILED.\n    3 Error(s)\n    2 Warning(s)", 3, 2)]
    [InlineData("Build FAILED.\n    0 Error(s)\n    0 Warning(s)", 0, 0)]
    [InlineData("Build succeeded.\n    0 Error(s)\n    1 Warning(s)", 0, 1)]
    [InlineData("no match here", 0, 0)]
    [InlineData("", 0, 0)]
    public void ParseBuildErrorCounts_ExtractsCorrectValues(
        string output, int expectedErrors, int expectedWarnings)
    {
        var (errors, warnings) = QualityGateValidator.ParseBuildErrorCounts(output);

        errors.Should().Be(expectedErrors);
        warnings.Should().Be(expectedWarnings);
    }

    // --- BuildCiFailureDetails Tests ---

    [Fact]
    public void BuildCiFailureDetails_ReturnsSummaryOnly()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build-and-test", State = PipelineRunState.Failed, FailureReason = "Process completed with exit code 1", JobId = 123, LogUrl = "https://example.com/logs" },
                new PipelineJobResult { Name = "lint", State = PipelineRunState.Passed, JobId = 456 }
            }
        };

        var details = QualityGateValidator.BuildCiFailureDetails(status);

        details.Should().Contain("1 job(s) failed");
        details.Should().Contain("'build-and-test'");
        // Should NOT contain verbose per-job details, log URLs, or file paths
        details.Should().NotContain("https://example.com/logs");
        details.Should().NotContain("Full CI log saved to");
    }

    [Fact]
    public void ParseTestCountsFromStdout_MultipleAssemblyLines_SumsAll()
    {
        var output = """
            Passed:  10, Failed:   0, Skipped:   1 - Assembly1.dll
            Passed:  25, Failed:   3, Skipped:   0 - Assembly2.dll
            """;

        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);

        passed.Should().Be(35);
        failed.Should().Be(3);
        skipped.Should().Be(1);
    }

    [Fact]
    public void ParseTestCountsFromStdout_DotNet10SummaryLine_ParsesCorrectly()
    {
        var output = "Test summary: total: 47; failed: 0; succeeded: 47; skipped: 0; duration: 1.4s";

        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);

        passed.Should().Be(47);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    // --- Helpers ---

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteTrxFile(string dir, string fileName,
        int passed, int failed, int notExecuted, int error = 0)
    {
        var total = passed + failed + notExecuted + error;
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="{total}" executed="{passed + failed + error}" passed="{passed}" failed="{failed}" error="{error}" notExecuted="{notExecuted}" />
              </ResultSummary>
            </TestRun>
            """;
        File.WriteAllText(Path.Combine(dir, fileName), xml);
    }

    private static string WriteCoberturaFile(string dir, string fileName,
        double lineRate, long linesValid)
    {
        var linesCovered = (long)(lineRate * linesValid);
        var linesUncovered = linesValid - linesCovered;

        // Generate line-level data matching the summary attributes
        var lineElements = new System.Text.StringBuilder();
        for (var i = 1; i <= linesCovered; i++)
            lineElements.AppendLine($"              <line number=\"{i}\" hits=\"1\" />");
        for (var i = (int)linesCovered + 1; i <= linesValid; i++)
            lineElements.AppendLine($"              <line number=\"{i}\" hits=\"0\" />");

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="{lineRate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}" lines-valid="{linesValid}" lines-covered="{linesCovered}" version="1.0">
              <packages>
                <package name="TestAssembly">
                  <classes>
                    <class name="TestClass" filename="{fileName}.cs">
                      <lines>
            {lineElements}
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }
}
