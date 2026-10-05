#pragma warning disable CS0618 // Obsolete StartedAt used intentionally for round-trip

using System.Text.Json;
using CodingAgent.Pipeline.Models;
using StackExchange.Redis;

namespace CodingAgent.Orchestration;

/// <summary>
/// Serialization helpers for converting <see cref="PipelineRun"/> to/from a Redis Hash.
///
/// <para>
/// <b>Excluded fields</b> (stored in separate Redis Lists or omitted):
/// <list type="bullet">
///   <item><c>OutputLines</c>, <c>ChatHistory</c>, <c>QualityGateHistory</c>, <c>RetryErrors</c> —
///     stored in <c>run:{id}:output</c>, <c>:chat</c>, <c>:qg</c>, <c>:retryerrors</c> Redis Lists.
///     Reconstructed empty by <see cref="FromHash"/>; <c>RemoveRun</c> hydrates them from Redis before
///     returning to callers that persist to Postgres history.</item>
///   <item><c>StartedAt</c> — deprecated shadow of <see cref="PipelineRun.StartedAtOffset"/>; omitted.</item>
///   <item><c>CompletedAt</c> — deprecated shadow of <see cref="PipelineRun.CompletedAtOffset"/>; omitted.</item>
///   <item><c>_startedAtLock</c>, <c>Metrics</c> internal fields — not serialized.</item>
/// </list>
/// </para>
/// </summary>
public static class PipelineRunHashExtensions
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    // ── ToHashEntries ─────────────────────────────────────────────────

    /// <summary>Converts a <see cref="PipelineRun"/> to a flat Redis Hash field array.</summary>
    public static HashEntry[] ToHashEntries(this PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return
        [
            // Required init-only
            F("runId",                      run.RunId),
            F("issueIdentifier",            run.IssueIdentifier.Value),
            F("issueProviderConfigId",      run.IssueProviderConfigId),
            F("repoProviderConfigId",       run.RepoProviderConfigId),

            // Nullable init-only
            F("brainProviderConfigId",      run.BrainProviderConfigId ?? ""),
            F("agentProviderConfigId",      run.AgentProviderConfigId ?? ""),
            F("reviewPrBranchName",         run.ReviewPrBranchName ?? ""),
            F("reviewPrTargetBranch",       run.ReviewPrTargetBranch ?? ""),
            F("reviewPrUrl",                run.ReviewPrUrl ?? ""),
            F("reviewPrDescription",        run.ReviewPrDescription ?? ""),
            F("reviewPrAuthor",             run.ReviewPrAuthor ?? ""),
            F("decompositionSource",        run.DecompositionSource ?? ""),
            F("initiatedBy",               run.InitiatedBy),

            // Enums
            F("runType",                    run.RunType.ToString()),

            // Volatile/Interlocked scalars — use property accessor
            F("currentStep",                ((int)run.CurrentStep).ToString()),
            F("highWaterMark",              ((int)run.HighWaterMark).ToString()),
            F("startedAtOffset",            run.StartedAtOffset.ToString("O")),
            F("lastStepChangeAt",           run.LastStepChangeAt.ToString("O")),
            F("completedAtOffset",          run.CompletedAtOffset?.ToString("O") ?? ""),

            // Interlocked code review counts
            F("codeReviewCriticalCount",    run.CodeReviewCriticalCount.ToString()),
            F("codeReviewWarningCount",     run.CodeReviewWarningCount.ToString()),
            F("codeReviewSuggestionCount",  run.CodeReviewSuggestionCount.ToString()),

            // Nullable strings
            F("issueTitle",                 run.IssueTitle ?? ""),
            F("issueUrl",                   run.IssueUrl ?? ""),
            F("agentId",                    run.AgentId ?? ""),
            F("branchName",                 run.BranchName ?? ""),
            F("failureReason",              run.FailureReason ?? ""),
            F("pullRequestUrl",             run.PullRequestUrl ?? ""),
            F("pullRequestBody",            run.PullRequestBody ?? ""),
            F("pullRequestNumber",          run.PullRequestNumber ?? ""),
            F("workspacePath",              run.WorkspacePath ?? ""),
            F("modelName",                  run.ModelName ?? ""),
            F("repositoryName",             run.RepositoryName ?? ""),
            F("codegenSessionId",           run.CodegenSessionId ?? ""),
            F("finalLabel",                 run.FinalLabel ?? ""),
            F("codeReviewChangeSummary",    run.CodeReviewChangeSummary ?? ""),
            F("codeReviewVerdictSummary",   run.CodeReviewVerdictSummary ?? ""),
            F("resolvedProfileId",          run.ResolvedProfileId ?? ""),
            F("pipelineProviderConfigId",   run.PipelineProviderConfigId ?? ""),
            F("projectId",                  run.ProjectId ?? ""),
            F("projectName",                run.ProjectName ?? ""),

            // Integers
            F("retryCount",                 run.RetryCount.ToString()),
            F("infrastructureRetryCount",   run.InfrastructureRetryCount.ToString()),
            F("codeReviewIterationsCompleted", run.CodeReviewIterationsCompleted.ToString()),
            F("codeReviewIterationInProgress", run.CodeReviewIterationInProgress.ToString()),
            F("codeReviewIterationsTotal",  run.CodeReviewIterationsTotal.ToString()),
            F("inlineCommentsPosted",       run.InlineCommentsPosted.ToString()),
            F("filesChangedCount",          run.FilesChangedCount.ToString()),
            F("linesAdded",                 run.LinesAdded.ToString()),
            F("linesRemoved",               run.LinesRemoved.ToString()),
            F("brainKnowledgeFileCount",    run.BrainKnowledgeFileCount.ToString()),
            F("brainFilesCommitted",        run.BrainFilesCommitted.ToString()),
            F("decompSubIssuesCreated",     run.DecompositionSubIssuesCreated.ToString()),
            F("decompSubIssuesAttempted",   run.DecompositionSubIssuesAttempted.ToString()),
            F("openIssuesDownloaded",       run.OpenIssuesDownloaded.ToString()),

            // Longs
            F("totalTokens",                run.TotalTokens.ToString()),
            F("cacheReadTokens",            run.CacheReadTokens.ToString()),
            F("cacheWriteTokens",           run.CacheWriteTokens.ToString()),

            // Decimals
            F("totalCost",                  run.TotalCost?.ToString("G", System.Globalization.CultureInfo.InvariantCulture) ?? ""),

            // Booleans
            F("brainContextLoaded",         run.BrainContextLoaded.ToString()),
            F("brainUpdatesPushed",         run.BrainUpdatesPushed.ToString()),
            F("isDraftPr",                  run.IsDraftPr.ToString()),
            F("analysisSkipped",            run.AnalysisSkipped.ToString()),
            F("mergeForceResolved",         run.MergeForceResolved.ToString()),
            F("inlineCommentsDegraded",     run.InlineCommentsDegraded.ToString()),
            F("inlineCommentsDegradedReason", run.InlineCommentsDegradedReason ?? ""),
            F("baselineHealthPassed",       run.BaselineHealthPassed?.ToString() ?? ""),

            // JSON sub-objects
            J("latestQualityReport",        run.LatestQualityReport),
            J("linkedPullRequest",          run.LinkedPullRequest),
            JEnum("analysisRecommendation", run.AnalysisRecommendation),
            J("acceptanceCriteriaReport",   run.AcceptanceCriteriaReport),
            J("brainValidation",            run.BrainValidation),
            J("feedback",                   run.Feedback),
            J("issueLabels",                run.IssueLabels),
            J("blacklistedFilesDetected",   run.BlacklistedFilesDetected),
            J("mergeConflictFiles",         run.MergeConflictFiles),
            J("subIssueResults",            run.SubIssueResults),
            J("analysisConcerns",           run.AnalysisConcerns),
            J("analysisBlockingIssues",     run.AnalysisBlockingIssues),
            J("codeReviewAgentsRun",        run.CodeReviewAgentsRun),
            J("resolvedQualityGateConfigIds", run.ResolvedQualityGateConfigIds),
            J("resolvedReviewerConfigIds",  run.ResolvedReviewerConfigIds),
            J("codeReviewAgentFindings",    run.CodeReviewAgentFindings),

            // Consolidation result fields
            JEnum("consolidationType",          run.ConsolidationType),
            F("consolidationTemplateId",        run.ConsolidationTemplateId ?? ""),
            F("consolidationResultSummary",     run.ConsolidationResultSummary ?? ""),
        ];
    }

    // ── FromHash ──────────────────────────────────────────────────────

    /// <summary>
    /// Reconstructs a <see cref="PipelineRun"/> from a Redis Hash field array.
    /// Returns null if required fields are missing (partial/corrupt hash).
    /// Queue fields (<c>OutputLines</c>, <c>ChatHistory</c>, etc.) are left empty.
    /// </summary>
    public static PipelineRun? FromHash(HashEntry[] hash)
    {
        if (hash is null || hash.Length == 0) return null;

        var r = new RedisHashReader(hash);

        // Required init-only fields — null means corrupt hash
        var runId = r.RequiredString("runId"); if (runId is null) return null;
        var issueIdStr = r.RequiredString("issueIdentifier"); if (issueIdStr is null) return null;
        var issuePcId = r.RequiredString("issueProviderConfigId"); if (issuePcId is null) return null;
        var repoPcId = r.RequiredString("repoProviderConfigId"); if (repoPcId is null) return null;

        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier(issueIdStr),
            IssueProviderConfigId = issuePcId,
            RepoProviderConfigId = repoPcId,
            // IssueTitle is serialised as "" when null — restore "" (not null) on missing/empty
            IssueTitle = r.OptionalString("issueTitle") ?? "",
            BrainProviderConfigId = r.OptionalString("brainProviderConfigId"),
            AgentProviderConfigId = r.OptionalString("agentProviderConfigId"),
            ReviewPrBranchName = r.OptionalString("reviewPrBranchName"),
            ReviewPrTargetBranch = r.OptionalString("reviewPrTargetBranch"),
            ReviewPrUrl = r.OptionalString("reviewPrUrl"),
            ReviewPrDescription = r.OptionalString("reviewPrDescription"),
            ReviewPrAuthor = r.OptionalString("reviewPrAuthor"),
            DecompositionSource = r.OptionalString("decompositionSource"),
            // InitiatedBy must never be null — used in routing/display logic without null guards
            InitiatedBy = r.OptionalString("initiatedBy") ?? InitiatedByConstants.Manual,
            RunType = r.Enum<PipelineRunType>("runType"),
        };

        ApplyVolatileFields(run, r);
        ApplyNullableStrings(run, r);
        ApplyRetryAndReviewCounters(run, r);
        ApplyChangeAndDecompositionCounters(run, r);
        ApplyTokenUsage(run, r);
        ApplyBooleans(run, r);
        ApplyJsonSubObjects(run, r);

        // Consolidation result fields
        run.ConsolidationType = r.EnumOrNull<ConsolidationRunType>("consolidationType");
        run.ConsolidationTemplateId = r.OptionalString("consolidationTemplateId");
        run.ConsolidationResultSummary = r.OptionalString("consolidationResultSummary");

        return run;
    }

    // Volatile/Interlocked fields — use property setters
    private static void ApplyVolatileFields(PipelineRun run, RedisHashReader r)
    {
        // PipelineStep is stored as an integer string (e.g. "8"), so Enum.TryParse handles
        // both name strings and numeric strings — use EnumOrNull to skip assignment on missing key.
        var step = r.EnumOrNull<PipelineStep>("currentStep");
        if (step.HasValue) run.CurrentStep = step.Value;

        var hwm = r.EnumOrNull<PipelineStep>("highWaterMark");
        if (hwm.HasValue) run.HighWaterMark = hwm.Value;

        // Use DateTimeOffsetOrNull so the guard matches parse-success (like the original
        // DateTimeOffset.TryParse guard), not value-equality against default(DateTimeOffset).
        var sao = r.DateTimeOffsetOrNull("startedAtOffset");
        if (sao.HasValue) run.ResetStartedAt(sao.Value);

        var lsca = r.DateTimeOffsetOrNull("lastStepChangeAt");
        if (lsca.HasValue) run.LastStepChangeAt = lsca.Value;

        var cao = r.DateTimeOffsetOrNull("completedAtOffset");
        if (cao.HasValue) run.MarkCompleted(cao.Value);

        // Code review counts (Interlocked) — parse each independently
        run.SetCodeReviewCounts(
            r.Int("codeReviewCriticalCount"),
            r.Int("codeReviewWarningCount"),
            r.Int("codeReviewSuggestionCount"));

        // AgentId (volatile)
        run.AgentId = r.OptionalString("agentId");
    }

    // Nullable strings
    private static void ApplyNullableStrings(PipelineRun run, RedisHashReader r)
    {
        run.BranchName = r.OptionalString("branchName");
        run.FailureReason = r.OptionalString("failureReason");
        run.IssueUrl = r.OptionalString("issueUrl");
        run.PullRequestUrl = r.OptionalString("pullRequestUrl");
        run.PullRequestBody = r.OptionalString("pullRequestBody");
        run.PullRequestNumber = r.OptionalString("pullRequestNumber");
        run.WorkspacePath = r.OptionalString("workspacePath");
        run.ModelName = r.OptionalString("modelName");
        run.RepositoryName = r.OptionalString("repositoryName");
        run.CodegenSessionId = r.OptionalString("codegenSessionId");
        run.FinalLabel = r.OptionalString("finalLabel");
        run.CodeReviewChangeSummary = r.OptionalString("codeReviewChangeSummary");
        run.CodeReviewVerdictSummary = r.OptionalString("codeReviewVerdictSummary");
        run.ResolvedProfileId = r.OptionalString("resolvedProfileId");
        run.PipelineProviderConfigId = r.OptionalString("pipelineProviderConfigId");
        run.ProjectId = r.OptionalString("projectId");
        run.ProjectName = r.OptionalString("projectName");
        run.InlineCommentsDegradedReason = r.OptionalString("inlineCommentsDegradedReason");
    }

    // Integers — retry and code review progress counters
    private static void ApplyRetryAndReviewCounters(PipelineRun run, RedisHashReader r)
    {
        run.RetryCount = r.Int("retryCount");
        run.InfrastructureRetryCount = r.Int("infrastructureRetryCount");
        run.CodeReviewIterationsCompleted = r.Int("codeReviewIterationsCompleted");
        run.CodeReviewIterationInProgress = r.Int("codeReviewIterationInProgress");
        run.CodeReviewIterationsTotal = r.Int("codeReviewIterationsTotal");
        run.InlineCommentsPosted = r.Int("inlineCommentsPosted");
    }

    // Integers — change, brain and decomposition statistics
    private static void ApplyChangeAndDecompositionCounters(PipelineRun run, RedisHashReader r)
    {
        run.FilesChangedCount = r.Int("filesChangedCount");
        run.LinesAdded = r.Int("linesAdded");
        run.LinesRemoved = r.Int("linesRemoved");
        run.BrainKnowledgeFileCount = r.Int("brainKnowledgeFileCount");
        run.BrainFilesCommitted = r.Int("brainFilesCommitted");
        run.DecompositionSubIssuesCreated = r.Int("decompSubIssuesCreated");
        run.DecompositionSubIssuesAttempted = r.Int("decompSubIssuesAttempted");
        run.OpenIssuesDownloaded = r.Int("openIssuesDownloaded");
    }

    // Longs and decimal — token usage and cost
    private static void ApplyTokenUsage(PipelineRun run, RedisHashReader r)
    {
        run.TotalTokens = r.Long("totalTokens");
        run.CacheReadTokens = r.Long("cacheReadTokens");
        run.CacheWriteTokens = r.Long("cacheWriteTokens");
        run.TotalCost = r.Decimal("totalCost");
    }

    // Booleans
    private static void ApplyBooleans(PipelineRun run, RedisHashReader r)
    {
        run.BrainContextLoaded = r.Bool("brainContextLoaded");
        run.BrainUpdatesPushed = r.Bool("brainUpdatesPushed");
        run.IsDraftPr = r.Bool("isDraftPr");
        run.AnalysisSkipped = r.Bool("analysisSkipped");
        run.MergeForceResolved = r.Bool("mergeForceResolved");
        run.InlineCommentsDegraded = r.Bool("inlineCommentsDegraded");
        run.BaselineHealthPassed = r.BoolNullable("baselineHealthPassed");
    }

    // JSON sub-objects
    private static void ApplyJsonSubObjects(PipelineRun run, RedisHashReader r)
    {
        run.LatestQualityReport = r.Json<QualityGateReport>("latestQualityReport");
        run.LinkedPullRequest = r.Json<LinkedPullRequest>("linkedPullRequest");
        run.AnalysisRecommendation = r.EnumOrNull<AnalysisGateResult>("analysisRecommendation");
        run.AcceptanceCriteriaReport = r.Json<AcceptanceCriteriaReport>("acceptanceCriteriaReport");
        run.BrainValidation = r.Json<BrainValidationResult>("brainValidation");
        run.Feedback = r.Json<RunFeedback>("feedback");

        run.IssueLabels = r.Json<List<string>>("issueLabels") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.BlacklistedFilesDetected = r.Json<List<string>>("blacklistedFilesDetected") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.MergeConflictFiles = r.Json<List<string>>("mergeConflictFiles") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.SubIssueResults = r.Json<List<SubIssueCreationResult>>("subIssueResults") ?? (IReadOnlyList<SubIssueCreationResult>)Array.Empty<SubIssueCreationResult>();
        run.AnalysisConcerns = r.Json<List<string>>("analysisConcerns") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.AnalysisBlockingIssues = r.Json<List<string>>("analysisBlockingIssues") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.CodeReviewAgentsRun = r.Json<List<string>>("codeReviewAgentsRun") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.ResolvedQualityGateConfigIds = r.Json<List<string>>("resolvedQualityGateConfigIds") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.ResolvedReviewerConfigIds = r.Json<List<string>>("resolvedReviewerConfigIds") ?? (IReadOnlyList<string>)Array.Empty<string>();

        var findingsDict = r.Json<Dictionary<string, string>>("codeReviewAgentFindings");
        if (findingsDict is not null)
            foreach (var kv in findingsDict)
                run.CodeReviewAgentFindings[kv.Key] = kv.Value;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static HashEntry F(string name, string value) => new(name, value);

    private static HashEntry J<T>(string name, T? value) where T : class
        => new(name, value is null ? "" : JsonSerializer.Serialize(value, JsonOpts));

    private static HashEntry JEnum<T>(string name, T? value) where T : struct, Enum
        => new(name, value.HasValue ? value.Value.ToString() : "");
}

#pragma warning restore CS0618
