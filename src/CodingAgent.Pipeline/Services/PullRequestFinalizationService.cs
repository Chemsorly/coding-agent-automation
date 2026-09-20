using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;
using OpenTelemetry.Trace;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Encapsulates the shared post-PR-creation logic (reflection, brain sync, feedback collection)
/// that was previously duplicated between PipelineOrchestrationService and LocalPipelineExecutor.
/// Stateless service — all dependencies are passed per-call.
/// </summary>
public sealed class PullRequestFinalizationService
{
    private readonly Serilog.ILogger _logger;
    private readonly Counter<long> _brainSyncSkipped;
    private readonly Histogram<double> _stepDuration;
    private readonly Counter<long> _stepCount;
    private const string PipelineRunIdTag = "pipeline.run_id";

    public PullRequestFinalizationService(Serilog.ILogger logger, IMeterFactory? meterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        if (meterFactory is not null)
        {
            var meter = meterFactory.Create(new MeterOptions(PipelineTelemetry.SourceName));
            _brainSyncSkipped = meter.CreateCounter<long>("brain.sync.skipped", "{sync}", "Runs where post-run brain sync was skipped (tagged by reason)");
            _stepDuration = meter.CreateHistogram<double>("pipeline.step.duration", "s", "Duration of individual pipeline steps",
                advice: new InstrumentAdvice<double>
                {
                    HistogramBucketBoundaries = [5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600]
                });
            _stepCount = meter.CreateCounter<long>("pipeline.step.count", "{step}", "Pipeline step execution count");
        }
        else
        {
            _brainSyncSkipped = PipelineTelemetry.BrainSyncSkipped;
            _stepDuration = PipelineTelemetry.StepDuration;
            _stepCount = PipelineTelemetry.StepCount;
        }
    }

