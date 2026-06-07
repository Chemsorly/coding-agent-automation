// Feature: 021-consolidation-loops
// Property 3: Template Filtering by Provider Configuration
// Property 4: Last-Run Timestamp Isolation
// Property 5: Concurrency Guard Rejects Duplicate Running
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for ConsolidationService and ConsolidationTemplateFilter.
/// **Validates: Requirements 2.3, 2.4, 3.7**
/// </summary>
public class ConsolidationServicePropertyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"consol-prop-{Guid.NewGuid():N}");

    public ConsolidationServicePropertyTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Property 3: Template Filtering by Provider Configuration
    /// For any set of PipelineJobTemplate instances with varying provider configurations,
    /// filtering includes a template for brain consolidation only if BrainProviderId is non-null/non-empty,
    /// and for refactoring only if both RepoProviderId and IssueProviderId are non-null/non-empty.
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(TemplateFilterArbitraries) })]
    public void TemplateFilter_BrainConsolidation_OnlyIncludesTemplatesWithBrainProvider(
        List<PipelineJobTemplate> templates)
    {
        var result = ConsolidationTemplateFilter.FilterByType(templates, ConsolidationRunType.BrainConsolidation);

        foreach (var t in result)
            t.BrainProviderId.Should().NotBeNullOrWhiteSpace(
                $"template '{t.Name}' was included for brain consolidation but has no BrainProviderId");

        var expected = templates.Where(t => !string.IsNullOrWhiteSpace(t.BrainProviderId)).ToList();
        result.Should().HaveCount(expected.Count);
    }

    /// <summary>
    /// Property 3 (continued): Refactoring detection requires both RepoProviderId and IssueProviderId.
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(TemplateFilterArbitraries) })]
    public void TemplateFilter_RefactoringDetection_OnlyIncludesTemplatesWithRepoAndIssueProvider(
        List<PipelineJobTemplate> templates)
    {
        var result = ConsolidationTemplateFilter.FilterByType(templates, ConsolidationRunType.RefactoringDetection);

        foreach (var t in result)
        {
            t.RepoProviderId.Should().NotBeNullOrWhiteSpace(
                $"template '{t.Name}' was included for refactoring but has no RepoProviderId");
            t.IssueProviderId.Should().NotBeNullOrWhiteSpace(
                $"template '{t.Name}' was included for refactoring but has no IssueProviderId");
        }

        var expected = templates.Where(t =>
            !string.IsNullOrWhiteSpace(t.RepoProviderId) &&
            !string.IsNullOrWhiteSpace(t.IssueProviderId)).ToList();
        result.Should().HaveCount(expected.Count);
    }

    /// <summary>
    /// Property 4: Last-Run Timestamp Isolation
    /// For any sequence of consolidation runs across multiple templates and types,
    /// GetLastRunAsync(type, templateId) returns only the most recent run matching that exact pair.
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void GetLastRunAsync_ReturnsOnlyMostRecentMatchingPair(PositiveInt runCount)
    {
        var runsDir = Path.Combine(_tempDir, $"runs-{Guid.NewGuid():N}");
        var config = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir };
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistory()).Returns([]);

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());

        var sut = new ConsolidationService(
            Serilog.Log.Logger, config, mockProjectStore.Object, mockHistory.Object,
            consolidationRunsDirectory: runsDir);

        var count = Math.Min(runCount.Get, 5);
        var types = new[] { ConsolidationRunType.BrainConsolidation, ConsolidationRunType.RefactoringDetection };
        var templateIds = new[] { "tmpl-A", "tmpl-B" };

        var runs = new List<ConsolidationRun>();
        for (var i = 0; i < count; i++)
        {
            var type = types[i % types.Length];
            var templateId = templateIds[i % templateIds.Length];
            var run = sut.TriggerAsync(type, templateId, CancellationToken.None).GetAwaiter().GetResult();
            if (run is not null)
                runs.Add(run);
        }

        foreach (var type in types)
        {
            foreach (var templateId in templateIds)
            {
                var lastRun = sut.GetLastRunAsync(type, templateId, CancellationToken.None).GetAwaiter().GetResult();
                var matchingRuns = runs.Where(r => r.Type == type && r.TemplateId == templateId).ToList();

                if (matchingRuns.Count == 0)
                {
                    lastRun.Should().BeNull();
                }
                else
                {
                    lastRun.Should().NotBeNull();
                    lastRun!.Type.Should().Be(type);
                    lastRun.TemplateId.Should().Be(templateId);
                    lastRun.StartedAtUtc.Should().Be(matchingRuns.Max(r => r.StartedAtUtc));
                }
            }
        }
    }

    /// <summary>
    /// Property 5: Concurrency Guard Rejects Duplicate Running
    /// For any type+templateId where a run with Status=Running exists,
    /// TriggerAsync returns null; different type or templateId is not rejected.
    /// **Validates: Requirements 3.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ConcurrencyGuard_RejectsDuplicateRunning_AllowsDifferentPair(bool useSameType)
    {
        var runsDir = Path.Combine(_tempDir, $"guard-{Guid.NewGuid():N}");
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempDir,
            PipelineJobTemplates = new List<PipelineJobTemplate>
            {
                new() { Id = "tmpl-1", Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp", BrainProviderId = "bp" },
                new() { Id = "tmpl-2", Name = "T2", IssueProviderId = "ip", RepoProviderId = "rp", BrainProviderId = "bp" }
            }
        };
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistory()).Returns([]);

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { "tmpl-1", "tmpl-2" }
                }
            });

        var sut = new ConsolidationService(
            Serilog.Log.Logger, config, mockProjectStore.Object, mockHistory.Object,
            consolidationRunsDirectory: runsDir);

        // First trigger succeeds
        var first = sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None)
            .GetAwaiter().GetResult();
        first.Should().NotBeNull();

        // Same type+template should be rejected
        var duplicate = sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None)
            .GetAwaiter().GetResult();
        duplicate.Should().BeNull("same type+templateId is already running");

        // Different pair should succeed
        var differentType = useSameType
            ? ConsolidationRunType.BrainConsolidation
            : ConsolidationRunType.RefactoringDetection;
        var differentTemplate = useSameType ? "tmpl-2" : "tmpl-1";

        var other = sut.TriggerAsync(differentType, differentTemplate, CancellationToken.None)
            .GetAwaiter().GetResult();
        other.Should().NotBeNull($"different pair ({differentType}, {differentTemplate}) should not be blocked");
    }
}

