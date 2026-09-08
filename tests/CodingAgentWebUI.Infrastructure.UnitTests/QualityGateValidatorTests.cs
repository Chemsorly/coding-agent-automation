using AwesomeAssertions;
using System.Runtime.InteropServices;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class QualityGateValidatorTests
{
    [Fact]
    public void AllPassed_WhenAllGatesPass_ReturnsTrue()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
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
        // TODO: This test is now structurally identical to AllPassed_WhenAllGatesPass_ReturnsTrue —
        // both set only Compilation and Tests (both passing) with no optional gates. The original
        // intent was to verify that a null optional gate (previously SecurityScan) does not block
        // AllPassed. Set ExternalCi = null explicitly to restore that intent (issue #2400).
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
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

    // --- Metric Instrumentation Tests ---

    [Fact]
    public async Task RunQgcTestsAsync_WhenProcessTimesOut_IncrementsTimeoutCounterAndRecordsDuration()
    {
        var factory = new TestMeterFactory();
        using var timeoutCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.process.timeout");
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(simulateTimeout: true, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "MyTestSuite",
            TestCommand = "dotnet",
            TestArguments = ["test"],
            ProcessTimeoutSeconds = 1
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Tests!.Passed.Should().BeFalse("timeout causes failure");

            // Timeout counter should have exactly 1 measurement with gate_name=tests and qgc_name=MyTestSuite
            timeoutCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Value == 1 &&
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "tests")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "MyTestSuite")),
                "quality_gate.process.timeout should be incremented once with correct tags on timeout");

            // Duration histogram should record with outcome=timeout
            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "tests")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "MyTestSuite")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "timeout")),
                "quality_gate.process.duration should be recorded with outcome=timeout");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    [Fact]
    public async Task RunQgcTestsAsync_WhenProcessSucceeds_RecordsDurationWithSuccessOutcome()
    {
        var factory = new TestMeterFactory();
        using var timeoutCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.process.timeout");
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(simulateTimeout: false, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "MyTestSuite",
            TestCommand = "dotnet",
            TestArguments = ["test"],
            ProcessTimeoutSeconds = 60
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            // No timeout counter increments on success
            timeoutCollector.GetMeasurementSnapshot().Should().BeEmpty("no timeout event on success");

            // Duration recorded with outcome=success
            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "tests")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "MyTestSuite")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "success")),
                "quality_gate.process.duration should be recorded with outcome=success");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    [Fact]
    public async Task RunQgcCompilationAsync_WhenProcessTimesOut_IncrementsTimeoutCounterAndRecordsDuration()
    {
        var factory = new TestMeterFactory();
        using var timeoutCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.process.timeout");
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(simulateTimeout: true, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "BuildProject",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            ProcessTimeoutSeconds = 1
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Compilation.Passed.Should().BeFalse("timeout causes failure");

            // Timeout counter should have exactly 1 measurement with gate_name=compilation
            timeoutCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Value == 1 &&
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "compilation")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "BuildProject")),
                "quality_gate.process.timeout should be incremented once with correct tags on compilation timeout");

            // Duration histogram should record with outcome=timeout
            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "compilation")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "BuildProject")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "timeout")),
                "quality_gate.process.duration should be recorded with outcome=timeout for compilation");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    private sealed class MetricCapturingValidator : QualityGateValidator
    {
        public enum ProcessBehavior { Succeed, Timeout, Cancel, ThrowError }

        private readonly ProcessBehavior _behavior;

        public MetricCapturingValidator(bool simulateTimeout, System.Diagnostics.Metrics.IMeterFactory factory)
            : this(simulateTimeout ? ProcessBehavior.Timeout : ProcessBehavior.Succeed, factory) { }

        public MetricCapturingValidator(ProcessBehavior behavior, System.Diagnostics.Metrics.IMeterFactory factory)
            : base(Serilog.Log.Logger, factory)
        {
            _behavior = behavior;
        }

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
        {
            return _behavior switch
            {
                ProcessBehavior.Timeout => throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds}s"),
                ProcessBehavior.Cancel => throw new OperationCanceledException("Cancelled"),
                ProcessBehavior.ThrowError => throw new InvalidOperationException("Simulated process error"),
                _ => Task.FromResult((0, "Passed: 5\nTest summary: total: 5; failed: 0; succeeded: 5; skipped: 0; duration: 0.1s", ""))
            };
        }
    }

    [Fact]
    public async Task RunQgcCompilationAsync_WhenProcessSucceeds_RecordsDurationWithSuccessOutcome()
    {
        var factory = new TestMeterFactory();
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(MetricCapturingValidator.ProcessBehavior.Succeed, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "BuildProject",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            ProcessTimeoutSeconds = 60
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "compilation")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "BuildProject")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "success")),
                "quality_gate.process.duration should be recorded with outcome=success for compilation");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    [Fact]
    public async Task RunQgcTestsAsync_WhenCancelled_RecordsDurationWithCancelledOutcomeAndRethrows()
    {
        var factory = new TestMeterFactory();
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(MetricCapturingValidator.ProcessBehavior.Cancel, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "MyTestSuite",
            TestCommand = "dotnet",
            TestArguments = ["test"],
            ProcessTimeoutSeconds = 60
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None));

            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "tests")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "MyTestSuite")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "cancelled")),
                "quality_gate.process.duration must be recorded with outcome=cancelled when OperationCanceledException is thrown");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    [Fact]
    public async Task RunQgcCompilationAsync_WhenCancelled_RecordsDurationWithCancelledOutcomeAndRethrows()
    {
        var factory = new TestMeterFactory();
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(MetricCapturingValidator.ProcessBehavior.Cancel, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "BuildProject",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            ProcessTimeoutSeconds = 60
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None));

            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "compilation")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "BuildProject")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "cancelled")),
                "quality_gate.process.duration must be recorded with outcome=cancelled for compilation when cancelled");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    [Fact]
    public async Task RunQgcTestsAsync_WhenProcessThrowsError_RecordsDurationWithErrorOutcomeAndRethrows()
    {
        var factory = new TestMeterFactory();
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(MetricCapturingValidator.ProcessBehavior.ThrowError, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "MyTestSuite",
            TestCommand = "dotnet",
            TestArguments = ["test"],
            ProcessTimeoutSeconds = 60
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None));

            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "tests")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "MyTestSuite")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "error")),
                "quality_gate.process.duration must be recorded with outcome=error when process throws unexpected exception");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    [Fact]
    public async Task RunQgcCompilationAsync_WhenProcessThrowsError_RecordsDurationWithErrorOutcomeAndRethrows()
    {
        var factory = new TestMeterFactory();
        using var durationCollector = new MetricCollector<double>(factory, PipelineTelemetry.SourceName, "quality_gate.process.duration");

        var validator = new MetricCapturingValidator(MetricCapturingValidator.ProcessBehavior.ThrowError, factory: factory);
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "BuildProject",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            ProcessTimeoutSeconds = 60
        };

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-metrics-test-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None));

            durationCollector.GetMeasurementSnapshot().Should().ContainSingle(m =>
                m.Tags.Contains(new KeyValuePair<string, object?>("gate_name", "compilation")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("qgc_name", "BuildProject")) &&
                m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "error")),
                "quality_gate.process.duration must be recorded with outcome=error for compilation when process throws unexpected exception");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
            factory.Dispose();
        }
    }

    // TODO: Add ActivityListener-based tests verifying that the "QualityGate.Tests" and "QualityGate.Compilation"
    // spans carry the expected tags: qgc_name, qgc.timeout_seconds (on every invocation) and qgc.timed_out=true
    // (on timeout). The production code sets these correctly but there is no regression guard — a refactor could
    // silently drop the tags without any test failing. Use ActivitySource.AddActivityListener with a filter on
    // PipelineTelemetry.ActivitySource.Name and assert Activity.Tags after ValidateAsync returns.

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



