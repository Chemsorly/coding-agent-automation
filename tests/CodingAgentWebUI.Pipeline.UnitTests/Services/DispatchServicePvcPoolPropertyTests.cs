using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for DispatchService.CalculateAvailablePvcs.
/// **Validates: Requirements 11.2**
/// </summary>
public class DispatchServicePvcPoolPropertyTests
{
    /// <summary>
    /// Property 12: PVC Pool Availability Calculation
    /// For any configured PVC list and any set of claimed PVCs (drawn from non-terminal WorkItems),
    /// available PVCs == configuredPvcs.Except(claimed).
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PvcPoolArbitraries) })]
    public void CalculateAvailablePvcs_ReturnsConfiguredExceptClaimed(PvcPoolTestInput input)
    {
        var available = DispatchService.CalculateAvailablePvcs(input.ConfiguredPvcs, input.ClaimedPvcs);

        var expected = input.ConfiguredPvcs
            .Except(input.ClaimedPvcs, StringComparer.Ordinal)
            .ToList();

        available.Should().BeEquivalentTo(expected);
    }
}

/// <summary>
/// Test input pairing configured PVC names with claimed PVC names.
/// Claimed PVCs may include names from the configured pool (simulating active WorkItems)
/// and names NOT in the pool (simulating stale claims or external PVCs).
/// </summary>
public record PvcPoolTestInput(IReadOnlyList<string> ConfiguredPvcs, IReadOnlyList<string> ClaimedPvcs);

/// <summary>
/// FsCheck arbitrary generators for PVC Pool Availability property tests.
/// Generates realistic PVC name pools and claimed subsets.
/// </summary>
public class PvcPoolArbitraries
{
    public static Arbitrary<PvcPoolTestInput> PvcPoolTestInputArbitrary()
    {
        var gen = from poolSize in Gen.Choose(0, 20)
                  from configuredNames in GeneratePvcName().ArrayOf(poolSize)
                  let configured = configuredNames.Distinct(StringComparer.Ordinal).ToList()
                  from claimedFromPool in GenSubset(configured)
                  from extraClaimedCount in Gen.Choose(0, 5)
                  from extraClaimed in GeneratePvcName().ArrayOf(extraClaimedCount)
                  let claimed = claimedFromPool.Concat(extraClaimed).Distinct(StringComparer.Ordinal).ToList()
                  select new PvcPoolTestInput(configured, claimed);

        return gen.ToArbitrary();
    }

    private static Gen<string> GeneratePvcName()
    {
        return from suffix in Gen.Choose(1, 999)
               select $"pvc-kiro-{suffix}";
    }

    private static Gen<IReadOnlyList<string>> GenSubset(IReadOnlyList<string> source)
    {
        if (source.Count == 0)
            return Gen.Constant<IReadOnlyList<string>>(Array.Empty<string>());

        return from flags in Gen.Elements(true, false).ArrayOf(source.Count)
               select (IReadOnlyList<string>)source.Where((_, i) => flags[i]).ToList();
    }
}
