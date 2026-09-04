using AwesomeAssertions;
using System.Runtime.InteropServices;
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

    // --- Pytest Stdout Parsing Tests ---

    [Fact]
    public void ParseTestCountsFromStdout_PytestAllPassed_ParsesCorrectly()
    {
        var output = "========================= 5 passed in 1.23s =========================";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(5);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromStdout_PytestMixed_ParsesCorrectly()
    {
        var output = "=================== 3 passed, 2 failed, 1 skipped in 4.56s ===================";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(3);
        failed.Should().Be(2);
        skipped.Should().Be(1);
    }

    [Fact]
    public void ParseTestCountsFromStdout_PytestWithErrors_CountsErrorsAsFailed()
    {
        var output = "=================== 5 passed, 1 error in 2.00s ===================";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(5);
        failed.Should().Be(1);
        skipped.Should().Be(0);
    }

    // --- Maven/JUnit Stdout Parsing Tests ---

    [Fact]
    public void ParseTestCountsFromStdout_MavenSingleModule_ParsesCorrectly()
    {
        var output = "Tests run: 10, Failures: 2, Errors: 1, Skipped: 3";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(4); // 10 - 2 - 1 - 3
        failed.Should().Be(3); // 2 failures + 1 error
        skipped.Should().Be(3);
    }

    [Fact]
    public void ParseTestCountsFromStdout_MavenMultiModule_SumsAcrossModules()
    {
        var output = """
            [INFO] Results:
            Tests run: 5, Failures: 0, Errors: 0, Skipped: 0
            [INFO] Results:
            Tests run: 8, Failures: 1, Errors: 0, Skipped: 2
            """;
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(10); // (5-0-0-0) + (8-1-0-2) = 5 + 5
        failed.Should().Be(1);
        skipped.Should().Be(2);
    }

    // --- BuildCiFailureDetails Edge Cases ---

    [Fact]
    public void BuildCiFailureDetails_WithMultipleFailedJobs_ListsAllJobNames()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Failed },
                new() { Name = "test", State = PipelineRunState.Passed },
                new() { Name = "lint", State = PipelineRunState.Failed }
            }
        };
        var details = QualityGateValidator.BuildCiFailureDetails(status);
        details.Should().Contain("'build'");
        details.Should().Contain("'lint'");
        details.Should().NotContain("'test'");
        details.Should().Contain("2 job(s) failed");
    }

    [Fact]
    public void BuildCiFailureDetails_NoFailedJobs_ShowsUnknown()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Passed }
            }
        };
        var details = QualityGateValidator.BuildCiFailureDetails(status);
        details.Should().Contain("0 job(s) failed");
        details.Should().Contain("unknown");
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

    [Fact]
    public async Task Compilation_Timeout_ReturnsFailedGateResult()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-timeout-test-{Guid.NewGuid():N}");
        try
        {
            var validator = new TimeoutSimulatingValidator(simulateTimeout: true);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 1
            };

            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Compilation.Passed.Should().BeFalse();
            report.QgcResults[0].Compilation!.Details.Should().Contain("timed out");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    [Fact]
    public async Task Tests_Timeout_ReturnsFailedGateResult()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-timeout-test-{Guid.NewGuid():N}");
        try
        {
            var validator = new TimeoutSimulatingValidator(simulateTimeout: true);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = 1
            };

            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Tests!.Passed.Should().BeFalse();
            report.QgcResults[0].Tests!.Details.Should().Contain("timed out");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    [Fact]
    public async Task NormalExecution_WithTimeout_CompletesSuccessfully()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-timeout-test-{Guid.NewGuid():N}");
        try
        {
            var validator = new TimeoutSimulatingValidator(simulateTimeout: false);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 600
            };

            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Compilation.Passed.Should().BeTrue();
            report.Compilation.Details.Should().NotContain("timed out");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    // TODO: Missing test for bounded pipe drain timeout. A process that holds stdout/stderr pipes
    // open after being killed (e.g., grandchild inheriting handles) should still allow the method
    // to return within ~5s due to the drain CancellationTokenSource.
    // (Tests moved to CodingAgentWebUI.Infrastructure.IntegrationTests/QualityGateValidatorProcessTests.cs)

    private sealed class TimeoutSimulatingValidator : QualityGateValidator
    {
        private readonly bool _simulateTimeout;

        public TimeoutSimulatingValidator(bool simulateTimeout) : base(Serilog.Log.Logger)
        {
            _simulateTimeout = simulateTimeout;
        }

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
        {
            if (_simulateTimeout)
                throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds}s");

            return Task.FromResult((0, "Build succeeded.", ""));
        }
    }
}

/// <summary>
/// Custom xUnit v2-compatible FactAttribute that skips the test on Windows.
/// Kept in the unit test file for reference; canonical definition moved to
/// CodingAgentWebUI.Infrastructure.IntegrationTests/QualityGateValidatorProcessTests.cs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SkipOnWindowsFact : FactAttribute
{
    public SkipOnWindowsFact(string reason = "Not supported on Windows")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = reason;
    }
}


