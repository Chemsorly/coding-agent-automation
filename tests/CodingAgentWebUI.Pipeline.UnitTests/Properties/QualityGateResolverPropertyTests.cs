using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for QualityGateResolver resolution correctness and QualityGateConfigValidator validation rules.
/// Feature: agent-configuration-management
/// </summary>
public class QualityGateResolverPropertyTests
{
    /// <summary>
    /// Property 5: QGC Resolution Correctness
    /// For any set of enabled QGCs and job labels, every resolved QGC must satisfy:
    /// (a) its MatchLabels is empty OR intersects with job labels, and
    /// (b) the list is ordered by ExecutionOrder ascending, then DisplayName alphabetically.
    /// **Validates: Requirements 4.1, 4.2, 4.3, 17.6, 17.7, 17.8**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(QualityGateResolverArbitraries) })]
    public void QgcResolution_ResolvedQgcsAreCorrectAndOrdered(QgcResolutionInput input)
    {
        var resolver = new QualityGateResolver();
        var result = resolver.Resolve(input.Configs, input.JobLabels);
        var jobLabelSet = new HashSet<string>(input.JobLabels, StringComparer.OrdinalIgnoreCase);

        // (a) Every resolved QGC must have empty MatchLabels OR intersect with job labels
        foreach (var qgc in result)
        {
            var matchLabelsEmpty = qgc.MatchLabels.Count == 0;
            var intersects = qgc.MatchLabels.Any(l => jobLabelSet.Contains(l));

            (matchLabelsEmpty || intersects).Should().BeTrue(
                $"QGC '{qgc.DisplayName}' (Id={qgc.Id}) must have empty MatchLabels or intersect with job labels");

            // Must be enabled
            qgc.Enabled.Should().BeTrue("only enabled QGCs should be resolved");
        }

        // (b) Ordered by ExecutionOrder ascending, then DisplayName alphabetically
        for (int i = 1; i < result.Count; i++)
        {
            var prev = result[i - 1];
            var curr = result[i];

            if (prev.ExecutionOrder > curr.ExecutionOrder)
            {
                Assert.Fail($"QGC at index {i - 1} (ExecutionOrder={prev.ExecutionOrder}) should come before index {i} (ExecutionOrder={curr.ExecutionOrder})");
            }

            if (prev.ExecutionOrder == curr.ExecutionOrder)
            {
                var cmp = string.Compare(prev.DisplayName, curr.DisplayName, StringComparison.OrdinalIgnoreCase);
                cmp.Should().BeLessThanOrEqualTo(0,
                    $"QGCs with same ExecutionOrder should be sorted by DisplayName: '{prev.DisplayName}' should come before or equal '{curr.DisplayName}'");
            }
        }
    }

    /// <summary>
    /// Property 4: QGC Validation Rejects No-Gate Configurations
    /// For any QGC where both CompilationCommand and TestCommand are null, validation must fail.
    /// **Validates: Requirements 3.6**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(QualityGateResolverArbitraries) })]
    public void QgcValidation_RejectsNoGateConfigurations(NoGateQgcInput input)
    {
        var result = QualityGateConfigValidator.Validate(input.Config);

        result.IsValid.Should().BeFalse("QGC with both CompilationCommand and TestCommand null must be rejected");
        result.ErrorMessage.Should().Contain("gate");
    }

    /// <summary>
    /// Property 14: QGC Resolution Negative
    /// For any QGC whose MatchLabels has zero intersection with job labels (and MatchLabels is non-empty),
    /// that QGC must NOT appear in the resolved list.
    /// **Validates: Requirements 4.1**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(QualityGateResolverArbitraries) })]
    public void QgcResolutionNegative_NonIntersectingExcluded(QgcResolutionInput input)
    {
        var resolver = new QualityGateResolver();
        var result = resolver.Resolve(input.Configs, input.JobLabels);
        var jobLabelSet = new HashSet<string>(input.JobLabels, StringComparer.OrdinalIgnoreCase);

        // For every enabled QGC with non-empty MatchLabels that has zero intersection with job labels,
        // it must NOT appear in the resolved list
        var nonIntersecting = input.Configs
            .Where(qgc => qgc.Enabled)
            .Where(qgc => qgc.MatchLabels.Count > 0)
            .Where(qgc => !qgc.MatchLabels.Any(l => jobLabelSet.Contains(l)))
            .ToList();

        foreach (var excluded in nonIntersecting)
        {
            result.Should().NotContain(excluded,
                $"QGC '{excluded.DisplayName}' (Id={excluded.Id}) has no label intersection with job labels and must be excluded");
        }
    }

