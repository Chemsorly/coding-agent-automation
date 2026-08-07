using System.Diagnostics;
using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Agent.Executors;

/// <summary>
/// Executes harness suggestion analysis: writes feedback data to a temp workspace,
/// runs the analysis agent prompt, and parses the resulting suggestions.
/// </summary>
public sealed class HarnessSuggestionExecutor : ConsolidationExecutorBase
{
    protected override string WorkspaceSuffix => "harness";
    protected override string ExecutorName => "Harness suggestion";

    public HarnessSuggestionExecutor(Serilog.ILogger logger) : base(logger)
    {
    }

    /// <summary>
    /// Executes the harness suggestion workflow:
    /// 1. Create temp workspace directory
    /// 2. If job.FeedbackDataJson is null or empty: return success with "No new feedback to analyze"
    /// 3. Write feedback data JSON to workspace file for agent context
    /// 4. Calculate feedbackCount and successRate from the data
    /// 5. Build prompt via ConsolidationPromptBuilder.BuildHarnessSuggestionPrompt
    /// 6. Execute agent in temp workspace
    /// 7. Write output to file (UseResume=true), then review via AdversarialReviewHelper
    /// 8. Parse suggestions from file (or fall back to response text)
    /// 9. Return result with HarnessSuggestions populated
    /// </summary>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        IAgentProvider agentProvider,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(agentProvider);

        // 2. If no feedback data: return success early
        if (string.IsNullOrWhiteSpace(job.FeedbackDataJson))
        {
            Logger.Information("No feedback data for harness suggestion run {RunId}, skipping agent call", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = "No new feedback to analyze"
            };
        }

        var invalid = ValidateJobId(job);
        if (invalid is not null) return invalid;

        var workspacePath = ResolveWorkspacePath(job);

        return await WrapWithCancellationHandlingAsync(job.JobId, async () =>
        {
            // 1. Create temp workspace
            Directory.CreateDirectory(workspacePath);

            // 3. Write feedback data to workspace
            var feedbackFilePath = Path.Combine(workspacePath, "feedback-data.json");
            await File.WriteAllTextAsync(feedbackFilePath, job.FeedbackDataJson, ct);

            // 4. Calculate feedbackCount and successRate from the data
            var (feedbackCount, successRate) = CalculateFeedbackMetrics(job.FeedbackDataJson);

            // 5. Build prompt
            var prompt = ConsolidationPromptBuilder.BuildHarnessSuggestionPrompt(feedbackCount, successRate);

            // 6. Execute agent
            Logger.Information("Executing harness suggestion agent for run {RunId} ({FeedbackCount} runs, {SuccessRate:F1}% success rate)",
                job.JobId, feedbackCount, successRate);
            AgentResult agentResult;
            ConsolidationJobResult? failure;
            (agentResult, failure) = await RunWithTracingAsync("HarnessSuggestion.AgentExecution", job.JobId, async _ =>
            {
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = prompt,
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout
                    },
                    job.JobId,
                    ct);
            });

            if (failure is not null) return failure;

            // 7+8. Write-to-file, adversarial review, and parse suggestions
            var responseText = string.Join("\n", agentResult.OutputLines);
            var suggestionsOutputPath = Path.Combine(workspacePath, AgentWorkspacePaths.HarnessSuggestionsOutputFilePath);

            var (suggestions, reviewTokenUsage, refinementTokenUsage) = await WriteReviewAndParseSuggestionsAsync(
                job, agentProvider, workspacePath, suggestionsOutputPath, responseText, onOutputLine, ct);

