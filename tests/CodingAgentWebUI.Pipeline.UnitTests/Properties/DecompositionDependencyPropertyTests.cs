using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for dependency resolution correctness (P8) and
/// sub-issue file schema validation (P9).
/// Feature: 027-epic-decomposition-pipeline, Properties P8, P9
/// </summary>
public class DecompositionDependencyPropertyTests
{
    private static readonly ILogger s_logger = new Mock<ILogger>().Object;

    #region P8: Dependency Resolution Correctness

    /// <summary>
    /// Property 8: Dependency Resolution — Case-insensitive matching.
    /// For any registered title and a dependency reference with different casing,
    /// the resolver SHALL produce a "Depends on #N" line.
    /// **Validates: Requirements 9.2, 9.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DependencyResolution_CaseInsensitiveMatching(NonEmptyString titleRaw, PositiveInt issueNum)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var issueNumber = issueNum.Get.ToString();
        var resolver = new DependencyResolver();
        resolver.Register(title, issueNumber);

        // Resolve with different casing
        var upperTitle = title.ToUpperInvariant();
        var lowerTitle = title.ToLowerInvariant();

        var resultUpper = resolver.Resolve([upperTitle], s_logger);
        var resultLower = resolver.Resolve([lowerTitle], s_logger);

        resultUpper.Should().HaveCount(1);
        resultUpper[0].Should().Be($"Depends on #{issueNumber}");

