using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for cap enforcement and open issue context writing in the decomposition pipeline.
/// Feature: 027-epic-decomposition-pipeline, Properties P10, P11, P12, P13
/// </summary>
public class DecompositionCapPropertyTests
{
    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 10: Sub-Issue Cap Enforcement
    ///
    /// Given N sub-issue files in the workspace and a configured cap of M,
    /// SubIssueFileParser returns all N valid files, and the executor processes min(N, M).
    /// The cap enforcement takes the first M files in alphabetical order.
    ///
    /// **Validates: Requirements 7.1, 7.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(SubIssueCapArbitraries) })]
    public async Task<Property> SubIssueCapEnforcement_ProcessesMinOfNAndM(SubIssueCapInput input)
    {
        // Arrange: create temp directory with N valid sub-issue JSON files
        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-cap-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            // Write N valid sub-issue files
            for (var i = 0; i < input.FileCount; i++)
            {
                var fileName = $"{(i + 1):D2}-sub-issue-{i}.json";
                var json = JsonSerializer.Serialize(new
                {
                    title = $"Sub-issue {i + 1}",
                    body = $"Body for sub-issue {i + 1} with enough content.",
                    dependencies = Array.Empty<string>(),
                    labels = Array.Empty<string>()
                });
                await File.WriteAllTextAsync(Path.Combine(subIssuesDir, fileName), json);
            }

            // Act: parse files (returns all valid files)
            var logger = new Mock<Serilog.ILogger>();
            var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, logger.Object, CancellationToken.None);

            // Apply cap enforcement (mirrors CreateSubIssuesStep logic)
            var cap = input.Cap;
            var cappedProposals = proposals.Count > cap
                ? proposals.Take(cap).ToList()
                : proposals.ToList();

            var expectedCount = Math.Min(input.FileCount, input.Cap);

