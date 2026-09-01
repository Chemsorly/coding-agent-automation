using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Core;
using OpenTelemetry.Trace;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Validates generated code against quality thresholds by running
/// dotnet build and dotnet test in the workspace directory.
/// Uses TRX reports for accurate test data.
/// Optionally validates against an external CI/CD pipeline.
/// </summary>
public class QualityGateValidator : IQualityGateValidator
{
    private readonly Serilog.ILogger _logger;

    public QualityGateValidator(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Timeout for draining stdout/stderr pipes after the process exits.
    /// Overridable for testing with shorter durations.
    /// </summary>
    protected virtual TimeSpan PipeDrainTimeout => TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public virtual async Task<QualityGateReport> ValidateAsync(
        string workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs, CancellationToken ct, string? baseBranch = null)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(qualityGateConfigs);

        // Clean up any leftover TestResults from previous quality gate iterations
        var testResultsRoot = Path.GetFullPath(Path.Combine(workspacePath, "TestResults"));
        try
        {
            if (Directory.Exists(testResultsRoot))
            {
                Directory.Delete(testResultsRoot, recursive: true);
                _logger.Debug("Cleaned up previous test results at {TestResultsRoot}", testResultsRoot);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up previous test results at {TestResultsRoot}", testResultsRoot);
        }

        // Clear quality gate output directory so the agent only sees output from this run
        var qualityGatesDir = Path.Combine(workspacePath, AgentWorkspacePaths.QualityGatesOutputDirectory);
        try
        {
            if (Directory.Exists(qualityGatesDir))
                Directory.Delete(qualityGatesDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up quality gates output at {QualityGatesDir}", qualityGatesDir);
        }

        var qgcResults = new List<QgcExecutionResult>();

        foreach (var qgc in qualityGateConfigs)
        {
            var (result, shouldStop) = await RunSingleQgcAsync(workspacePath, qgc, ct);
            qgcResults.Add(result);
            if (shouldStop)
                break;
        }

        return BuildAggregateReport(qgcResults);
    }

    /// <summary>
    /// Runs compilation, tests, and coverage for a single QGC.
    /// Returns the result record and whether processing should stop (i.e., a gate failed).
    /// </summary>
    private async Task<(QgcExecutionResult Result, bool ShouldStop)> RunSingleQgcAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct)
    {
        var compilationResult = await RunQgcCompilationAsync(workspacePath, qgc, ct);

        if (compilationResult is { Passed: false })
        {
            return (new QgcExecutionResult
            {
                QgcId = qgc.Id,
                DisplayName = qgc.DisplayName,
                Compilation = compilationResult,
                Tests = null,
                SecurityScan = null
            }, true);
        }

        var testsResult = await RunQgcTestsAsync(workspacePath, qgc, ct);

        if (testsResult is { Passed: false })
        {
            return (new QgcExecutionResult
            {
                QgcId = qgc.Id,
                DisplayName = qgc.DisplayName,
                Compilation = compilationResult,
                Tests = testsResult,
                SecurityScan = null
            }, true);
        }

        return (new QgcExecutionResult
        {
            QgcId = qgc.Id,
            DisplayName = qgc.DisplayName,
            Compilation = compilationResult,
            Tests = testsResult,
            SecurityScan = null
        }, false);
    }

    /// <summary>
    /// Builds the aggregate <see cref="QualityGateReport"/> from individual QGC execution results.
    /// </summary>
    private static QualityGateReport BuildAggregateReport(List<QgcExecutionResult> qgcResults)
    {
        // Build aggregate flat fields for backward compatibility
        var allCompilationsPassed = qgcResults.All(r => r.Compilation?.Passed ?? true);
        var allTestsPassed = qgcResults.All(r => r.Tests?.Passed ?? true);
        var firstFailingQgc = qgcResults.FirstOrDefault(r => !r.Passed);

        var aggregateCompilation = new GateResult
        {
            GateName = "Compilation",
            Passed = allCompilationsPassed,
            Details = allCompilationsPassed
                ? "All QGC compilations passed"
                : $"Compilation failed in QGC '{firstFailingQgc?.DisplayName}'"
        };

        var totalTestsPassed = qgcResults.Sum(r => r.Tests?.TestsPassed ?? 0);
        var totalTestsFailed = qgcResults.Sum(r => r.Tests?.TestsFailed ?? 0);
        var totalTestsSkipped = qgcResults.Sum(r => r.Tests?.TestsSkipped ?? 0);

        var testsDetails = allTestsPassed
            ? $"All QGC tests passed: {totalTestsPassed} passed, {totalTestsFailed} failed, {totalTestsSkipped} skipped"
            : $"Tests failed in QGC '{firstFailingQgc?.DisplayName}'";

        var aggregateTests = new GateResult
        {
            GateName = "Tests",
            Passed = allTestsPassed,
            Details = testsDetails,
            TestsPassed = totalTestsPassed,
            TestsFailed = totalTestsFailed,
            TestsSkipped = totalTestsSkipped
        };

        return new QualityGateReport
        {
            Compilation = aggregateCompilation,
            Tests = aggregateTests,
            SecurityScan = null,
            QgcResults = qgcResults
        };
    }

    /// <summary>
    /// Runs the compilation command for a single QGC. Returns null if no compilation command is defined.
    /// </summary>
    private async Task<GateResult?> RunQgcCompilationAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qgc.CompilationCommand))
            return null;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("QualityGate.Compilation");
        activity?.SetTag("gate_name", "compilation");

        try
        {
            var arguments = qgc.CompilationArguments != null
                ? string.Join(" ", qgc.CompilationArguments)
                : string.Empty;

            var timeout = TimeSpan.FromSeconds(qgc.ProcessTimeoutSeconds);
            var (exitCode, stdout, stderr) = await RunProcessAsync(
                qgc.CompilationCommand, arguments, workspacePath, ct, timeout);

            WriteGateOutput(workspacePath, $"{qgc.DisplayName}-compilation", stdout, stderr);

            string details;
            if (exitCode == ExitCodes.Success)
            {
                details = "Build succeeded";
            }
            else
            {
                var (errors, warnings) = ParseBuildErrorCounts(stdout + "\n" + stderr);
                details = $"Build failed with exit code {exitCode}. {errors} error(s), {warnings} warning(s).";
            }

            return new GateResult
            {
                GateName = "Compilation",
                Passed = exitCode == ExitCodes.Success,
                Details = details
            };
        }
        catch (TimeoutException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new GateResult
            {
                GateName = "Compilation",
                Passed = false,
                Details = $"Compilation timed out after {qgc.ProcessTimeoutSeconds}s"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }

    /// <summary>
    /// Runs the test command for a single QGC. Returns null if no test command is defined.
    /// Only appends .NET-specific flags (--logger trx, --results-directory, --collect) when
    /// the test command is "dotnet". For other languages (python, mvn, etc.), the test arguments
    /// are used as-is and test counts are parsed from stdout.
    /// </summary>
    private async Task<GateResult?> RunQgcTestsAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qgc.TestCommand))
            return null;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("QualityGate.Tests");
        activity?.SetTag("gate_name", "tests");

        var arguments = qgc.TestArguments != null
            ? string.Join(" ", qgc.TestArguments)
            : string.Empty;

        var isDotnet = string.Equals(qgc.TestCommand, "dotnet", StringComparison.OrdinalIgnoreCase);
        string? resultsDir = null;
        string fullArgs;

        if (isDotnet)
        {
            // .NET: Add TRX logger and results directory for test result parsing
            resultsDir = Path.GetFullPath(Path.Combine(workspacePath, "TestResults", $"qg-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(resultsDir);

            fullArgs = $"{arguments} --logger trx --results-directory \"{resultsDir}\"";
        }
        else
        {
            // Non-.NET: Use test arguments as-is (coverage flags should be in TestArguments)
            fullArgs = arguments;
        }

        int exitCode;
        string stdout, stderr;
        try
        {
            (exitCode, stdout, stderr) = await RunProcessAsync(
                qgc.TestCommand, fullArgs, workspacePath, ct, TimeSpan.FromSeconds(qgc.ProcessTimeoutSeconds));
        }
        catch (TimeoutException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new GateResult
            {
                GateName = "Tests",
                Passed = false,
                Details = $"Tests timed out after {qgc.ProcessTimeoutSeconds}s"
            };
        }

        WriteGateOutput(workspacePath, $"{qgc.DisplayName}-tests", stdout, stderr);

        var (passed, failed, skipped) = ResolveTestCounts(isDotnet, resultsDir, qgc, stdout);

        _logger.Information("QGC {QgcName} test results: {Passed} passed, {Failed} failed, {Skipped} skipped",
            qgc.DisplayName, passed, failed, skipped);

        var gatePassed = exitCode == ExitCodes.Success;

        // Clean up results directory (non-fatal)
        if (isDotnet && resultsDir != null)
            TryDeleteResultsDirectory(resultsDir);

        var details = gatePassed
            ? $"Tests passed: {passed} passed, {failed} failed, {skipped} skipped"
            : $"Tests failed: {passed} passed, {failed} failed, {skipped} skipped.";

        return new GateResult
        {
            GateName = "Tests",
            Passed = gatePassed,
            Details = details,
            TestsPassed = passed,
            TestsFailed = failed,
            TestsSkipped = skipped
        };
    }

    /// <summary>
    /// Resolves test counts from TRX files (for .NET) or stdout parsing (for other stacks).
    /// Falls back to stdout parsing when TRX files are missing or empty.
    /// TODO: [WARNING] No test covers the TRX-parse-found-nothing → stdout-fallback path after extraction.
    /// Add a test with an empty TRX results directory to assert stdout-based counts are returned,
    /// locking in the fallback behavior and preventing silent regression if the condition changes.
    /// </summary>
    private (int Passed, int Failed, int Skipped) ResolveTestCounts(
        bool isDotnet, string? resultsDir, QualityGateConfiguration qgc, string stdout)
    {
        if (isDotnet && resultsDir != null)
        {
            var trxResult = TrxTestResultParser.ParseTestResults(resultsDir);
            if (trxResult.Passed != 0 || trxResult.Failed != 0 || trxResult.Skipped != 0)
                return (trxResult.Passed, trxResult.Failed, trxResult.Skipped);

            _logger.Warning("No TRX results found in {ResultsDir} for QGC {QgcName}, falling back to stdout parsing",
                resultsDir, qgc.DisplayName);
        }

        return ParseTestCountsFromStdout(stdout);
    }

    /// <summary>
    /// Deletes a test results directory, logging (non-fatal) on failure.
    /// </summary>
    private void TryDeleteResultsDirectory(string resultsDir)
    {
        try
        {
            if (Directory.Exists(resultsDir))
                Directory.Delete(resultsDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to clean up test results directory {ResultsDir}", resultsDir);
        }
    }

    /// <summary>
    /// Formats a short CI failure summary for GateResult.Details.
    /// Verbose per-job logs are in .agent/quality-gates/ — the retry prompt points there.
    /// </summary>
    internal static string BuildCiFailureDetails(
        PipelineRunStatus status, IReadOnlyDictionary<long, string>? logPathMapping = null)
    {
        var failedJobs = status.Jobs.Where(j => j.State == PipelineRunState.Failed).ToList();
        var jobNames = failedJobs.Count > 0
            ? string.Join(", ", failedJobs.Select(j => $"'{j.Name}'"))
            : "unknown";
        return $"CI {status.State}. {failedJobs.Count} job(s) failed: {jobNames}.";
    }

    /// <summary>
    /// Parses all .trx files in the results directory and sums up test counts across all assemblies.
    /// TRX files contain a ResultSummary/Counters element with total/passed/failed/etc attributes.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromTrx(string resultsDir)
    {
        var result = TrxTestResultParser.ParseTestResults(resultsDir);
        return (result.Passed, result.Failed, result.Skipped);
    }

    /// <summary>
    /// Fallback: parses test counts from stdout when TRX files are not available.
    /// Handles .NET per-assembly format, .NET 10 summary line, pytest output, and Maven/JUnit output.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromStdout(string output)
        => StdoutTestResultParser.ParseTestCounts(output);

    /// <summary>
    /// Writes gate stdout/stderr to .agent/quality-gates/{gateName}-stdout.txt and
    /// {gateName}-stderr.txt so the agent can read them on demand.
    /// </summary>
    private void WriteGateOutput(string workspacePath, string gateName, string? stdout, string? stderr)
    {
        try
        {
            var dir = Path.Combine(workspacePath, AgentWorkspacePaths.QualityGatesOutputDirectory);
            Directory.CreateDirectory(dir);
            if (!string.IsNullOrEmpty(stdout))
                File.WriteAllText(Path.Combine(dir, $"{gateName}-stdout.txt"), stdout);
            if (!string.IsNullOrEmpty(stderr))
                File.WriteAllText(Path.Combine(dir, $"{gateName}-stderr.txt"), stderr);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to write quality gate output for {GateName}", gateName);
        }
    }

    /// <summary>
    /// Parses error and warning counts from MSBuild output.
    /// Looks for the summary line pattern: "X Error(s)" and "Y Warning(s)".
    /// </summary>
    internal static (int Errors, int Warnings) ParseBuildErrorCounts(string output)
        => BuildOutputParser.ParseBuildErrorCounts(output);

    private protected virtual async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Do not pass ct to ReadToEndAsync — cancellation is handled by killing the process (which
        // closes the pipes and causes ReadToEndAsync to complete). Passing ct would make the drain
        // in the external cancellation path a no-op since ct is already cancelled there.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* Intentional: best-effort kill; process may have already exited. */ }
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(stdoutTask.WaitAsync(drainCts.Token), stderrTask.WaitAsync(drainCts.Token)); } catch { /* Intentional: best-effort pipe drain after timeout kill; partial output is acceptable. */ }
            throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds}s");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* Intentional: best-effort kill; process may have already exited. */ }
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(stdoutTask.WaitAsync(drainCts.Token), stderrTask.WaitAsync(drainCts.Token)); } catch { /* Intentional: best-effort pipe drain after cancellation kill; partial output is acceptable. */ }
            throw;
        }

        // Bound the pipe drain to prevent indefinite hang if a grandchild process inherits
        // stdout/stderr handles and outlives the parent.
        string stdout, stderr;
        using (var pipeDrainCts = new CancellationTokenSource(PipeDrainTimeout))
        {
            try
            {
                var results = await Task.WhenAll(
                    stdoutTask.WaitAsync(pipeDrainCts.Token),
                    stderrTask.WaitAsync(pipeDrainCts.Token));
                stdout = results[0];
                stderr = results[1];
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("Pipe drain timed out after {TimeoutSeconds}s for process that exited with code {ExitCode}; output may be incomplete",
                    PipeDrainTimeout.TotalSeconds, process.ExitCode);
                stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
                stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            }
        }

        return (process.ExitCode, stdout, stderr);
    }
}
