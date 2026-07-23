using System.Diagnostics;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared utility for running environment setup commands as external bash processes
/// with timeout handling, secret injection, and output masking.
/// Follows the same pattern as <see cref="GitProcessRunner"/>.
/// </summary>
public static class SetupCommandRunner
{
    /// <summary>
    /// Runs a bash command with environment secret injection, 120-second timeout,
    /// concurrent stdout/stderr capture, and secret masking on all output.
    /// </summary>
    /// <param name="command">The bash command to execute.</param>
    /// <param name="stepName">Human-readable step name for error messages.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="environmentSecrets">Secrets to inject as environment variables and mask in output.</param>
    /// <param name="emitOutput">Callback to emit masked output lines.</param>
    /// <param name="ct">Cancellation token for pipeline-level cancellation.</param>
    /// <returns>A result indicating success or failure with a masked error message.</returns>
    public static async Task<SetupCommandResult> RunAsync(
        string command,
        string stepName,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentSecrets,
        Action<string> emitOutput,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(stepName);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(environmentSecrets);
        ArgumentNullException.ThrowIfNull(emitOutput);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);

            // Inject secrets as environment variables into the child process
            foreach (var (key, value) in environmentSecrets)
                psi.Environment[key] = value;

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Read stdout and stderr concurrently to avoid pipe buffer deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            // Wait with 120-second timeout per step
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — kill the process tree and return immediately
                // (do NOT await stdout/stderr tasks — matches existing behavior)
                // TODO: Abandoned stdoutTask/stderrTask may fault after process.Kill (IOException from closed pipe),
                // resulting in unobserved task exceptions. Consider observing them with a fire-and-forget handler.
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                var timeoutMessage = $"Setup step '{stepName}' timed out after 120 seconds";
                return new SetupCommandResult(false, SecretMasker.Mask(timeoutMessage, environmentSecrets), null);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Emit masked output
            if (!string.IsNullOrWhiteSpace(stdout))
                emitOutput(SecretMasker.Mask(stdout.TrimEnd(), environmentSecrets));
            if (!string.IsNullOrWhiteSpace(stderr))
                emitOutput(SecretMasker.Mask(stderr.TrimEnd(), environmentSecrets));

            if (process.ExitCode != 0)
            {
                var truncatedStderr = stderr.Length > 500 ? stderr[..500] : stderr;
                var failureMessage = $"Setup step '{stepName}' failed with exit code {process.ExitCode}: {truncatedStderr}";
                return new SetupCommandResult(false, SecretMasker.Mask(failureMessage, environmentSecrets), null);
            }

            return new SetupCommandResult(true, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate pipeline-level cancellation
        }
        catch (Exception ex)
        {
            var failureMessage = $"Setup step '{stepName}' threw an exception: {ex.Message}";
            return new SetupCommandResult(false, SecretMasker.Mask(failureMessage, environmentSecrets), ex);
        }
    }
}

/// <summary>
/// Result of a setup command execution.
/// </summary>
/// <param name="Success">Whether the command completed with exit code 0.</param>
/// <param name="FailureMessage">Masked failure message ready for FailRunAsync; null when Success is true.</param>
/// <param name="Exception">The exception that caused failure, if any. Used for telemetry recording.</param>
public readonly record struct SetupCommandResult(
    bool Success,
    string? FailureMessage,
    Exception? Exception);
