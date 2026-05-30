using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Configuration for a single adversarial review pass.
/// </summary>
public sealed record AdversarialReviewConfig
{
    /// <summary>Whether the review is enabled for this phase.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Timeout for both the review and refinement agent calls.</summary>
    public required TimeSpan AgentTimeout { get; init; }
}

/// <summary>
/// Result of an adversarial review pass, including whether review ran,
/// whether refinement was triggered, and token usage from both calls.
/// </summary>
public sealed record AdversarialReviewResult
{
    /// <summary>Whether the review was executed (false if disabled or skipped).</summary>
    public bool ReviewExecuted { get; init; }

    /// <summary>Whether refinement was triggered (CRITICAL/WARNING found).</summary>
    public bool RefinementTriggered { get; init; }

    /// <summary>Token usage from the discriminator call, or null if not executed.</summary>
    public TokenUsage? ReviewTokenUsage { get; init; }

    /// <summary>Token usage from the refinement call, or null if not executed.</summary>
    public TokenUsage? RefinementTokenUsage { get; init; }

    /// <summary>Severity counts parsed from the review findings.</summary>
    public CodeReview.SeverityCounts? Severities { get; init; }

    public static AdversarialReviewResult Skipped { get; } = new() { ReviewExecuted = false };
}

/// <summary>
/// Shared helper that encapsulates the adversarial review-and-refine pattern
/// used across all consolidation phases.
/// </summary>
public static class AdversarialReviewHelper
{
    /// <summary>
    /// Minimum character count for generator output files (diff summary, suggestions output)
    /// before the discriminator is dispatched. Prevents reviewing trivially short content.
    /// </summary>
    public const int MinimumContentThreshold = 20;

    /// <summary>
    /// Executes the review-and-refine cycle:
    /// 1. Delete existing review file (prevent stale findings)
    /// 2. Dispatch discriminator agent (UseResume=false, isolated session)
    /// 3. Read findings file, parse severity via SeverityParser
    /// 4. If CRITICAL or WARNING found: dispatch refinement (UseResume=true)
    /// 5. Return result with token usage and severity counts
    /// 
    /// All failures are caught (except OperationCanceledException) and logged.
    /// On failure, returns a result indicating review did not complete.
    /// </summary>
    public static async Task<AdversarialReviewResult> ExecuteReviewAsync(
        IAgentProvider agentProvider,
        string workspacePath,
        string reviewPrompt,
        string refinementPrompt,
        string reviewFilePath,
        AdversarialReviewConfig config,
        Action<string>? onOutputLine,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentProvider);
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(reviewPrompt);
        ArgumentNullException.ThrowIfNull(refinementPrompt);
        ArgumentNullException.ThrowIfNull(reviewFilePath);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        if (!config.Enabled)
        {
            onOutputLine?.Invoke("⏭️ Adversarial review disabled, skipping");
            return AdversarialReviewResult.Skipped;
        }

        var absoluteReviewPath = Path.Combine(workspacePath, reviewFilePath);

        // 1. Delete stale review file
        if (File.Exists(absoluteReviewPath))
            File.Delete(absoluteReviewPath);

        try
        {
            // CRITICAL 1: Capture generator's session ID before dispatching discriminator
            var generatorSessionId = await agentProvider.GetLatestSessionIdAsync(workspacePath, ct);

            // 2. Dispatch discriminator (isolated session)
            onOutputLine?.Invoke("🔍 Dispatching adversarial reviewer...");

            var reviewResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = reviewPrompt,
                    WorkspacePath = workspacePath,
                    Timeout = config.AgentTimeout,
                    UseResume = false // Isolated — no shared context
                },
                ct);

            var reviewUsage = reviewResult.Usage;

            // 2a. Check discriminator exit code — non-zero means agent crashed/failed
            if (!reviewResult.Success)
            {
                logger.Warning("Review agent exited with code {ExitCode}, skipping refinement", reviewResult.ExitCode);
                onOutputLine?.Invoke($"⚠️ Review agent exited with code {reviewResult.ExitCode} — skipping refinement");
                return new AdversarialReviewResult
                {
                    ReviewExecuted = true,
                    RefinementTriggered = false,
                    ReviewTokenUsage = reviewUsage
                };
            }

            // 3. Read and parse findings
            if (!File.Exists(absoluteReviewPath))
            {
                logger.Warning("Review agent did not write findings to {Path}, skipping refinement", reviewFilePath);
                onOutputLine?.Invoke("⚠️ Review produced no findings file — skipping refinement");
                return new AdversarialReviewResult
                {
                    ReviewExecuted = true,
                    RefinementTriggered = false,
                    ReviewTokenUsage = reviewUsage
                };
            }

            var findingsContent = await File.ReadAllTextAsync(absoluteReviewPath, ct);
            var findingsLines = findingsContent.Split('\n');
            var severities = CodeReview.SeverityParser.Parse(findingsLines);

            onOutputLine?.Invoke($"📋 Review complete: {severities.Critical} CRITICAL, " +
                                $"{severities.Warning} WARNING, {severities.Suggestion} SUGGESTION");

            // 4. Conditional refinement — only on CRITICAL or WARNING
            if (severities.Critical == 0 && severities.Warning == 0)
            {
                onOutputLine?.Invoke("✅ No critical/warning findings — skipping refinement");
                return new AdversarialReviewResult
                {
                    ReviewExecuted = true,
                    RefinementTriggered = false,
                    ReviewTokenUsage = reviewUsage,
                    Severities = severities
                };
            }

            // 5. Dispatch refinement (resume generator session)
            onOutputLine?.Invoke("📝 Refinement triggered — sending findings to generator...");

            // CRITICAL 2: Inner try-catch around refinement to preserve review usage on failure
            try
            {
                var refinementResult = await agentProvider.ExecuteAsync(
                    new AgentRequest
                    {
                        Prompt = refinementPrompt,
                        WorkspacePath = workspacePath,
                        Timeout = config.AgentTimeout,
                        UseResume = true,
                        ResumeSessionId = generatorSessionId // Explicitly target generator's session
                    },
                    ct);

                // 5a. Check refinement exit code — non-zero means refinement failed
                if (!refinementResult.Success)
                {
                    logger.Warning("Refinement agent exited with code {ExitCode}, keeping original output",
                        refinementResult.ExitCode);
                    onOutputLine?.Invoke($"⚠️ Refinement agent exited with code {refinementResult.ExitCode} — keeping original output");
                    return new AdversarialReviewResult
                    {
                        ReviewExecuted = true,
                        RefinementTriggered = false, // Treat as "not successfully refined"
                        ReviewTokenUsage = reviewUsage,
                        RefinementTokenUsage = refinementResult.Usage,
                        Severities = severities
                    };
                }

                onOutputLine?.Invoke("✅ Refinement complete");

                return new AdversarialReviewResult
                {
                    ReviewExecuted = true,
                    RefinementTriggered = true,
                    ReviewTokenUsage = reviewUsage,
                    RefinementTokenUsage = refinementResult.Usage,
                    Severities = severities
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warning(ex, "Refinement failed: {Message}", ex.Message);
                onOutputLine?.Invoke($"⚠️ Refinement failed: {ex.Message} — keeping original output");
                return new AdversarialReviewResult
                {
                    ReviewExecuted = true,
                    RefinementTriggered = false,
                    ReviewTokenUsage = reviewUsage,
                    Severities = severities
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Adversarial review failed: {Message}", ex.Message);
            onOutputLine?.Invoke($"⚠️ Review failed: {ex.Message} — proceeding with original output");
            return new AdversarialReviewResult { ReviewExecuted = false };
        }
    }
}
