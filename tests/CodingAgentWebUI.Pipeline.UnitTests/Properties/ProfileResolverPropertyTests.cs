using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for ProfileResolver resolution correctness and ProfileValidator validation rules.
/// Feature: agent-configuration-management
/// </summary>
public class ProfileResolverPropertyTests
{
    /// <summary>
    /// Property 2: Profile Resolution Correctness
    /// For any set of enabled profiles and agent labels, the resolved profile (if not null) must satisfy:
    /// (a) its MatchLabels are a subset of agent labels,
    /// (b) no other matching profile has higher specificity,
    /// (c) among equal specificity, no other has higher Priority,
    /// (d) among equal specificity+priority, no other has lexicographically earlier Id.
    /// **Validates: Requirements 2.1, 2.2, 1.6, 17.1, 17.2, 17.3, 17.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProfileResolverArbitraries) })]
    public void ProfileResolution_ResolvedProfileIsOptimal(ProfileResolutionInput input)
    {
        var resolver = new ProfileResolver();
        var result = resolver.Resolve(input.Profiles, input.AgentLabels);

        if (result is null)
        {
            // If null, no enabled profile's MatchLabels is a subset of agentLabels
            var agentLabelSet = new HashSet<string>(input.AgentLabels, StringComparer.OrdinalIgnoreCase);
            var anyMatch = input.Profiles.Any(p => p.Enabled && p.MatchLabels.All(l => agentLabelSet.Contains(l)));
            anyMatch.Should().BeFalse("no profile should match if resolver returns null");
            return;
        }

        var agentSet = new HashSet<string>(input.AgentLabels, StringComparer.OrdinalIgnoreCase);

        // (a) MatchLabels is a subset of agentLabels
        result.MatchLabels.All(l => agentSet.Contains(l)).Should().BeTrue(
            "resolved profile's MatchLabels must be a subset of agent labels");

        // Get all matching enabled profiles for comparison
        var allMatching = input.Profiles
            .Where(p => p.Enabled)
            .Where(p => p.MatchLabels.All(l => agentSet.Contains(l)))
            .ToList();

        allMatching.Should().Contain(result);

        foreach (var other in allMatching)
        {
            if (other.Id == result.Id && other.DisplayName == result.DisplayName) continue;

            // (b) no other has higher specificity
            if (other.MatchLabels.Count > result.MatchLabels.Count)
            {
                Assert.Fail($"Profile {other.Id} has higher specificity ({other.MatchLabels.Count}) than resolved {result.Id} ({result.MatchLabels.Count})");
            }

            // (c) among equal specificity, no other has higher Priority
            if (other.MatchLabels.Count == result.MatchLabels.Count && other.Priority > result.Priority)
            {
                Assert.Fail($"Profile {other.Id} has same specificity but higher priority ({other.Priority}) than resolved {result.Id} ({result.Priority})");
            }

            // (d) among equal specificity+priority, no other has lexicographically earlier Id
            if (other.MatchLabels.Count == result.MatchLabels.Count
                && other.Priority == result.Priority
                && string.Compare(other.Id, result.Id, StringComparison.Ordinal) < 0)
            {
                Assert.Fail($"Profile {other.Id} has same specificity+priority but earlier Id than resolved {result.Id}");
            }
        }
    }

    /// <summary>
    /// Property 3: Profile Validation Rejects Empty DisplayName
    /// For any null/empty/whitespace string as DisplayName, validation must fail.
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProfileResolverArbitraries) })]
    public void ProfileValidation_RejectsEmptyDisplayName(InvalidDisplayNameInput input)
    {
        var profile = new AgentProfile
        {
            Id = "test-id",
            DisplayName = input.DisplayName,
            MatchLabels = new[] { "kiro" },
            AgentProviderConfigId = "provider-1"
        };

        var result = ProfileValidator.Validate(profile, Array.Empty<AgentProfile>());

        result.IsValid.Should().BeFalse("empty/whitespace DisplayName must be rejected");
        result.ErrorMessage.Should().Contain("DisplayName");
    }

    /// <summary>
    /// Property 12: Default Profile Uniqueness Invariant
    /// For any profile set, validation rejects a second profile with empty MatchLabels when one already exists.
    /// **Validates: Requirements 1.6, 1.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProfileResolverArbitraries) })]
    public void DefaultProfileUniqueness_RejectsSecondDefault(DefaultProfileUniquenessInput input)
    {
        var existingDefault = new AgentProfile
        {
            Id = input.ExistingId,
            DisplayName = "Existing Default",
            MatchLabels = Array.Empty<string>(),
            AgentProviderConfigId = "provider-1"
        };

        var newDefault = new AgentProfile
        {
            Id = input.NewId,
            DisplayName = "New Default",
            MatchLabels = Array.Empty<string>(),
            AgentProviderConfigId = "provider-2"
        };

        var result = ProfileValidator.Validate(newDefault, new[] { existingDefault });

        result.IsValid.Should().BeFalse("a second default profile (empty MatchLabels) must be rejected");
        result.ErrorMessage.Should().Contain("default");
    }

    /// <summary>
    /// Property 13: Profile Resolution Negative
    /// For any set of profiles and agent labels, if the resolved profile is not null,
    /// then its MatchLabels IS a subset of agentLabels (no non-matching profile is ever returned).
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProfileResolverArbitraries) })]
    public void ProfileResolutionNegative_NonMatchingNeverReturned(ProfileResolutionInput input)
    {
        var resolver = new ProfileResolver();
        var result = resolver.Resolve(input.Profiles, input.AgentLabels);

        if (result is null) return;

        var agentSet = new HashSet<string>(input.AgentLabels, StringComparer.OrdinalIgnoreCase);

        // The resolved profile's MatchLabels must be a subset of agentLabels
        result.MatchLabels.All(l => agentSet.Contains(l)).Should().BeTrue(
            "resolved profile must have MatchLabels that are a subset of agent labels — non-matching profiles must never be returned");
    }

    /// <summary>
    /// Property 15: Label Matching Case Insensitivity
    /// For any profile MatchLabels and agent labels that differ only in case, the profile still matches.
    /// **Validates: Consistency with existing IsLabelMatch behavior (StringComparer.OrdinalIgnoreCase)**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProfileResolverArbitraries) })]
    public void LabelMatchingCaseInsensitivity_ProfileStillMatches(CaseInsensitiveLabelInput input)
    {
        var profile = new AgentProfile
        {
            Id = "case-test",
            DisplayName = "Case Test Profile",
            MatchLabels = input.OriginalLabels,
            AgentProviderConfigId = "provider-1",
            Enabled = true,
            Priority = 0
        };

        var resolver = new ProfileResolver();

        // Agent labels use the case variant
        var result = resolver.Resolve(new[] { profile }, input.CaseVariantLabels);

        result.Should().NotBeNull("profile should match when labels differ only in case");
        result!.Id.Should().Be("case-test");
    }
}

