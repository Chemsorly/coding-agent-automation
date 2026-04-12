using System.Diagnostics;
using System.Text.RegularExpressions;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Validates generated code against quality thresholds by running
/// dotnet build and dotnet test in the workspace directory.
/// Limitation: hardcoded to dotnet build/test commands (PoC).
/// </summary>
public partial class QualityGateValidator
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

        var compilation = await RunCompilationGateAsync(workspacePath, ct);
        var tests = await RunTestGateAsync(workspacePath, ct);

        GateResult? coverage = null;
        if (config.MinCoverageThreshold > 0)
        {
            coverage = new GateResult
            {
                GateName = "Coverage",
                Passed = true,
                Details = "Coverage measurement not available in PoC"
            };
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

        return new QualityGateReport
        {
            Compilation = compilation,
            Tests = tests,
            Coverage = coverage,
            SecurityScan = securityScan
        };
    }

    private async Task<GateResult> RunCompilationGateAsync(string workspacePath, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", "build --no-restore", workspacePath, ct);

        return new GateResult
        {
            GateName = "Compilation",
            Passed = exitCode == 0,
            Details = exitCode == 0
                ? "Build succeeded"
                : $"Build failed with exit code {exitCode}: {stderr}"
        };
    }

    private async Task<GateResult> RunTestGateAsync(string workspacePath, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", "test --no-build", workspacePath, ct);

        var (passed, failed, skipped) = ParseTestCounts(stdout);

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

    [GeneratedRegex(@"Passed:\s*(\d+)")]
    private static partial Regex PassedPattern();

    [GeneratedRegex(@"Failed:\s*(\d+)")]
    private static partial Regex FailedPattern();

    [GeneratedRegex(@"Skipped:\s*(\d+)")]
    private static partial Regex SkippedPattern();

    internal static (int Passed, int Failed, int Skipped) ParseTestCounts(string output)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        var passedMatch = PassedPattern().Match(output);
        if (passedMatch.Success)
            passed = int.Parse(passedMatch.Groups[1].Value);

        var failedMatch = FailedPattern().Match(output);
        if (failedMatch.Success)
            failed = int.Parse(failedMatch.Groups[1].Value);

        var skippedMatch = SkippedPattern().Match(output);
        if (skippedMatch.Success)
            skipped = int.Parse(skippedMatch.Groups[1].Value);

        return (passed, failed, skipped);
    }
}
