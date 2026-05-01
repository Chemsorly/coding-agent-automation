using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for ReviewerResolver resolution correctness and FlattenAgents behavior.
/// Feature: 014-reviewer-configuration-ui
/// </summary>
public class ReviewerResolverPropertyTests
{
    /// <summary>
    /// Property 3a: Resolver Inclusion Correctness
    /// For any set of ReviewerConfigurations and job labels, every resolved configuration must satisfy:
    /// (a) Enabled == true, AND (b) MatchLabels is empty OR intersects with job labels (case-insensitive).
    /// **Validates: Requirements 1.4, 3.2, 3.3, 3.4**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ReviewerResolverArbitraries) })]
    public void ResolverInclusion_ResolvedConfigsAreEnabledAndMatch(ReviewerResolutionInput input)
    {
        var resolver = new ReviewerResolver();
        var result = resolver.Resolve(input.Configs, input.JobLabels);
        var jobLabelSet = new HashSet<string>(input.JobLabels, StringComparer.OrdinalIgnoreCase);

        foreach (var rc in result)
        {
            // Must be enabled
            rc.Enabled.Should().BeTrue("only enabled configurations should be resolved");

            // Must have empty MatchLabels OR intersect with job labels
            var matchLabelsEmpty = rc.MatchLabels.Count == 0;
            var intersects = rc.MatchLabels.Any(l => jobLabelSet.Contains(l));

            (matchLabelsEmpty || intersects).Should().BeTrue(
                $"ReviewerConfiguration '{rc.DisplayName}' (Id={rc.Id}) must have empty MatchLabels or intersect with job labels");
        }
    }

    /// <summary>
    /// Property 3b: Resolver Exclusion Correctness
    /// Disabled configs never appear; enabled configs with non-empty MatchLabels and zero intersection never appear.
    /// **Validates: Requirements 3.3, 3.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ReviewerResolverArbitraries) })]
    public void ResolverExclusion_DisabledAndNonIntersectingExcluded(ReviewerResolutionInput input)
    {
        var resolver = new ReviewerResolver();
        var result = resolver.Resolve(input.Configs, input.JobLabels);
        var jobLabelSet = new HashSet<string>(input.JobLabels, StringComparer.OrdinalIgnoreCase);

        // Disabled configs must never appear
        var disabled = input.Configs.Where(rc => !rc.Enabled).ToList();
        foreach (var excluded in disabled)
        {
            result.Should().NotContain(excluded,
                $"Disabled config '{excluded.DisplayName}' must not appear in results");
        }

        // Enabled configs with non-empty MatchLabels and zero intersection must not appear
        var nonIntersecting = input.Configs
            .Where(rc => rc.Enabled)
            .Where(rc => rc.MatchLabels.Count > 0)
            .Where(rc => !rc.MatchLabels.Any(l => jobLabelSet.Contains(l)))
            .ToList();

        foreach (var excluded in nonIntersecting)
        {
            result.Should().NotContain(excluded,
                $"Config '{excluded.DisplayName}' has no label intersection and must be excluded");
        }
    }

    /// <summary>
    /// Property 3c: Resolver Ordering Correctness
    /// Results ordered by ExecutionOrder ascending, then DisplayName alphabetically (case-insensitive).
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ReviewerResolverArbitraries) })]
    public void ResolverOrdering_ResultsAreCorrectlyOrdered(ReviewerResolutionInput input)
    {
        var resolver = new ReviewerResolver();
        var result = resolver.Resolve(input.Configs, input.JobLabels);

        for (int i = 1; i < result.Count; i++)
        {
            var prev = result[i - 1];
            var curr = result[i];

            if (prev.ExecutionOrder > curr.ExecutionOrder)
            {
                Assert.Fail($"Config at index {i - 1} (ExecutionOrder={prev.ExecutionOrder}) should come before index {i} (ExecutionOrder={curr.ExecutionOrder})");
            }

            if (prev.ExecutionOrder == curr.ExecutionOrder)
            {
                var cmp = string.Compare(prev.DisplayName, curr.DisplayName, StringComparison.OrdinalIgnoreCase);
                cmp.Should().BeLessThanOrEqualTo(0,
                    $"Configs with same ExecutionOrder should be sorted by DisplayName: '{prev.DisplayName}' should come before or equal '{curr.DisplayName}'");
            }
        }
    }

    /// <summary>
    /// Property 3d: Case-Insensitive Label Matching
    /// Labels differing only in case still match.
    /// **Validates: Requirements 1.4, 3.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ReviewerResolverArbitraries) })]
    public void CaseInsensitiveLabelMatching_StillMatches(CaseInsensitiveReviewerLabelInput input)
    {
        var rc = new ReviewerConfiguration
        {
            Id = "case-test-rc",
            DisplayName = "Case Test Config",
            MatchLabels = input.OriginalLabels,
            Agents = new[] { new ReviewAgent { Name = "TestAgent", Prompt = "Test prompt" } },
            Enabled = true,
            ExecutionOrder = 0
        };

        var resolver = new ReviewerResolver();

        // Job labels use the case variant — should still match
        var result = resolver.Resolve(new[] { rc }, input.CaseVariantLabels);

        result.Should().NotBeEmpty("ReviewerConfiguration should match when labels differ only in case");
        result.Should().Contain(rc);
    }

    /// <summary>
    /// Property 5: Flattening Preserves All Agents
    /// FlattenAgents output count equals sum of all individual Agents.Count; order preserved.
    /// **Validates: Requirements 4.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(ReviewerResolverArbitraries) })]
    public void FlattenAgents_PreservesAllAgentsInOrder(FlattenAgentsInput input)
    {
        var result = ReviewerResolver.FlattenAgents(input.Configs);

        // Count must equal sum of all agents
        var expectedCount = input.Configs.Sum(rc => rc.Agents.Count);
        result.Count.Should().Be(expectedCount,
            "flattened count must equal sum of all individual Agents.Count");

        // Order must be preserved: config[0].agents ++ config[1].agents ++ ...
        var expectedAgents = input.Configs
            .SelectMany(rc => rc.Agents)
            .ToList();

        for (int i = 0; i < result.Count; i++)
        {
            result[i].Name.Should().Be(expectedAgents[i].Name,
                $"agent at index {i} should have matching Name");
            result[i].Prompt.Should().Be(expectedAgents[i].Prompt,
                $"agent at index {i} should have matching Prompt");
        }
    }
}

