using System.Reflection;
using System.Text.Json.Serialization;
using MessagePack;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Models;

[MessagePackObject]
public sealed record PipelineConfiguration
{
    public const string DefaultFixPrompt = DefaultPrompts.Fix;
    public const string DefaultCorrectnessReviewPrompt = DefaultPrompts.CorrectnessReview;
    public const string DefaultDotNetSpecialistReviewPrompt = DefaultPrompts.DotNetSpecialistReview;
    public const string DefaultSecurityReviewPrompt = DefaultPrompts.SecurityReview;
    public const string DefaultTestQualityReviewPrompt = DefaultPrompts.TestQualityReview;
    public const string DefaultAcceptanceCriteriaReviewPrompt = DefaultPrompts.AcceptanceCriteriaReview;

    /// <summary>Default review agents: Correctness + DotNetSpecialist + Security + TestQuality.</summary>
    public static IReadOnlyList<ReviewAgentConfig> DefaultReviewAgents { get; } = new[]
    {
        new ReviewAgentConfig { Name = "Correctness", Prompt = DefaultCorrectnessReviewPrompt },
        new ReviewAgentConfig { Name = "DotNetSpecialist", Prompt = DefaultDotNetSpecialistReviewPrompt },
        new ReviewAgentConfig { Name = "SecurityReviewer", Prompt = DefaultSecurityReviewPrompt },
        new ReviewAgentConfig { Name = "TestQualityReviewer", Prompt = DefaultTestQualityReviewPrompt }
    };

    /// <summary>
    /// Well-known ID for the default reviewer configuration.
    /// Used by the reset-to-defaults feature to identify/replace the factory configuration.
    /// </summary>
    public const string DefaultReviewerConfigurationId = "default-reviewers";

    /// <summary>
    /// Factory-default reviewer configurations. Used as the source of truth for
    /// "Reset collection to defaults" — replaces the entire reviewer config set.
    /// </summary>
    public static IReadOnlyList<ReviewerConfiguration> DefaultReviewerConfigurations { get; } = new[]
    {
        new ReviewerConfiguration
        {
            Id = DefaultReviewerConfigurationId,
            DisplayName = "Default Reviewers",
            MatchLabels = [],
            Agents = DefaultReviewAgents.Select(a => new ReviewAgent { Name = a.Name, Prompt = a.Prompt }).ToList(),
            Enabled = true,
            ExecutionOrder = 0
        }
    };

    public const string DefaultAnalysisPrompt = DefaultPrompts.Analysis;
    public const string DefaultAnalysisReviewPrompt = DefaultPrompts.AnalysisReview;
    public const string DefaultAnalysisRefinementPrompt = DefaultPrompts.AnalysisRefinement;
    public const string DefaultImplementationPrompt = DefaultPrompts.Implementation;

    // ── Domain-specific sub-configurations (source of truth) ────────────

    /// <summary>Retry and timeout settings.</summary>
    [JsonIgnore]
    [IgnoreMember]
    public RetryConfiguration Retry { get; init; } = new();

    /// <summary>Workspace directory and retention settings.</summary>
    [JsonIgnore]
    [IgnoreMember]
    public WorkspaceConfiguration Workspace { get; init; } = new();

    /// <summary>External CI integration settings.</summary>
    [JsonIgnore]
    [IgnoreMember]
    public ExternalCiConfiguration ExternalCi { get; init; } = new();

    /// <summary>Closed-loop polling settings.</summary>
    [JsonIgnore]
    [IgnoreMember]
    public ClosedLoopConfiguration ClosedLoop { get; init; } = new();

    /// <summary>Multi-agent orchestration settings.</summary>
    [JsonIgnore]
    [IgnoreMember]
    public AgentConfiguration Agent { get; init; } = new();

    /// <summary>Commit blacklist and enforcement settings.</summary>
    [JsonIgnore]
    [IgnoreMember]
    public CommitConfiguration Commit { get; init; } = new();

    // ── Flat properties (JSON serialization surface, delegate to sub-configs) ──

    [Key(41)]
    [ProjectOverridable(Order = 1)]
    public int MaxRetries
    {
        get => Retry.MaxRetries;
        init => Retry = Retry with { MaxRetries = value };
    }

