using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for DispatchService image mapping determinism.
/// **Validates: Requirements 5.5**
/// </summary>
public class DispatchServiceImageMappingPropertyTests
{
    /// <summary>
    /// Property 15: Image Mapping Determinism
    /// For any set of labels, regardless of the order they are provided,
    /// NormalizeSelector produces the same canonical key (sorted, comma-joined).
    /// Same key guarantees same image lookup.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(ImageMappingArbitraries) })]
    public void NormalizeSelector_ProducesSameKey_RegardlessOfInputOrder(string[] labels)
    {
        // Arrange: create two different orderings of the same label set
        var ordering1 = string.Join(",", labels);
        var ordering2 = string.Join(",", labels.Reverse());

        // Act: normalize both orderings
        var normalized1 = DispatchService.NormalizeSelector(ordering1);
        var normalized2 = DispatchService.NormalizeSelector(ordering2);

        // Assert: both produce the same canonical key
        normalized1.Should().Be(normalized2);
    }

    /// <summary>
    /// Property 15 (supplementary): NormalizeSelector is idempotent —
    /// normalizing an already-normalized selector produces the same result.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(ImageMappingArbitraries) })]
    public void NormalizeSelector_IsIdempotent(string[] labels)
    {
        var input = string.Join(",", labels);

        var normalized = DispatchService.NormalizeSelector(input);
        var normalizedAgain = DispatchService.NormalizeSelector(normalized);

        normalizedAgain.Should().Be(normalized);
    }

    /// <summary>
    /// Property 15 (supplementary): NormalizeSelector output is always sorted —
    /// the result labels are in lexicographic order.
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = new[] { typeof(ImageMappingArbitraries) })]
    public void NormalizeSelector_OutputIsSorted(string[] labels)
    {
        var input = string.Join(",", labels);

        var normalized = DispatchService.NormalizeSelector(input);
        var outputLabels = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries);

        var sorted = outputLabels.OrderBy(l => l, StringComparer.Ordinal).ToArray();
        outputLabels.Should().BeEquivalentTo(sorted, opts => opts.WithStrictOrdering());
    }
}

/// <summary>
/// FsCheck arbitrary generators for image mapping property tests.
/// Generates non-empty arrays of distinct label strings from a realistic pool.
/// </summary>
public class ImageMappingArbitraries
{
    private static readonly string[] LabelPool =
    [
        "kiro", "opencode", "dotnet", "dotnet10", "python", "python312",
        "java", "java21", "linux", "windows", "arm64", "amd64",
        "gpu", "large", "small", "preview", "stable", "nightly"
    ];

    public static Arbitrary<string[]> LabelSetArb()
    {
        var labelGen = Gen.Elements(LabelPool);

        var labelSetGen = Gen.Choose(1, 6)
            .SelectMany(count => labelGen.ArrayOf(count))
            .Select(arr => arr.Distinct().ToArray())
            .Where(arr => arr.Length > 0);

        return labelSetGen.ToArbitrary();
    }
}
