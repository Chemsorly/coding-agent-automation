using System.Reflection;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Stateless resolver responsible for applying the configuration resolution chain:
/// Global → Project overrides → Template overrides. Extracted from <see cref="PipelineConfiguration"/>
/// to separate resolution logic from the data record.
/// </summary>
public static class PipelineConfigurationResolver
{
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
}
