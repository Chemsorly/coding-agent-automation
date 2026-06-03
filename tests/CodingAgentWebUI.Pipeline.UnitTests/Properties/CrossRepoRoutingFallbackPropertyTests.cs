// Feature: 029-pipeline-projects
// Property 7: Cross-Repo Routing Fallback
// Verify unresolvable targetRepository always falls back to dispatching template's issue provider —
// never fails, never routes to random provider.
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for Cross-Repo Routing Fallback.
/// Verifies that when a SubIssueProposal has a targetRepository that doesn't match
/// any template name in the project context, the system falls back to the dispatching
/// template's default issue provider (returns null) — never throws, never returns a
/// random provider ID.
/// **Validates: Requirements 7.4, 7.5**
/// </summary>
public class CrossRepoRoutingFallbackPropertyTests
{
    /// <summary>
    /// Property 7a: Unresolvable targetRepository always returns null (fallback to default provider).
    /// For ANY targetRepository value that doesn't exactly match a template name in the project context,
    /// the result is always null — meaning use the dispatching template's default provider.
    /// Never throws, never returns a random provider ID.
    /// **Validates: Requirements 7.4, 7.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(CrossRepoRoutingArbitraries) })]
    public void ResolveTargetIssueProviderId_UnresolvableTarget_AlwaysReturnsNull(
        UnresolvableTargetInput input)
    {
        // Act — should never throw
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            input.TargetRepository,
            input.ProjectContext);

        // Assert — always null for unresolvable targets (fallback to default provider)
        result.Should().BeNull(
            $"targetRepository '{input.TargetRepository}' does not match any template name " +
            $"in project '{input.ProjectContext.ProjectName}', so result must be null (fallback)");
    }

    /// <summary>
    /// Property 7b: Null or empty targetRepository always returns null (default behavior).
    /// When targetRepository is null or empty, the system uses the dispatching template's
    /// default issue provider regardless of project context state.
    /// **Validates: Requirements 7.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(CrossRepoRoutingArbitraries) })]
    public void ResolveTargetIssueProviderId_NullOrEmptyTarget_AlwaysReturnsNull(
        NullOrEmptyTargetInput input)
    {
        // Act — should never throw
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            input.TargetRepository,
            input.ProjectContext);

        // Assert — always null for null/empty targets
        result.Should().BeNull(
            "null or empty targetRepository must always fall back to dispatching template's provider");
    }

    /// <summary>
    /// Property 7c: No project context always returns null (backward compatible).
    /// When no project context is available (per-template decomposition), any targetRepository
    /// value falls back to the dispatching template's default provider.
    /// **Validates: Requirements 7.4, 7.5**
    /// </summary>
    [Property]
    public void ResolveTargetIssueProviderId_NoProjectContext_AlwaysReturnsNull(
        NonEmptyString targetRepository)
    {
        // Act — should never throw even with a non-empty target and null context
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            targetRepository.Get,
            projectContext: null);

        // Assert — always null when no project context
        result.Should().BeNull(
            "with no project context, any targetRepository must fall back to default provider");
    }

    /// <summary>
    /// Property 7d: Resolvable targetRepository returns the correct provider ID (positive case).
    /// When targetRepository exactly matches a template name that has an IssueProviderId,
    /// the method returns that specific provider ID — never a random one from the project.
    /// **Validates: Requirements 7.4, 7.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(CrossRepoRoutingArbitraries) })]
    public void ResolveTargetIssueProviderId_ResolvableTarget_ReturnsCorrectProviderId(
        ResolvableTargetInput input)
    {
        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            input.TargetRepository,
            input.ProjectContext);

        // Assert — must return the exact provider ID of the matched template
        result.Should().Be(input.ExpectedProviderId,
            $"targetRepository '{input.TargetRepository}' matches template with " +
            $"IssueProviderId '{input.ExpectedProviderId}'");
    }

    /// <summary>
    /// Property 7e: Case-sensitive matching — different case never resolves.
    /// targetRepository matching is case-sensitive (StringComparison.Ordinal), so
    /// a value that differs only in case from a template name must fall back to null.
    /// **Validates: Requirements 7.4, 7.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(CrossRepoRoutingArbitraries) })]
    public void ResolveTargetIssueProviderId_CaseMismatch_ReturnsNull(
        CaseMismatchInput input)
    {
        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            input.TargetRepository,
            input.ProjectContext);

        // Assert — case mismatch means unresolvable, must return null
        result.Should().BeNull(
            $"targetRepository '{input.TargetRepository}' differs in case from template name " +
            $"'{input.OriginalTemplateName}', so it must not resolve");
    }
}

