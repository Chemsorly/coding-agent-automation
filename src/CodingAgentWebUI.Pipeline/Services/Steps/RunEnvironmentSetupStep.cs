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
public sealed class RunEnvironmentSetupStep : IPipelineStep
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

            var result = await SetupCommandRunner.RunAsync(
                step.Command, step.Name, context.Run.WorkspacePath!, secrets,
                line => context.Callbacks.EmitOutputLine(line), ct);

            if (!result.Success)
            {
                if (result.Exception is not null)
                    Activity.Current?.RecordMaskedError(result.Exception, secrets);
                // TODO: Timeout detection relies on string matching. SetupCommandResult should expose a structured
                // failure reason (e.g., IsTimeout bool or FailureKind enum) instead of requiring callers to inspect message content.
                else if (result.FailureMessage?.Contains("timed out") == true)
                    Activity.Current?.SetStatus(ActivityStatusCode.Error, result.FailureMessage);

                await context.FailRunAsync(result.FailureMessage!, ct);
                return StepResult.Stop;
            }
        }

        context.Callbacks.EmitOutputLine($"✅ Environment setup complete ({steps.Count} steps)");
        return StepResult.Continue;
    }
}
