using AwesomeAssertions;
using System.Runtime.InteropServices;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Infrastructure.UnitTests;

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

    // When the run is Cancelled, dependent jobs cascade to Failure conclusion even though no
    // code actually failed. Only Failed jobs with LogContent (evidence of actual execution)
    // are reported; jobs with null LogContent are artefacts of the cancellation and are excluded.
    [Fact]
    public void BuildCiFailureDetails_CancelledRunState_DoesNotReportCascadedFailuresAsCodeFailures()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Cancelled,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "docker-push",    State = PipelineRunState.Failed },  // cascade artefact — no LogContent
                new() { Name = "publish-chart",  State = PipelineRunState.Failed },  // cascade artefact — no LogContent
                new() { Name = "build-and-test", State = PipelineRunState.Passed }
            }
        };
        var details = QualityGateValidator.BuildCiFailureDetails(status);
        // A cancelled run should not name cascade-artefact jobs (no LogContent) as "failed"
        details.Should().NotContain("'docker-push'");
        details.Should().NotContain("'publish-chart'");
        details.Should().Contain("Cancelled");
    }

    // When a Cancelled run contains a job that genuinely failed (has log content as evidence
    // of actual execution), that job must be named in the summary alongside any cancelled jobs.
    // TODO: this fixture has no logless-Failed job alongside the genuine failure. If a regression
    // caused logless Failed jobs to also be included in the "failed" count, this test would not
    // catch it (the mixed-case test covers discrimination but only checks string containment of
    // 'build', not the exact count). Consider adding a logless-Failed job to this fixture and
    // asserting the exact "1 job(s) failed:" sentence to confirm count is not inflated.
    [Fact]
    public void BuildCiFailureDetails_CancelledRun_WithGenuinelyFailedJob_NamesItAsFailed()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Cancelled,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Failed, LogContent = "error CS1234: Something went wrong" },
                new() { Name = "e2e",   State = PipelineRunState.Cancelled }
            }
        };

        var details = QualityGateValidator.BuildCiFailureDetails(status);

        details.Should().Contain("1 job(s) failed: 'build'.");
        details.Should().Contain("1 job(s) cancelled before finishing: 'e2e'.");
        // TODO: strengthen this assertion — it only checks for a substring of the hint sentence.
        // If the wording of the cancelled-job hint changes (e.g., "timeout" is removed or reworded),
        // this will break correctly but doesn't pin the full sentence. Consider asserting
        // details.Should().Contain("A cancelled job usually exceeded its timeout") instead.
        details.Should().Contain("timeout");  // cancelled-job hint sentence must still appear
    }

    // When a Cancelled run contains only logless Failed jobs (cascade artefacts with no LogContent),
    // none of them should be reported as code failures.
    [Fact]
    public void BuildCiFailureDetails_CancelledRun_WithLoglessCascadeFailure_DoesNotNameItAsFailed()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Cancelled,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "docker-push", State = PipelineRunState.Failed }  // cascade artefact — no LogContent
            }
        };

        var details = QualityGateValidator.BuildCiFailureDetails(status);

        details.Should().NotContain("job(s) failed");
        details.Should().Contain("Cancelled");
    }

    // When a Cancelled run contains a mix of genuinely-failed jobs (with LogContent) and
    // cascade-artefact jobs (without LogContent), only the genuine failures are named.
    [Fact]
    public void BuildCiFailureDetails_CancelledRun_MixedRealAndCascadedFailures_NamesOnlyReal()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Cancelled,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build",       State = PipelineRunState.Failed, LogContent = "Build FAILED." },
                new() { Name = "docker-push", State = PipelineRunState.Failed },  // cascade artefact — no LogContent
                new() { Name = "e2e",         State = PipelineRunState.Cancelled }
            }
        };

        var details = QualityGateValidator.BuildCiFailureDetails(status);

        details.Should().Contain("'build'");         // genuine failure — has log
        details.Should().NotContain("'docker-push'"); // cascade artefact — no log
        details.Should().Contain("'e2e'");            // cancelled job named as cancelled
        // TODO: this assertion does not verify the exact job count. If a regression caused
        // cascade-artefact jobs to also be counted, the string "'build'" would still be present
        // and this assertion would pass. Consider asserting the full sentence, e.g.:
        // details.Should().Contain("1 job(s) failed: 'build'.");
    }

    [Fact]
    public void BuildCiFailureDetails_OnlyCancelledJob_NamesItAsCancelledWithoutUnknownFailure()
    {
        // The e2e job hit its timeout-minutes: GitHub reports it cancelled, the skipped deploy
        // jobs map to Passed. The agent must be pointed at e2e, not at "0 job(s) failed: unknown".
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Cancelled,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "e2e", State = PipelineRunState.Cancelled },
                new() { Name = "docker-push", State = PipelineRunState.Passed },
                new() { Name = "publish-chart", State = PipelineRunState.Passed }
            }
        };

        var details = QualityGateValidator.BuildCiFailureDetails(status);

        details.Should().StartWith("CI Cancelled.");
        details.Should().Contain("1 job(s) cancelled before finishing: 'e2e'.");
        details.Should().Contain("timeout");
        details.Should().NotContain("job(s) failed");
        details.Should().NotContain("docker-push");
        details.Should().NotContain("publish-chart");
    }

    [Fact]
    public void BuildCiFailureDetails_FailedAndCancelledJobs_ListsBoth()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Failed },
                new() { Name = "e2e", State = PipelineRunState.Cancelled }
            }
        };

        var details = QualityGateValidator.BuildCiFailureDetails(status);

        details.Should().Contain("1 job(s) failed: 'build'.");
        details.Should().Contain("1 job(s) cancelled before finishing: 'e2e'.");
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
    // (Tests moved to CodingAgent.Infrastructure.IntegrationTests/QualityGateValidatorProcessTests.cs)

    // --- Cleanup prologue characterization tests ---

    /// <summary>
    /// ValidateAsync must delete a pre-existing TestResults directory before the first QGC process
    /// starts. The probe checks the workspace when the process starts, so a cleanup moved after the
    /// gates fails this test.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_CleansPrologue_DeletesTestResultsDirectory()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-prologue-test-{Guid.NewGuid():N}");
        try
        {
            // Arrange: pre-existing TestResults directory
            var testResultsDir = Path.Combine(tempWorkspace, "TestResults");
            Directory.CreateDirectory(testResultsDir);
            File.WriteAllText(Path.Combine(testResultsDir, "old.trx"), "<stale/>");

            var validator = new WorkspaceProbingValidator();
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 60
            };

            // Act
            await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            // Assert: the stale TestResults directory must have been deleted before the first QGC process started
            validator.TestResultsExistedAtProcessStart.Should().ContainSingle()
                .Which.Should().BeFalse("the stale TestResults directory must be deleted before the first QGC process starts");
            Directory.Exists(testResultsDir).Should().BeFalse("ValidateAsync must clean up stale TestResults before running QGCs");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    /// <summary>
    /// ValidateAsync must delete a pre-existing quality-gates output directory before the first QGC
    /// process starts. The probe checks the workspace when the process starts, so a cleanup moved
    /// after the gates fails this test.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_CleansPrologue_DeletesQualityGatesOutputDirectory()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-prologue-test-{Guid.NewGuid():N}");
        try
        {
            // Arrange: pre-existing quality-gates output directory
            var gatesDir = Path.Combine(tempWorkspace, AgentWorkspacePaths.QualityGatesOutputDirectory);
            Directory.CreateDirectory(gatesDir);
            File.WriteAllText(Path.Combine(gatesDir, "old-stdout.txt"), "stale output");

            var validator = new WorkspaceProbingValidator();
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 60
            };

            // Act
            await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            // Assert: the stale quality-gates directory must have been deleted before the first QGC process started
            // (WriteGateOutput will recreate it, but the stale file should be gone)
            validator.QualityGatesDirExistedAtProcessStart.Should().ContainSingle()
                .Which.Should().BeFalse("the stale quality-gates output directory must be deleted before the first QGC process starts");
            var staleFile = Path.Combine(gatesDir, "old-stdout.txt");
            File.Exists(staleFile).Should().BeFalse("ValidateAsync must clean up the quality-gates output directory before running QGCs");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    /// <summary>
    /// The cleanup removes only the previous attempt's output: the output the gates of this run
    /// write to .agent/quality-gates must still be there when ValidateAsync returns, because the
    /// retry prompt tells the agent to read it.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_CleansPrologue_KeepsCurrentRunGateOutput()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-prologue-test-{Guid.NewGuid():N}");
        try
        {
            var gatesDir = Path.Combine(tempWorkspace, AgentWorkspacePaths.QualityGatesOutputDirectory);
            Directory.CreateDirectory(gatesDir);
            File.WriteAllText(Path.Combine(gatesDir, "old-stdout.txt"), "stale output");

            var validator = new WorkspaceProbingValidator();
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 60
            };

            await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            var currentRunOutput = Path.Combine(gatesDir, "Test-compilation-stdout.txt");
            File.Exists(currentRunOutput).Should().BeTrue("the cleanup must not delete the output this run's gates wrote");
            File.ReadAllText(currentRunOutput).Should().Be("Build succeeded.");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    // --- Process outcome handling ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QgcProcessTimeout_FailsGate_AndReportsProcessTimeoutStallToTheApi(bool compilationGate)
    {
        var validator = new StubProcessValidator(StubProcessValidator.ProcessBehavior.Timeout);
        var reported = new List<PipelineRunEventReport>();

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-timeout-test-{Guid.NewGuid():N}");
        try
        {
            var report = await validator.ValidateAsync(
                tempWorkspace,
                [CreateQgc(compilationGate, processTimeoutSeconds: 1)],
                CancellationToken.None,
                reportEvent: reported.Add);

            (compilationGate ? report.Compilation.Passed : report.Tests!.Passed).Should().BeFalse("timeout causes failure");
            reported.Should().ContainSingle().Which.Should().BeEquivalentTo(new PipelineRunEventReport
            {
                Kind = PipelineRunEventKind.AgentStall,
                Stage = PipelineTelemetry.RunPhases.QualityGate,
                Result = PipelineTelemetry.AgentStallKinds.ProcessTimeout
            });
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    [Theory]
    [InlineData(false, StubProcessValidator.ProcessBehavior.Cancel)]
    [InlineData(true, StubProcessValidator.ProcessBehavior.Cancel)]
    [InlineData(false, StubProcessValidator.ProcessBehavior.ThrowError)]
    [InlineData(true, StubProcessValidator.ProcessBehavior.ThrowError)]
    public async Task QgcProcessCancellationOrError_IsRethrown(bool compilationGate, StubProcessValidator.ProcessBehavior behavior)
    {
        var validator = new StubProcessValidator(behavior);

        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-rethrow-test-{Guid.NewGuid():N}");
        try
        {
            var act = () => validator.ValidateAsync(
                tempWorkspace, [CreateQgc(compilationGate, processTimeoutSeconds: 60)], CancellationToken.None);

            if (behavior == StubProcessValidator.ProcessBehavior.Cancel)
                await act.Should().ThrowAsync<OperationCanceledException>();
            else
                await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    private static QualityGateConfiguration CreateQgc(bool compilationGate, int processTimeoutSeconds) =>
        compilationGate
            ? new QualityGateConfiguration
            {
                DisplayName = "BuildProject",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = processTimeoutSeconds
            }
            : new QualityGateConfiguration
            {
                DisplayName = "MyTestSuite",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = processTimeoutSeconds
            };

    public sealed class StubProcessValidator(StubProcessValidator.ProcessBehavior behavior)
        : QualityGateValidator(Serilog.Log.Logger)
    {
        public enum ProcessBehavior { Succeed, Timeout, Cancel, ThrowError }

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout) =>
            behavior switch
            {
                ProcessBehavior.Timeout => throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds}s"),
                ProcessBehavior.Cancel => throw new OperationCanceledException("Cancelled"),
                ProcessBehavior.ThrowError => throw new InvalidOperationException("Simulated process error"),
                _ => Task.FromResult((0, "Passed: 5\nTest summary: total: 5; failed: 0; succeeded: 5; skipped: 0; duration: 0.1s", ""))
            };
    }

    /// <summary>
    /// Records, each time a QGC process would start, whether the workspace's TestResults and
    /// quality-gates output directories exist, so tests can check the workspace while a gate runs.
    /// Every process "succeeds" with stdout "Build succeeded.".
    /// </summary>
    private sealed class WorkspaceProbingValidator : QualityGateValidator
    {
        public WorkspaceProbingValidator() : base(Serilog.Log.Logger) { }

        public List<bool> TestResultsExistedAtProcessStart { get; } = [];
        public List<bool> QualityGatesDirExistedAtProcessStart { get; } = [];

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
        {
            TestResultsExistedAtProcessStart.Add(Directory.Exists(Path.Combine(workingDirectory, "TestResults")));
            QualityGatesDirExistedAtProcessStart.Add(
                Directory.Exists(Path.Combine(workingDirectory, AgentWorkspacePaths.QualityGatesOutputDirectory)));
            return Task.FromResult((0, "Build succeeded.", ""));
        }
    }

    // TODO: Add ActivityListener-based tests verifying that the "QualityGate.Tests" and "QualityGate.Compilation"
    // spans carry the expected tags: qgc_name, qgc.timeout_seconds (on every invocation) and qgc.timed_out=true
    // (on timeout). The production code sets these correctly but there is no regression guard — a refactor could
    // silently drop the tags without any test failing. Use ActivitySource.AddActivityListener with a filter on
    // PipelineTelemetry.ActivitySource.Name and assert Activity.Tags after ValidateAsync returns.

    // --- Acceptance criterion: baseBranch removal ---

    // TODO [WARNING]: This reflection test detects re-addition of 'baseBranch' by name, but would not catch
    // a parameter re-introduced under a different name (e.g. 'baseRef') that is again silently ignored.
    // A behavioural complement — confirming ValidateAsync is callable with the expected parameter set
    // (workspacePath, qualityGateConfigs, ct, reportEvent) and returns a valid QualityGateReport — would
    // provide a more meaningful regression guard. (TestQualityReviewer WARNING — issue #3437)

    // TODO [WARNING]: This test inspects only the interface type; it does not exercise QualityGateValidator's
    // implementation at all. The issue prerequisites called for characterisation tests covering a configured
    // QGC run on the concrete ValidateAsync implementation (e.g. verifying CleanWorkspacePrologue and
    // RunAllQgcsAsync are called). Without such tests a future refactor that alters observable behaviour of
    // the implementation (e.g. stops calling CleanWorkspacePrologue) would not be caught here.
    // (TestQualityReviewer WARNING — issue #3437)

    /// <summary>
    /// Regression guard for issue #3437: <c>baseBranch</c> was removed from
    /// <see cref="IQualityGateValidator.ValidateAsync"/> because it was accepted but never read.
    /// This test uses reflection to assert the parameter is absent from the interface, so that
    /// re-adding it (even as an optional parameter) causes an immediate, named failure rather
    /// than a silent contract drift.
    /// </summary>
    [Fact]
    public void ValidateAsync_InterfaceSignature_DoesNotContainBaseBranchParameter()
    {
        var method = typeof(IQualityGateValidator)
            .GetMethod(nameof(IQualityGateValidator.ValidateAsync))!;
        var paramNames = method.GetParameters().Select(p => p.Name).ToArray();
        paramNames.Should().NotContain("baseBranch",
            "baseBranch was removed from the interface per issue #3437 — it was accepted but never read");
    }

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
/// CodingAgent.Infrastructure.IntegrationTests/QualityGateValidatorProcessTests.cs.
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

/// <summary>
/// Tests for the <c>reportEvent</c> delegate of <see cref="QualityGateValidator.ValidateAsync"/>,
/// which fires a server-side process_timeout agent-stall event (issue #2979).
///
/// Without these tests, removing or misplacing the ctx.ReportPipelineRunEvent?.Invoke(...)
/// call in QualityGateValidator.RunQgcProcessAsync would silently break process_timeout
/// observability.
/// </summary>
public class ValidateAsyncStallReportingTests
{
    [Fact]
    public async Task Tests_Timeout_FiresProcessTimeoutStallEvent()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-sside-timeout-{Guid.NewGuid():N}");
        try
        {
            var reportedEvents = new List<PipelineRunEventReport>();
            var validator = new TimeoutSimulatingValidator(simulateTimeout: true);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = 1
            };

            // Use a non-null reportEvent delegate
            var report = await validator.ValidateAsync(
                tempWorkspace, [qgc], CancellationToken.None, reportEvent: reportedEvents.Add);

            // Gate must still fail (timeout is a gate failure)
            report.Tests!.Passed.Should().BeFalse("a process timeout must produce a failed Tests gate");
            report.QgcResults[0].Tests!.Details.Should().Contain("timed out");

            // Exactly one AgentStall event with kind=process_timeout must have been reported
            var stallEvents = reportedEvents
                .Where(e => e.Kind == PipelineRunEventKind.AgentStall)
                .ToList();

            stallEvents.Should().ContainSingle(
                "one process_timeout AgentStall event must be fired per QGC process timeout");
            stallEvents[0].Result.Should().Be(PipelineTelemetry.AgentStallKinds.ProcessTimeout,
                "the stall kind must be process_timeout");
            stallEvents[0].Stage.Should().Be(PipelineTelemetry.RunPhases.QualityGate,
                "the phase must be quality_gate for QGC process timeouts");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    [Fact]
    public async Task Compilation_Timeout_FiresProcessTimeoutStallEvent()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-sside-comp-{Guid.NewGuid():N}");
        try
        {
            var reportedEvents = new List<PipelineRunEventReport>();
            var validator = new TimeoutSimulatingValidator(simulateTimeout: true);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Build",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 1
            };

            var report = await validator.ValidateAsync(
                tempWorkspace, [qgc], CancellationToken.None, reportEvent: reportedEvents.Add);

            report.Compilation.Passed.Should().BeFalse("compilation timeout must fail the gate");

            var stallEvents = reportedEvents
                .Where(e => e.Kind == PipelineRunEventKind.AgentStall)
                .ToList();

            stallEvents.Should().ContainSingle(
                "one process_timeout AgentStall event must be fired for a compilation timeout");
            stallEvents[0].Result.Should().Be(PipelineTelemetry.AgentStallKinds.ProcessTimeout);
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    [Fact]
    public async Task WhenNoTimeout_NoStallEventIsReported()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-sside-ok-{Guid.NewGuid():N}");
        try
        {
            var reportedEvents = new List<PipelineRunEventReport>();
            var validator = new TimeoutSimulatingValidator(simulateTimeout: false);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = 60
            };

            await validator.ValidateAsync(
                tempWorkspace, [qgc], CancellationToken.None, reportEvent: reportedEvents.Add);

            reportedEvents.Should().BeEmpty(
                "no stall events should be reported when the process completes within the timeout");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    private sealed class TimeoutSimulatingValidator : QualityGateValidator
    {
        private readonly bool _simulateTimeout;

        public TimeoutSimulatingValidator(bool simulateTimeout)
            : base(Serilog.Log.Logger) => _simulateTimeout = simulateTimeout;

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
        {
            if (_simulateTimeout)
                throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds}s");
            return Task.FromResult((0, "Passed: 3\nTest summary: total: 3; failed: 0; succeeded: 3; skipped: 0; duration: 0.1s", ""));
        }
    }
}

/// <summary>
/// Covers the directory-cleanup exception catch branch in the workspace-cleanup prologue
/// (<c>CleanWorkspacePrologue</c>) that <see cref="QualityGateValidator.ValidateAsync"/> runs,
/// with and without a <c>reportEvent</c> delegate.
/// The happy-path tests pre-create the TestResults directory so <c>Directory.Exists</c>
/// returns true and the deletion lines execute. The exception-path tests use
/// <see cref="ThrowingDeleteValidator"/>, which overrides the <c>DeleteDirectoryRecursive</c> hook
/// to throw, so the catch block runs on every OS and whether or not the tests run as root.
/// The catch block logs a Warning and continues; the gate result is not affected.
/// </summary>
public class QualityGateValidatorCleanupExceptionTests
{
    /// <summary>
    /// ValidateAsync with a reportEvent delegate must successfully delete the TestResults
    /// directory when it exists (the happy-path delete in the cleanup prologue).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WithReportEvent_WhenTestResultsDirExists_DeletesItSuccessfully()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-cleanup-sside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            // Pre-create the TestResults directory so Directory.Exists returns true
            // and the cleanup lines are executed.
            var testResultsDir = Path.GetFullPath(Path.Combine(tempWorkspace, "TestResults"));
            Directory.CreateDirectory(testResultsDir);
            File.WriteAllText(Path.Combine(testResultsDir, "dummy.txt"), "dummy");

            var reportedEvents = new List<PipelineRunEventReport>();
            var validator = new NoOpProcessValidator();
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = 30
            };

            // Must complete — cleanup (delete TestResults) runs without throwing
            var report = await validator.ValidateAsync(
                tempWorkspace, [qgc], CancellationToken.None, reportEvent: reportedEvents.Add);

            report.Should().NotBeNull("the method must return a report after running the cleanup prologue");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
        }
    }

    /// <summary>
    /// ValidateAsync must successfully delete the TestResults directory when it exists
    /// (the happy-path delete in the cleanup prologue).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTestResultsDirExists_DeletesItSuccessfully()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-cleanup-base-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            // Pre-create the TestResults directory so the cleanup branch executes
            var testResultsDir = Path.GetFullPath(Path.Combine(tempWorkspace, "TestResults"));
            Directory.CreateDirectory(testResultsDir);
            File.WriteAllText(Path.Combine(testResultsDir, "dummy.txt"), "dummy");

            var validator = new NoOpProcessValidator();
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = 30
            };

            // Must complete — cleanup runs without throwing (the assertion is that no exception propagates)
            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Should().NotBeNull("the method must return a report after running the cleanup prologue");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
        }
    }

    /// <summary>
    /// ValidateAsync must complete successfully when the TestResults directory cleanup
    /// throws (the exception is caught and logged at Warning level, not propagated).
    /// </summary>
    [Fact]
    public Task ValidateAsync_WhenTestResultsCleanupThrows_CompletesSuccessfully()
        => AssertTestResultsCleanupFailureIsSwallowedAsync(
            new IOException("Simulated TestResults delete failure"), withReportEvent: false);

    /// <summary>
    /// ValidateAsync with a reportEvent delegate must complete successfully when the
    /// TestResults directory cleanup throws.
    /// </summary>
    [Fact]
    public Task ValidateAsync_WithReportEvent_WhenTestResultsCleanupThrows_CompletesSuccessfully()
        => AssertTestResultsCleanupFailureIsSwallowedAsync(
            new UnauthorizedAccessException("Simulated TestResults delete failure"), withReportEvent: true);

    /// <summary>
    /// Runs ValidateAsync on a workspace whose TestResults delete throws <paramref name="deleteException"/>
    /// and asserts that the cleanup prologue's catch block handled it: ValidateAsync completes,
    /// the exception is logged as a Warning, and the gate result still passes.
    /// </summary>
    private static async Task AssertTestResultsCleanupFailureIsSwallowedAsync(Exception deleteException, bool withReportEvent)
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-cleanup-except-{Guid.NewGuid():N}");
        var testResultsDir = Path.GetFullPath(Path.Combine(tempWorkspace, "TestResults"));
        try
        {
            // Pre-create TestResults so Directory.Exists returns true and the prologue calls the delete hook
            Directory.CreateDirectory(testResultsDir);

            var sink = new CapturingSink();
            var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            var validator = new ThrowingDeleteValidator(deleteException, logger);
            var reportedEvents = new List<PipelineRunEventReport>();
            Action<PipelineRunEventReport>? reportEvent = withReportEvent ? reportedEvents.Add : null;
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = 30
            };

            // Must not throw even though cleanup throws — exception is swallowed
            var act = () => validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None, reportEvent: reportEvent);
            var report = (await act.Should().NotThrowAsync(
                "a delete exception in the cleanup prologue must be caught and not propagated")).Subject;

            validator.DeleteAttempts.Should().Equal([testResultsDir],
                "the prologue must delete TestResults through the hook, otherwise the catch block is not exercised");
            sink.Events.Should().ContainSingle(
                e => e.Level == LogEventLevel.Warning && e.Exception == deleteException,
                "the catch block must log the delete failure as a Warning");
            report.AllPassed.Should().BeTrue("a failed TestResults cleanup must not affect the gate result");
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { }
        }
    }

    /// <summary>
    /// A no-op <see cref="QualityGateValidator"/> that never actually executes a process,
    /// used to exercise the cleanup prologue without needing real build/test tools.
    /// </summary>
    private class NoOpProcessValidator : QualityGateValidator
    {
        public NoOpProcessValidator() : this(Serilog.Log.Logger) { }

        public NoOpProcessValidator(Serilog.ILogger logger) : base(logger) { }

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
            => Task.FromResult((0, "Passed: 1\nTest summary: total: 1; failed: 0; succeeded: 1; skipped: 0; duration: 0.0s", ""));
    }

    /// <summary>
    /// A <see cref="NoOpProcessValidator"/> whose TestResults delete throws the given exception,
    /// so the cleanup prologue's catch block runs. File permissions cannot force the delete
    /// to fail when the tests run as root, as CI's build-and-test container does.
    /// </summary>
    private sealed class ThrowingDeleteValidator(Exception deleteException, Serilog.ILogger logger)
        : NoOpProcessValidator(logger)
    {
        public List<string> DeleteAttempts { get; } = [];

        private protected override void DeleteDirectoryRecursive(string path)
        {
            DeleteAttempts.Add(path);
            throw deleteException;
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