    /// <summary>
    /// Maximum number of retry attempts for the analysis phase.
    /// Default 1 = 2 total attempts (initial + 1 retry).
    /// Set to 0 to disable retry (fail on first failure).
    /// </summary>
    [Key(35)]
    [ProjectOverridable(Order = 2)]
    public int MaxAnalysisRetries
    {
        get => Retry.MaxAnalysisRetries;
        init => Retry = Retry with { MaxAnalysisRetries = value };
    }

    [Key(33)]
    public int IssuePageSize { get; init; } = 25;

    [Key(4)]
    [ProjectOverridable(Order = 3)]
    public TimeSpan AgentTimeout
    {
        get => Retry.AgentTimeout;
        init => Retry = Retry with { AgentTimeout = value };
    }

    [Key(52)]
    public string WorkspaceBaseDirectory
    {
        get => Workspace.WorkspaceBaseDirectory;
        init => Workspace = Workspace with { WorkspaceBaseDirectory = value };
    }

    [Key(22)]
    [ProjectOverridable(Order = 10, DeepMerge = true)]
    public CodeReviewConfiguration CodeReview { get; init; } = new();

    [Key(5)]
    [ProjectOverridable(Order = 4)]
    public string AnalysisPrompt { get; init; } = DefaultAnalysisPrompt;

    [Key(32)]
    [ProjectOverridable(Order = 5)]
    public string ImplementationPrompt { get; init; } = DefaultImplementationPrompt;

    /// <summary>
    /// When true, a second agent reviews the analysis in an isolated session and feeds
    /// findings back to the original analysis agent for refinement. This adversarial
    /// loop improves analysis quality by catching missed components, incorrect assumptions,
    /// and feasibility issues before implementation begins. Default: true.
    /// </summary>
    [Key(7)]
    [ProjectOverridable(Order = 6)]
    public bool AnalysisReviewEnabled { get; init; } = true;

    /// <summary>
    /// Prompt sent to the isolated review agent that evaluates the analysis.
    /// The agent reads .agent/analysis.md, .agent/analysis-assessment.json, and .agent/issue-context.md,
    /// then writes findings to .agent/analysis-review.md.
    /// </summary>
    [Key(8)]
    [ProjectOverridable(Order = 7)]
    public string AnalysisReviewPrompt { get; init; } = DefaultAnalysisReviewPrompt;

    /// <summary>
    /// Prompt sent back to the original analysis session instructing it to refine
    /// the analysis based on the review feedback at .agent/analysis-review.md.
    /// </summary>
    [Key(6)]
    [ProjectOverridable(Order = 8)]
    public string AnalysisRefinementPrompt { get; init; } = DefaultAnalysisRefinementPrompt;

    /// <summary>
    /// When true, a dedicated acceptance criteria compliance check runs in parallel with
    /// code reviewers, producing a structured JSON report. Default: true.
    /// </summary>
    [Key(0)]
    [ProjectOverridable(Order = 9)]
    public bool AcceptanceCriteriaEnabled { get; init; } = true;

    /// <summary>
    /// Prompt sent to the acceptance criteria agent that evaluates implementation compliance.
    /// The agent writes structured JSON to .agent/acceptance-criteria.json.
    /// </summary>
    [Key(1)]
    public string AcceptanceCriteriaPrompt { get; init; } = DefaultPrompts.AcceptanceCriteriaCompliance;

    /// <summary>
    /// When true, refactoring proposals are reviewed by an isolated discriminator agent
    /// before issues are created. Default: true.
    /// </summary>
    [Key(48)]
    [ProjectOverridable(Order = 23)]
    public bool RefactoringReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, brain consolidation changes are reviewed by an isolated discriminator
    /// agent before being committed. Default: true.
    /// </summary>
    [Key(11)]
    [ProjectOverridable(Order = 24)]
    public bool BrainConsolidationReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, harness suggestions are reviewed by an isolated discriminator agent
    /// before being persisted. Default: true.
    /// </summary>
    [Key(28)]
    [ProjectOverridable(Order = 25)]
    public bool HarnessSuggestionsReviewEnabled { get; init; } = true;

    /// <summary>
    /// When true, the pipeline runs a baseline health check (agent environment + workspace build)
    /// after branch creation and before code analysis. Default: true.
    /// </summary>
    [Key(9)]
    [ProjectOverridable(Order = 11)]
    public bool BaselineHealthCheckEnabled { get; init; } = true;

    [Key(26)]
    [ProjectOverridable(Order = 12)]
    public TimeSpan ExternalCiTimeout
    {
        get => ExternalCi.ExternalCiTimeout;
        init => ExternalCi = ExternalCi with { ExternalCiTimeout = value };
    }

