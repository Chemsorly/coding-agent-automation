using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Validates generated code against quality thresholds by running
/// dotnet build and dotnet test in the workspace directory.
/// Uses TRX and Cobertura XML reports for accurate test/coverage data.
/// Optionally validates against an external CI/CD pipeline.
/// </summary>
public class QualityGateValidator : IQualityGateValidator
{
    private readonly Serilog.ILogger _logger;

    public QualityGateValidator(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    public virtual async Task<QualityGateReport> ValidateAsync(
        string workspacePath, PipelineConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(config);

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

        var compilation = await RunCompilationGateAsync(workspacePath, ct);

        // Use a dedicated results directory — must be absolute so dotnet test
        // (which runs with WorkingDirectory=workspacePath) writes to the correct location
        var resultsDir = Path.GetFullPath(Path.Combine(workspacePath, "TestResults", $"qg-{Guid.NewGuid():N}"));
        var collectCoverage = config.MinCoverageThreshold > 0;

        var tests = await RunTestGateAsync(workspacePath, resultsDir, collectCoverage, ct);

        GateResult? coverage = null;
        if (collectCoverage)
        {
            coverage = ParseCoverageFromReports(resultsDir, config.MinCoverageThreshold);
        }

        GateResult? securityScan = null;
        if (config.SecurityScanEnabled)
        {
            _logger.Warning("Security scan not configured — returning passed as placeholder");
            securityScan = new GateResult
            {
                GateName = "Security Scan",
                Passed = true,
                Details = "Security scan not configured"
            };
        }

        // External CI gate is handled by PipelineOrchestrationService after local gates pass

        // Clean up results directory (non-fatal)
        try
        {
            if (Directory.Exists(resultsDir))
                Directory.Delete(resultsDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to clean up test results directory {ResultsDir}", resultsDir);
        }

        return new QualityGateReport
        {
            Compilation = compilation,
            Tests = tests,
            Coverage = coverage,
            SecurityScan = securityScan
        };
    }

    /// <summary>
    /// Formats CI failure details for display in quality gate error summaries.
    /// References log file paths when available so the agent can read them on demand.
    /// </summary>
    internal static string BuildCiFailureDetails(PipelineRunStatus status)
    {
        var lines = new List<string> { $"CI {status.State}." };

        var failedJobs = status.Jobs.Where(j => j.State == PipelineRunState.Failed).ToList();
        if (failedJobs.Count > 0)
        {
            lines.Add($"{failedJobs.Count} job(s) failed:");
            foreach (var job in failedJobs)
            {
                var reason = !string.IsNullOrEmpty(job.FailureReason) ? $" — {job.FailureReason}" : "";
                var logLink = !string.IsNullOrEmpty(job.LogUrl) ? $" (logs: {job.LogUrl})" : "";
                lines.Add($"  - {job.Name}{reason}{logLink}");

                if (!string.IsNullOrEmpty(job.LogFilePath))
                {
                    lines.Add($"    Full CI log saved to: {job.LogFilePath}");
                    lines.Add($"    Read this file (raw GitHub Actions job log) to diagnose the failure.");
                }
            }
        }

        if (!string.IsNullOrEmpty(status.Url))
            lines.Add($"Full run: {status.Url}");

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<GateResult> RunCompilationGateAsync(string workspacePath, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunProcessAsync("dotnet", "build", workspacePath, ct);

        return new GateResult
        {
            GateName = "Compilation",
            Passed = exitCode == 0,
            Details = exitCode == 0
                ? "Build succeeded"
                : $"Build failed with exit code {exitCode}: {stderr}"
        };
    }

    private async Task<GateResult> RunTestGateAsync(
        string workspacePath, string resultsDir, bool collectCoverage, CancellationToken ct)
    {
        // Ensure the results directory exists before running tests
        Directory.CreateDirectory(resultsDir);

        var args = $"test --logger trx --results-directory \"{resultsDir}\"";
        if (collectCoverage)
            args += " --collect:\"XPlat Code Coverage\"";

        _logger.Debug("Running tests: dotnet {Args} in {WorkspacePath}", args, workspacePath);

        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", args, workspacePath, ct);

        _logger.Debug("Test results directory contents: {Files}",
            Directory.Exists(resultsDir)
                ? string.Join(", ", Directory.GetFiles(resultsDir, "*", SearchOption.AllDirectories))
                : "DIRECTORY NOT FOUND");

        // Parse TRX files for accurate test counts
        var (passed, failed, skipped) = ParseTestCountsFromTrx(resultsDir);

        // If TRX parsing found nothing, fall back to stdout parsing
        if (passed == 0 && failed == 0 && skipped == 0)
        {
            _logger.Warning("No TRX results found in {ResultsDir}, falling back to stdout parsing", resultsDir);
            (passed, failed, skipped) = ParseTestCountsFromStdout(stdout);
        }

        _logger.Information("Test results: {Passed} passed, {Failed} failed, {Skipped} skipped",
            passed, failed, skipped);

        return new GateResult
        {
            GateName = "Tests",
            Passed = exitCode == 0,
            Details = exitCode == 0
                ? $"Tests passed: {passed} passed, {failed} failed, {skipped} skipped"
                : $"Tests failed: {passed} passed, {failed} failed, {skipped} skipped. {stderr}",
            TestsPassed = passed,
            TestsFailed = failed,
            TestsSkipped = skipped
        };
    }

    /// <summary>
    /// Parses all .trx files in the results directory and sums up test counts across all assemblies.
    /// TRX files contain a ResultSummary/Counters element with total/passed/failed/etc attributes.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromTrx(string resultsDir)
    {
        if (!Directory.Exists(resultsDir))
            return (0, 0, 0);

        var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);
        if (trxFiles.Length == 0)
            return (0, 0, 0);

        var totalPassed = 0;
        var totalFailed = 0;
        var totalSkipped = 0;

        foreach (var trxFile in trxFiles)
        {
            try
            {
                var doc = XDocument.Load(trxFile);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
                if (counters == null) continue;

                totalPassed += ParseIntAttribute(counters, "passed");
                totalFailed += ParseIntAttribute(counters, "failed") + ParseIntAttribute(counters, "error");
                totalSkipped += ParseIntAttribute(counters, "notExecuted");
            }
            catch
            {
                // Skip malformed TRX files
            }
        }

        return (totalPassed, totalFailed, totalSkipped);
    }

    /// <summary>
    /// Parses Cobertura XML coverage reports from the results directory.
    /// Returns a GateResult with the aggregate line coverage percentage.
    /// </summary>
    private GateResult ParseCoverageFromReports(string resultsDir, double threshold)
    {
        if (!Directory.Exists(resultsDir))
        {
            _logger.Warning("Results directory {ResultsDir} not found for coverage parsing", resultsDir);
            return new GateResult
            {
                GateName = "Coverage",
                Passed = false,
                Details = "No coverage data — results directory not found",
                CoveragePercent = null
            };
        }

        var coberturaFiles = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
        if (coberturaFiles.Length == 0)
        {
            _logger.Warning("No Cobertura coverage files found in {ResultsDir}", resultsDir);
            return new GateResult
            {
                GateName = "Coverage",
                Passed = false,
                Details = "No coverage data — Cobertura XML not found",
                CoveragePercent = null
            };
        }

        var coveragePercent = ParseCoverageFromCobertura(coberturaFiles);

        _logger.Information("Coverage: {CoveragePercent:F1}% (threshold: {Threshold:F1}%)",
            coveragePercent, threshold);

        var passed = coveragePercent >= threshold;
        return new GateResult
        {
            GateName = "Coverage",
            Passed = passed,
            Details = passed
                ? $"Coverage {coveragePercent:F1}% meets threshold {threshold:F1}%"
                : $"Coverage {coveragePercent:F1}% below threshold {threshold:F1}%",
            CoveragePercent = coveragePercent
        };
    }

    /// <summary>
    /// Parses Cobertura XML files and returns the weighted average line-rate across all reports.
    /// Each coverage element has a line-rate attribute (0.0 to 1.0) and lines-valid count.
    /// </summary>
    internal static double ParseCoverageFromCobertura(string[] coberturaFiles)
    {
        var totalLines = 0L;
        var coveredLines = 0L;

        foreach (var file in coberturaFiles)
        {
            try
            {
                var doc = XDocument.Load(file);
                var coverage = doc.Root;
                if (coverage == null) continue;

                var lineRate = ParseDoubleAttribute(coverage, "line-rate");
                var linesValid = ParseLongAttribute(coverage, "lines-valid");
                var linesCovered = ParseLongAttribute(coverage, "lines-covered");

                // Prefer the explicit lines-covered attribute; fall back to computing from line-rate
                if (linesValid > 0)
                {
                    totalLines += linesValid;
                    coveredLines += linesCovered > 0 ? linesCovered : (long)(lineRate * linesValid);
                }
            }
            catch
            {
                // Skip malformed files
            }
        }

        return totalLines > 0 ? (double)coveredLines / totalLines * 100.0 : 0.0;
    }

    /// <summary>
    /// Fallback: parses test counts from stdout when TRX files are not available.
    /// Handles both the per-assembly format and the .NET 10 summary line.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromStdout(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0, 0);

        // Try .NET 10 summary line first: "Test summary: total: 47; failed: 0; succeeded: 47; skipped: 0"
        var summaryMatch = System.Text.RegularExpressions.Regex.Match(output,
            @"Test summary:.*?failed:\s*(\d+).*?succeeded:\s*(\d+).*?skipped:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (summaryMatch.Success)
        {
            int.TryParse(summaryMatch.Groups[2].Value, out var succeeded);
            int.TryParse(summaryMatch.Groups[1].Value, out var summaryFailed);
            int.TryParse(summaryMatch.Groups[3].Value, out var summarySkipped);
            return (succeeded, summaryFailed, summarySkipped);
        }

        // Fall back to summing per-assembly lines: "Passed:  10, Failed:   2, Skipped:   1"
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(output,
            @"Passed:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Cast<System.Text.RegularExpressions.Match>())
        {
            if (int.TryParse(match.Groups[1].Value, out var p)) passed += p;
            if (int.TryParse(match.Groups[2].Value, out var f)) failed += f;
            if (int.TryParse(match.Groups[3].Value, out var s)) skipped += s;
        }

        return (passed, failed, skipped);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken ct)
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private static int ParseIntAttribute(XElement element, string name)
    {
        var attr = element.Attribute(name);
        return attr != null && int.TryParse(attr.Value, out var val) ? val : 0;
    }

    private static long ParseLongAttribute(XElement element, string name)
    {
        var attr = element.Attribute(name);
        return attr != null && long.TryParse(attr.Value, out var val) ? val : 0;
    }

    private static double ParseDoubleAttribute(XElement element, string name)
    {
        var attr = element.Attribute(name);
        return attr != null && double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : 0.0;
    }
}
