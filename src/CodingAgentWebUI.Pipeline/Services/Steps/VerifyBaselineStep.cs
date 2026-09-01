using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Verifies agent environment health (via IAgentProvider.ValidateAsync) and workspace baseline
/// (quality gates) before code generation begins. Agent environment failure is fatal; workspace
/// failure is non-fatal.
/// </summary>
public sealed class VerifyBaselineStep : IPipelineStep
{
    public string StepName => "VerifyBaseline";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (!context.Config.BaselineHealthCheckEnabled)
        {
            context.Callbacks.EmitOutputLine("⏭️ Baseline health check disabled, skipping");
            return StepResult.Continue;
        }

        context.Callbacks.TransitionTo(PipelineStep.VerifyingBaseline);

        // Phase 1: Agent environment health (fatal)
        var doctorResult = await RunAgentHealthCheckAsync(context, ct);
        if (!doctorResult)
            return StepResult.Stop;

        // Phase 2: Workspace baseline (non-fatal)
        await RunWorkspaceBaselineAsync(context, ct);

        return StepResult.Continue;
    }

    private static async Task<bool> RunAgentHealthCheckAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.EmitOutputLine("🩺 Running agent environment health check...");

        try
        {
            await context.AgentProvider.ValidateAsync(ct);
            context.Callbacks.EmitOutputLine("✅ Agent environment healthy");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Run.BaselineHealthPassed = false;
            await context.FailRunAsync($"Agent environment unhealthy: {ex.Message}", ct);
            return false;
        }
    }

    private static void EmitReportDetails(PipelineStepContext context, QualityGateReport report)
    {
        if (report.QgcResults.Count > 0)
        {
            foreach (var qgc in report.QgcResults.Where(r => !r.Passed))
                EmitQgcFailureLines(context, qgc);
        }
        else
        {
            EmitAggregateFailureLines(context, report);
        }
    }

    /// <summary>
    /// Emits failure output lines for a single failing QGC execution result.
    /// </summary>
    private static void EmitQgcFailureLines(PipelineStepContext context, QgcExecutionResult qgc)
    {
        context.Callbacks.EmitOutputLine($"  ▸ QGC '{qgc.DisplayName}':");
        if (qgc.Compilation is { Passed: false })
            context.Callbacks.EmitOutputLine($"    ❌ Compilation: {qgc.Compilation.Details}");
        if (qgc.Tests is { Passed: false })
            context.Callbacks.EmitOutputLine($"    ❌ Tests: {qgc.Tests.Details}");
        if (qgc.SecurityScan is { Passed: false })
            context.Callbacks.EmitOutputLine($"    ❌ Security: {qgc.SecurityScan.Details}");
    }

    /// <summary>
    /// Emits failure output lines from the aggregate report fields (backward-compat fallback).
    /// </summary>
    private static void EmitAggregateFailureLines(PipelineStepContext context, QualityGateReport report)
    {
        if (report.Compilation is { Passed: false })
            context.Callbacks.EmitOutputLine($"  ❌ Compilation: {report.Compilation.Details}");
        if (report.Tests is { Passed: false })
            context.Callbacks.EmitOutputLine($"  ❌ Tests: {report.Tests.Details}");
        if (report.SecurityScan is { Passed: false })
            context.Callbacks.EmitOutputLine($"  ❌ Security: {report.SecurityScan.Details}");
        if (report.ExternalCi is { Passed: false })
            context.Callbacks.EmitOutputLine($"  ❌ External CI: {report.ExternalCi.Details}");
    }

    private static async Task RunWorkspaceBaselineAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.QualityGateValidator is null || string.IsNullOrEmpty(context.Run.WorkspacePath))
        {
            context.Callbacks.EmitOutputLine("⏭️ Workspace baseline skipped: validator or workspace not available");
            context.Run.BaselineHealthPassed = null;
            return;
        }

        var qgcs = await LoadQualityGateConfigsAsync(context, ct);
        if (qgcs is null)
            return;

        context.Callbacks.EmitOutputLine("🔍 Running workspace baseline verification...");

        try
        {
            var report = await context.QualityGateValidator.ValidateAsync(context.Run.WorkspacePath, qgcs, ct);
            context.Run.BaselineHealthPassed = report.AllPassed;

            if (report.AllPassed)
            {
                context.Callbacks.EmitOutputLine("✅ Workspace baseline healthy");
            }
            else
            {
                context.Callbacks.EmitOutputLine("⚠️ Workspace baseline has pre-existing issues (non-fatal):");
                EmitReportDetails(context, report);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Run.BaselineHealthPassed = false;
            context.Callbacks.EmitOutputLine($"⚠️ Workspace baseline check failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Loads quality gate configurations from the pre-resolved set or config store.
    /// Returns null (and sets BaselineHealthPassed to null) when no configs are found.
    /// </summary>
    private static async Task<IReadOnlyList<QualityGateConfiguration>?> LoadQualityGateConfigsAsync(
        PipelineStepContext context, CancellationToken ct)
    {
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
            return null;
        }

        return qgcs;
    }
}