        resultLower.Should().HaveCount(1);
        resultLower[0].Should().Be($"Depends on #{issueNumber}");
    }

    /// <summary>
    /// Property 8: Dependency Resolution — Whitespace trimming.
    /// For any registered title, resolving with leading/trailing whitespace
    /// SHALL still match correctly.
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DependencyResolution_WhitespaceTrimming(NonEmptyString titleRaw, PositiveInt issueNum)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var issueNumber = issueNum.Get.ToString();
        var resolver = new DependencyResolver();
        resolver.Register(title, issueNumber);

        // Resolve with extra whitespace
        var paddedTitle = $"   {title}   ";
        var result = resolver.Resolve([paddedTitle], s_logger);

        result.Should().HaveCount(1);
        result[0].Should().Be($"Depends on #{issueNumber}");
    }

    /// <summary>
    /// Property 8: Dependency Resolution — First-registered wins for duplicates.
    /// When two issues are registered with the same normalized title (case-insensitive),
    /// the resolver SHALL use the first-created issue's number.
    /// **Validates: Requirements 9.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DependencyResolution_FirstRegisteredWins(
        NonEmptyString titleRaw, PositiveInt firstNum, PositiveInt secondNum)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var firstNumber = firstNum.Get.ToString();
        var secondNumber = (secondNum.Get + 1000).ToString(); // Ensure different number

        var resolver = new DependencyResolver();
        resolver.Register(title, firstNumber);
        resolver.Register(title.ToUpperInvariant(), secondNumber); // Same title, different case

        var result = resolver.Resolve([title], s_logger);

        result.Should().HaveCount(1);
        result[0].Should().Be($"Depends on #{firstNumber}");
    }

    /// <summary>
    /// Property 8: Dependency Resolution — Unresolved titles are omitted.
    /// For any dependency title that does not match any registered title,
    /// the resolver SHALL omit it from the result (no "Depends on" line produced).
    /// **Validates: Requirements 9.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DependencyResolution_UnresolvedTitlesOmitted(
        NonEmptyString registeredTitleRaw, NonEmptyString unresolvedTitleRaw, PositiveInt issueNum)
    {
        var registeredTitle = registeredTitleRaw.Get.Replace("\0", "").Trim();
        var unresolvedTitle = unresolvedTitleRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(registeredTitle) || string.IsNullOrWhiteSpace(unresolvedTitle)) return;

        // Ensure the unresolved title is actually different from the registered one
        if (string.Equals(registeredTitle.Trim(), unresolvedTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        var resolver = new DependencyResolver();
        resolver.Register(registeredTitle, issueNum.Get.ToString());

        var result = resolver.Resolve([unresolvedTitle], s_logger);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Property 8: Dependency Resolution — Mixed resolved and unresolved.
    /// For any sequence of dependencies where some match registered titles and some don't,
    /// the resolver SHALL produce "Depends on #N" lines only for resolved ones.
    /// **Validates: Requirements 9.2, 9.3, 9.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DependencyResolution_MixedResolvedAndUnresolved(PositiveInt countRaw)
    {
        var count = Math.Min(countRaw.Get, 10);
        var resolver = new DependencyResolver();

        // Register some titles
        var registeredTitles = new List<(string Title, string Number)>();
        for (int i = 0; i < count; i++)
        {
            var title = $"Issue Title {i}";
            var number = (i + 100).ToString();
            resolver.Register(title, number);
            registeredTitles.Add((title, number));
        }

        // Build dependency list: mix of registered and unregistered
        var dependencies = new List<string>();
        var expectedResults = new List<string>();

        for (int i = 0; i < count; i++)
        {
            dependencies.Add(registeredTitles[i].Title);
            expectedResults.Add($"Depends on #{registeredTitles[i].Number}");
        }

        // Add unresolved titles
        dependencies.Add("Nonexistent Title A");
        dependencies.Add("Nonexistent Title B");

        var result = resolver.Resolve(dependencies, s_logger);

        // Only resolved dependencies should appear
        result.Should().HaveCount(expectedResults.Count);
        for (int i = 0; i < expectedResults.Count; i++)
        {
            result[i].Should().Be(expectedResults[i]);
        }
    }

    /// <summary>
    /// Property 8: Dependency Resolution — Empty dependencies produce empty result.
    /// **Validates: Requirements 9.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DependencyResolution_EmptyDependencies_ProducesEmptyResult(PositiveInt issueNum)
    {
        var resolver = new DependencyResolver();
        resolver.Register("Some Title", issueNum.Get.ToString());

        var result = resolver.Resolve([], s_logger);

        result.Should().BeEmpty();
    }

    #endregion

    #region P9: Sub-Issue File Schema Validation

    /// <summary>
    /// Property 9: Schema Validation — Accepts valid JSON with all required fields.
    /// For any valid sub-issue JSON with non-empty title, non-empty body,
    /// dependencies array, and labels array, the parser SHALL accept it.
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_AcceptsValidJson(
        NonEmptyString titleRaw, NonEmptyString bodyRaw, byte depCount, byte labelCount)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        var body = bodyRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return;

        var dependencies = Enumerable.Range(0, Math.Min(depCount, (byte)5))
            .Select(i => $"Dep Title {i}")
            .ToList();

        var labels = Enumerable.Range(0, Math.Min(labelCount, (byte)5))
            .Select(i => $"label-{i}")
            .ToList();

        var json = JsonSerializer.Serialize(new
        {
            title,
            body,
            dependencies,
            labels
        });

        // Write to temp directory and parse
        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            var filePath = Path.Combine(subIssuesDir, "01-test-issue.json");
            await File.WriteAllTextAsync(filePath, json);

            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);

            result.Should().HaveCount(1);
            result[0].Title.Should().Be(title);
            result[0].Body.Should().Be(body);
            result[0].Dependencies.Should().HaveCount(dependencies.Count);
            result[0].Labels.Should().HaveCount(labels.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects JSON missing 'title' field.
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsMissingTitle(NonEmptyString bodyRaw)
    {
        var body = bodyRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        var json = JsonSerializer.Serialize(new
        {
            body,
            dependencies = new string[] { },
            labels = new string[] { }
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects JSON missing 'body' field.
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsMissingBody(NonEmptyString titleRaw)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var json = JsonSerializer.Serialize(new
        {
            title,
            dependencies = new string[] { },
            labels = new string[] { }
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects JSON missing 'dependencies' field.
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsMissingDependencies(NonEmptyString titleRaw, NonEmptyString bodyRaw)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        var body = bodyRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return;

        var json = JsonSerializer.Serialize(new
        {
            title,
            body,
            labels = new string[] { }
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects JSON missing 'labels' field.
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsMissingLabels(NonEmptyString titleRaw, NonEmptyString bodyRaw)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        var body = bodyRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return;

        var json = JsonSerializer.Serialize(new
        {
            title,
            body,
            dependencies = new string[] { }
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects empty title.
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsEmptyTitle(NonEmptyString bodyRaw)
    {
        var body = bodyRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        var json = JsonSerializer.Serialize(new
        {
            title = "",
            body,
            dependencies = new string[] { },
            labels = new string[] { }
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects empty body.
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsEmptyBody(NonEmptyString titleRaw)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var json = JsonSerializer.Serialize(new
        {
            title,
            body = "",
            dependencies = new string[] { },
            labels = new string[] { }
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects invalid JSON (not parseable).
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsInvalidJson(NonEmptyString garbageRaw)
    {
        // Ensure the content is not valid JSON by prepending invalid chars
        var garbage = "{{{" + garbageRaw.Get + "}}}invalid";

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), garbage);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects wrong type for 'dependencies' (not array).
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsWrongTypeDependencies(NonEmptyString titleRaw, NonEmptyString bodyRaw)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        var body = bodyRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return;

        // dependencies as a string instead of array
        var json = $@"{{""title"":""{EscapeJson(title)}"",""body"":""{EscapeJson(body)}"",""dependencies"":""not-an-array"",""labels"":[]}}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 9: Schema Validation — Rejects wrong type for 'labels' (not array).
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SchemaValidation_RejectsWrongTypeLabels(NonEmptyString titleRaw, NonEmptyString bodyRaw)
    {
        var title = titleRaw.Get.Replace("\0", "").Trim();
        var body = bodyRaw.Get.Replace("\0", "").Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) return;

        // labels as a number instead of array
        var json = $@"{{""title"":""{EscapeJson(title)}"",""body"":""{EscapeJson(body)}"",""dependencies"":[],""labels"":42}}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-schema-{Guid.NewGuid():N}");
        var subIssuesDir = Path.Combine(tempDir, ".agent", "sub-issues");
        Directory.CreateDirectory(subIssuesDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(subIssuesDir, "01-test.json"), json);
            var result = await SubIssueFileParser.ParseSubIssueFilesAsync(tempDir, s_logger, CancellationToken.None);
            result.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region Helpers

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    #endregion
}
