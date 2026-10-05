using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Parsers;
using CodingAgent.Pipeline.Telemetry;
using KiroCliLib.Core;
using OpenTelemetry.Trace;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Validates generated code against quality thresholds by running
/// dotnet build and dotnet test in the workspace directory.
/// Uses TRX reports for accurate test data.
/// Optionally validates against an external CI/CD pipeline.
/// </summary>
public class QualityGateValidator : IQualityGateValidator
{
    private readonly Serilog.ILogger _logger;

    // Attribute names on the QualityGate.Compilation / QualityGate.Tests spans.
    private const string TagGateName = "gate_name";
    private const string TagQgcName = "qgc_name";

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
        WorkspacePath workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs, CancellationToken ct, string? baseBranch = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value, nameof(workspacePath));
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
            var (result, shouldStop) = await RunSingleQgcAsync(workspacePath, qgc, ct, reportEvent: null);
            qgcResults.Add(result);
            if (shouldStop)
                break;
        }

        return BuildAggregateReport(qgcResults);
    }

    /// <summary>
    /// Same as <see cref="ValidateAsync"/> but also fires a server-side
    /// <c>process_timeout</c> event via <paramref name="reportEvent"/> when a QGC process
    /// exceeds its timeout (issue #2979). Called by <see cref="QualityGateExecutor"/> when a
    /// <see cref="Models.QualityGateContext.ReportPipelineRunEvent"/> delegate is wired.
    /// </summary>
    // TODO [WARNING]: This method duplicates the full directory-cleanup prologue (TestResults +
    // QualityGatesOutputDirectory deletion) from ValidateAsync. If the cleanup logic in ValidateAsync
    // is changed (e.g. new directories added), this method will silently diverge. Refactor to extract
    // the cleanup into a shared private helper (e.g. CleanWorkspacePrologueAsync) used by both paths,
    // or add an optional reportEvent parameter to ValidateAsync and delegate the cleanup from there.
    // (DotNetSpecialist #2979)
    internal async Task<QualityGateReport> ValidateWithServerSideReportingAsync(
        WorkspacePath workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs,
        CancellationToken ct, Action<PipelineRunEventReport> reportEvent, string? baseBranch = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value, nameof(workspacePath));
        ArgumentNullException.ThrowIfNull(qualityGateConfigs);
        ArgumentNullException.ThrowIfNull(reportEvent);

        // Reuse cleanup logic by delegating to the base method would duplicate cleanup;
        // instead call directly to avoid duplication.
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
            var (result, shouldStop) = await RunSingleQgcAsync(workspacePath, qgc, ct, reportEvent);
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
    // TODO [WARNING]: WorkspacePath is implicitly converted to string when passed into RunSingleQgcAsync
    // and all further private helpers (RunQgcCompilationAsync, RunQgcTestsAsync, etc.). The WorkspacePath
    // value type is accepted at the public surface (ValidateAsync) but not carried through the call chain,
    // so the validated-absolute-path invariant is lost at the first private boundary. Consider propagating
    // WorkspacePath through the private helpers or at minimum documenting that the implicit conversion is
    // intentional and safe here.
    private async Task<(QgcExecutionResult Result, bool ShouldStop)> RunSingleQgcAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct,
        Action<PipelineRunEventReport>? reportEvent)
    {
        var compilationResult = await RunQgcCompilationAsync(workspacePath, qgc, ct, reportEvent);

        if (compilationResult is { Passed: false })
        {
            return (new QgcExecutionResult
            {
                QgcId = qgc.Id,
                DisplayName = qgc.DisplayName,
                Compilation = compilationResult,
                Tests = null
            }, true);
        }

        var testsResult = await RunQgcTestsAsync(workspacePath, qgc, ct, reportEvent);

        if (testsResult is { Passed: false })
        {
            return (new QgcExecutionResult
            {
                QgcId = qgc.Id,
                DisplayName = qgc.DisplayName,
                Compilation = compilationResult,
                Tests = testsResult
            }, true);
        }

        return (new QgcExecutionResult
        {
            QgcId = qgc.Id,
            DisplayName = qgc.DisplayName,
            Compilation = compilationResult,
            Tests = testsResult
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
            TestsSkipped = totalTestsSkipped,
            // TODO [WARNING]: IsInfrastructureFailure is populated from firstFailingQgc?.Tests?.IsInfrastructureFailure,
            // where firstFailingQgc is the first QGC whose overall Passed==false. If the first failing QGC has a
            // compilation failure but no test command (Tests==null), the null-conditional resolves to null even when
            // a later QGC has an infra-kill Tests result. The retry-loop call site compensates by also checking
            // report.QgcResults.Any(r => r.Tests?.IsInfrastructureFailure == true), so the retry prompt is
            // correct — but the aggregate Tests.IsInfrastructureFailure field itself is misleading to any consumer
            // that reads it directly (e.g. history recording, future callers). Fix: align BuildAggregateReport
            // to use Any() over QgcResults, matching the retry-loop derivation:
            //   IsInfrastructureFailure = qgcResults.Any(r => r.Tests?.IsInfrastructureFailure == true) ? true : null
            // See review finding: Correctness WARNING — QualityGateValidator.cs BuildAggregateReport
            IsInfrastructureFailure = firstFailingQgc?.Tests?.IsInfrastructureFailure
        };

        return new QualityGateReport
        {
            Compilation = aggregateCompilation,
            Tests = aggregateTests,
            QgcResults = qgcResults
        };
    }

    /// <summary>
    /// Runs a QGC process command with shared timeout/cancellation/error handling on the gate span.
    /// Returns <c>(exitCode, stdout, stderr)</c> on success.
    /// Re-throws <see cref="OperationCanceledException"/> and unexpected exceptions unchanged;
    /// converts a process timeout into a <see cref="QgcProcessTimedOutException"/> so callers
    /// can distinguish it from external cancellation without duplicating catch blocks.
    /// </summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunQgcProcessAsync(
        string command, string arguments, QgcProcessContext ctx, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(ctx.TimeoutSeconds);
        try
        {
            return await RunProcessAsync(command, arguments, ctx.WorkspacePath, ct, timeout);
        }
        catch (TimeoutException ex)
        {
            ctx.Activity?.SetTag("qgc.timed_out", true);
            ctx.Activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            // Report process_timeout stall event server-side (issue #2979).
            ctx.ReportPipelineRunEvent?.Invoke(new PipelineRunEventReport
            {
                Kind = PipelineRunEventKind.AgentStall,
                Stage = PipelineTelemetry.RunPhases.QualityGate,
                Result = PipelineTelemetry.AgentStallKinds.ProcessTimeout
            });

            throw new QgcProcessTimedOutException(ctx.TimeoutSeconds, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ctx.Activity?.AddException(ex);
            throw;
        }
    }

    /// <summary>
    /// Contextual parameters for a single <see cref="RunQgcProcessAsync"/> invocation:
    /// workspace path, timeout, and the gate's tracing activity.
    /// </summary>
    private sealed record QgcProcessContext(
        string WorkspacePath,
        int TimeoutSeconds,
        Activity? Activity)
    {
        /// <summary>
        /// Optional delegate to report a <c>process_timeout</c> stall event server-side (issue #2979).
        /// When non-null, called with the normalized phase when <see cref="RunQgcProcessAsync"/> detects
        /// a process timeout, so the API can record <c>pipeline.run.agent_stalls</c> with
        /// <c>kind=process_timeout</c>.
        /// </summary>
        public Action<PipelineRunEventReport>? ReportPipelineRunEvent { get; init; }
    }

    /// <summary>Thrown by <see cref="RunQgcProcessAsync"/> when the process exceeds its timeout.</summary>
    // TODO [WARNING]: This class was changed from private to public. It is an internal implementation
    // detail of QualityGateValidator and is not referenced outside the file. Exposing it as public
    // widens the API surface unnecessarily and may encourage callers in other assemblies to catch it
    // by type, creating coupling to an internal timeout protocol. Consider reverting to internal (with
    // InternalsVisibleTo for tests) rather than public.
    // See review finding: DotNetSpecialist WARNING — QualityGateValidator.cs QgcProcessTimedOutException
    public sealed class QgcProcessTimedOutException(int timeoutSeconds, Exception inner)
        : Exception($"Process timed out after {timeoutSeconds}s", inner)
    {
        public int TimeoutSeconds { get; } = timeoutSeconds;
    }

    /// <summary>
    /// Runs the compilation command for a single QGC. Returns null if no compilation command is defined.
    /// </summary>
    private async Task<GateResult?> RunQgcCompilationAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct,
        Action<PipelineRunEventReport>? reportEvent = null)
    {
        if (string.IsNullOrWhiteSpace(qgc.CompilationCommand))
            return null;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("QualityGate.Compilation");
        activity?.SetTag(TagGateName, "compilation");
        activity?.SetTag(TagQgcName, qgc.DisplayName);
        activity?.SetTag("qgc.timeout_seconds", qgc.ProcessTimeoutSeconds);

        var arguments = qgc.CompilationArguments != null
            ? string.Join(" ", qgc.CompilationArguments)
            : string.Empty;

        int exitCode;
        string stdout, stderr;
        try
        {
            (exitCode, stdout, stderr) = await RunQgcProcessAsync(
                qgc.CompilationCommand, arguments,
                new QgcProcessContext(workspacePath, qgc.ProcessTimeoutSeconds, activity)
                {
                    ReportPipelineRunEvent = reportEvent
                },
                ct);
        }
        catch (QgcProcessTimedOutException ex)
        {
            return new GateResult
            {
                GateName = "Compilation",
                Passed = false,
                Details = $"Compilation timed out after {ex.TimeoutSeconds}s"
            };
        }

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

    /// <summary>
    /// Runs the test command for a single QGC. Returns null if no test command is defined.
    /// Only appends .NET-specific flags (--logger trx, --results-directory, --collect) when
    /// the test command is "dotnet". For other languages (python, mvn, etc.), the test arguments
    /// are used as-is and test counts are parsed from stdout.
    /// </summary>
    private async Task<GateResult?> RunQgcTestsAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct,
        Action<PipelineRunEventReport>? reportEvent = null)
    {
        if (string.IsNullOrWhiteSpace(qgc.TestCommand))
            return null;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("QualityGate.Tests");
        activity?.SetTag(TagGateName, "tests");
        activity?.SetTag(TagQgcName, qgc.DisplayName);
        activity?.SetTag("qgc.timeout_seconds", qgc.ProcessTimeoutSeconds);

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
            (exitCode, stdout, stderr) = await RunQgcProcessAsync(
                qgc.TestCommand, fullArgs,
                new QgcProcessContext(workspacePath, qgc.ProcessTimeoutSeconds, activity)
                {
                    ReportPipelineRunEvent = reportEvent
                },
                ct);
        }
        catch (QgcProcessTimedOutException ex)
        {
            return new GateResult
            {
                GateName = "Tests",
                Passed = false,
                Details = $"Tests timed out after {ex.TimeoutSeconds}s"
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

        // Infra-kill heuristic: if the process exited non-zero but produced no output at all
        // (both stdout and stderr empty) and no TRX files were written, this is characteristic
        // of an OOM kill (SIGKILL/exit 137), a container resource limit, or a pipe-drain timeout —
        // not a real test failure. A genuine test failure always produces at least one count in
        // the TRX or stdout. Both streams must be empty to avoid misclassifying failures where
        // stderr contains a diagnosable error (e.g. missing SDK, wrong test project path).
        // NOTE [WARNING]: The heuristic does not explicitly verify that no TRX files exist. The
        // issue requirement states "no TRX files were written" as a formal precondition. In most
        // cases this is implicitly satisfied: ResolveTestCounts (called above) extracts counts from
        // TRX files, so a TRX with non-zero counts would already set passed/failed/skipped > 0 and
        // prevent the heuristic from firing. However, a partially written or malformed TRX with
        // zero counts (e.g., test host wrote the XML header before OOM) could produce all-zero
        // counts while a file exists on disk, causing a false infra-kill classification.
        // TryDeleteResultsDirectory runs before this point, cleaning up prior-run leftovers, but
        // current-run partial TRX files are not removed. An explicit check on the results directory
        // would fully match the stated precondition.
        var isInfraFailure = !gatePassed
            && passed == 0 && failed == 0 && skipped == 0
            && string.IsNullOrWhiteSpace(stdout)
            && string.IsNullOrWhiteSpace(stderr);

        string details;
        if (gatePassed)
            details = $"Tests passed: {passed} passed, {failed} failed, {skipped} skipped";
        else if (isInfraFailure)
            details = $"Tests failed: process exited with code {exitCode} — no test output produced (probable infrastructure failure, not a code failure)";
        else
            details = $"Tests failed: {passed} passed, {failed} failed, {skipped} skipped.";

        return new GateResult
        {
            GateName = "Tests",
            Passed = gatePassed,
            Details = details,
            TestsPassed = passed,
            TestsFailed = failed,
            TestsSkipped = skipped,
            IsInfrastructureFailure = isInfraFailure ? true : null
        };
    }

    /// <summary>
    /// Resolves test counts from TRX files (for .NET) or stdout parsing (for other stacks).
    /// Falls back to stdout parsing when TRX files are missing or empty.
    /// NOTE [WARNING]: No test covers the TRX-parse-found-nothing → stdout-fallback path after extraction.
    /// A test with an empty TRX results directory should be added to assert stdout-based counts are returned,
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
        // When the overall run was Cancelled (e.g. by concurrency:cancel-in-progress), dependent
        // jobs cascade to Failure conclusion even though no code failed. Do not report those as
        // "failed jobs" — they are artefacts of the cancellation, not real code failures.
        var failedJobs = status.State == PipelineRunState.Cancelled
            ? new List<PipelineJobResult>()
            : status.Jobs.Where(j => j.State == PipelineRunState.Failed).ToList();
        var cancelledJobs = status.Jobs.Where(j => j.State == PipelineRunState.Cancelled).ToList();

        var details = new System.Text.StringBuilder($"CI {status.State}.");
        if (failedJobs.Count > 0 || (cancelledJobs.Count == 0 && status.State != PipelineRunState.Cancelled))
        {
            var jobNames = failedJobs.Count > 0
                ? string.Join(", ", failedJobs.Select(j => $"'{j.Name}'"))
                : "unknown";
            details.Append($" {failedJobs.Count} job(s) failed: {jobNames}.");
        }
        if (cancelledJobs.Count > 0)
        {
            var jobNames = string.Join(", ", cancelledJobs.Select(j => $"'{j.Name}'"));
            details.Append($" {cancelledJobs.Count} job(s) cancelled before finishing: {jobNames}.");
            details.Append(" A cancelled job usually exceeded its timeout or hung on a test; its log shows how far it got.");
        }
        return details.ToString();
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
        ChildProcessEnvironment.StripTelemetry(psi);

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
            catch (OperationCanceledException ex)
            {
                _logger.Warning(ex, "Pipe drain timed out after {TimeoutSeconds}s for process that exited with code {ExitCode}; output may be incomplete",
                    PipeDrainTimeout.TotalSeconds, process.ExitCode);
                stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
                stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            }
        }

        return (process.ExitCode, stdout, stderr);
    }
}