    /// <summary>
    /// Runs the full PR creation and post-PR finalization flow: transition → create PR → post-PR sequence → set final state.
    /// Encapsulates the complete lifecycle from "ready to create PR" through to "run completed/failed".
    /// Sets CompletedAt, CurrentStep, FinalLabel, and (on failure) FailureReason on the run.
    /// Emits <c>pipeline.step.duration{step_name="CreatePullRequest"}</c> covering only the PR creation
    /// portion (up to but not including <see cref="RunPostPrSequenceAsync"/>).
    /// </summary>
    // TODO: Validate non-nullable parameters (run, report, prOrchestrator, repoProvider, agentProvider, config, feedbackService, emitOutputLine, transitionCallback) with ArgumentNullException.ThrowIfNull for fail-fast behavior on public API surface.
    public async Task RunFullPrCreationAsync(
        PrCreationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = request.Run;
        var isDraft = request.IsDraft;
        var prOrchestrator = request.PrOrchestrator;
        var repoProvider = request.RepoProvider;
        var agentProvider = request.AgentProvider;
        var brainProvider = request.BrainProvider;
        var brainSync = request.BrainSync;
        var config = request.Config;
        var issue = request.Issue;
        var issueComments = request.IssueComments;
        var feedbackService = request.FeedbackService;
        var historyService = request.HistoryService;
        var emitOutputLine = request.EmitOutputLine;
        var transitionCallback = request.TransitionCallback;
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CreatePullRequest");
        activity?.SetTag(PipelineRunIdTag, run.RunId);
        activity?.SetTag("pipeline.issue", run.IssueIdentifier);
        activity?.SetTag("pipeline.pr.is_draft", isDraft);
        PipelineTelemetry.SetProjectTags(activity, run.ProjectId, run.ProjectName);

        // Tracks only the PR creation portion — stopped before RunPostPrSequenceAsync runs.
        var sw = Stopwatch.StartNew();
        var finalStep = PipelineStep.Completed;
        var prCreationSucceeded = false;

        try
        {
            // NOTE: QualityGateExecutor already transitions to PreparingForPullRequest
            // during its cleanup phase, so we skip that transition here to avoid duplicates.

            await transitionCallback(PipelineStep.FinalizingPullRequest);

            if (run.LinkedPullRequest is not null)
            {
                run.PullRequestUrl = run.LinkedPullRequest.Url;
                run.PullRequestNumber = run.LinkedPullRequest.Number.ToString();
            }

            var prUrl = await prOrchestrator.CreatePullRequestAsync(
                run, isDraft, repoProvider, issue, issueComments, config, ct,
                emitOutputLine, isRework: run.LinkedPullRequest is not null);

            if (prUrl is null)
            {
                run.FailureReason = "Agent did not produce any changes. No commits ahead of base branch.";
                run.MarkCompleted();
                run.CurrentStep = PipelineStep.Failed;
                return;
            }

            finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;
            if (isDraft)
            {
                // Preserve a more specific failure reason if already set (e.g., "CI never started after N retries").
                // Fall back to the generic draft message for all other exhaustion paths.
                run.FailureReason ??= "Quality gates failed after max retries; draft PR created.";
            }
            // Label swap (agent:done / agent:error) is handled by the orchestrator in ReportJobCompleted.

            prCreationSucceeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.Error(ex, "Pipeline {RunId} PR creation failed", run.RunId);
            throw;
        }
        finally
        {
            sw.Stop();
            var tags = PipelineTelemetry.BuildStepTags("CreatePullRequest", run.RunType, run.ProjectId, run.ProjectName);
            // TODO: This finally block fires on OperationCanceledException as well as on the null-PR
            // bail-out path (prUrl is null). On cancellation the elapsed time is partial and
            // prCreationSucceeded=false, so the metric misrepresents the step as having run to
            // completion. On the null-PR path no PR was actually created, yet pipeline.step.count
            // is incremented. Consider guarding with a cancelled/skipped flag to suppress
            // misleading observations on these paths.
            _stepDuration.Record(sw.Elapsed.TotalSeconds, tags);
            _stepCount.Add(1, tags);
        }

        if (!prCreationSucceeded)
            return;

        try
        {
            await RunPostPrSequenceAsync(
                new PostPrSequenceRequest
                {
                    Run = run,
                    IsDraft = isDraft,
                    AgentProvider = agentProvider,
                    RepoProvider = repoProvider,
                    Config = config,
                    BrainSync = brainSync,
                    BrainProvider = brainProvider,
                    FeedbackService = feedbackService,
                    HistoryService = historyService,
                    EmitOutputLine = emitOutputLine,
                    TransitionCallback = transitionCallback
                },
                ct);
        }
        finally
        {
            // Always set terminal state regardless of whether RunPostPrSequenceAsync threw
            // (including OperationCanceledException from brain sync or reflection steps).
            // A run that reached PR creation must always exit with CompletedAt set so that
            // DatabaseMaintenanceService retention sweeps can clean it up and the Active Runs
            // panel does not accumulate ghost records with null CompletedAt.
            run.MarkCompleted();
            run.CurrentStep = finalStep;
            // FinalLabel is set unconditionally in the finally block so that it is assigned on all
            // exit paths including OperationCanceledException from RunPostPrSequenceAsync. The OCE
            // case is covered by RunFullPrCreationAsync_DraftOce_SetsFinalLabelError (draft) and
            // RunFullPrCreationAsync_OceFromRunPostPrSequenceAsync_StillSetsCompletedAt (non-draft).
            // The caller (orchestrator) may apply a further label-swap via ReportJobCompleted; that
            // interaction is intentional and documented in AgentJobLifecycleService.
            run.FinalLabel = isDraft ? AgentLabels.Error : AgentLabels.Done;
        }
    }