/// <summary>
/// Tests for the infrastructure-kill heuristic in <see cref="QualityGateValidator.RunQgcTestsAsync"/>.
/// Uses a test subclass that overrides RunProcessAsync to return controlled (exitCode, stdout, stderr)
/// without spawning real processes.
/// </summary>
public class QualityGateValidatorInfraKillTests
{
    // Subclass that returns controlled (exitCode, stdout, stderr) from RunProcessAsync.
    private sealed class InfraKillSimulatingValidator : QualityGateValidator
    {
        private readonly int _exitCode;
        private readonly string _stdout;
        private readonly string _stderr;

        public InfraKillSimulatingValidator(int exitCode, string stdout, string stderr)
            : base(Serilog.Log.Logger)
        {
            _exitCode = exitCode;
            _stdout = stdout;
            _stderr = stderr;
        }

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
            => Task.FromResult((_exitCode, _stdout, _stderr));
    }

    private static QualityGateConfiguration DotnetTestQgc() => new()
    {
        DisplayName = "Test",
        TestCommand = "dotnet",
        TestArguments = ["test"],
        ProcessTimeoutSeconds = 600
    };

    private static string CreateTempWorkspace() =>
        Path.Combine(Path.GetTempPath(), $"qg-infra-test-{Guid.NewGuid():N}");

