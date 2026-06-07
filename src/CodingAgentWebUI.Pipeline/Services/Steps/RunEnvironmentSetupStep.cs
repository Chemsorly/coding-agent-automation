using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Orchestrator-side pipeline step that executes per-repository environment setup commands
/// (e.g., configuring private NuGet feeds, installing tools) after clone but before the coding agent starts.
/// Only runs on Linux — skipped with a debug log on non-Linux orchestrators.
/// Secrets from the repo config are injected as environment variables into each setup step process.
/// </summary>
internal sealed class RunEnvironmentSetupStep : IPipelineStep
{
    public string StepName => "RunEnvironmentSetup";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("RunEnvironmentSetup");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        if (!OperatingSystem.IsLinux())
        {
            context.Logger.Debug("Environment setup steps skipped (non-Linux orchestrator)");
            return StepResult.Continue;
        }

        var repoConfig = await context.ConfigStore.GetProviderConfigByIdAsync(
            context.Run.RepoProviderConfigId, ProviderKind.Repository, ct);

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
            context.Callbacks.EmitOutputLine(SecretMasker.Mask($"🔧 Running setup: {step.Name}", secrets));

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
                    await context.FailRunAsync(SecretMasker.Mask(timeoutMessage, secrets), ct);
                    return StepResult.Stop;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                // Emit masked output
                if (!string.IsNullOrWhiteSpace(stdout))
                    context.Callbacks.EmitOutputLine(SecretMasker.Mask(stdout.TrimEnd(), secrets));
                if (!string.IsNullOrWhiteSpace(stderr))
                    context.Callbacks.EmitOutputLine(SecretMasker.Mask(stderr.TrimEnd(), secrets));

                if (process.ExitCode != 0)
                {
                    var truncatedStderr = stderr.Length > 500 ? stderr[..500] : stderr;
                    var failureMessage = $"Setup step '{step.Name}' failed with exit code {process.ExitCode}: {truncatedStderr}";
                    await context.FailRunAsync(SecretMasker.Mask(failureMessage, secrets), ct);
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
                await context.FailRunAsync(SecretMasker.Mask(failureMessage, secrets), ct);
                return StepResult.Stop;
            }
        }

        context.Callbacks.EmitOutputLine($"✅ Environment setup complete ({steps.Count} steps)");
        return StepResult.Continue;
    }
}