    /// <summary>
    /// Runs the full post-PR finalization sequence: PR description → reflection → brain sync → feedback.
    /// Conditionally skips steps based on isDraft, brain provider availability, and config.
    /// Does not set CompletedAt or CurrentStep — those remain the caller's responsibility.
    /// </summary>
    // TODO: Validate non-nullable parameters (run, agentProvider, repoProvider, config, feedbackService, emitOutputLine, transitionCallback) with ArgumentNullException.ThrowIfNull for fail-fast consistency.
    public async Task RunPostPrSequenceAsync(
        PostPrSequenceRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var run = request.Run;
        var isDraft = request.IsDraft;
        var agentProvider = request.AgentProvider;
        var repoProvider = request.RepoProvider;
        var config = request.Config;
        var brainSync = request.BrainSync;
        var brainProvider = request.BrainProvider;
        var feedbackService = request.FeedbackService;
        var historyService = request.HistoryService;
        var emitOutputLine = request.EmitOutputLine;
        var transitionCallback = request.TransitionCallback;

        if (!isDraft && !string.IsNullOrEmpty(run.PullRequestNumber))
        {
            await GeneratePrDescriptionAsync(run, agentProvider, repoProvider, config, emitOutputLine, ct);

            // Mark ready-for-review last — after description is applied so reviewers see the complete body.
            // Non-fatal: a failure to mark-ready is logged but does not abort the post-PR sequence.
            if (int.TryParse(run.PullRequestNumber, out var prNum))
            {
                try
                {
                    // TODO [WARNING]: run.PullRequestBody may be stale here when GeneratePrDescriptionAsync
                    // did not update it (file missing, empty output, or invalid PR number inside
                    // GeneratePrDescriptionAsync). In those skip paths run.PullRequestBody still holds the
                    // pre-description body, causing the mark-ready PATCH to send body content that differs
                    // from what was last written to the API by GeneratePrDescriptionAsync on the happy path.
                    // Consider always keeping run.PullRequestBody in sync with the latest value sent to the
                    // API on exit from GeneratePrDescriptionAsync, or passing null for the body argument here
                    // to make this a state-change-only call that avoids overwriting with potentially stale
                    // content. The body divergence is functionally harmless (idempotent on the happy path,
                    // fallback body on skip) but obscures which body revision was used for the final PR state.
                    // TODO [WARNING]: TaskCanceledException is a subclass of OperationCanceledException, so the
                    // "when (ex is not OperationCanceledException)" filter correctly excludes both. This is the
                    // intended behavior matching all other UpdatePullRequestAsync guards in this file.
                    await repoProvider.UpdatePullRequestAsync(prNum, run.PullRequestBody ?? "", true, ct);
                    emitOutputLine($"✅ PR #{run.PullRequestNumber} marked ready for review");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} failed to mark PR ready for review, continuing", run.RunId);
                }
            }
            else
            {
                _logger.Warning("Pipeline {RunId} mark-ready skipped — PullRequestNumber '{PrNumber}' is not a valid integer",
                    run.RunId, run.PullRequestNumber);
            }
        }
        else
        {
            _logger.Information(
                "Pipeline {RunId} skipping PR description: isDraft={IsDraft}, hasPrNumber={HasPrNumber}",
                run.RunId, isDraft, !string.IsNullOrEmpty(run.PullRequestNumber));
        }

        if (!isDraft && brainProvider is not null && brainSync is not null && !config.BrainReadOnly)
        {
            await transitionCallback(PipelineStep.ReflectingOnRun);
            await RunReflectionAsync(run, agentProvider, config, emitOutputLine, ct);

            await transitionCallback(PipelineStep.SyncingBrainRepoPostRun);
            await SyncBrainPostRunAsync(run, brainSync, brainProvider, config, emitOutputLine, ct);
        }
        else
        {
            // Emit a tagged skip counter so brain.sync.skipped always appears in Prometheus
            // for runs that reach finalization, making the skip reason diagnosable without Loki.
            // Priority: isDraft wins → no_provider → no_sync_service → read_only.
            var skipReason = isDraft ? "is_draft"
                : brainProvider is null ? "no_provider"
                : brainSync is null ? "no_sync_service"
                : "read_only";
            _brainSyncSkipped.Add(1,
                new KeyValuePair<string, object?>("reason", skipReason));
            // TODO [WARNING]: The five log arguments (RunId, isDraft, brainProvider!=null, brainSync!=null, BrainReadOnly)
            // resolve to Information(string, params object[]) because Serilog's ILogger has generic overloads only up to
            // 3 type params. Moq Verify calls targeting this log must match the params-array overload, not individual
            // typed matchers, to avoid silently vacuous assertions.
            _logger.Information(
                "Pipeline {RunId} skipping brain post-run sync: isDraft={IsDraft}, brainProvider={HasProvider}, brainSync={HasSync}, brainReadOnly={ReadOnly}",
                run.RunId, isDraft, brainProvider is not null, brainSync is not null, config.BrainReadOnly);
        }

