using System.Text.Json;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Agent.Executors;

/// <summary>
/// Executes harness suggestion analysis: writes feedback data to a temp workspace,
/// runs the analysis agent prompt, and parses the resulting suggestions.
/// </summary>
public sealed class HarnessSuggestionExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Serilog.ILogger _logger;

    public HarnessSuggestionExecutor(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
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
            _logger.Information("No feedback data for harness suggestion run {RunId}, skipping agent call", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = "No new feedback to analyze"
            };
        }

        // WARNING 5 fix: Validate job ID is a valid GUID to prevent path traversal
        if (!Guid.TryParse(job.JobId, out _))
        {
            _logger.Warning("Invalid JobId format: {JobId}", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Invalid JobId format"
            };
        }

        // CRITICAL 3 fix: Use workspace path from job message, fall back to temp path
        var workspacePath = job.WorkspacePath is not null
            ? Path.Combine(job.WorkspacePath, "harness")
            : Path.Combine(Path.GetTempPath(), "consolidation", job.JobId, "harness");

        try
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
            _logger.Information("Executing harness suggestion agent for run {RunId} ({FeedbackCount} runs, {SuccessRate:F1}% success rate)",
                job.JobId, feedbackCount, successRate);
            var agentResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = workspacePath,
                    Timeout = job.PipelineConfiguration.AgentTimeout
                },
                ct,
                onOutputLine);

            if (!agentResult.Success)
            {
                _logger.Warning("Harness suggestion agent exited with code {ExitCode} for run {RunId}",
                    agentResult.ExitCode, job.JobId);
                return new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorMessage = $"Agent exited with code {agentResult.ExitCode}"
                };
            }

            // 7. Write output to file step + adversarial review
            var responseText = string.Join("\n", agentResult.OutputLines);
            var suggestionsOutputPath = Path.Combine(workspacePath, AgentWorkspacePaths.HarnessSuggestionsOutputFilePath);
            var skipReview = false;
            TokenUsage? reviewTokenUsage = null;
            TokenUsage? refinementTokenUsage = null;

            // 7a. Write-to-file step (wrapped in try-catch for error isolation)
            try
            {
                onOutputLine?.Invoke("📄 Instructing agent to write suggestions to file...");
                var writeResult = await agentProvider.ExecuteAsync(
                    new AgentRequest
                    {
                        Prompt = "Write your harness improvement suggestions as a JSON array to the file `.agent/harness-suggestions-output.json`. Use the same JSON format you used in your response. Do not modify any other files.",
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout,
                        UseResume = true
                    },
                    ct,
                    onOutputLine);

                if (!File.Exists(suggestionsOutputPath))
                {
                    _logger.Warning("Agent did not write suggestions output file for run {RunId}, falling back to response text parsing", job.JobId);
                    onOutputLine?.Invoke("⚠️ Suggestions output file not created — falling back to response text parsing");
                    skipReview = true;
                }
                else
                {
                    var fileContent = await File.ReadAllTextAsync(suggestionsOutputPath, ct);
                    if (fileContent.Trim().Length < AdversarialReviewHelper.MinimumContentThreshold)
                    {
                        _logger.Warning("Suggestions output file too short ({Length} chars) for run {RunId}, falling back to response text parsing",
                            fileContent.Trim().Length, job.JobId);
                        onOutputLine?.Invoke("⚠️ Suggestions output file too short — falling back to response text parsing");
                        skipReview = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Write-to-file step failed for run {RunId}: {Message}, falling back to response text parsing",
                    job.JobId, ex.Message);
                onOutputLine?.Invoke($"⚠️ Write-to-file failed: {ex.Message} — falling back to response text parsing");
                skipReview = true;
            }

            // 7b. Adversarial review step
            HarnessSuggestions? suggestions = null;
            if (!skipReview)
            {
                // Delete stale review file before dispatching discriminator
                var reviewAbsolutePath = Path.Combine(workspacePath, AgentWorkspacePaths.HarnessSuggestionsReviewFilePath);
                if (File.Exists(reviewAbsolutePath))
                    File.Delete(reviewAbsolutePath);

                var reviewResult = await AdversarialReviewHelper.ExecuteReviewAsync(
                    agentProvider,
                    workspacePath,
                    ConsolidationPromptBuilder.BuildHarnessSuggestionsReviewPrompt(),
                    ConsolidationPromptBuilder.BuildHarnessSuggestionsRefinementPrompt(),
                    AgentWorkspacePaths.HarnessSuggestionsReviewFilePath,
                    new AdversarialReviewConfig
                    {
                        Enabled = job.PipelineConfiguration.HarnessSuggestionsReviewEnabled,
                        AgentTimeout = job.PipelineConfiguration.AgentTimeout
                    },
                    onOutputLine,
                    _logger,
                    ct);

                reviewTokenUsage = reviewResult.ReviewTokenUsage;
                refinementTokenUsage = reviewResult.RefinementTokenUsage;

                // 7c. If refinement triggered, re-read and re-parse suggestions file
                if (reviewResult.RefinementTriggered)
                {
                    try
                    {
                        var refinedJson = await File.ReadAllTextAsync(suggestionsOutputPath, ct);
                        var refinedSuggestions = ParseSuggestions(refinedJson);
                        if (refinedSuggestions is not null)
                        {
                            suggestions = refinedSuggestions;
                            _logger.Information("Using refined suggestions for run {RunId}", job.JobId);
                        }
                        else
                        {
                            _logger.Warning("Refined suggestions file is malformed for run {RunId}, falling back to response text parsing", job.JobId);
                            onOutputLine?.Invoke("⚠️ Refined suggestions file malformed — falling back to response text parsing");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Failed to re-read refined suggestions file for run {RunId}, falling back to response text parsing", job.JobId);
                        onOutputLine?.Invoke("⚠️ Failed to re-read refined suggestions — falling back to response text parsing");
                    }
                }
                else
                {
                    // No refinement — parse from the output file written in step 7a
                    try
                    {
                        var fileJson = await File.ReadAllTextAsync(suggestionsOutputPath, ct);
                        var fileSuggestions = ParseSuggestions(fileJson);
                        if (fileSuggestions is not null)
                        {
                            suggestions = fileSuggestions;
                        }
                        else
                        {
                            _logger.Warning("Suggestions output file is malformed for run {RunId}, falling back to response text parsing", job.JobId);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Failed to read suggestions output file for run {RunId}, falling back to response text parsing", job.JobId);
                    }
                }
            }

            // 8. Fall back to response text parsing if no suggestions parsed from file
            if (suggestions is null)
            {
                suggestions = ParseSuggestions(responseText);
            }

            if (suggestions is null)
            {
                _logger.Warning("Failed to parse harness suggestions from agent output for run {RunId}", job.JobId);
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
            _logger.Information("Harness suggestion run {RunId} completed: {Summary}", job.JobId, summary);

            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = summary,
                HarnessSuggestions = suggestions,
                ReviewTokenUsage = reviewTokenUsage,
                RefinementTokenUsage = refinementTokenUsage
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Consolidation run was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Harness suggestion run {RunId} failed: {Message}", job.JobId, ex.Message);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
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
        var jsonBlock = ExtractJsonBlock(responseText);
        if (jsonBlock is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<HarnessSuggestions>(jsonBlock, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a JSON block from the response text.
    /// Tries fenced JSON blocks first (```json ... ```), then bare JSON objects.
    /// Handles string literals correctly when counting braces.
    /// </summary>
    private static string? ExtractJsonBlock(string responseText)
    {
        // Try fenced JSON block first (with timeout to prevent catastrophic backtracking)
        var fencedMatch = Regex.Match(responseText, @"```(?:json)?\s*\n([\s\S]*?)\n\s*```",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        if (fencedMatch.Success)
        {
            var candidate = fencedMatch.Groups[1].Value.Trim();
            if (candidate.StartsWith('{'))
                return candidate;
        }

        // Fall back to bare JSON object — search all balanced blocks until one matches
        var searchStart = 0;
        while (searchStart < responseText.Length)
        {
            var braceStart = responseText.IndexOf('{', searchStart);
            if (braceStart < 0)
                break;

            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = braceStart; i < responseText.Length; i++)
            {
                var c = responseText[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (c == '{') depth++;
                else if (c == '}') depth--;

                if (depth == 0)
                {
                    var candidate = responseText[braceStart..(i + 1)];
                    // Validate it looks like a suggestions object
                    if (candidate.Contains("suggestions", StringComparison.OrdinalIgnoreCase))
                        return candidate;
                    // Not a match — continue searching from after this block
                    searchStart = i + 1;
                    break;
                }
            }

            // If we never found a matching close brace, stop searching
            if (depth != 0)
                break;
        }

        return null;
    }
}