// --- Wrapper types for FsCheck ---

/// <summary>Input for profile resolution property tests.</summary>
public sealed class ProfileResolutionInput
{
    public required IReadOnlyList<AgentProfile> Profiles { get; init; }
    public required IReadOnlyList<string> AgentLabels { get; init; }
    public override string ToString() => $"Profiles={Profiles.Count}, AgentLabels=[{string.Join(",", AgentLabels)}]";
}

/// <summary>Input for invalid DisplayName property tests.</summary>
public sealed class InvalidDisplayNameInput
{
    public required string DisplayName { get; init; }
    public override string ToString() => $"DisplayName='{DisplayName}'";
}

/// <summary>Input for default profile uniqueness property tests.</summary>
public sealed class DefaultProfileUniquenessInput
{
    public required string ExistingId { get; init; }
    public required string NewId { get; init; }
    public override string ToString() => $"ExistingId={ExistingId}, NewId={NewId}";
}

/// <summary>Input for case-insensitive label matching property tests.</summary>
public sealed class CaseInsensitiveLabelInput
{
    public required IReadOnlyList<string> OriginalLabels { get; init; }
    public required IReadOnlyList<string> CaseVariantLabels { get; init; }
    public override string ToString() => $"Original=[{string.Join(",", OriginalLabels)}], Variant=[{string.Join(",", CaseVariantLabels)}]";
}

// --- Arbitrary generators ---

public class ProfileResolverArbitraries
{
    private static readonly string[] LabelPool = ["kiro", "dotnet", "python", "java", "node", "dotnet10", "python312", "java21"];
    private static readonly string[] ProfileIds = ["p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8"];
    private static readonly string[] DisplayNames = ["Profile A", "Profile B", "Profile C", "Profile D"];

    public static Arbitrary<ProfileResolutionInput> ProfileResolutionInputArb()
    {
        var labelSubsetGen = Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList());

        var profileGen =
            from id in Gen.Elements(ProfileIds)
            from displayName in Gen.Elements(DisplayNames)
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
            from count in Gen.Choose(1, 6)
            from profiles in Gen.ArrayOf(profileGen, count)
            from agentLabels in labelSubsetGen
            select new ProfileResolutionInput
            {
                Profiles = profiles.ToList(),
                AgentLabels = agentLabels
            };

        return inputGen.ToArbitrary();
    }

    public static Arbitrary<InvalidDisplayNameInput> InvalidDisplayNameInputArb()
    {
        var gen = Gen.Elements("", " ", "  ", "\t", "\n", " \t\n ")
            .Select(s => new InvalidDisplayNameInput { DisplayName = s });

        return gen.ToArbitrary();
    }

    public static Arbitrary<DefaultProfileUniquenessInput> DefaultProfileUniquenessInputArb()
    {
        var gen =
            from existingId in Gen.Elements(ProfileIds)
            from newId in Gen.Elements(ProfileIds)
            where existingId != newId
            select new DefaultProfileUniquenessInput
            {
                ExistingId = existingId,
                NewId = newId
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<CaseInsensitiveLabelInput> CaseInsensitiveLabelInputArb()
    {
        var gen =
            from count in Gen.Choose(1, 5)
            from labels in Gen.ArrayOf(Gen.Elements(LabelPool), count)
            let distinct = labels.Distinct().ToList()
            where distinct.Count > 0
            from upperFlags in Gen.ArrayOf(Gen.Elements(true, false), distinct.Count)
            select new CaseInsensitiveLabelInput
            {
                OriginalLabels = distinct,
                CaseVariantLabels = distinct.Select((l, i) => i < upperFlags.Length && upperFlags[i] ? l.ToUpperInvariant() : l.ToLowerInvariant()).ToList()
            };

        return gen.ToArbitrary();
    }
}
