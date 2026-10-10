using System.Text.Json;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// The triage investigation: the agent investigates and writes <see cref="AgentWorkspacePaths.TriageResultFilePath"/>;
/// when <see cref="PipelineConfiguration.TriageReviewEnabled"/>, an isolated reviewer checks it and the agent
/// refines it; then deterministic validation trims it. The validated result replaces the file, for
/// <see cref="ReportTriageResultStep"/>.
/// </summary>
public sealed class TriageInvestigationStep : IPipelineStep
{
    private readonly bool _reportToTracker;

    /// <param name="reportToTracker">True when the result is posted on a tracker issue.</param>
    public TriageInvestigationStep(bool reportToTracker)
    {
        _reportToTracker = reportToTracker;
    }

    public string StepName => "TriageInvestigation";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("TriageInvestigation");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());

        var run = context.Run;
        var config = context.Config;
        var logger = context.Logger;
        var resultPath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.TriageResultFilePath);

        context.Callbacks.TransitionTo(PipelineStep.Investigating);
        context.Callbacks.EmitOutputLine("🔎 Investigating the reported problem...");

        // 1. The investigation
        AgentResult? agentResult = null;
        var execResult = await context.TryCriticalAsync(async () =>
        {
            agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                new AgentMonitorContext(context.AgentProvider, run, config, "Triage agent", context.Callbacks.NotifyChange, logger),
                new AgentRequest
                {
                    Prompt = TriagePromptBuilder.BuildInvestigationPrompt(_reportToTracker, context.ProjectContext),
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = false
                },
                ct,
                line => context.Callbacks.EmitOutputLine(line),
                reportStallEvent: AgentPhaseExecutor.BuildStallEventReporter(context.ReportPipelineRunEvent),
                phase: "triage");
        }, "Agent execution", ct);

        if (execResult == StepResult.Stop)
            return StepResult.Stop;

        run.AccumulateTokenUsage(agentResult!, phase: "triage");

        if (!agentResult!.Success)
        {
            context.Callbacks.EmitOutputLine($"❌ Agent exited with code {agentResult.ExitCode}");
            await context.FailRunAsync($"Agent exited with non-zero exit code: {agentResult.ExitCode}", FailureReason.ExitCodeFailure, ct);
            return StepResult.Stop;
        }

        var parsed = await ReadResultAsync(resultPath, ct);
        if (parsed.Result is null)
        {
            context.Callbacks.EmitOutputLine($"❌ {parsed.Error}");
            await context.FailRunAsync(parsed.Error!, ct);
            return StepResult.Stop;
        }
        LogWarnings(context, parsed.Warnings);
        context.Callbacks.EmitOutputLine(
            $"✅ Result: {TriageCommentRenderer.VerdictText(parsed.Result.Verdict)}, {parsed.Result.Investigated.Count} checks, {parsed.Result.Drafts.Count} drafts");

        // 2. The adversarial review
        var result = parsed.Result;
        if (config.TriageReviewEnabled)
        {
            context.Callbacks.TransitionTo(PipelineStep.ReviewingRca);

            var review = await AdversarialReviewHelper.ExecuteReviewAsync(
                context.AgentProvider,
                run.WorkspacePath!,
                new AdversarialReviewPrompts(
                    TriagePromptBuilder.BuildReviewPrompt(_reportToTracker),
                    TriagePromptBuilder.BuildRefinementPrompt(),
                    AgentWorkspacePaths.TriageReviewFilePath),
                new AdversarialReviewConfig { Enabled = true, AgentTimeout = config.AgentTimeout },
                line => context.Callbacks.EmitOutputLine(line),
                logger,
                ct);

            if (review.ReviewTokenUsage is not null)
                run.AccumulateTokenUsage(review.ReviewTokenUsage, phase: "triage_review");
            if (review.RefinementTokenUsage is not null)
                run.AccumulateTokenUsage(review.RefinementTokenUsage, phase: "triage_refinement");

            if (!review.ReviewExecuted)
            {
                context.Callbacks.EmitOutputLine("❌ The review of the root cause analysis failed to execute");
                await context.FailRunAsync("Adversarial review failed to execute", ct);
                return StepResult.Stop;
            }

            if (review.RefinementTriggered)
            {
                var refined = await ReadResultAsync(resultPath, ct);
                if (refined.Result is not null)
                {
                    LogWarnings(context, refined.Warnings);
                    result = refined.Result;
                }
                else
                {
                    // Keep the reviewed result rather than lose the investigation over a broken rewrite
                    logger.Warning("Pipeline {RunId}: the refined triage result could not be read ({Error}); keeping the first result",
                        run.RunId, refined.Error);
                    context.Callbacks.EmitOutputLine($"⚠️ The refined result could not be read ({refined.Error}); keeping the first result");
                }
            }
        }

        // 3. Deterministic validation
        var repositories = context.ProjectContext?.Repositories ?? [];
        var executorRepository = repositories.FirstOrDefault(r => r.RepoProviderId == run.RepoProviderConfigId)?.TemplateName;
        var validated = TriageResultValidator.Validate(result, repositories.Select(r => r.TemplateName).ToList(), executorRepository);
        if (validated.Result is null)
        {
            context.Callbacks.EmitOutputLine($"❌ {validated.Error}");
            await context.FailRunAsync(validated.Error!, ct);
            return StepResult.Stop;
        }
        LogWarnings(context, validated.Warnings);

        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(validated.Result, PipelineJsonOptions.Default), ct);
        context.Callbacks.EmitOutputLine("✅ Triage investigation complete");
        return StepResult.Continue;
    }

    /// <summary>Reads and parses the result file, refusing one over <see cref="TriageConstants.MaxResultBytes"/>.</summary>
    internal static async Task<TriageResultParser.ParseOutcome> ReadResultAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return new TriageResultParser.ParseOutcome(null, $"Agent did not produce a triage result at {AgentWorkspacePaths.TriageResultFilePath}", []);

        var size = new FileInfo(path).Length;
        if (size > TriageConstants.MaxResultBytes)
            return new TriageResultParser.ParseOutcome(null,
                $"The triage result is too large ({size} bytes, at most {TriageConstants.MaxResultBytes})", []);

        return TriageResultParser.Parse(await File.ReadAllTextAsync(path, ct));
    }

    private static void LogWarnings(PipelineStepContext context, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
        {
            context.Logger.Warning("Pipeline {RunId} triage result: {Warning}", context.Run.RunId, warning);
            context.Callbacks.EmitOutputLine($"⚠️ {warning}");
        }
    }
}