/// <summary>
/// FsCheck arbitrary generators for template filtering property tests.
/// </summary>
public class TemplateFilterArbitraries
{
    private static readonly string[] NamePool = ["DotNet", "Python", "Java", "Go", "Rust"];
    private static readonly string[] IdPool = ["id-1", "id-2", "id-3", "id-4", "id-5"];
    private static readonly string[] ProviderPool = ["repo-1", "repo-2", "issue-1", "brain-1"];

    public static Arbitrary<List<PipelineJobTemplate>> ListArb()
    {
        var templateGen =
            from id in Gen.Elements(IdPool)
            from name in Gen.Elements(NamePool)
            from hasRepo in Gen.Elements(true, false)
            from hasIssue in Gen.Elements(true, false)
            from hasBrain in Gen.Elements(true, false)
            from provider in Gen.Elements(ProviderPool)
            select new PipelineJobTemplate
            {
                Id = id + Guid.NewGuid().ToString()[..4],
                Name = name,
                RepoProviderId = hasRepo ? provider : "",
                IssueProviderId = hasIssue ? provider : "",
                BrainProviderId = hasBrain ? provider : null
            };

        return Gen.Choose(0, 5)
            .SelectMany(count => Gen.ArrayOf(templateGen, count))
            .Select(arr => arr.ToList())
            .ToArbitrary();
    }
}
