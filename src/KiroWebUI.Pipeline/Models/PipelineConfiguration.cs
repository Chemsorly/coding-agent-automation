namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Controls how blacklisted path violations are handled during commits.
/// </summary>
public enum BlacklistMode
{
    /// <summary>Unstage blacklisted files, log a warning, and continue the pipeline.</summary>
    WarnAndExclude,

    /// <summary>Fail the pipeline with a clear error listing the violating files.</summary>
    Fail
}

/// <summary>
/// Configuration for a single specialized review agent.
/// </summary>
public sealed record ReviewAgentConfig
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
}

public sealed record CodeReviewConfiguration
{
    public bool Enabled { get; init; } = true;
    public int MaxIterations { get; init; } = 1;
    public string Prompt { get; init; } = PipelineConfiguration.DefaultCodeReviewPrompt;

    /// <summary>
    /// When set, the review step splits into find-then-fix: the review prompt reports findings
    /// with severity markers, then this fix prompt is sent only if [CRITICAL] findings exist.
    /// When null/empty, falls back to single-pass behavior (review prompt does both find and fix).
    /// </summary>
    public string? FixPrompt { get; init; }

    /// <summary>
    /// When populated, the review step runs each agent sequentially instead of using the
    /// single <see cref="Prompt"/>. The second agent onwards sees previous findings via
    /// <c>--resume</c>. When null or empty, falls back to the single <see cref="Prompt"/>.
    /// </summary>
    public IReadOnlyList<ReviewAgentConfig>? Agents { get; init; }
}

public sealed record PipelineConfiguration
{
    public const string DefaultCodeReviewPrompt =
        "Review the changes against the original issue requirements. Use a sub-agent for the review.\n" +
        "Output findings as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- Logic errors against acceptance criteria\n" +
        "- Unhandled null references and exception paths\n" +
        "- Off-by-one errors in loops and collections\n" +
        "- Race conditions in async code\n" +
        "- Missing input validation on public API boundaries\n" +
        "- Edge cases not covered by the implementation\n" +
        "- IDisposable resources not properly disposed (missing using/await using)\n" +
        "- Async/await deadlock patterns (sync-over-async, .Result, .Wait())\n" +
        "- CancellationToken not propagated through async call chains\n\n" +
        "DO NOT FLAG:\n" +
        "- Style preferences or naming conventions\n" +
        "- Missing XML documentation comments\n" +
        "- Theoretical risks requiring unlikely preconditions\n" +
        "- Issues in unchanged code outside the diff\n" +
        "- \"Consider using library X\" suggestions\n" +
        "- Performance micro-optimizations\n" +
        "- Missing nullable annotations on internal code\n" +
        "- Test code conventions\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string DefaultFixPrompt =
        "Review the findings above. Fix only items marked [CRITICAL]. " +
        "For [WARNING] items, add a TODO comment at the relevant location. Ignore [SUGGESTION] items.";

    public const string DefaultCorrectnessReviewPrompt =
        "Review the changes against the original issue requirements. Use a sub-agent for the review. " +
        "Output findings as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- Logic errors against acceptance criteria\n" +
        "- Unhandled null references and exception paths\n" +
        "- Off-by-one errors in loops and collections\n" +
        "- Race conditions in async code\n" +
        "- Missing input validation on public API boundaries\n" +
        "- Edge cases not covered by the implementation\n\n" +
        "DO NOT FLAG:\n" +
        "- Style preferences or naming conventions\n" +
        "- Missing XML documentation comments\n" +
        "- Theoretical risks requiring unlikely preconditions\n" +
        "- Issues in unchanged code outside the diff\n" +
        "- \"Consider using library X\" suggestions\n" +
        "- Performance micro-optimizations\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string DefaultDotNetSpecialistReviewPrompt =
        "Review the changes for .NET-specific issues. The previous review covered correctness — " +
        "do not duplicate those findings. Output findings as a numbered list with severity " +
        "[CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- IDisposable resources not properly disposed (missing using/await using)\n" +
        "- Async/await deadlock patterns (sync-over-async, .Result, .Wait())\n" +
        "- DI lifetime mismatches (scoped service injected into singleton)\n" +
        "- CancellationToken not propagated through async call chains\n" +
        "- ArgumentNullException.ThrowIfNull missing on public method parameters\n" +
        "- Collections exposed as mutable (List<T> instead of IReadOnlyList<T>)\n\n" +
        "DO NOT FLAG:\n" +
        "- Business logic correctness (already covered by previous review)\n" +
        "- Style or formatting preferences\n" +
        "- Missing nullable annotations on internal code\n" +
        "- Test code conventions\n" +
        "- Suggestions to add more abstractions or interfaces\n\n" +
        "Do NOT fix anything. Only report findings.";

    /// <summary>Default review agents: Correctness + .NET Specialist.</summary>
    public static IReadOnlyList<ReviewAgentConfig> DefaultReviewAgents { get; } = new[]
    {
        new ReviewAgentConfig { Name = "Correctness", Prompt = DefaultCorrectnessReviewPrompt },
        new ReviewAgentConfig { Name = "DotNetSpecialist", Prompt = DefaultDotNetSpecialistReviewPrompt }
    };