    /// <summary>
    /// Property 15: Label Matching Case Insensitivity
    /// For any QGC MatchLabels and job labels that differ only in case, the QGC still matches.
    /// **Validates: Consistency with existing IsLabelMatch behavior (StringComparer.OrdinalIgnoreCase)**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(QualityGateResolverArbitraries) })]
    public void LabelMatchingCaseInsensitivity_QgcStillMatches(CaseInsensitiveQgcLabelInput input)
    {
        var qgc = new QualityGateConfiguration
        {
            Id = "case-test-qgc",
            DisplayName = "Case Test QGC",
            MatchLabels = input.OriginalLabels,
            CompilationCommand = "dotnet",
            CompilationArguments = new[] { "build" },
            Enabled = true,
            ExecutionOrder = 0
        };

        var resolver = new QualityGateResolver();

        // Job labels use the case variant — should still match
        var result = resolver.Resolve(new[] { qgc }, input.CaseVariantLabels);

        result.Should().NotBeEmpty("QGC should match when labels differ only in case");
        result.Should().Contain(qgc);
    }
}

// --- Wrapper types for FsCheck ---

/// <summary>Input for QGC resolution property tests.</summary>
public sealed class QgcResolutionInput
{
    public required IReadOnlyList<QualityGateConfiguration> Configs { get; init; }
    public required IReadOnlyList<string> JobLabels { get; init; }
    public override string ToString() => $"Configs={Configs.Count}, JobLabels=[{string.Join(",", JobLabels)}]";
}

/// <summary>Input for no-gate QGC validation property tests.</summary>
public sealed class NoGateQgcInput
{
    public required QualityGateConfiguration Config { get; init; }
    public override string ToString() => $"QGC Id={Config.Id}, DisplayName={Config.DisplayName}";
}

/// <summary>Input for case-insensitive QGC label matching property tests.</summary>
public sealed class CaseInsensitiveQgcLabelInput
{
    public required IReadOnlyList<string> OriginalLabels { get; init; }
    public required IReadOnlyList<string> CaseVariantLabels { get; init; }
    public override string ToString() => $"Original=[{string.Join(",", OriginalLabels)}], Variant=[{string.Join(",", CaseVariantLabels)}]";
}

// --- Arbitrary generators ---

public class QualityGateResolverArbitraries
{
    private static readonly string[] LabelPool = ["kiro", "dotnet", "python", "java", "node", "dotnet10", "python312", "java21"];
    private static readonly string[] QgcIds = ["qgc-1", "qgc-2", "qgc-3", "qgc-4", "qgc-5", "qgc-6", "qgc-7", "qgc-8"];
    private static readonly string[] DisplayNames = ["Build Gate", "Test Gate", "Coverage Gate", "Security Gate", "Lint Gate"];

    public static Arbitrary<QgcResolutionInput> QgcResolutionInputArb()
    {
        var labelSubsetGen = Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList());

        var qgcGen =
            from id in Gen.Elements(QgcIds)
            from displayName in Gen.Elements(DisplayNames)
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
            from count in Gen.Choose(1, 6)
            from configs in Gen.ArrayOf(qgcGen, count)
            from jobLabels in labelSubsetGen
            select new QgcResolutionInput
            {
                Configs = configs.ToList(),
                JobLabels = jobLabels
            };

        return inputGen.ToArbitrary();
    }

    public static Arbitrary<NoGateQgcInput> NoGateQgcInputArb()
    {
        var labelSubsetGen = Gen.SubListOf(LabelPool).Select(l => (IReadOnlyList<string>)l.ToList());

        var gen =
            from id in Gen.Elements(QgcIds)
            from displayName in Gen.Elements(DisplayNames)
            from matchLabels in labelSubsetGen
            from enabled in Gen.Elements(true, false)
            from executionOrder in Gen.Choose(0, 5)
            select new NoGateQgcInput
            {
                Config = new QualityGateConfiguration
                {
                    Id = id,
                    DisplayName = displayName,
                    MatchLabels = matchLabels,
                    CompilationCommand = null,
                    TestCommand = null,
                    Enabled = enabled,
                    ExecutionOrder = executionOrder
                }
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<CaseInsensitiveQgcLabelInput> CaseInsensitiveQgcLabelInputArb()
    {
        var gen =
            from count in Gen.Choose(1, 5)
            from labels in Gen.ArrayOf(Gen.Elements(LabelPool), count)
            let distinct = labels.Distinct().ToList()
            where distinct.Count > 0
            from upperFlags in Gen.ArrayOf(Gen.Elements(true, false), distinct.Count)
            select new CaseInsensitiveQgcLabelInput
            {
                OriginalLabels = distinct,
                CaseVariantLabels = distinct.Select((l, i) => i < upperFlags.Length && upperFlags[i] ? l.ToUpperInvariant() : l.ToLowerInvariant()).ToList()
            };

        return gen.ToArbitrary();
    }
}
