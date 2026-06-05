using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Pipeline step that executes per-repository environment setup commands (e.g., configuring
/// private NuGet feeds, installing tools) after clone but before the coding agent starts.
/// Merges project-level and repo-level secrets (repo wins on key collision) and injects them
/// as process-wide environment variables for the entire run. Also injects into each setup step process.
/// </summary>
internal sealed class RunEnvironmentSetupStep : IPipelineStep
{
    public string StepName => "RunEnvironmentSetup";

    private readonly JobAssignmentMessage _job;

    public RunEnvironmentSetupStep(JobAssignmentMessage job)
    {
        _job = job;
    }

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var repoConfig = _job.ProviderConfigs.FirstOrDefault(c => c.Id == _job.RepoProviderConfigId);

        // Merge secrets: project-level as base, repo-level overlays (repo wins on key collision)
        var (effectiveSecrets, supersededKeys) = MergeSecrets(_job.ProjectSecrets, repoConfig?.Secrets);

        var hasSecrets = effectiveSecrets.Count > 0;
        var hasSetupSteps = repoConfig?.SetupSteps is { Count: > 0 };

        // Skip if both secrets and setup steps are empty/null
        if (!hasSecrets && !hasSetupSteps)
            return StepResult.Continue;

        context.Callbacks.TransitionTo(PipelineStep.RunningEnvironmentSetup);

        // Inject merged secrets as process-wide environment variables
        if (hasSecrets)
        {
            var injectedKeys = new List<string>(effectiveSecrets.Count);
            foreach (var (key, value) in effectiveSecrets)
            {
                Environment.SetEnvironmentVariable(key, value);
                injectedKeys.Add(key);
            }

            context.InjectedSecretKeys = injectedKeys;
            context.InjectedSecrets = effectiveSecrets;

            var keyList = string.Join(", ", injectedKeys);
            context.Callbacks.EmitOutputLine(
                MaskSecrets($"🔐 Injected {injectedKeys.Count} environment secrets (keys: {keyList})", effectiveSecrets));

            if (supersededKeys.Count > 0)
            {
                var supersededList = string.Join(", ", supersededKeys);
                context.Callbacks.EmitOutputLine(
                    $"⚠️ Repo-level secrets superseded project-level for keys: {supersededList}");
            }
        }

        var steps = repoConfig?.SetupSteps ?? [];

        foreach (var step in steps)
        {
            context.Callbacks.EmitOutputLine(MaskSecrets($"🔧 Running setup: {step.Name}", effectiveSecrets));

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

                // Inject secrets as environment variables into the child process
                foreach (var (key, value) in effectiveSecrets)
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
                    await context.FailRunAsync(MaskSecrets(timeoutMessage, effectiveSecrets), ct);
                    return StepResult.Stop;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                // Emit masked output
                if (!string.IsNullOrWhiteSpace(stdout))
                    context.Callbacks.EmitOutputLine(MaskSecrets(stdout.TrimEnd(), effectiveSecrets));
                if (!string.IsNullOrWhiteSpace(stderr))
                    context.Callbacks.EmitOutputLine(MaskSecrets(stderr.TrimEnd(), effectiveSecrets));

                if (process.ExitCode != 0)
                {
                    var truncatedStderr = stderr.Length > 500 ? stderr[..500] : stderr;
                    var failureMessage = $"Setup step '{step.Name}' failed with exit code {process.ExitCode}: {truncatedStderr}";
                    await context.FailRunAsync(MaskSecrets(failureMessage, effectiveSecrets), ct);
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
                await context.FailRunAsync(MaskSecrets(failureMessage, effectiveSecrets), ct);
                return StepResult.Stop;
            }
        }

        context.Callbacks.EmitOutputLine($"✅ Environment setup complete ({steps.Count} steps)");
        return StepResult.Continue;
    }

    /// <summary>
    /// Merges project-level and repo-level secrets. Repo secrets take precedence on key collision.
    /// </summary>
    private static (Dictionary<string, string> Merged, List<string> SupersededKeys) MergeSecrets(
        Dictionary<string, string>? projectSecrets,
        Dictionary<string, string>? repoSecrets)
    {
        if (projectSecrets is null or { Count: 0 } && repoSecrets is null or { Count: 0 })
            return (new Dictionary<string, string>(), []);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var supersededKeys = new List<string>();

        // Start with project secrets as base
        if (projectSecrets is { Count: > 0 })
        {
            foreach (var (key, value) in projectSecrets)
                merged[key] = value;
        }

        // Overlay repo secrets (repo wins on key collision)
        if (repoSecrets is { Count: > 0 })
        {
            foreach (var (key, value) in repoSecrets)
            {
                if (merged.ContainsKey(key))
                    supersededKeys.Add(key);
                merged[key] = value;
            }
        }

        return (merged, supersededKeys);
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
