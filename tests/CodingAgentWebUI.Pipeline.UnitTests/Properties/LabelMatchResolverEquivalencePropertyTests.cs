using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests verifying that the generic LabelMatchResolver produces identical results
/// to the original QualityGateResolver, ReviewerResolver, and ProfileResolver implementations.
/// Feature: 018-encapsulation-improvements, Properties 1 &amp; 2: LabelMatchResolver equivalence
/// </summary>
public class LabelMatchResolverEquivalencePropertyTests
{
    /// <summary>
    /// Property 1: LabelMatchResolver intersection equivalence (QualityGateResolver)
    /// For any collection of QualityGateConfigurations and target labels, calling LabelMatchResolver.Resolve
    /// directly with Intersection strategy produces the same result as QualityGateResolver.Resolve().
    /// **Validates: Requirements 25.3, 25.4, 25.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(LabelMatchResolverEquivalenceArbitraries) })]
    public void IntersectionEquivalence_QualityGateResolver_MatchesGenericResolver(QgcEquivalenceInput input)
    {
        // Call via QualityGateResolver (the public API)
        var resolver = new QualityGateResolver();
        var resolverResult = resolver.Resolve(input.Configs, input.TargetLabels);

        // Call LabelMatchResolver directly with the same parameters
        var genericResult = LabelMatchResolver.Resolve(
            input.Configs,
            input.TargetLabels,
            enabledPredicate: qgc => qgc.Enabled,
            labelSelector: qgc => qgc.MatchLabels,
            matchStrategy: LabelMatchStrategies.Intersection,
            orderBy: items => items
                .OrderBy(qgc => qgc.ExecutionOrder)
                .ThenBy(qgc => qgc.DisplayName, StringComparer.OrdinalIgnoreCase));

        // Results must be identical in count and order
        genericResult.Count.Should().Be(resolverResult.Count,
            "generic resolver and QualityGateResolver must return same number of results");

        for (int i = 0; i < resolverResult.Count; i++)
        {
            genericResult[i].Should().Be(resolverResult[i],
                $"item at index {i} must be identical between generic resolver and QualityGateResolver");
        }
    }

    /// <summary>
    /// Property 1: LabelMatchResolver intersection equivalence (ReviewerResolver)
    /// For any collection of ReviewerConfigurations and target labels, calling LabelMatchResolver.Resolve
    /// directly with Intersection strategy produces the same result as ReviewerResolver.Resolve().
    /// **Validates: Requirements 25.3, 25.4, 25.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(LabelMatchResolverEquivalenceArbitraries) })]
    public void IntersectionEquivalence_ReviewerResolver_MatchesGenericResolver(ReviewerEquivalenceInput input)
    {
        // Call via ReviewerResolver (the public API)
        var resolver = new ReviewerResolver();
        var resolverResult = resolver.Resolve(input.Configs, input.TargetLabels);

        // Call LabelMatchResolver directly with the same parameters
        var genericResult = LabelMatchResolver.Resolve(
            input.Configs,
            input.TargetLabels,
            enabledPredicate: rc => rc.Enabled,
            labelSelector: rc => rc.MatchLabels,
            matchStrategy: LabelMatchStrategies.Intersection,
            orderBy: items => items
                .OrderBy(rc => rc.ExecutionOrder)
                .ThenBy(rc => rc.DisplayName, StringComparer.OrdinalIgnoreCase));

        // Results must be identical in count and order
        genericResult.Count.Should().Be(resolverResult.Count,
            "generic resolver and ReviewerResolver must return same number of results");

        for (int i = 0; i < resolverResult.Count; i++)
        {
            genericResult[i].Should().Be(resolverResult[i],
                $"item at index {i} must be identical between generic resolver and ReviewerResolver");
        }
    }

