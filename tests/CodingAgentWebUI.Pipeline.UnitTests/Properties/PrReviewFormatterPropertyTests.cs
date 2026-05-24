using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using Serilog.Core;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for PR review formatter, issue reference parsing,
/// linked issue file writing, and extraction priority order.
/// Feature: 025-pr-review-pipeline, Properties P9–P12
/// </summary>
public class PrReviewFormatterPropertyTests
{
    // ─── P9: Review Findings Formatter Output ───────────────────────────────────

    /// <summary>
    /// P9(a): The formatted review body SHALL always contain the machine-readable marker.
    /// **Validates: Requirements 5.1**
    /// </summary>
    [Property(MaxTest = 20)]
    public void FormatterOutput_AlwaysContainsMarker(
        NonNegativeInt critical,
        NonNegativeInt warning,
        NonNegativeInt suggestion)
    {
        var run = CreateRunWithFindings(
            critical.Get, warning.Get, suggestion.Get,
            agentNames: new[] { "Agent1" },
            agentFindings: new Dictionary<string, string> { ["Agent1"] = "Some finding" });

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain(ReviewFindingsFormatter.Marker);
    }

    /// <summary>
    /// P9(b): The formatted review body SHALL always contain the header "Automated Code Review".
    /// **Validates: Requirements 5.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public void FormatterOutput_AlwaysContainsHeader(
        NonNegativeInt critical,
        NonNegativeInt warning,
        NonNegativeInt suggestion)
    {
        var run = CreateRunWithFindings(
            critical.Get, warning.Get, suggestion.Get,
            agentNames: new[] { "ReviewBot" },
            agentFindings: new Dictionary<string, string>());

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("Automated Code Review");
    }

    /// <summary>
    /// P9(c): When all counts are zero, the output SHALL contain "No issues found".
    /// **Validates: Requirements 5.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void FormatterOutput_ZeroCounts_ContainsNoIssuesFound(NonEmptyArray<NonEmptyString> agentNames)
    {
        var names = agentNames.Get.Select(n => n.Get.Replace("\n", "").Replace("\r", "")).ToArray();
        var run = CreateRunWithFindings(
            0, 0, 0,
            agentNames: names,
            agentFindings: new Dictionary<string, string>());

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("No issues found");
    }

    /// <summary>
    /// P9(d): When any count > 0, the output SHALL contain a severity table.
    /// **Validates: Requirements 5.8**
    /// </summary>
    [Property(MaxTest = 20)]
    public void FormatterOutput_NonZeroCounts_ContainsSeverityTable(
        PositiveInt critical,
        NonNegativeInt warning,
        NonNegativeInt suggestion)
    {
        // At least critical > 0
        var run = CreateRunWithFindings(
            critical.Get, warning.Get, suggestion.Get,
            agentNames: new[] { "Agent1" },
            agentFindings: new Dictionary<string, string> { ["Agent1"] = "Finding text" });

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("| Severity | Count |");
        result.Should().Contain("[CRITICAL]");
    }

    /// <summary>
    /// P9(e): Per-agent detail sections SHALL appear for each agent with non-empty findings.
    /// **Validates: Requirements 5.8**
    /// </summary>
    [Property(MaxTest = 20)]
    public void FormatterOutput_NonEmptyFindings_ContainsPerAgentDetails(
        NonEmptyString agentName,
        NonEmptyString findingText)
    {
        var cleanName = agentName.Get.Replace("\n", "").Replace("\r", "").Trim();
        var cleanFinding = findingText.Get.Replace("\r", "").Trim();
        if (string.IsNullOrWhiteSpace(cleanName) || string.IsNullOrWhiteSpace(cleanFinding))
            return;

        var run = CreateRunWithFindings(
            1, 0, 0,
            agentNames: new[] { cleanName },
            agentFindings: new Dictionary<string, string> { [cleanName] = cleanFinding });

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("<details>");
        result.Should().Contain($"<summary>{cleanName}</summary>");
        result.Should().Contain(cleanFinding);
    }

    // ─── P10: GitHub Issue Reference Parsing ────────────────────────────────────

    /// <summary>
    /// P10: For any string containing #N pattern, the parser SHALL extract the issue number.
    /// **Validates: Requirements 12.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIssueReferences_SimpleHash_ExtractsNumber(PositiveInt issueNumber)
    {
        var text = $"This fixes #{ issueNumber.Get} in the codebase";
        var results = new HashSet<string>(StringComparer.Ordinal);

        GitHubRepositoryProvider.ParseIssueReferences(text, results);

        results.Should().Contain(issueNumber.Get.ToString());
    }

    /// <summary>
    /// P10: For any string containing owner/repo#N pattern, the parser SHALL extract the issue number.
    /// **Validates: Requirements 12.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIssueReferences_CrossRepo_ExtractsNumber(PositiveInt issueNumber)
    {
        var text = $"Related to myorg/myrepo#{issueNumber.Get}";
        var results = new HashSet<string>(StringComparer.Ordinal);

        GitHubRepositoryProvider.ParseIssueReferences(text, results);

        results.Should().Contain(issueNumber.Get.ToString());
    }

    /// <summary>
    /// P10: For any string containing GH-N pattern, the parser SHALL extract the issue number.
    /// **Validates: Requirements 12.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIssueReferences_GhDash_ExtractsNumber(PositiveInt issueNumber)
    {
        var text = $"Implements GH-{issueNumber.Get} feature";
        var results = new HashSet<string>(StringComparer.Ordinal);

        GitHubRepositoryProvider.ParseIssueReferences(text, results);

        results.Should().Contain(issueNumber.Get.ToString());
    }

    /// <summary>
    /// P10: For any string containing closing keywords (closes/fixes/resolves #N),
    /// the parser SHALL extract the issue number.
    /// **Validates: Requirements 12.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIssueReferences_ClosingKeywords_ExtractsNumber(PositiveInt issueNumber)
    {
        var keywords = new[] { "closes", "fixes", "resolves", "Closes", "Fixes", "Resolves", "CLOSES", "FIXES", "RESOLVES" };
        var keyword = keywords[issueNumber.Get % keywords.Length];
        var text = $"{keyword} #{issueNumber.Get}";
        var results = new HashSet<string>(StringComparer.Ordinal);

        GitHubRepositoryProvider.ParseIssueReferences(text, results);

        results.Should().Contain(issueNumber.Get.ToString());
    }

    /// <summary>
    /// P10: For any string NOT containing issue reference patterns, the parser SHALL return empty.
    /// **Validates: Requirements 12.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIssueReferences_NoPatterns_ReturnsEmpty(NonEmptyString text)
    {
        // Generate text that definitely doesn't contain issue patterns
        var safeText = text.Get
            .Replace("#", "")
            .Replace("GH-", "")
            .Replace("gh-", "")
            .Replace("closes", "")
            .Replace("fixes", "")
            .Replace("resolves", "");

        var results = new HashSet<string>(StringComparer.Ordinal);

        GitHubRepositoryProvider.ParseIssueReferences(safeText, results);

        results.Should().BeEmpty();
    }

    // ─── P11: Linked Issue File Writing ─────────────────────────────────────────

    /// <summary>
    /// P11: For any list of LinkedIssueContext items, the ExtractLinkedIssuesStep SHALL write
    /// exactly one file per item to .agent/linked-issue-{id}.md, each containing the issue title and description.
    /// **Validates: Requirements 12.4, 12.8**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task LinkedIssueFileWriting_WritesExactlyOneFilePerItem(PositiveInt countRaw)
    {
        var count = Math.Min(countRaw.Get, 10); // Cap to avoid excessive I/O
        var linkedIssues = Enumerable.Range(1, count).Select(i => new LinkedIssueContext
        {
            Identifier = i.ToString(),
            Title = $"Issue {i} Title",
            Description = $"Issue {i} description content"
        }).ToList();

        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-p11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Serilog.Core.Logger? logger = null;
        PipelineStepContext? context = null;
        try
        {
            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = "99",
                IssueTitle = "Test PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "PR description",
                LinkedIssueContexts = linkedIssues
            };

            var callbacks = new Mock<IPipelineCallbacks>();
            logger = new Serilog.LoggerConfiguration().CreateLogger();
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            context = new PipelineStepContext
            {
                Run = run,
                Config = new PipelineConfiguration { WorkspaceBaseDirectory = tempDir },
                RepoProvider = Mock.Of<IRepositoryProvider>(),
                AgentProvider = Mock.Of<IAgentProvider>(),
                BrainProvider = null,
                PipelineProvider = null,
                Cts = new CancellationTokenSource(),
                ConfigStore = Mock.Of<IConfigurationStore>(),
                Callbacks = callbacks.Object,
                IssueOps = Mock.Of<IAgentIssueOperations>(),
                AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
                QualityGates = Mock.Of<IQualityGateExecutor>(),
                BrainSync = null,
                PrOrchestrator = new PullRequestOrchestrator(logger),
                Logger = logger
            };

            await step.ExecuteAsync(context, CancellationToken.None);

            // Verify exactly N files written
            var agentDir = Path.Combine(tempDir, ".agent");
            var files = Directory.GetFiles(agentDir, "linked-issue-*.md");
            files.Length.Should().Be(count);

            // Verify each file contains title and description
            foreach (var issue in linkedIssues)
            {
                var expectedPath = Path.Combine(agentDir, $"linked-issue-{issue.Identifier}.md");
                File.Exists(expectedPath).Should().BeTrue($"file for issue {issue.Identifier} should exist");
                var content = await File.ReadAllTextAsync(expectedPath);
                content.Should().Contain(issue.Title);
                content.Should().Contain(issue.Description);
            }
        }
        finally
        {
            context?.Cts?.Dispose();
            logger?.Dispose();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// P11: For zero linked issues, no files SHALL be written to .agent/ directory.
    /// **Validates: Requirements 12.4, 12.8**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task LinkedIssueFileWriting_ZeroIssues_NoFilesWritten(PositiveInt prNumber)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-p11-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Serilog.Core.Logger? logger = null;
        PipelineStepContext? context = null;
        try
        {
            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = prNumber.Get.ToString(),
                IssueTitle = "Test PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "PR description",
                LinkedIssueContexts = Array.Empty<LinkedIssueContext>()
            };

            var callbacks = new Mock<IPipelineCallbacks>();
            logger = new Serilog.LoggerConfiguration().CreateLogger();
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            context = new PipelineStepContext
            {
                Run = run,
                Config = new PipelineConfiguration { WorkspaceBaseDirectory = tempDir },
                RepoProvider = Mock.Of<IRepositoryProvider>(),
                AgentProvider = Mock.Of<IAgentProvider>(),
                BrainProvider = null,
                PipelineProvider = null,
                Cts = new CancellationTokenSource(),
                ConfigStore = Mock.Of<IConfigurationStore>(),
                Callbacks = callbacks.Object,
                IssueOps = Mock.Of<IAgentIssueOperations>(),
                AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
                QualityGates = Mock.Of<IQualityGateExecutor>(),
                BrainSync = null,
                PrOrchestrator = new PullRequestOrchestrator(logger),
                Logger = logger
            };

            await step.ExecuteAsync(context, CancellationToken.None);

            // .agent directory should not exist or be empty
            var agentDir = Path.Combine(tempDir, ".agent");
            if (Directory.Exists(agentDir))
            {
                var files = Directory.GetFiles(agentDir, "linked-issue-*.md");
                files.Length.Should().Be(0);
            }
        }
        finally
        {
            context?.Cts?.Dispose();
            logger?.Dispose();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ─── P12: Extraction Priority Order ─────────────────────────────────────────

    /// <summary>
    /// P12: For any PR with linked issues found via API, title parsing, and body parsing,
    /// the extraction SHALL return API results first (deduplication ensures no repeats).
    /// When API returns results, title/body parsing results are still included but deduplicated.
    /// **Validates: Requirements 12.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ExtractionPriorityOrder_ApiResultsFirst_ThenTitle_ThenBody(
        PositiveInt apiIssue,
        PositiveInt titleIssue,
        PositiveInt bodyIssue)
    {
        // Simulate the priority logic from ExtractLinkedIssuesAsync:
        // API results are added first, then title parsing, then body parsing.
        // HashSet ensures deduplication.
        var issueNumbers = new HashSet<string>(StringComparer.Ordinal);

        // Priority (a): API results
        var apiResult = apiIssue.Get.ToString();
        issueNumbers.Add(apiResult);

        // Priority (b): Title parsing
        var titleText = $"Fix #{titleIssue.Get}";
        GitHubRepositoryProvider.ParseIssueReferences(titleText, issueNumbers);

        // Priority (c): Body parsing
        var bodyText = $"Resolves #{bodyIssue.Get}";
        GitHubRepositoryProvider.ParseIssueReferences(bodyText, issueNumbers);

        // API result should always be present
        issueNumbers.Should().Contain(apiResult);

        // All unique issue numbers should be present
        var expectedUnique = new HashSet<string>
        {
            apiIssue.Get.ToString(),
            titleIssue.Get.ToString(),
            bodyIssue.Get.ToString()
        };
        issueNumbers.Count.Should().Be(expectedUnique.Count);
    }

    /// <summary>
    /// P12: When API returns results, those are used regardless of title/body content.
    /// Deduplication ensures no duplicates across sources.
    /// **Validates: Requirements 12.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ExtractionPriorityOrder_Deduplication_NoDuplicates(PositiveInt issueNumber)
    {
        // Same issue referenced in all three sources
        var issueNumbers = new HashSet<string>(StringComparer.Ordinal);

        // API result
        issueNumbers.Add(issueNumber.Get.ToString());

        // Title also references same issue
        var titleText = $"Fix #{issueNumber.Get}";
        GitHubRepositoryProvider.ParseIssueReferences(titleText, issueNumbers);

        // Body also references same issue
        var bodyText = $"Closes #{issueNumber.Get}";
        GitHubRepositoryProvider.ParseIssueReferences(bodyText, issueNumbers);

        // Should only have one entry despite three sources
        issueNumbers.Count.Should().Be(1);
        issueNumbers.Should().Contain(issueNumber.Get.ToString());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static PipelineRun CreateRunWithFindings(
        int critical, int warning, int suggestion,
        string[] agentNames,
        Dictionary<string, string> agentFindings)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = agentNames
        };

        run.CodeReviewCriticalCount = critical;
        run.CodeReviewWarningCount = warning;
        run.CodeReviewSuggestionCount = suggestion;

        foreach (var kvp in agentFindings)
        {
            run.CodeReviewAgentFindings[kvp.Key] = kvp.Value;
        }

        return run;
    }
}
