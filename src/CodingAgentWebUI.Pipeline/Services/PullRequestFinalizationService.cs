using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;
using OpenTelemetry.Trace;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Encapsulates the shared post-PR-creation logic (reflection, brain sync, feedback collection)
/// that was previously duplicated between PipelineOrchestrationService and LocalPipelineExecutor.
/// Stateless service — all dependencies are passed per-call.
/// </summary>
public sealed class PullRequestFinalizationService
{
    private readonly Serilog.ILogger _logger;

    public PullRequestFinalizationService(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Generates an agent-written PR description and updates the PR body.
    /// Does not throw on failure — logs a warning and returns.
    /// </summary>
    public async Task GeneratePrDescriptionAsync(
        PipelineRun run, IAgentProvider agentProvider, IRepositoryProvider repoProvider,
        PipelineConfiguration config, Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("GeneratePrDescription");
        activity?.SetTag("pipeline.run_id", run.RunId);

        emitOutputLine("📝 Generating PR description...");
        try
        {
            var prompt = PromptBuilder.BuildPrDescriptionPrompt(run);

            var result = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = true
                },
                ct,
                line => emitOutputLine(line));

            run.AccumulateTokenUsage(result);

            var description = string.Join("\n", result.OutputLines).Trim();
            if (string.IsNullOrWhiteSpace(description))
            {
                _logger.Warning("Pipeline {RunId} PR description generation returned empty output", run.RunId);
                return;
            }

            // Prepend agent summary above existing PR body
            if (!int.TryParse(run.PullRequestNumber, out var prNumber))
            {
                _logger.Warning("Pipeline {RunId} PR description skipped — PullRequestNumber '{PrNumber}' is not a valid integer", run.RunId, run.PullRequestNumber);
                return;
            }
            var currentBody = run.PullRequestBody ?? "";
            var newBody = $"{description}\n\n---\n\n{currentBody}";
            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, false, ct);
            run.PullRequestBody = newBody;

            _logger.Information("Pipeline {RunId} PR description generated and applied", run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} PR description generation failed, continuing", run.RunId);
        }
    }

    /// <summary>
    /// Executes the reflection step: builds a reflection prompt and asks the agent to review
    /// the run and enrich .brain/ knowledge. Accumulates token usage on the run.
    /// Does not throw on failure — logs a warning and returns.
    /// </summary>
    public async Task RunReflectionAsync(
        PipelineRun run, IAgentProvider agentProvider, PipelineConfiguration config,
        Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Reflection");
        activity?.SetTag("pipeline.run_id", run.RunId);

        emitOutputLine("🧠 Reflecting on run and updating brain knowledge...");
        try
        {
            var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(
                run, run.IssueTitle, run.RepositoryName?.Split('/').LastOrDefault());
            _logger.Debug("Pipeline {RunId} reflection prompt:\n{Prompt}", run.RunId, reflectionPrompt);

            var reflectionResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = reflectionPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = true
                },
                ct,
                line => emitOutputLine(line));

            run.AccumulateTokenUsage(reflectionResult);
            _logger.Information("Pipeline {RunId} reflection step completed", run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} reflection step failed, continuing with brain sync", run.RunId);
        }
    }

    /// <summary>
    /// Syncs the brain repository after the run. Delegates to brainSync.SyncPostRunAsync.
    /// Does not throw on failure — logs a warning and sets run.BrainUpdatesPushed = false.
    /// </summary>
    public async Task SyncBrainPostRunAsync(
        PipelineRun run, IBrainSyncService brainSync, IRepositoryProvider brainProvider,
        PipelineConfiguration config, Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("BrainSyncPostRun");
        activity?.SetTag("pipeline.run_id", run.RunId);

        try
        {
            await brainSync.SyncPostRunAsync(run, brainProvider, ct, emitOutputLine, config.BrainPushMaxRetries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} brain post-run sync failed", run.RunId);
            run.BrainUpdatesPushed = false;
        }
    }

    /// <summary>
    /// Collects structured feedback from the agent about the run.
    /// On failure, creates a fallback feedback record via feedbackService.
    /// </summary>
    public async Task CollectFeedbackAsync(
        PipelineRun run, IAgentProvider agentProvider, FeedbackService feedbackService,
        IPipelineRunHistoryService? historyService, Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("FeedbackCollection");
        activity?.SetTag("pipeline.run_id", run.RunId);

        emitOutputLine("📋 Collecting run feedback...");
        try
        {
            var elapsed = DateTime.UtcNow - run.StartedAt;
            var recentSummaries = (historyService?.GetRunHistory() ?? [])
                .OrderByDescending(s => s.StartedAt)
                .Take(FeedbackConstraints.MaxRecentRunsForCategories)
                .ToList();

            var harnessCategories = recentSummaries
                .Where(s => s.Feedback?.Harness.Category is not null)
                .Select(s => s.Feedback!.Harness.Category!)
                .Distinct()
                .ToList();

            var issueCategories = recentSummaries
                .Where(s => s.Feedback?.Issue?.Category is not null)
                .Select(s => s.Feedback!.Issue!.Category!)
                .Distinct()
                .ToList();

            var feedbackPrompt = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
                run, elapsed, harnessCategories, issueCategories);

            var feedbackResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = feedbackPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = TimeSpan.FromSeconds(FeedbackConstraints.FailureFeedbackTimeoutSeconds),
                    UseResume = true
                },
                ct,
                line => emitOutputLine(line));

            var responseText = string.Join("\n", feedbackResult.OutputLines);
            run.Feedback = feedbackService.ParseFeedbackFromResponse(responseText, FeedbackOutcome.Success, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} feedback collection failed, using fallback", run.RunId);
            run.Feedback = feedbackService.CreateFallbackFeedback(FeedbackOutcome.Success,
                $"Feedback collection failed: {ex.Message}", DateTime.UtcNow);
        }
    }
}