        // No step transition for feedback — intentionally matches existing behavior
        if (!isDraft)
        {
            await CollectFeedbackAsync(run, agentProvider, feedbackService, historyService, emitOutputLine, ct, config);
        }
    }

    /// <summary>
    /// Generates an agent-written PR description and updates the PR body.
    /// Does not throw on failure — logs a warning and returns.
    /// Emits <c>pipeline.step.duration{step_name="GeneratePrDescription"}</c> unconditionally (including on failure).
    /// </summary>
    public async Task GeneratePrDescriptionAsync(
        PipelineRun run, IAgentProvider agentProvider, IRepositoryProvider repoProvider,
        PipelineConfiguration config, Action<string> emitOutputLine, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("GeneratePrDescription");
        activity?.SetTag(PipelineRunIdTag, run.RunId);

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

            run.AccumulateTokenUsage(result, phase: "pr_description");

            // TODO: TOCTOU race — File.Exists followed by File.ReadAllTextAsync means the file could be deleted
            // between the two calls, causing FileNotFoundException to be caught by the outer handler with a
            // misleading log message. Prefer attempting File.ReadAllTextAsync directly and catching
            // FileNotFoundException explicitly to make the "file absent" intent distinct from unexpected failures.
            var filePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.PrDescriptionFilePath);
            if (!File.Exists(filePath))
            {
                _logger.Warning("Pipeline {RunId} PR description file not found at {Path}, using OutputLines fallback",
                    run.RunId, filePath);

                var fallbackText = StripBlockquotePrefix(string.Join("\n", result.OutputLines));
                if (!string.IsNullOrWhiteSpace(fallbackText))
                {
                    // TODO: The file-present path has the same pattern, but neither checks ct.IsCancellationRequested
                    // before the int.TryParse guard early-return. Consider adding ct.ThrowIfCancellationRequested()
                    // before the async call for consistency with general cancellation patterns.
                    if (!int.TryParse(run.PullRequestNumber, out var prNumberFallback))
                    {
                        _logger.Warning("Pipeline {RunId} PR description fallback skipped — PullRequestNumber '{PrNumber}' is not a valid integer",
                            run.RunId, run.PullRequestNumber);
                        return;
                    }
                    // TODO: When run.PullRequestBody is null (no body set before description generation), ?? ""
                    // produces an empty string and the resulting body ends with a spurious "\n\n---\n\n" separator.
                    // The file-present path at line ~371 has the same pattern. Consider omitting the separator
                    // entirely when currentBody is empty: newBody = string.IsNullOrWhiteSpace(currentBody)
                    //   ? fallbackText : $"{fallbackText}\n\n---\n\n{currentBody}".
                    var currentBodyFallback = run.PullRequestBody ?? "";
                    var newBodyFallback = $"{fallbackText}\n\n---\n\n{currentBodyFallback}";
                    await repoProvider.UpdatePullRequestAsync(prNumberFallback, newBodyFallback, null, ct);
                    run.PullRequestBody = newBodyFallback;
                    _logger.Information("Pipeline {RunId} PR description applied from OutputLines fallback", run.RunId);
                }
                else
                {
                    _logger.Warning("Pipeline {RunId} PR description fallback: OutputLines also empty, description skipped", run.RunId);
                }
                return;
            }

            var rawDescription = await File.ReadAllTextAsync(filePath, ct);
            var description = StripBlockquotePrefix(rawDescription);
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
            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, null, ct);
            run.PullRequestBody = newBody;

            _logger.Information("Pipeline {RunId} PR description generated and applied", run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} PR description generation failed, continuing", run.RunId);
        }
        finally
        {
            sw.Stop();
            var tags = PipelineTelemetry.BuildStepTags("GeneratePrDescription", run.RunType, run.ProjectId, run.ProjectName);
            _stepDuration.Record(sw.Elapsed.TotalSeconds, tags);
            _stepCount.Add(1, tags);
        }
    }

    /// <summary>
    /// Executes the reflection step: builds a reflection prompt and asks the agent to review
    /// the run and enrich .brain/ knowledge. Accumulates token usage on the run.
    /// Does not throw on failure — logs a warning and returns.
    /// Emits <c>pipeline.step.duration{step_name="Reflection"}</c> unconditionally (including on failure).
    /// </summary>
    public async Task RunReflectionAsync(
        PipelineRun run, IAgentProvider agentProvider, PipelineConfiguration config,
        Action<string> emitOutputLine, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Reflection");
        activity?.SetTag(PipelineRunIdTag, run.RunId);

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

            run.AccumulateTokenUsage(reflectionResult, phase: "reflection");
            _logger.Information("Pipeline {RunId} reflection step completed", run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} reflection step failed, continuing with brain sync", run.RunId);
        }
        finally
        {
            sw.Stop();
            var tags = PipelineTelemetry.BuildStepTags("Reflection", run.RunType, run.ProjectId, run.ProjectName);
            _stepDuration.Record(sw.Elapsed.TotalSeconds, tags);
            _stepCount.Add(1, tags);
        }
    }

    /// <summary>
    /// Syncs the brain repository after the run. Delegates to brainSync.SyncPostRunAsync.
    /// Does not throw on failure — logs a warning and sets run.BrainUpdatesPushed = false.
    /// Emits <c>pipeline.step.duration{step_name="BrainSyncPostRun"}</c> unconditionally (including on failure).
    /// </summary>
    public async Task SyncBrainPostRunAsync(
        PipelineRun run, IBrainSyncService brainSync, IRepositoryProvider brainProvider,
        PipelineConfiguration config, Action<string> emitOutputLine, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("BrainSyncPostRun");
        activity?.SetTag(PipelineRunIdTag, run.RunId);

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
        finally
        {
            sw.Stop();
            var tags = PipelineTelemetry.BuildStepTags("BrainSyncPostRun", run.RunType, run.ProjectId, run.ProjectName);
            _stepDuration.Record(sw.Elapsed.TotalSeconds, tags);
            _stepCount.Add(1, tags);
        }
    }

    /// <summary>
    /// Collects structured feedback from the agent about the run.
    /// On failure, creates a fallback feedback record via feedbackService.
    /// Emits <c>pipeline.step.duration{step_name="FeedbackCollection"}</c> unconditionally (including on failure).
    /// </summary>
    public async Task CollectFeedbackAsync(
        PipelineRun run, IAgentProvider agentProvider, FeedbackService feedbackService,
        IPipelineRunHistoryService? historyService, Action<string> emitOutputLine, CancellationToken ct,
        PipelineConfiguration config)
    {
        var sw = Stopwatch.StartNew();
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("FeedbackCollection");
        activity?.SetTag(PipelineRunIdTag, run.RunId);

        emitOutputLine("📋 Collecting run feedback...");
        try
        {
            var elapsed = DateTimeOffset.UtcNow - run.StartedAtOffset;
            var (harnessCategories, issueCategories) = await feedbackService.LoadPreviousCategoriesAsync(historyService, ct).ConfigureAwait(false);

            var feedbackPrompt = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
                run, elapsed, harnessCategories, issueCategories);

            var feedbackResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = feedbackPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = TimeSpan.FromSeconds(config.FeedbackTimeoutSeconds),
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
        finally
        {
            sw.Stop();
            var tags = PipelineTelemetry.BuildStepTags("FeedbackCollection", run.RunType, run.ProjectId, run.ProjectName);
            _stepDuration.Record(sw.Elapsed.TotalSeconds, tags);
            _stepCount.Add(1, tags);
        }
    }

    /// <summary>
    /// Strips leading blockquote prefix (<c>&gt; </c>) from each line.
    /// Kiro CLI prefixes assistant response lines with <c>&gt;</c> on stdout.
    /// Lines starting with "<c>&gt; </c>" have the prefix removed; bare "<c>&gt;</c>" lines become empty strings.
    /// Mid-line <c>&gt;</c> characters (code, comparisons) are preserved.
    /// </summary>
    private static string StripBlockquotePrefix(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var stripped = lines.Select(line =>
        {
            if (line.StartsWith("> ")) return line[2..];
            if (line == ">") return "";
            return line;
        });
        return string.Join("\n", stripped).Trim();
    }
}