    [Key(25)]
    [ProjectOverridable(Order = 13)]
    public TimeSpan ExternalCiPollInterval
    {
        get => ExternalCi.ExternalCiPollInterval;
        init => ExternalCi = ExternalCi with { ExternalCiPollInterval = value };
    }

    [Key(38)]
    [ProjectOverridable(Order = 16)]
    public int MaxInfrastructureRetries
    {
        get => ExternalCi.MaxInfrastructureRetries;
        init => ExternalCi = ExternalCi with { MaxInfrastructureRetries = value };
    }

    /// <summary>
    /// How long to wait for CI runs to appear before concluding CI never started.
    /// Triggers a re-push retry instead of burning the full ExternalCiTimeout. Default: 5 minutes.
    /// </summary>
    [Key(53)]
    [ProjectOverridable(Order = 14)]
    public TimeSpan CiNotStartedTimeout
    {
        get => ExternalCi.CiNotStartedTimeout;
        init => ExternalCi = ExternalCi with { CiNotStartedTimeout = value };
    }

    /// <summary>
    /// Maximum re-push retries when CI never starts. Default: 5.
    /// </summary>
    [Key(54)]
    [ProjectOverridable(Order = 15)]
    public int CiNotStartedMaxRetries
    {
        get => ExternalCi.CiNotStartedMaxRetries;
        init => ExternalCi = ExternalCi with { CiNotStartedMaxRetries = value };
    }

    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// The warning resets after each occurrence so it fires again after another interval of silence.
    /// </summary>
    [Key(51)]
    [ProjectOverridable(Order = 17)]
    public TimeSpan StallWarningInterval
    {
        get => Retry.StallWarningInterval;
        init => Retry = Retry with { StallWarningInterval = value };
    }

    /// <summary>
    /// How often the stall monitor polls <see cref="IAgentProvider.GetHealthStatus"/>.
    /// Default is 30 seconds. Tests can set a shorter interval for faster execution.
    /// </summary>
    [Key(50)]
    public TimeSpan StallPollInterval
    {
        get => Retry.StallPollInterval;
        init => Retry = Retry with { StallPollInterval = value };
    }

    [Key(10)]
    [ProjectOverridable(Order = 26)]
    public IReadOnlyList<string> BlacklistedPaths
    {
        get => Commit.BlacklistedPaths;
        init => Commit = Commit with { BlacklistedPaths = value };
    }

    /// <summary>
    /// Agent-provider-specific paths that are ALWAYS unstaged before commit, regardless of
    /// <see cref="BlacklistedPaths"/> configuration. Populated from
    /// <see cref="IAgentProvider.PipelineInjectedPaths"/> at pipeline startup.
    /// </summary>
    [Key(44)]
    public IReadOnlyList<string> PipelineInjectedPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Number of commits on the default branch since the last analysis that triggers
    /// an automatic analysis refresh. Set to 0 to disable commit-count staleness detection.
    /// Configurable at global level and overridable per project via <see cref="ApplyProjectOverrides"/>.
    /// </summary>
    [Key(56)]
    [ProjectOverridable(Order = 28)]
    public int AnalysisCommitThreshold { get; init; } = PipelineConstants.DefaultAnalysisCommitThreshold;



