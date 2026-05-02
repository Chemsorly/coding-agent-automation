using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Loads quality gate configurations, builds the <see cref="QualityGateContext"/>,
/// and delegates to <see cref="QualityGateOrchestrator.ProceedToQualityGatesAsync"/>.
/// </summary>
internal sealed class RunQualityGatesStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        IReadOnlyList<QualityGateConfiguration> allQgcs;
        if (context.PreResolvedQualityGateConfigs is not null)
        {
            allQgcs = context.PreResolvedQualityGateConfigs;
        }
        else
        {
            allQgcs = await context.ConfigStore.LoadQualityGateConfigsAsync(ct);
        }

        var qualityGateContext = new QualityGateContext
        {
            Run = context.Run,
            Config = context.Config,
            AgentProvider = context.AgentProvider,
            RepoProvider = context.RepoProvider,
            PipelineProvider = context.PipelineProvider,
            OrchestratorCts = context.Cts,
            TransitionTo = context.TransitionTo,
            IssueOps = context.IssueOps,
            RemoveAllAgentLabels = context.RemoveAllAgentLabels,
            AddRunToHistory = context.AddRunToHistory,
            OnOutputLine = context.EmitOutputLine,
            OnChange = context.NotifyChange,
            CreatePullRequest = context.CreatePullRequest,
            QualityGateConfigs = allQgcs,
            QgcsConfiguredAtDispatch = allQgcs.Count > 0
        };

        await context.QualityGates.ProceedToQualityGatesAsync(qualityGateContext, ct);

        // TODO: [WARNING] QgcsConfiguredAtDispatch has inconsistent semantics between orchestrator and agent paths.
        // On the orchestrator path, allQgcs represents ALL configured QGCs; on the agent path, it's the pre-resolved subset.

        if (context.Run.CurrentStep is PipelineStep.Failed or PipelineStep.Completed or PipelineStep.Cancelled)
            return StepResult.Stop;

        return StepResult.Continue;
    }
}
