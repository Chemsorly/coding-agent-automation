using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Handles agent execution phases: analysis, code generation (with stall monitoring),
/// and code review iterations. Extracted from PipelineOrchestrationService.
/// Split into partial classes by phase for maintainability.
/// </summary>
public partial class AgentPhaseExecutor : IAgentPhaseExecutor
{
    /// <summary>Minimum length in bytes for analysis.md to be considered valid.</summary>
    internal const int MinAnalysisLength = PipelineConstants.MinAnalysisLength;

    private readonly Serilog.ILogger _logger;

    public AgentPhaseExecutor(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Builds the brain context section and writes it to the workspace if non-empty.
    /// Always ensures the .agent directory exists before writing (fixing inconsistency
    /// where Analysis previously skipped directory creation).
    /// </summary>
    /// <returns>True if brain context was written, false otherwise.</returns>
    private async Task<bool> WriteBrainContextIfNeededAsync(PipelineRun run, CancellationToken ct)
    {
        if (run.WorkspacePath is null) return false;

        var brainContext = PromptBuilder.BuildBrainContextSection(
            run.BrainContextLoaded,
            run.RepositoryName?.Split('/').LastOrDefault());

        if (string.IsNullOrEmpty(brainContext)) return false;

        var agentDir = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.MetadataDirectory);
        Directory.CreateDirectory(agentDir);

        var contextPath = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.BrainContextFilePath);
        await File.WriteAllTextAsync(contextPath, brainContext, ct);
        _logger.Debug("Pipeline {RunId} wrote brain context to {FilePath}", run.RunId, AgentWorkspacePaths.BrainContextFilePath);

        return true;
    }

    /// <summary>
    /// Unified agent execution helper that encapsulates the standard pattern:
    /// build AgentRequest → execute with stall monitoring → enqueue output → record to ChatHistory → handle exceptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This helper consolidates the repeated agent execution pattern found across QualityGateExecutor retry loops,
    /// CodeReview fix agent calls, and similar sites. It handles:
    /// </para>
    /// <list type="bullet">
    /// <item>Building an <see cref="AgentRequest"/> with UseResume=true, WorkspacePath, and Timeout from config</item>
    /// <item>Executing via <see cref="AgentStallMonitor.ExecuteWithMonitoringAsync"/> with output line callback</item>
    /// <item>Enqueuing output lines to <c>run.OutputLines</c> and emitting via <c>callbacks.EmitOutputLine</c></item>
    /// <item>Recording the output summary (last 10 lines) to <c>run.ChatHistory</c></item>
    /// <item>Exception handling: rethrows <see cref="OperationCanceledException"/>, logs and records other exceptions</item>
    /// </list>
    /// <para>
    /// <b>Sites NOT consolidated into this helper:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><c>AgentPhaseExecutor.CodeGeneration</c> — has unique post-execution logic: exit code inspection,
    /// timeout detection via plain OperationCanceledException (distinct from orchestrator cancellation), and fail-phase
    /// transitions that cannot be cleanly parameterized without making the helper overly complex.</item>
    /// <item><c>AgentPhaseExecutor.Analysis</c> — has its own retry loop with AnalysisIncompleteException handling
    /// and post-execution file validation that is fundamentally different from the simple execute-and-record pattern.</item>
    /// <item><c>AgentPhaseExecutor.CodeReview</c> (ExecuteFollowUpAsync, GenerateReviewSummarySafeAsync) — consolidated
    /// into <see cref="ExecuteAgentRawAsync"/> which handles the subset pattern without history recording or exception absorption.</item>
    /// </list>
    /// </remarks>
    /// <returns>The <see cref="AgentResult"/> on success, or <c>null</c> if a non-cancellation exception was caught and absorbed.</returns>
    internal static async Task<AgentResult?> ExecuteAgentAndRecordAsync(
        AgentExecutionRequest request,
        IPipelineCallbacks callbacks,
        CancellationToken ct,
        bool recordOutputToHistory = true,
        string? resumeSessionId = null)
    {
        try
        {
            var agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                request.AgentProvider,
                new AgentRequest
                {
                    Prompt = request.Prompt,
                    WorkspacePath = request.Run.WorkspacePath!,
                    Timeout = request.Config.AgentTimeout,
                    UseResume = resumeSessionId is null,
                    ResumeSessionId = resumeSessionId,
                    EnvironmentVariables = request.EnvironmentVariables
                },
                request.Run, request.Config, request.Description, callbacks.NotifyChange, request.Logger, ct,
                line => callbacks.EmitOutputLine(line));

            request.Run.AccumulateTokenUsage(agentResult, phase: request.Phase);

            if (recordOutputToHistory)
            {
                var outputSummary = agentResult.OutputLines.Count > 0
                    ? string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(PipelineConstants.OutputTailLineCount))
                    : PipelineConstants.NoOutputFallback;
                request.Run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = outputSummary });
            }

            return agentResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            request.Logger.Warning(ex, "Pipeline {RunId} {Description} failed", request.Run.RunId, request.Description);
            request.Run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent error during {request.Description}: {ex.Message}"
            });
            return null;
        }
    }

    /// <summary>
    /// Executes an agent prompt with stall monitoring and token accumulation, without recording
    /// output to ChatHistory or absorbing exceptions. Suitable for call sites where the caller
    /// handles output processing and exception semantics independently.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ExecuteAgentAndRecordAsync"/>, this method:
    /// <list type="bullet">
    /// <item>Always sets <c>UseResume = false</c> (fresh prompt, no session resume)</item>
    /// <item>Does NOT record output to <c>run.ChatHistory</c></item>
    /// <item>Does NOT catch or absorb exceptions — all exceptions propagate to the caller</item>
    /// </list>
    /// Used by <c>ExecuteFollowUpAsync</c> and <c>GenerateReviewSummarySafeAsync</c> in the CodeReview partial.
    /// </remarks>
    internal static async Task<AgentResult> ExecuteAgentRawAsync(
        AgentExecutionRequest request,
        CancellationToken ct)
    {
        var agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
            request.AgentProvider,
            new AgentRequest
            {
                Prompt = request.Prompt,
                WorkspacePath = request.Run.WorkspacePath!,
                Timeout = request.Config.AgentTimeout,
                UseResume = false,
                EnvironmentVariables = request.EnvironmentVariables
            },
            request.Run, request.Config, request.Description, request.OnChange, request.Logger, ct,
            request.OnOutputLine);

        request.Run.AccumulateTokenUsage(agentResult, phase: request.Phase);
        return agentResult;
    }

    /// <summary>
    /// Unified failure helper that encapsulates the standard fail-phase pattern:
    /// set FailureReason → set CompletedAt → swap label → transition step → add to history → return false.
    /// </summary>
    private static async Task<bool> FailPhaseAsync(FailPhaseRequest request)
    {
        var run = request.Run;
        run.FailureReason = request.FailureReason;
        run.FinalLabel = request.Label;
        run.MarkCompleted();
        await request.IssueOps.SwapLabelAsync(run.IssueIdentifier, request.Label, request.Ct);
        request.Callbacks.TransitionTo(request.Step);
        await request.Callbacks.AddRunToHistoryAsync(run);
        return false;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