    /// <summary>
    /// Applies non-null project overrides to a PipelineConfiguration instance.
    /// Called BEFORE ApplyTemplateOverrides in the dispatch pipeline.
    /// Each non-null property on the project replaces the corresponding global value.
    /// Nested objects (e.g., CodeReview) use deep-merge semantics via ApplyOverrides.
    /// </summary>
    public static PipelineConfiguration ApplyProjectOverrides(
        PipelineConfiguration config, PipelineProject? project)
    {
        if (project is null) return config;

        // Clone once via the compiler-generated <Clone>$ method, then mutate via PropertyInfo.SetValue.
        // This is equivalent to the previous per-property `config = config with { Prop = value }` pattern.
        // Init setters are callable via reflection because they are regular setters at the IL level —
        // the runtime does not enforce init-only semantics during reflection. This is a stable .NET
        // contract relied upon by System.Text.Json and MessagePack serializers.
        // TODO: Consider wrapping clone invocation in try block — if s_cloneMethod.Invoke throws
        // (e.g., OOM), the exception propagates unhandled since the catch only handles TargetInvocationException
        // wrapping ArgumentOutOfRangeException. Low probability but differs from original per-property `with` pattern.
        var clone = (PipelineConfiguration)s_cloneMethod.Invoke(config, null)!;

        try
        {
            foreach (var mapping in s_overrideMappings)
            {
                var projectValue = mapping.ProjectGetter(project);
                if (projectValue is null) continue;

                if (mapping.DeepMerge)
                {
                    // Deep-merge: read current config value, invoke ApplyOverrides, assign result
                    // TODO: This assumes deep-merge properties are simple auto-properties (not delegating).
                    // If a future deep-merge property delegates to a sub-config, GetValue after SetValue on
                    // a shallow clone could read stale data. Currently safe (CodeReview is the only deep-merge property).
                    var currentValue = mapping.ConfigProperty.GetValue(clone);
                    // TODO: MethodInfo.Invoke wraps all exceptions in TargetInvocationException, changing
                    // observable exception types for callers vs the original direct-call implementation.
                    // Non-ArgumentOutOfRangeException failures from ApplyOverrides will propagate wrapped.
                    var merged = mapping.ApplyOverridesMethod!.Invoke(currentValue, [projectValue]);
                    mapping.ConfigProperty.SetValue(clone, merged);
                }
                else
                {
                    // Simple replacement: unwrap Nullable<T> if needed, then assign
                    var unwrapped = mapping.UnwrapNullable(projectValue);
                    mapping.ConfigProperty.SetValue(clone, unwrapped);
                }
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException rangeEx)
        {
            // INVARIANT: Partial-apply is safe because all validated init setters
            // (CiNotStartedMaxRetries, MaxInfrastructureRetries, MaxDecompositionSubIssues) use
            // fail-fast patterns (ternary throw or ThrowIf) that either fully assign or throw
            // without leaving partial state. If a future setter mutates state before validation,
            // add a per-property try/catch instead.
            // TODO: Log message says "falling back to global defaults" but returns partially-mutated clone.
            // This matches original behavior but is misleading — consider "retaining partially-applied overrides".
            Log.Warning(
                "Project '{ProjectName}' (ID: {ProjectId}) has out-of-range override values — falling back to global defaults. {ErrorMessage}",
                project.Name, project.Id, rangeEx.Message);
            return clone;
        }

        return clone;
    }

    // ── Reflection-based override engine (cached at static init) ────────────────

    private static readonly MethodInfo s_cloneMethod = typeof(PipelineConfiguration)
        .GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public)!;

    private static readonly IReadOnlyList<OverrideMapping> s_overrideMappings = BuildOverrideMappings();

    private static IReadOnlyList<OverrideMapping> BuildOverrideMappings()
    {
        var configProperties = typeof(PipelineConfiguration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<ProjectOverridableAttribute>()))
            .Where(x => x.Attribute is not null)
            .OrderBy(x => x.Attribute!.Order)
            .ToList();

        var projectProperties = typeof(PipelineProject)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(p => p.Name);

        var mappings = new List<OverrideMapping>(configProperties.Count);

        foreach (var (configProp, attr) in configProperties)
        {
            if (!projectProperties.TryGetValue(configProp.Name, out var projectProp))
            {
                throw new InvalidOperationException(
                    $"[ProjectOverridable] property '{configProp.Name}' on PipelineConfiguration " +
                    $"has no matching property on PipelineProject.");
            }

            MethodInfo? applyOverridesMethod = null;
            if (attr!.DeepMerge)
            {
                // For deep-merge, find the ApplyOverrides method on the config property's type
                // that accepts the project property's type as a parameter.
                var configPropType = configProp.PropertyType;
                var projectPropType = projectProp.PropertyType;
                // The project type is nullable reference — the non-null value is passed directly
                applyOverridesMethod = configPropType.GetMethod(
                    "ApplyOverrides",
                    BindingFlags.Instance | BindingFlags.Public,
                    [projectPropType]);

                if (applyOverridesMethod is null)
                {
                    throw new InvalidOperationException(
                        $"[ProjectOverridable(DeepMerge = true)] property '{configProp.Name}': " +
                        $"type '{configPropType.Name}' has no ApplyOverrides({projectPropType.Name}) method.");
                }
            }

            // Build a fast getter delegate for the project property
            var projectGetter = BuildProjectGetter(projectProp);

            // Determine if the project property is Nullable<T> (value type)
            var isNullableValueType = Nullable.GetUnderlyingType(projectProp.PropertyType) is not null;

            mappings.Add(new OverrideMapping
            {
                ConfigProperty = configProp,
                ProjectGetter = projectGetter,
                DeepMerge = attr.DeepMerge,
                ApplyOverridesMethod = applyOverridesMethod,
                IsNullableValueType = isNullableValueType,
            });
        }