    /// <summary>
    /// AC: exitCode=137, stdout="", stderr="", no TRX → GateResult.Details contains
    /// "infrastructure failure" and "137". Represents OOM/SIGKILL scenario.
    /// </summary>
    [Fact]
    public async Task InfraKill_ExitCode137_EmptyStdoutStderr_NoTrx_DetailsContainsInfraFailure()
    {
        var tempWorkspace = CreateTempWorkspace();
        try
        {
            var validator = new InfraKillSimulatingValidator(exitCode: 137, stdout: "", stderr: "");
            var report = await validator.ValidateAsync(tempWorkspace, [DotnetTestQgc()], CancellationToken.None);

            var testsResult = report.QgcResults[0].Tests!;
            testsResult.Passed.Should().BeFalse();
            testsResult.Details.Should().Contain("infrastructure failure");
            testsResult.Details.Should().Contain("137");
            testsResult.IsInfrastructureFailure.Should().BeTrue();
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    /// <summary>
    /// AC: exitCode=1, non-zero test counts parsed from stdout → GateResult.Details uses count
    /// format, no "infrastructure" mention. Represents a genuine test failure where
    /// ParseTestCountsFromStdout can extract non-zero counts (failed > 0), which is the primary
    /// suppression mechanism for the infra-kill heuristic.
    /// Uses the per-assembly format "Passed:  0, Failed:   1, Skipped:   0" which is recognised
    /// by StdoutTestResultParser, ensuring ResolveTestCounts returns non-zero counts and the
    /// heuristic fires (or not) based on counts rather than stdout non-emptiness alone.
    /// </summary>
    [Fact]
    public async Task RealFailure_ExitCode1_StdoutHasTestCount_DetailsUsesCountFormat()
    {
        var tempWorkspace = CreateTempWorkspace();
        try
        {
            // Use a stdout format recognised by ParseTestCountsFromStdout so that
            // ResolveTestCounts returns failed=1 (not zero), ensuring the infra-kill heuristic
            // is suppressed by the non-zero count condition, not merely by stdout non-emptiness.
            var validator = new InfraKillSimulatingValidator(exitCode: 1, stdout: "Passed:  0, Failed:   1, Skipped:   0", stderr: "");
            var report = await validator.ValidateAsync(tempWorkspace, [DotnetTestQgc()], CancellationToken.None);

            var testsResult = report.QgcResults[0].Tests!;
            testsResult.Passed.Should().BeFalse();
            // Count-based format: must contain the actual non-zero failure count
            testsResult.Details.Should().Contain("1 failed");
            testsResult.Details.Should().Contain("0 passed");
            testsResult.Details.Should().NotContain("infrastructure");
            // TODO [WARNING]: IsInfrastructureFailure.Should().BeNull() locks in the null-not-false
            // contract (production code uses `isInfraFailure ? true : null`, never `false`). This is
            // stricter than the acceptance criterion which only requires no "infrastructure" mention.
            // If the contract changes (e.g. to emit `false` for confirmed non-infra paths), this
            // assertion will fail without explanation. Consider adding a comment linking to the
            // IsInfrastructureFailure XML doc comment that defines the null=unknown/not-applicable contract.
            // See review finding: TestQualityReviewer WARNING — QualityGateValidatorTests.cs
            testsResult.IsInfrastructureFailure.Should().BeNull();
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    /// <summary>
    /// Edge case: zero test counts + non-zero exit + non-empty stdout → heuristic must NOT fire.
    /// Stdout with content (even without parseable test counts) disqualifies the infra-kill path.
    /// </summary>
    // TODO [WARNING]: These two edge-case tests (NonEmptyStdout and NonEmptyStderr) are duplicates differing
    // only in which stream is non-empty. Consider collapsing into a single [Theory] with two [InlineData]
    // cases so a removed case is immediately visible as a missing test rather than a silent gap.
    // See review finding: TestQualityReviewer WARNING — QualityGateValidatorTests.cs:952
    [Fact]
    public async Task InfraKill_AllCountsZero_NonEmptyStdout_DoesNotTriggerInfraHeuristic()
    {
        var tempWorkspace = CreateTempWorkspace();
        try
        {
            var validator = new InfraKillSimulatingValidator(exitCode: 1, stdout: "partial MSBuild output", stderr: "");
            var report = await validator.ValidateAsync(tempWorkspace, [DotnetTestQgc()], CancellationToken.None);

            var testsResult = report.QgcResults[0].Tests!;
            testsResult.Details.Should().NotContain("infrastructure");
            testsResult.IsInfrastructureFailure.Should().BeNull();
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    /// <summary>
    /// Edge case: zero test counts + non-zero exit + non-empty stderr → heuristic must NOT fire.
    /// stderr with content (e.g., missing SDK) disqualifies the infra-kill path to avoid
    /// misclassifying diagnosable failures as infrastructure failures.
    /// </summary>
    [Fact]
    public async Task InfraKill_AllCountsZero_NonEmptyStderr_DoesNotTriggerInfraHeuristic()
    {
        var tempWorkspace = CreateTempWorkspace();
        try
        {
            var validator = new InfraKillSimulatingValidator(exitCode: 1, stdout: "", stderr: "MSBUILD: error MSB1003: Could not load file");
            var report = await validator.ValidateAsync(tempWorkspace, [DotnetTestQgc()], CancellationToken.None);

            var testsResult = report.QgcResults[0].Tests!;
            testsResult.Details.Should().NotContain("infrastructure");
            testsResult.IsInfrastructureFailure.Should().BeNull();
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    /// <summary>
    /// Edge case: exit code 0 with zero test counts and empty output → heuristic must NOT fire.
    /// Represents a run where no test projects were found (passes vacuously).
    /// </summary>
    [Fact]
    public async Task InfraKill_ExitCode0_AllCountsZero_DoesNotTriggerInfraHeuristic()
    {
        var tempWorkspace = CreateTempWorkspace();
        try
        {
            var validator = new InfraKillSimulatingValidator(exitCode: 0, stdout: "", stderr: "");
            var report = await validator.ValidateAsync(tempWorkspace, [DotnetTestQgc()], CancellationToken.None);

            var testsResult = report.QgcResults[0].Tests!;
            testsResult.Passed.Should().BeTrue();
            testsResult.Details.Should().NotContain("infrastructure");
            testsResult.IsInfrastructureFailure.Should().BeNull();
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    /// <summary>
    /// Aggregate propagation: when a QGC's Tests gate has IsInfrastructureFailure=true,
    /// the aggregate QualityGateReport.Tests.IsInfrastructureFailure must also be true.
    /// Tests BuildAggregateReport propagation via the firstFailingQgc path.
    /// </summary>
    // TODO: [WARNING] This test only exercises the single-QGC path. In a multi-QGC run where the
    // first failing QGC has a compilation failure (Tests gate is null) and a later QGC has an
    // infra-kill Tests result, BuildAggregateReport's firstFailingQgc points to the compilation-
    // failing QGC, so firstFailingQgc?.Tests?.IsInfrastructureFailure resolves to null — the
    // aggregate field is wrong even though an infra kill occurred. The retry-prompt logic in
    // RetryLoop.cs compensates via report.QgcResults.Any(...), so the prompt is still correct,
    // but the aggregate report.Tests.IsInfrastructureFailure field is misleading. Add a multi-QGC
    // test covering this divergence and consider aligning BuildAggregateReport to use Any() to
    // match the retry-loop logic.
    [Fact]
    public async Task Aggregate_InfraFailureQgcPropagated_ToAggregateReport()
    {
        var tempWorkspace = CreateTempWorkspace();
        try
        {
            var validator = new InfraKillSimulatingValidator(exitCode: 137, stdout: "", stderr: "");
            var report = await validator.ValidateAsync(tempWorkspace, [DotnetTestQgc()], CancellationToken.None);

            // Aggregate Tests gate must propagate IsInfrastructureFailure from the failing QGC
            report.Tests.IsInfrastructureFailure.Should().BeTrue();
            report.Tests.Passed.Should().BeFalse();
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }
}
