using System.Diagnostics;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared utility for running git commands as external processes with timeout handling.
/// </summary>
internal static class GitProcessRunner
{
    internal static async Task<string> RunAsync(
        string workingDirectory,
        string arguments,
        CancellationToken ct,
        bool throwOnNonZeroExit = true)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "";

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
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await outputTask; } catch { }
            try { await errorTask; } catch { }
            throw new TimeoutException($"git {arguments} timed out after 30 seconds");
        }

        var output = await outputTask;
        var stderr = await errorTask;

        if (throwOnNonZeroExit && process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}: {stderr}");

        return output;
    }
}