    /// <summary>
    /// Property 2: LabelMatchResolver subset equivalence (ProfileResolver)
    /// For any collection of AgentProfiles and agent labels, calling LabelMatchResolver.Resolve
    /// directly with Subset strategy + .FirstOrDefault() produces the same result as ProfileResolver.Resolve().
    /// **Validates: Requirements 25.5, 25.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(LabelMatchResolverEquivalenceArbitraries) })]
    public void SubsetEquivalence_ProfileResolver_MatchesGenericResolver(ProfileEquivalenceInput input)
    {
        // Call via ProfileResolver (the public API)
        var resolver = new ProfileResolver();
        var resolverResult = resolver.Resolve(input.Profiles, input.AgentLabels);

        // Call LabelMatchResolver directly with the same parameters + FirstOrDefault
        var genericResult = LabelMatchResolver.Resolve(
            input.Profiles,
            input.AgentLabels,
            enabledPredicate: p => p.Enabled,
            labelSelector: p => p.MatchLabels,
            matchStrategy: LabelMatchStrategies.Subset,
            orderBy: items => items
                .OrderByDescending(p => p.MatchLabels.Count)
                .ThenByDescending(p => p.Priority)
                .ThenBy(p => p.Id, StringComparer.Ordinal))
            .FirstOrDefault();

        // Results must be identical (both null or same instance)
        if (resolverResult is null)
        {
            genericResult.Should().BeNull(
                "generic resolver with Subset strategy must return null when ProfileResolver returns null");
        }
        else
        {
            genericResult.Should().NotBeNull(
                "generic resolver with Subset strategy must return a result when ProfileResolver returns one");
            genericResult.Should().Be(resolverResult,
                "generic resolver with Subset strategy must return the same profile as ProfileResolver");
        }
    }
}

// --- Wrapper types for FsCheck ---

/// <summary>Input for QualityGateResolver equivalence property tests.</summary>
public sealed class QgcEquivalenceInput
{
    public required IReadOnlyList<QualityGateConfiguration> Configs { get; init; }
    public required IReadOnlyList<string> TargetLabels { get; init; }
    public override string ToString() => $"Configs={Configs.Count}, TargetLabels=[{string.Join(",", TargetLabels)}]";
}

/// <summary>Input for ReviewerResolver equivalence property tests.</summary>
public sealed class ReviewerEquivalenceInput
{
    public required IReadOnlyList<ReviewerConfiguration> Configs { get; init; }
    public required IReadOnlyList<string> TargetLabels { get; init; }
    public override string ToString() => $"Configs={Configs.Count}, TargetLabels=[{string.Join(",", TargetLabels)}]";
}

/// <summary>Input for ProfileResolver equivalence property tests.</summary>
public sealed class ProfileEquivalenceInput
{
    public required IReadOnlyList<AgentProfile> Profiles { get; init; }
    public required IReadOnlyList<string> AgentLabels { get; init; }
    public override string ToString() => $"Profiles={Profiles.Count}, AgentLabels=[{string.Join(",", AgentLabels)}]";
}

// --- Arbitrary generators ---

public class LabelMatchResolverEquivalenceArbitraries
{
    private static readonly string[] LabelPool = ["kiro", "dotnet", "python", "java", "node", "dotnet10", "python312", "java21"];
    private static readonly string[] QgcIds = ["qgc-1", "qgc-2", "qgc-3", "qgc-4", "qgc-5", "qgc-6"];
    private static readonly string[] QgcDisplayNames = ["Build Gate", "Test Gate", "Coverage Gate", "Security Gate", "Lint Gate"];
    private static readonly string[] ProfileIds = ["p1", "p2", "p3", "p4", "p5", "p6"];
    private static readonly string[] ProfileDisplayNames = ["Profile A", "Profile B", "Profile C", "Profile D"];
    private static readonly string[] ReviewerDisplayNames = ["Global Reviewers", "DotNet Reviewers", "Python Reviewers", "Security Gate", "Performance Gate"];
    private static readonly string[] AgentNamePool = ["Correctness", "Security", "DotNetSpecialist", "PythonLinter", "Performance"];

