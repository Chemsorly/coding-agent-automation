using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Pipeline step that executes per-repository environment setup commands (e.g., configuring
/// private NuGet feeds, installing tools) after clone but before the coding agent starts.
/// Secrets from the repo config are injected as environment variables into each setup step process.
/// </summary>
internal sealed class RunEnvironmentSetupStep : IPipelineStep
{
    private readonly JobAssignmentMessage _job;

    public RunEnvironmentSetupStep(JobAssignmentMessage job)
    {
        _job = job;
    }

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var repoConfig = _job.ProviderConfigs.FirstOrDefault(c => c.Id == _job.RepoProviderConfigId);

        var hasSecrets = repoConfig?.Secrets is { Count: > 0 };
        var hasSetupSteps = repoConfig?.SetupSteps is { Count: > 0 };

        // Skip if both secrets and setup steps are empty/null
        if (!hasSecrets && !hasSetupSteps)
            return StepResult.Continue;

        context.Callbacks.TransitionTo(PipelineStep.RunningEnvironmentSetup);

        var secrets = repoConfig!.Secrets ?? new Dictionary<string, string>();
        var steps = repoConfig.SetupSteps ?? [];

        foreach (var step in steps)
        {
            context.Callbacks.EmitOutputLine(MaskSecrets($"🔧 Running setup: {step.Name}", secrets));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    WorkingDirectory = context.Run.WorkspacePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(step.Command);

                // Inject secrets as environment variables
                foreach (var (key, value) in secrets)
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
                    // Timeout — kill the process tree
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    var timeoutMessage = $"Setup step '{step.Name}' timed out after 120 seconds";
                    await context.FailRunAsync(MaskSecrets(timeoutMessage, secrets), ct);
                    return StepResult.Stop;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                // Emit masked output
                if (!string.IsNullOrWhiteSpace(stdout))
                    context.Callbacks.EmitOutputLine(MaskSecrets(stdout.TrimEnd(), secrets));
                if (!string.IsNullOrWhiteSpace(stderr))
                    context.Callbacks.EmitOutputLine(MaskSecrets(stderr.TrimEnd(), secrets));

                if (process.ExitCode != 0)
                {
                    var truncatedStderr = stderr.Length > 500 ? stderr[..500] : stderr;
                    var failureMessage = $"Setup step '{step.Name}' failed with exit code {process.ExitCode}: {truncatedStderr}";
                    await context.FailRunAsync(MaskSecrets(failureMessage, secrets), ct);
                    return StepResult.Stop;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate pipeline-level cancellation
            }
            catch (Exception ex)
            {
                var failureMessage = $"Setup step '{step.Name}' threw an exception: {ex.Message}";
                await context.FailRunAsync(MaskSecrets(failureMessage, secrets), ct);
                return StepResult.Stop;
            }
        }

        context.Callbacks.EmitOutputLine($"✅ Environment setup complete ({steps.Count} steps)");
        return StepResult.Continue;
    }

    /// <summary>
    /// Masks known secret values in output text. Values shorter than 4 characters are not masked
    /// to avoid excessive false-positive redaction.
    /// </summary>
    private static string MaskSecrets(string output, Dictionary<string, string> secrets)
    {
        foreach (var (_, value) in secrets)
        {
            if (value.Length >= 4)
                output = output.Replace(value, "***");
        }
        return output;
    }
}