        return mappings;
    }

    private static Func<PipelineProject, object?> BuildProjectGetter(PropertyInfo projectProp)
    {
        // Use the PropertyInfo getter — boxed for Nullable<T>, returns null for reference types
        return project => projectProp.GetValue(project);
    }

    private sealed class OverrideMapping
    {
        public required PropertyInfo ConfigProperty { get; init; }
        public required Func<PipelineProject, object?> ProjectGetter { get; init; }
        public required bool DeepMerge { get; init; }
        public MethodInfo? ApplyOverridesMethod { get; init; }
        public required bool IsNullableValueType { get; init; }

        /// <summary>
        /// For Nullable&lt;T&gt; value types, the boxed value is already the unwrapped T.
        /// For reference types, the value is used as-is.
        /// </summary>
        public object UnwrapNullable(object value) => value;
    }

    /// <summary>
    /// Applies per-repo blacklist overrides from a <see cref="ProviderConfig"/>.
    /// When the provider config specifies <see cref="ProviderConfig.BlacklistedPaths"/>,
    /// it takes precedence over the global pipeline configuration default.
    /// </summary>
    public static PipelineConfiguration ApplyBlacklistOverride(PipelineConfiguration config, ProviderConfig? repoProviderConfig)
    {
        if (repoProviderConfig is null)
            return config;

        if (repoProviderConfig.BlacklistedPaths is { Count: > 0 })
            config = config with { BlacklistedPaths = repoProviderConfig.BlacklistedPaths };
        return config;
    }

    /// <summary>
    /// Merges provider-specific pipeline-injected paths into the configurable blacklist.
    /// Called after agent provider creation to ensure injected files are excluded from commits.
    /// </summary>
    public static PipelineConfiguration ApplyProviderBlacklist(PipelineConfiguration config, IReadOnlyList<string> providerPaths)
    {
        if (providerPaths.Count == 0) return config;
        return config with { BlacklistedPaths = config.BlacklistedPaths.Concat(providerPaths).Distinct().ToList() };
    }

    /// <summary>
    /// Applies template-level overrides to the pipeline configuration.
    /// Resolution order: find matching template by repo+brain provider IDs → apply BrainReadOnly
    /// (one-directional: only overrides to true) → apply blacklist from repo provider config.
    /// Called AFTER <see cref="ApplyProjectOverrides"/> in the dispatch pipeline.
    /// </summary>
    public static PipelineConfiguration ApplyTemplateOverrides(
        PipelineConfiguration config,
        string repoProviderId,
        string? brainProviderId,
        IReadOnlyList<ProviderConfig> providerConfigs,
        IReadOnlyList<PipelineJobTemplate> templates)
    {
        var matchingTemplate = templates.FirstOrDefault(t =>
            t.RepoProviderId == repoProviderId && t.BrainProviderId == brainProviderId);
        if (matchingTemplate is { BrainReadOnly: true })
            config = config with { BrainReadOnly = true };

        return ApplyBlacklistOverride(config, providerConfigs.FirstOrDefault(c => c.Id == repoProviderId));
    }

    /// <summary>
    /// Loads the pipeline configuration and applies the full resolution chain:
    /// Global → Project overrides → Template overrides (blacklist from ProviderConfig).
    /// Callers provide delegates to decouple from any specific store interface shape.
    /// </summary>
    public static async Task<PipelineConfiguration> ResolveAsync(
        Func<CancellationToken, Task<PipelineConfiguration>> loadConfig,
        Func<CancellationToken, Task<IReadOnlyList<PipelineJobTemplate>>> loadTemplates,
        PipelineProject project,
        string repoProviderId,
        string? brainProviderId,
        IReadOnlyList<ProviderConfig> providerConfigs,
        CancellationToken ct)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull for loadConfig, loadTemplates parameters to produce clear validation errors instead of NRE
        var config = await loadConfig(ct);
        config = ApplyProjectOverrides(config, project);
        var templates = await loadTemplates(ct);
        return ApplyTemplateOverrides(config, repoProviderId, brainProviderId, providerConfigs, templates);
    }

    /// <summary>
    /// Applies the resolution chain to a pre-loaded configuration:
    /// Project overrides → Template overrides (blacklist from ProviderConfig).
    /// Used when the caller needs the raw config before overrides (e.g., for WorkspaceBaseDirectory).
    /// </summary>
    public static async Task<PipelineConfiguration> ResolveAsync(
        PipelineConfiguration preLoaded,
        Func<CancellationToken, Task<IReadOnlyList<PipelineJobTemplate>>> loadTemplates,
        PipelineProject project,
        string repoProviderId,
        string? brainProviderId,
        IReadOnlyList<ProviderConfig> providerConfigs,
        CancellationToken ct)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull for preLoaded, loadTemplates parameters to produce clear validation errors instead of NRE
        var config = ApplyProjectOverrides(preLoaded, project);
        var templates = await loadTemplates(ct);
        return ApplyTemplateOverrides(config, repoProviderId, brainProviderId, providerConfigs, templates);
    }

    /// <summary>
    /// Number of days to retain workspace folders for failed or cancelled runs.
    /// Set to 0 to delete immediately. Set to -1 to retain indefinitely.
    /// </summary>
    [Key(27)]
    public int FailedWorkspaceRetentionDays
    {
        get => Workspace.FailedWorkspaceRetentionDays;
        init => Workspace = Workspace with { FailedWorkspaceRetentionDays = value };
    }

    /// <summary>
    /// Records the last-used provider ID for each provider selection per pipeline.
    /// Keys: "issue", "repository", "agent", "brain", "pipeline".
    /// Values: provider config IDs.
    /// Pre-populates dropdowns on subsequent pipeline runs.
    /// </summary>
    [Key(34)]
    public IReadOnlyDictionary<string, string> LastUsedProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// When true, the brain repository operates in read-only mode: pre-run sync
    /// (clone/pull) and context injection proceed normally, but all write operations
    /// are skipped — write instructions are omitted from the prompt, validation is
    /// skipped, and the SyncingBrainRepoPostRun step (commit and push) is skipped
    /// entirely. Defaults to false.
    /// </summary>
    [Key(13)]
    [ProjectOverridable(Order = 27)]
    public bool BrainReadOnly
    {
        get => Agent.BrainReadOnly;
        init => Agent = Agent with { BrainReadOnly = value };
    }

    /// <summary>
    /// When true, the pipeline loop starts automatically on application startup.
    /// Set to true when user starts the loop, false when user stops it.
    /// </summary>
    [Key(15)]
    public bool ClosedLoopAutoStart
    {
        get => ClosedLoop.AutoStart;
        init => ClosedLoop = ClosedLoop with { AutoStart = value };
    }

    /// <summary>
    /// Poll interval for the closed pipeline loop when checking for new agent:next issues.
    /// Default: 60 seconds.
    /// </summary>
    [Key(21)]
    public TimeSpan ClosedLoopPollInterval
    {
        get => ClosedLoop.ClosedLoopPollInterval;
        init => ClosedLoop = ClosedLoop with { ClosedLoopPollInterval = value };
    }

    /// <summary>
    /// Maximum number of issues to process per poll cycle in the closed loop.
    /// 0 means unlimited (process entire backlog). Counter resets each poll cycle.
    /// </summary>
    [Key(20)]
    public int ClosedLoopMaxRunsPerCycle
    {
        get => ClosedLoop.ClosedLoopMaxRunsPerCycle;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxRunsPerCycle = value };
    }

    /// <summary>
    /// Number of consecutive poll failures before the circuit breaker pauses the loop.
    /// Default: 5.
    /// </summary>
    [Key(18)]
    public int ClosedLoopMaxConsecutivePollFailures
    {
        get => ClosedLoop.ClosedLoopMaxConsecutivePollFailures;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxConsecutivePollFailures = value };
    }

    /// <summary>
    /// Maximum backoff interval between poll retries after consecutive failures.
    /// Backoff uses exponential formula capped at this value. Default: 15 minutes.
    /// </summary>
    [Key(17)]
    public TimeSpan ClosedLoopMaxBackoffInterval
    {
        get => ClosedLoop.ClosedLoopMaxBackoffInterval;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxBackoffInterval = value };
    }

    /// <summary>
    /// Maximum number of pages to fetch when polling for agent:next issues.
    /// Each page contains up to 100 issues. Default: 10 (1000 issues max).
    /// </summary>
    [Key(19)]
    public int ClosedLoopMaxPagesToFetch
    {
        get => ClosedLoop.ClosedLoopMaxPagesToFetch;
        init => ClosedLoop = ClosedLoop with { ClosedLoopMaxPagesToFetch = value };
    }

    /// <summary>
    /// Cooldown duration before the circuit breaker auto-resumes polling.
    /// After this period the loop resets failure counters and retries. Default: 5 minutes.
    /// </summary>
    [Key(16)]
    public TimeSpan ClosedLoopCircuitBreakerCooldown
    {
        get => ClosedLoop.ClosedLoopCircuitBreakerCooldown;
        init => ClosedLoop = ClosedLoop with { ClosedLoopCircuitBreakerCooldown = value };
    }

    // ── Multi-agent fields ──────────────────────────────────────────────

    /// <summary>
    /// Global fallback for agent label routing when a repository's ProviderConfig
    /// does not specify <see cref="ProviderConfig.RequiredLabels"/>. Comma-separated string (e.g., "kiro,dotnet").
    /// Null means any idle agent can be selected.
    /// </summary>
    [Key(24)]
    public string? DefaultRequiredAgentLabels
    {
        get => Agent.DefaultRequiredAgentLabels;
        init => Agent = Agent with { DefaultRequiredAgentLabels = value };
    }

    /// <summary>
    /// Maximum number of retry attempts when brain repo push fails with a non-fast-forward error
    /// (concurrent push conflict). Each retry fetches, rebases, resolves conflicts, and retries push.
    /// Default: 3.
    /// </summary>
    [Key(12)]
    public int BrainPushMaxRetries
    {
        get => Agent.BrainPushMaxRetries;
        init => Agent = Agent with { BrainPushMaxRetries = value };
    }

    /// <summary>
    /// How long to wait after an agent disconnects before marking its active run as Failed.
    /// Default: 5 minutes.
    /// </summary>
    [Key(3)]
    public TimeSpan AgentDisconnectGracePeriod
    {
        get => Agent.AgentDisconnectGracePeriod;
        init => Agent = Agent with { AgentDisconnectGracePeriod = value };
    }

    /// <summary>
    /// How long a busy agent can go without pipeline step progress before being marked as stuck.
    /// Default: 60 minutes.
    /// </summary>
    [Key(2)]
    public TimeSpan AgentBusyProgressTimeout
    {
        get => Agent.AgentBusyProgressTimeout;
        init => Agent = Agent with { AgentBusyProgressTimeout = value };
    }

    /// <summary>
    /// Maximum number of output lines to retain per active pipeline run (ring buffer capacity).
    /// Default: 10,000.
    /// </summary>
    [Key(42)]
    public int OutputBufferCapacity
    {
        get => Agent.OutputBufferCapacity;
        init => Agent = Agent with { OutputBufferCapacity = value };
    }

    /// <summary>
    /// Maximum number of output lines in PipelineRun.OutputLines bounded queue.
    /// Default: 5,000.
    /// </summary>
    [Key(43)]
    public int OutputLinesCapacity
    {
        get => Agent.OutputLinesCapacity;
        init => Agent = Agent with { OutputLinesCapacity = value };
    }

    /// <summary>
    /// Maximum number of chat entries in PipelineRun.ChatHistory bounded queue.
    /// Default: 200.
    /// </summary>
    [Key(14)]
    public int ChatHistoryCapacity
    {
        get => Agent.ChatHistoryCapacity;
        init => Agent = Agent with { ChatHistoryCapacity = value };
    }

    /// <summary>
    /// Maximum number of quality gate reports in PipelineRun.QualityGateHistory bounded queue.
    /// Default: 50.
    /// </summary>
    [Key(46)]
    public int QualityGateHistoryCapacity
    {
        get => Agent.QualityGateHistoryCapacity;
        init => Agent = Agent with { QualityGateHistoryCapacity = value };
    }

    /// <summary>
    /// Maximum number of retry error messages in PipelineRun.RetryErrors bounded queue.
    /// Default: 100.
    /// </summary>
    [Key(49)]
    public int RetryErrorsCapacity
    {
        get => Agent.RetryErrorsCapacity;
        init => Agent = Agent with { RetryErrorsCapacity = value };
    }

    /// <summary>
    /// Interval in seconds between heartbeat monitor sweeps. Requires restart to take effect.
    /// Default: 60.
    /// </summary>
    [Key(29)]
    public int HeartbeatSweepIntervalSeconds
    {
        get => Agent.HeartbeatSweepIntervalSeconds;
        init => Agent = Agent with { HeartbeatSweepIntervalSeconds = value };
    }

    /// <summary>
    /// Seconds without a heartbeat before an agent is considered stale.
    /// Default: 90.
    /// </summary>
    [Key(30)]
    public int HeartbeatTimeoutSeconds
    {
        get => Agent.HeartbeatTimeoutSeconds;
        init => Agent = Agent with { HeartbeatTimeoutSeconds = value };
    }

    /// <summary>
    /// Interval in minutes between orphaned label recovery sweeps.
    /// Default: 30.
    /// </summary>
    [Key(55)]
    public int OrphanedLabelSweepIntervalMinutes
    {
        get => Agent.OrphanedLabelSweepIntervalMinutes;
        init => Agent = Agent with { OrphanedLabelSweepIntervalMinutes = value };
    }

    // ── Multi-repo pipeline loop ────────────────────────────────────────

    // Key(45): retired (was PipelineJobTemplates) — do NOT reuse this Key index

    /// <summary>
    /// Maximum number of refactoring proposals the agent is instructed to produce
    /// and the executor will create issues for. Controls both the prompt instruction
    /// ("Produce at most N proposals") and the issue creation cap in RefactoringExecutor.
    /// Default: 3.
    /// </summary>
    [Key(40)]
    [ProjectOverridable(Order = 22)]
    public int MaxRefactoringProposals { get; init; } = 3;

    /// <summary>
    /// Time window for git hotspot analysis in refactoring detection.
    /// Only commits within this window are counted. Default: 90 days.
    /// </summary>
    [Key(31)]
    public TimeSpan HotspotAnalysisLookback { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Maximum number of sub-issues per epic decomposition (range: 1–20). Default: 10.
    /// </summary>
    [Key(37)]
    [ProjectOverridable(Order = 18)]
    public int MaxDecompositionSubIssues
    {
        get => field;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxDecompositionSubIssues));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 20, nameof(MaxDecompositionSubIssues));
            field = value;
        }
    } = 10;

    /// <summary>
    /// Maximum simultaneous decomposition runs. Default: 2.
    /// </summary>
    [Key(36)]
    [ProjectOverridable(Order = 19)]
    public int MaxConcurrentDecompositions { get; init; } = 2;

    /// <summary>
    /// Timeout for each decomposition phase. Default: 15 minutes.
    /// </summary>
    [Key(23)]
    [ProjectOverridable(Order = 20)]
    public TimeSpan DecompositionTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum open issues downloaded for deduplication context. Default: 50.
    /// </summary>
    [Key(39)]
    [ProjectOverridable(Order = 21)]
    public int MaxOpenIssuesForContext { get; init; } = 50;

    /// <summary>
    /// Time window for querying past refactoring proposal outcomes.
    /// Only closed issues within this window are included in the feedback context.
    /// Default: 90 days.
    /// </summary>
    [Key(47)]
    public TimeSpan RefactoringOutcomeLookback { get; init; } = TimeSpan.FromDays(90);

    // ── Issue image extraction settings ─────────────────────────────────

    /// <summary>
    /// Maximum number of images to extract per issue/PR. Default: 10.
    /// </summary>
    [Key(63)]
    public int MaxIssueImages { get; init; } = 10;

    /// <summary>
    /// Maximum size in bytes for a single downloaded image. Default: 5 MB.
    /// </summary>
    [Key(64)]
    public long MaxImageSizeBytes { get; init; } = 5_242_880;

    /// <summary>
    /// Maximum total bytes for all downloaded images combined. Default: 20 MB.
    /// </summary>
    [Key(65)]
    public long MaxTotalImageSizeBytes { get; init; } = 20_971_520;

    /// <summary>
    /// Total time budget in seconds for downloading all images. Default: 60.
    /// </summary>
    [Key(66)]
    public int TotalImageDownloadTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// When true, issue/PR image extraction and download is enabled. Default: true.
    /// </summary>
    [Key(67)]
    public bool EnableIssueImageExtraction { get; init; } = true;

    /// <summary>
    /// When true, downloaded images are sent as native image parts to the agent API. Default: true.
    /// </summary>
    [Key(68)]
    public bool EnableNativeImageParts { get; init; } = true;

    /// <summary>
    /// Timeout in seconds for downloading a single image. Default: 30.
    /// </summary>
    [Key(69)]
    public int ImageDownloadTimeoutSeconds { get; init; } = 30;

}