    public const string DefaultAnalysisPrompt =
        "Analyze the codebase in context of the following issue. Read the issue carefully, " +
        "then explore the relevant source files to understand the current architecture and identify what needs to change.\n\n" +
        "Your analysis should cover:\n" +
        "1. **Planned Approach** — What files need to change and how. Be specific about the strategy.\n" +
        "2. **Affected Components** — Which files, classes, or modules will be touched.\n" +
        "3. **Test Coverage** — What existing tests cover the affected code, and what new tests will be needed.\n" +
        "4. **Risks & Considerations** — Breaking changes, edge cases, backward compatibility, or anything that needs special attention.";

    public const string DefaultImplementationPrompt =
        "Implement the following issue. Write the code — do not just analyze or plan.\n\n" +
        "Follow this approach:\n" +
        "1. **Understand** — Read the analysis and the issue. Explore relevant files before making changes.\n" +
        "2. **Implement** — Make focused, minimal changes. Fix root causes, not symptoms. Maintain the existing code style and conventions.\n" +
        "3. **Verify** — Run the project's build, linter, and tests. If a command fails, fix the issue and re-run to confirm.\n\n" +
        "Keep working until the implementation is complete. If something fails, diagnose and fix it rather than stopping.";

    public int MaxRetries { get; init; } = 3;
    public int IssuePageSize { get; init; } = 25;
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public double MinCoverageThreshold { get; init; } = 80.0;
    public bool SecurityScanEnabled { get; init; } = false;
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";
    public CodeReviewConfiguration CodeReview { get; init; } = new();
    public string AnalysisPrompt { get; init; } = DefaultAnalysisPrompt;
    public string ImplementationPrompt { get; init; } = DefaultImplementationPrompt;
    public bool ExternalCiEnabled { get; init; } = false;
    public TimeSpan ExternalCiTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ExternalCiPollInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// The warning resets after each occurrence so it fires again after another interval of silence.
    /// </summary>
    public TimeSpan StallWarningInterval { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>
    /// How often the stall monitor polls <see cref="IAgentProvider.GetHealthStatus"/>.
    /// Default is 30 seconds. Tests can set a shorter interval for faster execution.
    /// </summary>
    public TimeSpan StallPollInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// How long the agent can be continuously silent before the stall monitor forcefully
    /// kills the process via <see cref="IAgentProvider.KillAsync"/>. Defaults to
    /// <see cref="AgentTimeout"/>. Set independently for scenarios where a shorter silence
    /// threshold is desired (e.g., kill after 10 minutes of silence even if the 30-minute
    /// total timeout hasn't elapsed).
    /// </summary>
    public TimeSpan? StallKillTimeout { get; init; }
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".kiro", ".github", ".brain" };
    public BlacklistMode BlacklistMode { get; init; } = BlacklistMode.WarnAndExclude;

    /// <summary>
    /// When enabled, workspace folders are deleted after a successful (non-draft) PR is created.
    /// </summary>
    public bool CleanupSuccessfulWorkspaces { get; init; } = true;

    /// <summary>
    /// Number of days to retain workspace folders for failed or cancelled runs.
    /// Set to 0 to delete immediately. Set to -1 to retain indefinitely.
    /// </summary>
    public int FailedWorkspaceRetentionDays { get; init; } = 7;

    /// <summary>
    /// Records the last-used provider ID for each provider selection per pipeline.
    /// Keys: "issue", "repository", "agent", "brain", "pipeline".
    /// Values: provider config IDs.
    /// Pre-populates dropdowns on subsequent pipeline runs.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastUsedProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// When true, the brain repository operates in read-only mode: pre-run sync
    /// (clone/pull) and context injection proceed normally, but all write operations
    /// are skipped — write instructions are omitted from the prompt, validation is
    /// skipped, and the SyncingBrainRepoPostRun step (commit and push) is skipped
    /// entirely. Defaults to false.
    /// </summary>
    public bool BrainReadOnly { get; init; } = false;

    /// <summary>
    /// Poll interval for the closed pipeline loop when checking for new agent:next issues.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan ClosedLoopPollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of issues to process per poll cycle in the closed loop.
    /// 0 means unlimited (process entire backlog). Counter resets each poll cycle.
    /// </summary>
    public int ClosedLoopMaxRunsPerCycle { get; init; } = 0;

    /// <summary>
    /// Number of consecutive poll failures before the circuit breaker pauses the loop.
    /// Default: 5.
    /// </summary>
    // TODO: [RES-03] Add server-side validation to reject values < 1 — UI has min="1" but JSON config can bypass it (review finding #3)
    public int ClosedLoopMaxConsecutivePollFailures { get; init; } = 5;

    /// <summary>
    /// Maximum backoff interval between poll retries after consecutive failures.
    /// Backoff uses exponential formula capped at this value. Default: 15 minutes.
    /// </summary>
    public TimeSpan ClosedLoopMaxBackoffInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum number of pages to fetch when polling for agent:next issues.
    /// Each page contains up to 100 issues. Default: 10 (1000 issues max).
    /// </summary>
    // TODO: [REF-01] Add server-side validation to reject values < 1 — values ≤ 0 silently fetch only 1 page (review finding #2)
    public int ClosedLoopMaxPagesToFetch { get; init; } = 10;
}