    public static Arbitrary<QgcEquivalenceInput> QgcEquivalenceInputArb()
    {
        var labelSubsetGen = Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList());

        var qgcGen =
            from id in Gen.Elements(QgcIds)
            from displayName in Gen.Elements(QgcDisplayNames)
            from matchLabels in labelSubsetGen
            from enabled in Gen.Elements(true, false)
            from executionOrder in Gen.Choose(0, 10)
            from hasCompilation in Gen.Elements(true, false)
            from hasTest in Gen.Elements(true, false)
            let compilationCommand = hasCompilation || !hasTest ? "dotnet" : null
            let testCommand = hasTest || !hasCompilation ? "dotnet" : null
            select new QualityGateConfiguration
            {
                Id = id,
                DisplayName = displayName,
                MatchLabels = matchLabels,
                CompilationCommand = compilationCommand,
                CompilationArguments = compilationCommand != null ? new[] { "build", "--no-restore" } : null,
                TestCommand = testCommand,
                TestArguments = testCommand != null ? new[] { "test", "--no-restore" } : null,
                Enabled = enabled,
                ExecutionOrder = executionOrder
            };

        var inputGen =
            from count in Gen.Choose(1, 8)
            from configs in Gen.ArrayOf(qgcGen, count)
            from targetLabels in labelSubsetGen
            select new QgcEquivalenceInput
            {
                Configs = configs.ToList(),
                TargetLabels = targetLabels
            };

        return inputGen.ToArbitrary();
    }

    public static Arbitrary<ReviewerEquivalenceInput> ReviewerEquivalenceInputArb()
    {
        var labelSubsetGen = Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList());

        var agentGen =
            from name in Gen.Elements(AgentNamePool)
            from prompt in Gen.Elements("Review for correctness", "Check security issues", "Verify .NET patterns")
            select new ReviewAgent { Name = name, Prompt = prompt };

        var configGen =
            from displayName in Gen.Elements(ReviewerDisplayNames)
            from matchLabels in labelSubsetGen
            from enabled in Gen.Elements(true, false)
            from executionOrder in Gen.Choose(0, 10)
            from agentCount in Gen.Choose(1, 3)
            from agents in Gen.ArrayOf(agentGen, agentCount)
            select new ReviewerConfiguration
            {
                DisplayName = displayName,
                MatchLabels = matchLabels,
                Agents = agents.ToList(),
                Enabled = enabled,
                ExecutionOrder = executionOrder
            };

        var inputGen =
            from count in Gen.Choose(1, 8)
            from configs in Gen.ArrayOf(configGen, count)
            from targetLabels in labelSubsetGen
            select new ReviewerEquivalenceInput
            {
                Configs = configs.ToList(),
                TargetLabels = targetLabels
            };

        return inputGen.ToArbitrary();
    }

    public static Arbitrary<ProfileEquivalenceInput> ProfileEquivalenceInputArb()
    {
        var labelSubsetGen = Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList());

        var profileGen =
            from id in Gen.Elements(ProfileIds)
            from displayName in Gen.Elements(ProfileDisplayNames)
            from matchLabels in labelSubsetGen
            from enabled in Gen.Elements(true, false)
            from priority in Gen.Choose(0, 10)
            select new AgentProfile
            {
                Id = id,
                DisplayName = displayName,
                MatchLabels = matchLabels,
                AgentProviderConfigId = "provider-1",
                Enabled = enabled,
                Priority = priority
            };

        var inputGen =
            from count in Gen.Choose(1, 8)
            from profiles in Gen.ArrayOf(profileGen, count)
            from agentLabels in labelSubsetGen
            select new ProfileEquivalenceInput
            {
                Profiles = profiles.ToList(),
                AgentLabels = agentLabels
            };

        return inputGen.ToArbitrary();
    }
}