// --- Wrapper types for FsCheck ---

/// <summary>Input for ReviewerResolver resolution property tests.</summary>
public sealed class ReviewerResolutionInput
{
    public required IReadOnlyList<ReviewerConfiguration> Configs { get; init; }
    public required IReadOnlyList<string> JobLabels { get; init; }
    public override string ToString() => $"Configs={Configs.Count}, JobLabels=[{string.Join(",", JobLabels)}]";
}

/// <summary>Input for case-insensitive reviewer label matching property tests.</summary>
public sealed class CaseInsensitiveReviewerLabelInput
{
    public required IReadOnlyList<string> OriginalLabels { get; init; }
    public required IReadOnlyList<string> CaseVariantLabels { get; init; }
    public override string ToString() => $"Original=[{string.Join(",", OriginalLabels)}], Variant=[{string.Join(",", CaseVariantLabels)}]";
}

/// <summary>Input for FlattenAgents property tests.</summary>
public sealed class FlattenAgentsInput
{
    public required IReadOnlyList<ReviewerConfiguration> Configs { get; init; }
    public override string ToString() => $"Configs={Configs.Count}, TotalAgents={Configs.Sum(c => c.Agents.Count)}";
}

// --- Arbitrary generators ---

public class ReviewerResolverArbitraries
{
    private static readonly string[] LabelPool = ["kiro", "dotnet", "python", "java", "node", "csharp", "python312", "java21"];
    private static readonly string[] AgentNamePool = ["Correctness", "Security", "DotNetSpecialist", "PythonLinter", "Performance"];
    private static readonly string[] DisplayNames = ["Global Reviewers", "DotNet Reviewers", "Python Reviewers", "Security Gate", "Performance Gate", "Java Reviewers"];

    public static Arbitrary<ReviewerResolutionInput> ReviewerResolutionInputArb()
    {
        var labelSubsetGen = Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList());

        var agentGen =
            from name in Gen.Elements(AgentNamePool)
            from prompt in Gen.Elements("Review for correctness", "Check security issues", "Verify .NET patterns", "Lint Python code", "Check performance")
            select new ReviewAgent { Name = name, Prompt = prompt };

        var configGen =
            from displayName in Gen.Elements(DisplayNames)
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
            from count in Gen.Choose(1, 6)
            from configs in Gen.ArrayOf(configGen, count)
            from jobLabels in labelSubsetGen
            select new ReviewerResolutionInput
            {
                Configs = configs.ToList(),
                JobLabels = jobLabels
            };

        return inputGen.ToArbitrary();
    }

    public static Arbitrary<CaseInsensitiveReviewerLabelInput> CaseInsensitiveReviewerLabelInputArb()
    {
        var gen =
            from count in Gen.Choose(1, 5)
            from labels in Gen.ArrayOf(Gen.Elements(LabelPool), count)
            let distinct = labels.Distinct().ToList()
            where distinct.Count > 0
            from upperFlags in Gen.ArrayOf(Gen.Elements(true, false), distinct.Count)
            select new CaseInsensitiveReviewerLabelInput
            {
                OriginalLabels = distinct,
                CaseVariantLabels = distinct.Select((l, i) => i < upperFlags.Length && upperFlags[i] ? l.ToUpperInvariant() : l.ToLowerInvariant()).ToList()
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<FlattenAgentsInput> FlattenAgentsInputArb()
    {
        var agentGen =
            from name in Gen.Elements(AgentNamePool)
            from prompt in Gen.Elements("Review for correctness", "Check security issues", "Verify .NET patterns", "Lint Python code", "Check performance")
            select new ReviewAgent { Name = name, Prompt = prompt };

        var configGen =
            from displayName in Gen.Elements(DisplayNames)
            from agentCount in Gen.Choose(1, 4)
            from agents in Gen.ArrayOf(agentGen, agentCount)
            select new ReviewerConfiguration
            {
                DisplayName = displayName,
                MatchLabels = [],
                Agents = agents.ToList(),
                Enabled = true,
                ExecutionOrder = 0
            };

        var inputGen =
            from count in Gen.Choose(1, 5)
            from configs in Gen.ArrayOf(configGen, count)
            select new FlattenAgentsInput
            {
                Configs = configs.ToList()
            };

        return inputGen.ToArbitrary();
    }
}