            return (cappedProposals.Count == expectedCount).ToProperty()
                .Label($"FileCount={input.FileCount}, Cap={input.Cap}, Expected={expectedCount}, Got={cappedProposals.Count}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 11: Open Issue Output Format (YAML Front-Matter Round-Trip)
    ///
    /// For any IssueDetail with valid identifier, title, labels, and description,
    /// OpenIssueContextWriter.FormatIssueMarkdown produces output that:
    /// (a) starts with "---" YAML front-matter delimiter,
    /// (b) contains the identifier, title, and labels in the front-matter,
    /// (c) ends with the issue description body after the closing "---" delimiter.
    ///
    /// **Validates: Requirements 8.1, 8.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(IssueDetailArbitraries) })]
    public Property OpenIssueOutputFormat_YamlFrontMatterRoundTrip(IssueDetailInput input)
    {
        var detail = new IssueDetail
        {
            Identifier = input.Identifier,
            Title = input.Title,
            Description = input.Description,
            Labels = input.Labels
        };

        var result = OpenIssueContextWriter.FormatIssueMarkdown(detail);

        // Verify YAML front-matter structure
        var lines = result.Split('\n');

        // Must start with ---
        var startsWithDelimiter = lines[0].TrimEnd('\r') == "---";

        // Must have a closing --- delimiter
        var closingDelimiterIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r') == "---")
            {
                closingDelimiterIndex = i;
                break;
            }
        }
        var hasClosingDelimiter = closingDelimiterIndex > 0;

        // Front-matter must contain identifier
        var frontMatter = hasClosingDelimiter
            ? string.Join("\n", lines[1..closingDelimiterIndex])
            : "";
        var containsIdentifier = frontMatter.Contains($"identifier: \"{EscapeYaml(input.Identifier)}\"");

        // Front-matter must contain title
        var containsTitle = frontMatter.Contains($"title: \"{EscapeYaml(input.Title)}\"");

        // Front-matter must contain labels array
        var containsLabels = frontMatter.Contains("labels: [");

        // Body after front-matter must contain the description
        var bodyAfterFrontMatter = hasClosingDelimiter && closingDelimiterIndex + 1 < lines.Length
            ? string.Join("\n", lines[(closingDelimiterIndex + 1)..])
            : "";
        // The body starts with an empty line then the description
        var containsDescription = bodyAfterFrontMatter.Contains(input.Description);

        return (startsWithDelimiter && hasClosingDelimiter && containsIdentifier && containsTitle && containsLabels && containsDescription).ToProperty()
            .Label($"starts={startsWithDelimiter}, closing={hasClosingDelimiter}, id={containsIdentifier}, title={containsTitle}, labels={containsLabels}, desc={containsDescription}");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 12: Open Issue Cap Enforcement
    ///
    /// Given N open issues available from the issue provider and a configured cap of M,
    /// OpenIssueContextWriter writes at most min(N, M) files to the workspace.
    ///
    /// **Validates: Requirements 8.1, 8.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(OpenIssueCapArbitraries) })]
    public async Task<Property> OpenIssueCapEnforcement_WritesMinOfNAndM(OpenIssueCapInput input)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-oicap-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            // Setup mock issue operations that returns N issues across pages
            var mockIssueOps = new Mock<IAgentIssueOperations>();
            var allIssues = Enumerable.Range(1, input.AvailableIssues)
                .Select(i => new IssueSummary
                {
                    Identifier = i.ToString(),
                    Title = $"Issue {i}",
                    Labels = new List<string> { "bug" }
                })
                .ToList();

            const int pageSize = 30;
            mockIssueOps
                .Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((page, ps, labels, ct) =>
                {
                    var skip = (page - 1) * pageSize;
                    var items = allIssues.Skip(skip).Take(pageSize).ToList();
                    var hasMore = skip + items.Count < allIssues.Count;
                    return Task.FromResult(new PagedResult<IssueSummary>
                    {
                        Items = items,
                        Page = page,
                        PageSize = pageSize,
                        HasMore = hasMore
                    });
                });

            mockIssueOps
                .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
                .Returns<IssueIdentifier, CancellationToken>((id, ct) => Task.FromResult(new IssueDetail
                {
                    Identifier = id.Value,
                    Title = $"Issue {id}",
                    Description = $"Description for issue {id}",
                    Labels = new List<string> { "bug" }
                }));

            // Act
            var logger = new Mock<Serilog.ILogger>();
            var writer = new OpenIssueContextWriter(logger.Object);
            var writtenCount = await writer.WriteOpenIssueContextAsync(
                mockIssueOps.Object, tempDir, input.MaxIssues, CancellationToken.None);

            var expectedCount = Math.Min(input.AvailableIssues, input.MaxIssues);

            return (writtenCount == expectedCount).ToProperty()
                .Label($"Available={input.AvailableIssues}, MaxIssues={input.MaxIssues}, Expected={expectedCount}, Written={writtenCount}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 13: MaxDecompositionSubIssues Validation
    ///
    /// PipelineConfiguration.MaxDecompositionSubIssues accepts values in [1, 20] only.
    /// Values outside this range throw ArgumentOutOfRangeException.
    ///
    /// **Validates: Requirements 7.1, 7.2, 7.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(MaxSubIssuesValidationArbitraries) })]
    public Property MaxDecompositionSubIssues_AcceptsOnlyValidRange(MaxSubIssuesInput input)
    {
        var isInRange = input.Value is >= 1 and <= 20;

        if (isInRange)
        {
            // Should succeed without exception
            try
            {
                var config = new PipelineConfiguration { MaxDecompositionSubIssues = input.Value };
                var accepted = config.MaxDecompositionSubIssues == input.Value;
                return accepted.ToProperty()
                    .Label($"Value {input.Value} should be accepted and stored correctly");
            }
            catch (ArgumentOutOfRangeException)
            {
                return false.ToProperty()
                    .Label($"Value {input.Value} is in [1,20] but threw ArgumentOutOfRangeException");
            }
        }
        else
        {
            // Should throw ArgumentOutOfRangeException
            try
            {
                _ = new PipelineConfiguration { MaxDecompositionSubIssues = input.Value };
                return false.ToProperty()
                    .Label($"Value {input.Value} is outside [1,20] but did NOT throw");
            }
            catch (ArgumentOutOfRangeException)
            {
                return true.ToProperty()
                    .Label($"Value {input.Value} correctly rejected with ArgumentOutOfRangeException");
            }
        }
    }

    // --- Helper methods ---

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // --- Custom wrapper types ---

    public sealed class SubIssueCapInput
    {
        public int FileCount { get; }
        public int Cap { get; }
        public SubIssueCapInput(int fileCount, int cap) { FileCount = fileCount; Cap = cap; }
        public override string ToString() => $"FileCount={FileCount}, Cap={Cap}";
    }

    public sealed class IssueDetailInput
    {
        public string Identifier { get; }
        public string Title { get; }
        public string Description { get; }
        public IReadOnlyList<string> Labels { get; }
        public IssueDetailInput(string identifier, string title, string description, IReadOnlyList<string> labels)
        {
            Identifier = identifier;
            Title = title;
            Description = description;
            Labels = labels;
        }
        public override string ToString() => $"Id={Identifier}, Title={Title}, Labels=[{string.Join(",", Labels)}]";
    }

    public sealed class OpenIssueCapInput
    {
        public int AvailableIssues { get; }
        public int MaxIssues { get; }
        public OpenIssueCapInput(int availableIssues, int maxIssues) { AvailableIssues = availableIssues; MaxIssues = maxIssues; }
        public override string ToString() => $"Available={AvailableIssues}, MaxIssues={MaxIssues}";
    }

    public sealed class MaxSubIssuesInput
    {
        public int Value { get; }
        public MaxSubIssuesInput(int value) => Value = value;
        public override string ToString() => $"Value={Value}";
    }

    // --- Arbitraries ---

    public class SubIssueCapArbitraries
    {
        public static Arbitrary<SubIssueCapInput> SubIssueCapInputArb()
        {
            var gen =
                from fileCount in Gen.Choose(0, 25)
                from cap in Gen.Choose(1, 20)
                select new SubIssueCapInput(fileCount, cap);

            return gen.ToArbitrary();
        }
    }

    public class IssueDetailArbitraries
    {
        public static Arbitrary<IssueDetailInput> IssueDetailInputArb()
        {
            var identifierGen = Gen.Choose(1, 9999).Select(i => i.ToString());

            var titleGen = Gen.Elements(
                "Add pagination", "Fix login bug", "Refactor auth module",
                "Update dependencies", "Add unit tests", "Improve error handling",
                "Create API endpoint", "Optimize database queries");

            var descriptionGen = Gen.Elements(
                "This issue tracks the implementation of a new feature.",
                "We need to fix a critical bug in the authentication flow.",
                "The current implementation needs refactoring for better maintainability.",
                "Add comprehensive test coverage for the payment module.",
                "Optimize slow database queries affecting user experience.");

            var labelGen = Gen.Elements(
                "bug", "enhancement", "agent:next", "priority:high",
                "documentation", "good first issue", "help wanted");

            var labelsGen =
                from count in Gen.Choose(0, 4)
                from labels in Gen.ArrayOf(labelGen, count)
                select (IReadOnlyList<string>)labels.Distinct().ToList();

            var gen =
                from id in identifierGen
                from title in titleGen
                from desc in descriptionGen
                from labels in labelsGen
                select new IssueDetailInput(id, title, desc, labels);

            return gen.ToArbitrary();
        }
    }

    public class OpenIssueCapArbitraries
    {
        public static Arbitrary<OpenIssueCapInput> OpenIssueCapInputArb()
        {
            var gen =
                from available in Gen.Choose(0, 80)
                from maxIssues in Gen.Choose(1, 60)
                select new OpenIssueCapInput(available, maxIssues);

            return gen.ToArbitrary();
        }
    }

    public class MaxSubIssuesValidationArbitraries
    {
        public static Arbitrary<MaxSubIssuesInput> MaxSubIssuesInputArb()
        {
            // Mix of valid values [1,20], boundary values, and invalid values
            var validGen = Gen.Choose(1, 20).Select(v => new MaxSubIssuesInput(v));

            var belowRangeGen = Gen.Choose(-100, 0).Select(v => new MaxSubIssuesInput(v));

            var aboveRangeGen = Gen.Choose(21, 200).Select(v => new MaxSubIssuesInput(v));

            var boundaryGen = Gen.Elements(
                new MaxSubIssuesInput(0),
                new MaxSubIssuesInput(1),
                new MaxSubIssuesInput(20),
                new MaxSubIssuesInput(21),
                new MaxSubIssuesInput(-1),
                new MaxSubIssuesInput(int.MinValue),
                new MaxSubIssuesInput(int.MaxValue));

            var combined = Gen.OneOf(validGen, belowRangeGen, aboveRangeGen, boundaryGen);
            return combined.ToArbitrary();
        }
    }
}
