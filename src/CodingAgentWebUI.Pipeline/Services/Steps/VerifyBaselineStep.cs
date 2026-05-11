using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Verifies agent environment health (kiro-cli doctor) and workspace baseline (quality gates)
/// before code generation begins. Agent environment failure is fatal; workspace failure is non-fatal.
/// </summary>
internal sealed class VerifyBaselineStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (!context.Config.BaselineHealthCheckEnabled)
        {
            context.Callbacks.EmitOutputLine("⏭️ Baseline health check disabled, skipping");
            return StepResult.Continue;
        }

        context.Callbacks.TransitionTo(PipelineStep.VerifyingBaseline);

        // Phase 1: Agent environment health (fatal)
        if (context.KiroCliPath is not null)
        {
            var doctorResult = await RunDoctorCheckAsync(context, ct);
            if (!doctorResult)
                return StepResult.Stop;
        }

        // Phase 2: Workspace baseline (non-fatal)
        await RunWorkspaceBaselineAsync(context, ct);

        return StepResult.Continue;
    }

    private static async Task<bool> RunDoctorCheckAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.EmitOutputLine("🩺 Running agent environment health check...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = context.KiroCliPath!,
                Arguments = "doctor --all --strict",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // TODO: Register ct callback to kill process tree on cancellation to avoid orphaned child process
            using var process = Process.Start(psi);
            if (process is null)
            {
                context.Run.BaselineHealthPassed = false;
                await context.FailRunAsync("Agent environment unhealthy: failed to start kiro-cli doctor process");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var details = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                context.Run.BaselineHealthPassed = false;
                await context.FailRunAsync($"Agent environment unhealthy: kiro-cli doctor exited with code {process.ExitCode}. {details}");
                return false;
            }

            context.Callbacks.EmitOutputLine("✅ Agent environment healthy");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Run.BaselineHealthPassed = false;
            await context.FailRunAsync($"Agent environment unhealthy: {ex.Message}");
            return false;
        }
    }

    private static async Task RunWorkspaceBaselineAsync(PipelineStepContext context, CancellationToken ct)
    {
        // TODO: Emit output line explaining why workspace baseline was skipped when validator/workspace is null
        if (context.QualityGateValidator is null || string.IsNullOrEmpty(context.Run.WorkspacePath))
        {
            context.Run.BaselineHealthPassed = null;
            return;
        }

        IReadOnlyList<QualityGateConfiguration> qgcs;
        if (context.PreResolvedQualityGateConfigs is not null)
        {
            qgcs = context.PreResolvedQualityGateConfigs;
        }
        else
        {
            qgcs = await context.ConfigStore.LoadQualityGateConfigsAsync(ct);
        }

        if (qgcs.Count == 0)
        {
            context.Run.BaselineHealthPassed = null;
            context.Callbacks.EmitOutputLine("⏭️ No quality gate configs found, skipping workspace baseline");
            return;
        }

        context.Callbacks.EmitOutputLine("🔍 Running workspace baseline verification...");

        try
        {
            var report = await context.QualityGateValidator.ValidateAsync(context.Run.WorkspacePath, qgcs, ct);
            context.Run.BaselineHealthPassed = report.AllPassed;

            if (report.AllPassed)
                context.Callbacks.EmitOutputLine("✅ Workspace baseline healthy");
            else
                context.Callbacks.EmitOutputLine("⚠️ Workspace baseline has pre-existing issues (non-fatal)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Run.BaselineHealthPassed = false;
            context.Callbacks.EmitOutputLine($"⚠️ Workspace baseline check failed (non-fatal): {ex.Message}");
        }
    }
}
