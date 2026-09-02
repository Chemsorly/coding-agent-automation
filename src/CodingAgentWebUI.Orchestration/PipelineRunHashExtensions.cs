#pragma warning disable CS0618 // Obsolete StartedAt used intentionally for round-trip

using System.Text.Json;
using CodingAgentWebUI.Pipeline.Models;
using StackExchange.Redis;

namespace CodingAgentWebUI.Orchestration;

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
            F("totalCost",                  run.TotalCost?.ToString("G") ?? ""),

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

        var d = hash.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);

        // Required init-only fields — null means corrupt hash
        if (!d.TryGetValue("runId", out var runId) || string.IsNullOrEmpty(runId)) return null;
        if (!d.TryGetValue("issueIdentifier", out var issueIdStr) || string.IsNullOrEmpty(issueIdStr)) return null;
        if (!d.TryGetValue("issueProviderConfigId", out var issuePcId) || string.IsNullOrEmpty(issuePcId)) return null;
        if (!d.TryGetValue("repoProviderConfigId", out var repoPcId) || string.IsNullOrEmpty(repoPcId)) return null;

        _ = Enum.TryParse<PipelineRunType>(d.GetValueOrDefault("runType"), out var runType);

        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier(issueIdStr),
            IssueProviderConfigId = issuePcId,
            RepoProviderConfigId = repoPcId,
            IssueTitle = d.GetValueOrDefault("issueTitle") ?? "",
            BrainProviderConfigId = NullIfEmpty(d.GetValueOrDefault("brainProviderConfigId")),
            AgentProviderConfigId = NullIfEmpty(d.GetValueOrDefault("agentProviderConfigId")),
            ReviewPrBranchName = NullIfEmpty(d.GetValueOrDefault("reviewPrBranchName")),
            ReviewPrTargetBranch = NullIfEmpty(d.GetValueOrDefault("reviewPrTargetBranch")),
            ReviewPrUrl = NullIfEmpty(d.GetValueOrDefault("reviewPrUrl")),
            ReviewPrDescription = NullIfEmpty(d.GetValueOrDefault("reviewPrDescription")),
            ReviewPrAuthor = NullIfEmpty(d.GetValueOrDefault("reviewPrAuthor")),
            DecompositionSource = NullIfEmpty(d.GetValueOrDefault("decompositionSource")),
            InitiatedBy = d.GetValueOrDefault("initiatedBy") ?? InitiatedByConstants.Manual,
            RunType = runType,
        };

        // Volatile/Interlocked fields — use property setters
        if (Enum.TryParse<PipelineStep>(d.GetValueOrDefault("currentStep"), out var step))
            run.CurrentStep = step;
        if (Enum.TryParse<PipelineStep>(d.GetValueOrDefault("highWaterMark"), out var hwm))
            run.HighWaterMark = hwm;
        if (DateTimeOffset.TryParse(d.GetValueOrDefault("startedAtOffset"), out var sao))
            run.ResetStartedAt(sao);
        if (DateTimeOffset.TryParse(d.GetValueOrDefault("lastStepChangeAt"), out var lsca))
            run.LastStepChangeAt = lsca;
        if (DateTimeOffset.TryParse(d.GetValueOrDefault("completedAtOffset"), out var cao))
            run.MarkCompleted(cao);

        // Code review counts (Interlocked) — parse each independently
        _ = int.TryParse(d.GetValueOrDefault("codeReviewCriticalCount"), out var crit);
        _ = int.TryParse(d.GetValueOrDefault("codeReviewWarningCount"), out var warn);
        _ = int.TryParse(d.GetValueOrDefault("codeReviewSuggestionCount"), out var sugg);
        run.SetCodeReviewCounts(crit, warn, sugg);

        // AgentId (volatile)
        run.AgentId = NullIfEmpty(d.GetValueOrDefault("agentId"));

        // Nullable strings
        run.BranchName = NullIfEmpty(d.GetValueOrDefault("branchName"));
        run.FailureReason = NullIfEmpty(d.GetValueOrDefault("failureReason"));
        run.PullRequestUrl = NullIfEmpty(d.GetValueOrDefault("pullRequestUrl"));
        run.PullRequestBody = NullIfEmpty(d.GetValueOrDefault("pullRequestBody"));
        run.PullRequestNumber = NullIfEmpty(d.GetValueOrDefault("pullRequestNumber"));
        run.WorkspacePath = NullIfEmpty(d.GetValueOrDefault("workspacePath"));
        run.ModelName = NullIfEmpty(d.GetValueOrDefault("modelName"));
        run.RepositoryName = NullIfEmpty(d.GetValueOrDefault("repositoryName"));
        run.CodegenSessionId = NullIfEmpty(d.GetValueOrDefault("codegenSessionId"));
        run.FinalLabel = NullIfEmpty(d.GetValueOrDefault("finalLabel"));
        run.CodeReviewChangeSummary = NullIfEmpty(d.GetValueOrDefault("codeReviewChangeSummary"));
        run.CodeReviewVerdictSummary = NullIfEmpty(d.GetValueOrDefault("codeReviewVerdictSummary"));
        run.ResolvedProfileId = NullIfEmpty(d.GetValueOrDefault("resolvedProfileId"));
        run.PipelineProviderConfigId = NullIfEmpty(d.GetValueOrDefault("pipelineProviderConfigId"));
        run.ProjectId = NullIfEmpty(d.GetValueOrDefault("projectId"));
        run.ProjectName = NullIfEmpty(d.GetValueOrDefault("projectName"));
        run.InlineCommentsDegradedReason = NullIfEmpty(d.GetValueOrDefault("inlineCommentsDegradedReason"));

        // Integers
        if (int.TryParse(d.GetValueOrDefault("retryCount"), out var rc)) run.RetryCount = rc;
        if (int.TryParse(d.GetValueOrDefault("infrastructureRetryCount"), out var irc)) run.InfrastructureRetryCount = irc;
        if (int.TryParse(d.GetValueOrDefault("codeReviewIterationsCompleted"), out var cric)) run.CodeReviewIterationsCompleted = cric;
        if (int.TryParse(d.GetValueOrDefault("codeReviewIterationInProgress"), out var criip)) run.CodeReviewIterationInProgress = criip;
        if (int.TryParse(d.GetValueOrDefault("codeReviewIterationsTotal"), out var crit2)) run.CodeReviewIterationsTotal = crit2;
        if (int.TryParse(d.GetValueOrDefault("inlineCommentsPosted"), out var icp)) run.InlineCommentsPosted = icp;
        if (int.TryParse(d.GetValueOrDefault("filesChangedCount"), out var fcc)) run.FilesChangedCount = fcc;
        if (int.TryParse(d.GetValueOrDefault("linesAdded"), out var la)) run.LinesAdded = la;
        if (int.TryParse(d.GetValueOrDefault("linesRemoved"), out var lr)) run.LinesRemoved = lr;
        if (int.TryParse(d.GetValueOrDefault("brainKnowledgeFileCount"), out var bkfc)) run.BrainKnowledgeFileCount = bkfc;
        if (int.TryParse(d.GetValueOrDefault("brainFilesCommitted"), out var bfc)) run.BrainFilesCommitted = bfc;
        if (int.TryParse(d.GetValueOrDefault("decompSubIssuesCreated"), out var dsic)) run.DecompositionSubIssuesCreated = dsic;
        if (int.TryParse(d.GetValueOrDefault("decompSubIssuesAttempted"), out var dsia)) run.DecompositionSubIssuesAttempted = dsia;
        if (int.TryParse(d.GetValueOrDefault("openIssuesDownloaded"), out var oid)) run.OpenIssuesDownloaded = oid;

        // Longs
        if (long.TryParse(d.GetValueOrDefault("totalTokens"), out var tt)) run.TotalTokens = tt;
        if (long.TryParse(d.GetValueOrDefault("cacheReadTokens"), out var crt)) run.CacheReadTokens = crt;
        if (long.TryParse(d.GetValueOrDefault("cacheWriteTokens"), out var cwt)) run.CacheWriteTokens = cwt;

        // Decimal
        if (decimal.TryParse(d.GetValueOrDefault("totalCost"), out var tc)) run.TotalCost = tc;

        // Booleans
        if (bool.TryParse(d.GetValueOrDefault("brainContextLoaded"), out var bcl)) run.BrainContextLoaded = bcl;
        if (bool.TryParse(d.GetValueOrDefault("brainUpdatesPushed"), out var bup)) run.BrainUpdatesPushed = bup;
        if (bool.TryParse(d.GetValueOrDefault("isDraftPr"), out var idp)) run.IsDraftPr = idp;
        if (bool.TryParse(d.GetValueOrDefault("analysisSkipped"), out var ask)) run.AnalysisSkipped = ask;
        if (bool.TryParse(d.GetValueOrDefault("mergeForceResolved"), out var mfr)) run.MergeForceResolved = mfr;
        if (bool.TryParse(d.GetValueOrDefault("inlineCommentsDegraded"), out var icd)) run.InlineCommentsDegraded = icd;
        if (bool.TryParse(d.GetValueOrDefault("baselineHealthPassed"), out var bhp)) run.BaselineHealthPassed = bhp;

        // JSON sub-objects
        run.LatestQualityReport = J<QualityGateReport>(d, "latestQualityReport");
        run.LinkedPullRequest = J<LinkedPullRequest>(d, "linkedPullRequest");
        run.AnalysisRecommendation = JEnum<AnalysisGateResult>(d, "analysisRecommendation");
        run.AcceptanceCriteriaReport = J<AcceptanceCriteriaReport>(d, "acceptanceCriteriaReport");
        run.BrainValidation = J<BrainValidationResult>(d, "brainValidation");
        run.Feedback = J<RunFeedback>(d, "feedback");

        run.IssueLabels = J<List<string>>(d, "issueLabels") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.BlacklistedFilesDetected = J<List<string>>(d, "blacklistedFilesDetected") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.MergeConflictFiles = J<List<string>>(d, "mergeConflictFiles") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.SubIssueResults = J<List<SubIssueCreationResult>>(d, "subIssueResults") ?? (IReadOnlyList<SubIssueCreationResult>)Array.Empty<SubIssueCreationResult>();
        run.AnalysisConcerns = J<List<string>>(d, "analysisConcerns") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.AnalysisBlockingIssues = J<List<string>>(d, "analysisBlockingIssues") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.CodeReviewAgentsRun = J<List<string>>(d, "codeReviewAgentsRun") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.ResolvedQualityGateConfigIds = J<List<string>>(d, "resolvedQualityGateConfigIds") ?? (IReadOnlyList<string>)Array.Empty<string>();
        run.ResolvedReviewerConfigIds = J<List<string>>(d, "resolvedReviewerConfigIds") ?? (IReadOnlyList<string>)Array.Empty<string>();

        var findingsDict = J<Dictionary<string, string>>(d, "codeReviewAgentFindings");
        if (findingsDict is not null)
            foreach (var kv in findingsDict)
                run.CodeReviewAgentFindings[kv.Key] = kv.Value;

        return run;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static HashEntry F(string name, string value) => new(name, value);

    private static HashEntry J<T>(string name, T? value) where T : class
        => new(name, value is null ? "" : JsonSerializer.Serialize(value, JsonOpts));

    private static HashEntry JEnum<T>(string name, T? value) where T : struct, Enum
        => new(name, value.HasValue ? value.Value.ToString() : "");

    private static T? J<T>(Dictionary<string, string?> d, string key) where T : class
    {
        if (!d.TryGetValue(key, out var json) || string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return null; }
    }

    private static T? JEnum<T>(Dictionary<string, string?> d, string key) where T : struct, Enum
    {
        if (!d.TryGetValue(key, out var val) || string.IsNullOrEmpty(val)) return null;
        return Enum.TryParse<T>(val, out var result) ? result : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

#pragma warning restore CS0618
