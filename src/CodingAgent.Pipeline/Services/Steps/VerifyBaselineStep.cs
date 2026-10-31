using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Verifies agent environment health (via IAgentProvider.ValidateAsync) and starts the workspace baseline
/// (quality gates). Agent environment failure is fatal; workspace failure is non-fatal. The result only
/// informs people, so the baseline does not hold up the run: it continues next to the analysis until
/// <see cref="AwaitBaselineStep"/> waits for it before code generation. The <c>VerifyBaseline</c> span
/// covers it to its end.
/// </summary>
public sealed class VerifyBaselineStep : IPipelineStep
{
    public string StepName => "VerifyBaseline";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var activity = PipelineTelemetry.ActivitySource.StartActivity("VerifyBaseline");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);

        try
        {
            if (!context.Config.BaselineHealthCheckEnabled)
            {
                context.Callbacks.EmitOutputLine("⏭️ Baseline health check disabled, skipping");
                return StepResult.Continue;
            }

            context.Callbacks.TransitionTo(PipelineStep.VerifyingBaseline);

            // Phase 1: Agent environment health (fatal)
            var doctorResult = await RunAgentHealthCheckAsync(context, activity, ct);
            if (!doctorResult)
                return StepResult.Stop;

            // Phase 2: Workspace baseline (non-fatal) — starts in the background
            if (await StartWorkspaceBaselineAsync(context, activity, ct))
                activity = null; // The running baseline ends the span.

            return StepResult.Continue;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private static async Task<bool> RunAgentHealthCheckAsync(PipelineStepContext context, Activity? activity, CancellationToken ct)
    {
        context.Callbacks.EmitOutputLine("🩺 Running agent environment health check...");

        try
        {
            await context.AgentProvider.ValidateAsync(ct);
            if (!string.IsNullOrEmpty(context.Run.WorkspacePath))
                await context.AgentProvider.EnsureSessionAsync(context.Run.WorkspacePath, ct);
            context.Callbacks.EmitOutputLine("✅ Agent environment healthy");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Run.BaselineHealthPassed = false;
            // Fatal: set Error status on the VerifyBaseline span before failing the run.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            await context.FailRunAsync($"Agent environment unhealthy: {ex.Message}", FailureReason.InfrastructureFailure, ct);
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
        if (report.ExternalCi is { Passed: false })
            context.Callbacks.EmitOutputLine($"  ❌ External CI: {report.ExternalCi.Details}");
    }

    /// <summary>
    /// Starts the workspace baseline in the background. Returns false, with nothing running, when there is
    /// nothing to verify.
    /// </summary>
    private static async Task<bool> StartWorkspaceBaselineAsync(PipelineStepContext context, Activity? activity, CancellationToken ct)
    {
        if (context.QualityGateValidator is null || string.IsNullOrEmpty(context.Run.WorkspacePath))
        {
            context.Callbacks.EmitOutputLine("⏭️ Workspace baseline skipped: validator or workspace not available");
            context.Run.BaselineHealthPassed = null;
            return false;
        }

        var qgcs = await LoadQualityGateConfigsAsync(context, ct);
        if (qgcs is null)
            return false;

        context.Callbacks.EmitOutputLine("🔍 Running workspace baseline verification...");

        var validator = context.QualityGateValidator;
        var workspacePath = context.Run.WorkspacePath;
        context.RunningBaseline = BackgroundBaseline.Start(
            baselineCt => RunWorkspaceBaselineAsync(context, validator, workspacePath, qgcs, activity, baselineCt),
            activity,
            ct);
        return true;
    }

    /// <summary>Returns whether the baseline is healthy. Writes nothing to the run: <see cref="AwaitBaselineStep"/> does that.</summary>
    private static async Task<bool?> RunWorkspaceBaselineAsync(
        PipelineStepContext context,
        IQualityGateValidator validator,
        string workspacePath,
        IReadOnlyList<QualityGateConfiguration> qgcs,
        Activity? activity,
        CancellationToken ct)
    {
        try
        {
            var report = await validator.ValidateAsync(workspacePath, qgcs, ct);

            if (report.AllPassed)
            {
                context.Callbacks.EmitOutputLine("✅ Workspace baseline healthy");
            }
            else
            {
                context.Callbacks.EmitOutputLine("⚠️ Workspace baseline has pre-existing issues (non-fatal):");
                EmitReportDetails(context, report);
            }

            return report.AllPassed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Callbacks.EmitOutputLine($"⚠️ Workspace baseline check failed (non-fatal): {ex.Message}");
            // Non-fatal: add exception event without setting Error status on the span.
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName ?? "<unknown>" },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.StackTrace ?? "" },
                { "pipeline.non_critical", true }
            }));
            return false;
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