// --- Input wrapper types for FsCheck ---

/// <summary>Input where targetRepository doesn't match any template name in the project.</summary>
public sealed class UnresolvableTargetInput
{
    public required string TargetRepository { get; init; }
    public required DecompositionProjectContext ProjectContext { get; init; }

    public override string ToString() =>
        $"Target='{TargetRepository}', Project='{ProjectContext.ProjectName}' " +
        $"(templates: [{string.Join(", ", ProjectContext.Repositories.Select(r => r.TemplateName))}])";
}

/// <summary>Input where targetRepository is null or empty.</summary>
public sealed class NullOrEmptyTargetInput
{
    public required string? TargetRepository { get; init; }
    public required DecompositionProjectContext? ProjectContext { get; init; }

    public override string ToString() =>
        $"Target='{TargetRepository ?? "<null>"}', HasContext={ProjectContext is not null}";
}

/// <summary>Input where targetRepository matches a template name with a valid IssueProviderId.</summary>
public sealed class ResolvableTargetInput
{
    public required string TargetRepository { get; init; }
    public required DecompositionProjectContext ProjectContext { get; init; }
    public required string ExpectedProviderId { get; init; }

    public override string ToString() =>
        $"Target='{TargetRepository}', Expected='{ExpectedProviderId}'";
}

/// <summary>Input where targetRepository differs only in case from a template name.</summary>
public sealed class CaseMismatchInput
{
    public required string TargetRepository { get; init; }
    public required string OriginalTemplateName { get; init; }
    public required DecompositionProjectContext ProjectContext { get; init; }

    public override string ToString() =>
        $"Target='{TargetRepository}', Original='{OriginalTemplateName}'";
}

// --- Arbitrary generators ---

/// <summary>
/// FsCheck generators for cross-repo routing fallback property tests.
/// Generates random project contexts and targetRepository values that exercise
/// the routing resolution logic's fallback paths.
/// </summary>
public class CrossRepoRoutingArbitraries
{
    private static readonly string[] TemplateNamePool =
    [
        "backend-api", "frontend-web", "shared-library", "data-pipeline",
        "mobile-app", "infrastructure", "documentation", "auth-service",
        "payment-gateway", "notification-service"
    ];

    private static readonly string[] ProviderIdPool =
    [
        "provider-001", "provider-002", "provider-003", "provider-004",
        "provider-005", "provider-006", "provider-007", "provider-008"
    ];

    private static readonly string[] UnresolvableNamePool =
    [
        "nonexistent-repo", "unknown-service", "deleted-template",
        "typo-in-name", "old-project-name", "repo-that-was-removed",
        "BACKEND-API", "Frontend-Web", "SHARED-LIBRARY" // case variants
    ];

    private static Gen<string> GenRandomUnresolvableName() =>
        from len in Gen.Choose(5, 25)
        from chars in Gen.ArrayOf(Gen.Elements(
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm',
            'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z',
            '-', '_', '0', '1', '2', '3', '4', '5'), len)
        select "rnd-" + new string(chars);

    private static Gen<RepositoryTarget> GenRepositoryTarget() =>
        from name in Gen.Elements(TemplateNamePool)
        from providerId in Gen.Frequency(
            (4, Gen.Elements(ProviderIdPool).Select<string, string?>(p => p)),
            (1, Gen.Constant<string?>(null))) // Some targets have no provider ID
        from description in Gen.Elements("API service", "Web frontend", "Core library", "ETL pipeline")
        from decompositionEnabled in Gen.Elements(true, false)
        select new RepositoryTarget
        {
            TemplateName = name,
            Description = description,
            DecompositionEnabled = decompositionEnabled,
            Available = true,
            IssueProviderId = providerId
        };

