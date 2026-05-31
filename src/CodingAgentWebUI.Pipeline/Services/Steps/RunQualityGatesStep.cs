using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Loads quality gate configurations, builds the <see cref="QualityGateContext"/>,
/// and delegates to <see cref="QualityGateExecutor.ProceedToQualityGatesAsync"/>.
/// </summary>
internal sealed class RunQualityGatesStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("RunQualityGates");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);

        IReadOnlyList<QualityGateConfiguration> allQgcs;
        bool qgcsConfiguredAtDispatch;

        if (context.PreResolvedQualityGateConfigs is not null)
        {
            // Agent path: pre-resolved configs imply QGCs were configured in the system at dispatch time.
            allQgcs = context.PreResolvedQualityGateConfigs;
            qgcsConfiguredAtDispatch = true;
        }
        else
        {
            // Orchestrator path: load all QGCs from the config store.
            allQgcs = await context.ConfigStore.LoadQualityGateConfigsAsync(ct);
            qgcsConfiguredAtDispatch = allQgcs.Count > 0;
        }

        var qualityGateContext = new QualityGateContext
        {
            Run = context.Run,
            Config = context.Config,
            AgentProvider = context.AgentProvider,
            RepoProvider = context.RepoProvider,
            PipelineProvider = context.PipelineProvider,
            OrchestratorCts = context.Cts,
            IssueOps = context.IssueOps,
            Callbacks = context.Callbacks,
            QualityGateConfigs = allQgcs,
            QgcsConfiguredAtDispatch = qgcsConfiguredAtDispatch,
            Issue = context.Issue,
            IssueReference = context.IssueProvider?.FormatIssueReference(context.Run.IssueIdentifier)
        };

        await context.QualityGates.ProceedToQualityGatesAsync(qualityGateContext, ct);

        if (context.Run.CurrentStep is PipelineStep.Failed or PipelineStep.Completed or PipelineStep.Cancelled)
            return StepResult.Stop;

        return StepResult.Continue;
    }
}
