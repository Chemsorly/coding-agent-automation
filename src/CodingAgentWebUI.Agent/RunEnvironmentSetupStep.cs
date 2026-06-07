using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Serilog;
using ILogger = Serilog.ILogger;

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
    private readonly ILogger _logger;

    public RunEnvironmentSetupStep(JobAssignmentMessage job, ILogger? logger = null)
    {
        _job = job;
        _logger = logger ?? Log.Logger;
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

            _logger.Information("Pipeline {RunId} injected {Count} environment secrets (keys: {Keys})",
                context.Run.RunId, injectedKeys.Count, string.Join(", ", injectedKeys));

            var keyList = string.Join(", ", injectedKeys);
            context.Callbacks.EmitOutputLine(
                SecretMasker.Mask($"🔐 Injected {injectedKeys.Count} environment secrets (keys: {keyList})", effectiveSecrets));

            if (supersededKeys.Count > 0)
            {
                var supersededList = string.Join(", ", supersededKeys);
                _logger.Information("Pipeline {RunId} repo-level secrets superseded project-level for keys: {SupersededKeys}",
                    context.Run.RunId, supersededList);
                context.Callbacks.EmitOutputLine(
                    $"⚠️ Repo-level secrets superseded project-level for keys: {supersededList}");
            }
        }

        var steps = repoConfig?.SetupSteps ?? [];

        _logger.Information("Pipeline {RunId} executing {StepCount} setup steps", context.Run.RunId, steps.Count);

        foreach (var step in steps)
        {
            _logger.Information("Pipeline {RunId} running setup step '{StepName}': {Command}",
                context.Run.RunId, step.Name, step.Command);
            context.Callbacks.EmitOutputLine(SecretMasker.Mask($"🔧 Running setup: {step.Name}", effectiveSecrets));

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
                    _logger.Error("Pipeline {RunId} setup step '{StepName}' timed out after 120s (command: {Command})",
                        context.Run.RunId, step.Name, step.Command);
                    await context.FailRunAsync(SecretMasker.Mask(timeoutMessage, effectiveSecrets), ct);
                    return StepResult.Stop;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                // Emit masked output
                if (!string.IsNullOrWhiteSpace(stdout))
                    context.Callbacks.EmitOutputLine(SecretMasker.Mask(stdout.TrimEnd(), effectiveSecrets));
                if (!string.IsNullOrWhiteSpace(stderr))
                    context.Callbacks.EmitOutputLine(SecretMasker.Mask(stderr.TrimEnd(), effectiveSecrets));

                if (process.ExitCode != 0)
                {
                    var truncatedStderr = stderr.Length > 500 ? stderr[..500] : stderr;
                    var failureMessage = $"Setup step '{step.Name}' failed with exit code {process.ExitCode}: {truncatedStderr}";
                    _logger.Error("Pipeline {RunId} setup step '{StepName}' failed with exit code {ExitCode} (command: {Command}): {Stderr}",
                        context.Run.RunId, step.Name, process.ExitCode, step.Command, truncatedStderr.Trim());
                    await context.FailRunAsync(SecretMasker.Mask(failureMessage, effectiveSecrets), ct);
                    return StepResult.Stop;
                }

                _logger.Information("Pipeline {RunId} setup step '{StepName}' completed successfully (exit code 0)",
                    context.Run.RunId, step.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate pipeline-level cancellation
            }
            catch (Exception ex)
            {
                var failureMessage = $"Setup step '{step.Name}' threw an exception: {ex.Message}";
                _logger.Error(ex, "Pipeline {RunId} setup step '{StepName}' threw an exception (command: {Command})",
                    context.Run.RunId, step.Name, step.Command);
                await context.FailRunAsync(SecretMasker.Mask(failureMessage, effectiveSecrets), ct);
                return StepResult.Stop;
            }
        }

        _logger.Information("Pipeline {RunId} environment setup complete ({StepCount} steps executed successfully)",
            context.Run.RunId, steps.Count);
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

}