    private static Gen<DecompositionProjectContext> GenProjectContext() =>
        from repoCount in Gen.Choose(1, 6)
        from repos in Gen.ArrayOf(GenRepositoryTarget()).Resize(repoCount)
        from projectName in Gen.Elements("MyProject", "CrossRepoProject", "TestProject", "PlatformTeam")
        select new DecompositionProjectContext
        {
            ProjectName = projectName,
            // Ensure unique template names within a project context
            Repositories = repos.DistinctBy(r => r.TemplateName).ToList()
        };

    /// <summary>
    /// Generates inputs where targetRepository does NOT match any template name.
    /// Uses names that are guaranteed not to be in the TemplateNamePool (or uses case-different variants).
    /// </summary>
    public static Arbitrary<UnresolvableTargetInput> UnresolvableTargetInputArb()
    {
        var gen =
            from context in GenProjectContext()
            from target in Gen.Frequency(
                (3, Gen.Elements(UnresolvableNamePool)),
                (2, GenRandomUnresolvableName()))
            // Filter out any accidental matches with actual template names in the context
            where !context.Repositories.Any(r =>
                string.Equals(r.TemplateName, target, StringComparison.Ordinal))
            select new UnresolvableTargetInput
            {
                TargetRepository = target,
                ProjectContext = context
            };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates inputs where targetRepository is null or empty.
    /// Project context varies (null, empty repos, populated repos) to verify
    /// null/empty target always falls back regardless of context state.
    /// </summary>
    public static Arbitrary<NullOrEmptyTargetInput> NullOrEmptyTargetInputArb()
    {
        var gen =
            from target in Gen.Elements<string?>(null, "", "   ")
            from hasContext in Gen.Elements(true, false)
            from context in hasContext
                ? GenProjectContext().Select<DecompositionProjectContext, DecompositionProjectContext?>(c => c)
                : Gen.Constant<DecompositionProjectContext?>(null)
            select new NullOrEmptyTargetInput
            {
                TargetRepository = target,
                ProjectContext = context
            };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates inputs where targetRepository exactly matches a template name
    /// that has a non-null IssueProviderId. Verifies the positive case (correct routing).
    /// </summary>
    public static Arbitrary<ResolvableTargetInput> ResolvableTargetInputArb()
    {
        var gen =
            from context in GenProjectContext()
            // Only select templates that have a non-null/non-empty IssueProviderId
            let validTargets = context.Repositories
                .Where(r => !string.IsNullOrEmpty(r.IssueProviderId))
                .ToList()
            where validTargets.Count > 0
            from targetIdx in Gen.Choose(0, validTargets.Count - 1)
            let target = validTargets[targetIdx]
            select new ResolvableTargetInput
            {
                TargetRepository = target.TemplateName,
                ProjectContext = context,
                ExpectedProviderId = target.IssueProviderId!
            };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates inputs where targetRepository differs from a template name only in case.
    /// Verifies that matching is case-sensitive (Ordinal comparison).
    /// </summary>
    public static Arbitrary<CaseMismatchInput> CaseMismatchInputArb()
    {
        var gen =
            from context in GenProjectContext()
            where context.Repositories.Count > 0
            from targetIdx in Gen.Choose(0, context.Repositories.Count - 1)
            let original = context.Repositories[targetIdx].TemplateName
            from caseVariant in Gen.Elements(
                original.ToUpperInvariant(),
                original.ToLowerInvariant(),
                char.ToUpper(original[0]) + original[1..],
                original[..^1] + char.ToUpper(original[^1]))
            // Only use variants that actually differ from the original
            where !string.Equals(caseVariant, original, StringComparison.Ordinal)
            // Also ensure the case variant doesn't accidentally match another template
            where !context.Repositories.Any(r =>
                string.Equals(r.TemplateName, caseVariant, StringComparison.Ordinal))
            select new CaseMismatchInput
            {
                TargetRepository = caseVariant,
                OriginalTemplateName = original,
                ProjectContext = context
            };

        return gen.ToArbitrary();
    }
}