            if (suggestions is null)
            {
                Logger.Warning("Failed to parse harness suggestions from agent output for run {RunId}", job.JobId);
                return new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorMessage = "Failed to parse harness suggestions from agent output",
                    ReviewTokenUsage = reviewTokenUsage,
                    RefinementTokenUsage = refinementTokenUsage
                };
            }

            // 9. Return result with suggestions
            var summary = $"Generated {suggestions.Suggestions.Count} suggestion(s) from {suggestions.BasedOnRunCount} runs ({suggestions.SuccessRate:F1}% success rate)";
            Logger.Information("{ExecutorName} run {RunId} completed: {Summary}", ExecutorName, job.JobId, summary);

            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = summary,
                HarnessSuggestions = suggestions,
                ReviewTokenUsage = reviewTokenUsage,
                RefinementTokenUsage = refinementTokenUsage
            };
        }, ct);
    }

    /// <summary>
    /// Executes the write-to-file step, adversarial review, and parses suggestions from the result.
    /// Returns (suggestions, reviewTokenUsage, refinementTokenUsage).
    /// </summary>
    private async Task<(HarnessSuggestions? Suggestions, TokenUsage? ReviewTokenUsage, TokenUsage? RefinementTokenUsage)>
        WriteReviewAndParseSuggestionsAsync(
            ConsolidationJobMessage job, IAgentProvider agentProvider, string workspacePath,
            string suggestionsOutputPath, string responseText, Action<string>? onOutputLine, CancellationToken ct)
    {
        HarnessSuggestions? suggestions = null;
        TokenUsage? reviewTokenUsage = null;
        TokenUsage? refinementTokenUsage = null;
        var skipReview = false;

        using (var writeActivity = PipelineTelemetry.ActivitySource.StartActivity("HarnessSuggestion.WriteToFile"))
        {
            writeActivity?.SetTag("pipeline.run_id", job.JobId);
            try
            {
                onOutputLine?.Invoke("📄 Instructing agent to write suggestions to file...");
                await agentProvider.ExecuteAsync(
                    new AgentRequest
                    {
                        Prompt = "Write your harness improvement suggestions as a JSON array to the file `.agent/harness-suggestions-output.json`. Use the same JSON format you used in your response. Do not modify any other files.",
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout,
                        UseResume = true
                    }, ct, onOutputLine);

                if (!File.Exists(suggestionsOutputPath))
                {
                    Logger.Warning("Agent did not write suggestions output file for run {RunId}, falling back to response text parsing", job.JobId);
                    onOutputLine?.Invoke("⚠️ Suggestions output file not created — falling back to response text parsing");
                    skipReview = true;
                }
                else
                {
                    var fileContent = await File.ReadAllTextAsync(suggestionsOutputPath, ct);
                    if (fileContent.Trim().Length < AdversarialReviewHelper.MinimumContentThreshold)
                    {
                        Logger.Warning("Suggestions output file too short ({Length} chars) for run {RunId}, falling back to response text parsing",
                            fileContent.Trim().Length, job.JobId);
                        onOutputLine?.Invoke("⚠️ Suggestions output file too short — falling back to response text parsing");
                        skipReview = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                writeActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                writeActivity?.AddException(ex);
                Logger.Warning(ex, "Write-to-file step failed for run {RunId}: {Message}, falling back to response text parsing", job.JobId, ex.Message);
                onOutputLine?.Invoke($"⚠️ Write-to-file failed: {ex.Message} — falling back to response text parsing");
                skipReview = true;
            }
        }

        if (!skipReview)
        {
            await RunWithTracingAsync("HarnessSuggestion.AdversarialReview", job.JobId, async _ =>
            {
                var reviewResult = await AdversarialReviewHelper.ExecuteReviewAsync(
                    agentProvider, workspacePath,
                    ConsolidationPromptBuilder.BuildHarnessSuggestionsReviewPrompt(),
                    ConsolidationPromptBuilder.BuildHarnessSuggestionsRefinementPrompt(),
                    AgentWorkspacePaths.HarnessSuggestionsReviewFilePath,
                    new AdversarialReviewConfig
                    {
                        Enabled = job.PipelineConfiguration.HarnessSuggestionsReviewEnabled,
                        AgentTimeout = job.PipelineConfiguration.AgentTimeout
                    },
                    onOutputLine, Logger, ct);

                reviewTokenUsage = reviewResult.ReviewTokenUsage;
                refinementTokenUsage = reviewResult.RefinementTokenUsage;

                suggestions = await TryParseSuggestionsFromFileAsync(
                    suggestionsOutputPath, reviewResult.RefinementTriggered, job.JobId, onOutputLine, ct);

                return (object?)null;
            });
        }

        suggestions ??= ParseSuggestions(responseText);
        return (suggestions, reviewTokenUsage, refinementTokenUsage);
    }

    private async Task<HarnessSuggestions?> TryParseSuggestionsFromFileAsync(
        string path, bool refinementTriggered, string jobId, Action<string>? onOutputLine, CancellationToken ct)
    {
        var label = refinementTriggered ? "Refined" : "";
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var parsed = ParseSuggestions(json);
            if (parsed is not null)
            {
                if (refinementTriggered) Logger.Information("Using refined suggestions for run {RunId}", jobId);
                return parsed;
            }
            Logger.Warning("{Label} suggestions file is malformed for run {RunId}, falling back to response text parsing", label, jobId);
            onOutputLine?.Invoke($"⚠️ {label} suggestions file malformed — falling back to response text parsing".TrimStart());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Failed to read {Label} suggestions file for run {RunId}, falling back to response text parsing", label, jobId);
            onOutputLine?.Invoke($"⚠️ Failed to re-read {label} suggestions — falling back to response text parsing".TrimStart());
        }
        return null;
    }

    /// <summary>
    /// Calculates feedback count and success rate from the raw feedback JSON data.
    /// The feedback data is expected to be a JSON array of objects with an "outcome" field.
    /// </summary>
    internal static (int FeedbackCount, decimal SuccessRate) CalculateFeedbackMetrics(string feedbackDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(feedbackDataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (0, 0m);

            var totalCount = doc.RootElement.GetArrayLength();
            if (totalCount == 0)
                return (0, 0m);

            var successCount = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("outcome", out var outcome) ||
                    element.TryGetProperty("Outcome", out outcome))
                {
                    var outcomeStr = outcome.GetString();
                    if (string.Equals(outcomeStr, "Success", StringComparison.OrdinalIgnoreCase))
                        successCount++;
                }
            }

            var successRate = (decimal)successCount / totalCount * 100m;
            return (totalCount, successRate);
        }
        catch (JsonException)
        {
            return (0, 0m);
        }
    }

    /// <summary>
    /// Parses the HarnessSuggestions model from the agent's response text.
    /// Extracts JSON from fenced code blocks or bare JSON objects.
    /// </summary>
    internal static HarnessSuggestions? ParseSuggestions(string responseText)
    {
        var jsonBlock = JsonBlockExtractor.Extract(responseText,
            c => c.Contains("suggestions", StringComparison.OrdinalIgnoreCase));
        if (jsonBlock is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<HarnessSuggestions>(jsonBlock, PipelineJsonOptions.Lenient);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
