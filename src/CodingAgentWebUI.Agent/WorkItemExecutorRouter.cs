using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Routes work item execution to the appropriate executor based on <see cref="WorkItemTaskType"/>.
/// Implementation/Review/Decomposition → <see cref="LocalPipelineExecutor"/>.
/// Consolidation → <see cref="LocalConsolidationExecutor"/> (adapts result to <see cref="JobCompletionPayload"/>).
/// </summary>
public sealed class WorkItemExecutorRouter : IWorkItemExecutor
{
    private readonly IPipelineExecutor _pipelineExecutor;
    private readonly IConsolidationExecutor _consolidationExecutor;
    private readonly Serilog.ILogger _logger;

    public WorkItemExecutorRouter(
        IPipelineExecutor pipelineExecutor,
        IConsolidationExecutor consolidationExecutor,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipelineExecutor);
        ArgumentNullException.ThrowIfNull(consolidationExecutor);
        ArgumentNullException.ThrowIfNull(logger);

        _pipelineExecutor = pipelineExecutor;
        _consolidationExecutor = consolidationExecutor;
        _logger = logger;
    }

    public async Task<JobCompletionPayload> ExecuteAsync(
        JobAssignmentMessage assignment,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(outputBatcher);

        if (assignment.TaskType == WorkItemTaskType.Consolidation)
        {
            return await ExecuteConsolidationAsync(assignment, connection, ct);
        }

        // Standard pipeline execution (Implementation, Review, Decomposition)
        return await _pipelineExecutor.ExecuteAsync(assignment, connection, outputBatcher, onStepChanged, ct);
    }

    private async Task<JobCompletionPayload> ExecuteConsolidationAsync(
        JobAssignmentMessage assignment,
        HubConnection connection,
        CancellationToken ct)
    {
        _logger.Information("Routing work item {JobId} to consolidation executor (type={ConsolidationType})",
            assignment.JobId, assignment.ConsolidationRunType);

        // Build ConsolidationJobMessage from the unified assignment
        var consolidationJob = new ConsolidationJobMessage
        {
            JobId = assignment.JobId,
            Type = assignment.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
            TemplateId = assignment.ConsolidationTemplateId,
            WorkspacePath = assignment.ConsolidationWorkspacePath,
            ProviderConfigs = assignment.ProviderConfigs,
            PipelineConfiguration = assignment.PipelineConfiguration,
            AutoDispatch = assignment.AutoDispatch
        };

        // Execute and report via the consolidation-specific hub method
        var result = await _consolidationExecutor.ExecuteAsync(consolidationJob, connection, ct);

        // Note: LocalConsolidationExecutor already calls ReportConsolidationComplete internally.
        // Adapt ConsolidationJobResult → JobCompletionPayload for uniform return type.
        return AdaptResult(result);
    }

    private static JobCompletionPayload AdaptResult(ConsolidationJobResult result)
    {
        return new JobCompletionPayload
        {
            FinalStep = result.Success ? PipelineStep.Completed : PipelineStep.Failed,
            FailureReason = result.ErrorMessage,
            FailureCategory = result.FailureCategory,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
