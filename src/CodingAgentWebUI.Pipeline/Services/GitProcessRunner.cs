using System.Diagnostics;

namespace CodingAgentWebUI.Pipeline.Services;

// NOTE: GitProcessRunner.RunAsync is public and accepts arbitrary git arguments.
// Callers must not interpolate user-controlled strings into the arguments parameter
// without validation. If used in less-trusted contexts, add input sanitization.
/// <summary>
/// Shared utility for running git commands as external processes with timeout handling.
/// </summary>
public static class GitProcessRunner
{
    public static async Task<string> RunAsync(
        string workingDirectory,
        string arguments,
        CancellationToken ct,
        bool throwOnNonZeroExit = true)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "git.exe" : "/usr/bin/git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "";

        ct.ThrowIfCancellationRequested();

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* Intentional: best-effort kill; process may have already exited. */ }
            try { await outputTask; } catch (Exception) { /* Intentional: output is discarded after timeout; partial reads are acceptable. */ }
            try { await errorTask; } catch (Exception) { /* Intentional: error output is discarded after timeout; partial reads are acceptable. */ }
            throw new TimeoutException($"git {arguments} timed out after 30 seconds");
        }

        var output = await outputTask;
        var stderr = await errorTask;

        if (throwOnNonZeroExit && process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}: {stderr}");

        return output;
    }
}
