using System.Globalization;
using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="RefactoringExecutor"/>.
/// Tests: creates no issues when agent finds nothing, handles malformed JSON.
/// </summary>
public class RefactoringExecutorTests : IDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IIssueProvider> _mockIssueProvider = new();
    private readonly Mock<IAgentProvider> _mockAgentProvider = new();
    private readonly string _tempDir;

    public RefactoringExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"refactoring-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        // Default: return empty issue lists so the issue-context query path succeeds
        var emptyResult = new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 50, HasMore = false };
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private RefactoringExecutor CreateExecutor() => new(_mockLogger.Object);

    private ConsolidationJobMessage CreateJob(string? jobId = null)
    {
        var id = jobId ?? Guid.NewGuid().ToString();
        return new ConsolidationJobMessage
        {
            JobId = id,
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            TemplateName = "Test Template",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir }
        };
    }

    [Fact]
    public async Task ExecuteAsync_AgentFindsNothing_CreatesNoIssues()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        // Setup clone to create the workspace directory structure
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Agent succeeds but produces no proposals file
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["No refactoring opportunities found."]
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");

        // Should never call CreateIssueAsync
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedJson_ReturnsFailedResult()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        // Setup clone to create workspace and write malformed JSON
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                // Create the .agent directory and write malformed JSON
                var agentDir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(Path.Combine(agentDir, "refactoring-proposals.json"), "{ invalid json [[[");
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Analysis complete."]
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("parse");

        // Should never call CreateIssueAsync when JSON is malformed
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ValidProposals_CreatesIssues()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Service.cs", "src/Handler.cs"],
                    "description": "Both files duplicate input validation.",
                    "rationale": "DRY principle violation."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Found 1 refactoring opportunity."]
            });

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "42", Url = "https://github.com/test/repo/issues/42" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(1);
        result.Summary.Should().Contain("1");
        result.Summary.Should().Contain("42");

        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(
                It.Is<string>(t => t.Contains("Extract shared validation logic")),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void FormatRefactoringSummary_NoIssues_ReturnsNoOpportunities()
    {
        // Act
        var summary = RefactoringExecutor.FormatRefactoringSummary([]);

        // Assert
        summary.Should().Contain("No refactoring opportunities identified");
    }

    [Fact]
    public void FormatRefactoringSummary_WithIssues_IncludesCountAndIdentifiers()
    {
        // Arrange
        var issues = new List<CreatedIssueInfo>
        {
            new() { Identifier = "10", Title = "Fix duplication", Url = "https://example.com/10" },
            new() { Identifier = "11", Title = "Rename methods", Url = "https://example.com/11" }
        };

        // Act
        var summary = RefactoringExecutor.FormatRefactoringSummary(issues);

        // Assert
        summary.Should().Contain("2");
        summary.Should().Contain("#10");
        summary.Should().Contain("#11");
    }

    [Fact]
    public async Task ExecuteAsync_IssueQuerySucceeds_PromptContainsIssueContext()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        var openIssues = new PagedResult<IssueSummary>
        {
            Items =
            [
                new IssueSummary
                {
                    Identifier = "100", Title = "Extract retry logic", Labels = ["agent:generated"], CreatedAt = DateTime.UtcNow.AddDays(-5),
                    Description = $"## Problem\n...\n*{RefactoringExecutor.GeneratedIssueFooter}.*"
                },
                new IssueSummary { Identifier = "200", Title = "Add caching layer", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-2) }
            ],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openIssues);

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — the scan's own issue is told apart by its footer, not by a label
        capturedPrompt.Should().Contain("Do Not Duplicate");
        capturedPrompt.Should().Contain("### Open Refactoring Scan Issues (still pending)\n- #100 \"Extract retry logic\"".ReplaceLineEndings());
        capturedPrompt.Should().Contain("### Other Open Issues (may overlap)\n- #200 \"Add caching layer\"".ReplaceLineEndings());
    }

    [Fact]
    public async Task ExecuteAsync_IssueQueryThrows_ContinuesWithoutContext()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider unavailable"));

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        var result = await executor.ExecuteAsync(job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — run should succeed (graceful degradation), not fail due to issue query
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_IssueQueryReturnsOldIssues_IncludesThem()
    {
        // Arrange — an open issue is a duplicate risk however old it is
        var executor = CreateExecutor();
        var job = CreateJob();

        var oldIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "50", Title = "Old issue", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-60) }],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldIssues);

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        capturedPrompt.Should().Contain("Do Not Duplicate");
        capturedPrompt.Should().Contain("#50 \"Old issue\"");
    }

    [Fact]
    public async Task ExecuteAsync_ClosedIssueQueryFails_ContinuesWithoutContext()
    {
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API failure"));

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Should succeed (graceful degradation) — no proposals file means "no proposals"
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");
    }

    [Fact]
    public async Task ExecuteAsync_ClosedIssuesFound_InjectsOutcomeContextIntoPrompt()
    {
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var scanFooter = $"*{RefactoringExecutor.GeneratedIssueFooter}.*";
        var closedIssues = new PagedResult<IssueSummary>
        {
            Items = new[]
            {
                new IssueSummary { Identifier = "100", Title = "Implemented refactoring", Labels = new[] { "agent:generated", "agent:done" }, Description = scanFooter },
                new IssueSummary { Identifier = "101", Title = "Rejected refactoring", Labels = new[] { "agent:generated", "agent:wont-do" }, Description = scanFooter },
                // A decomposition sub-issue shares agent:generated but is not a scan proposal
                new IssueSummary { Identifier = "102", Title = "Sub-issue of an epic", Labels = new[] { "agent:generated", "agent:done" }, Description = "## Summary\nPart 2 of the epic." }
            },
            Page = 1,
            PageSize = 50,
            HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(1, 50,
                It.Is<IReadOnlyList<string>>(l => l.Contains("agent:generated")),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(closedIssues);
        _mockIssueProvider
            .Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        AgentRequest? capturedRequest = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedRequest = req)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Prompt.Should().Contain("Past Proposal Outcomes");
        capturedRequest.Prompt.Should().Contain("#100 \"Implemented refactoring\"");
        capturedRequest.Prompt.Should().Contain("#101 \"Rejected refactoring\"");
        capturedRequest.Prompt.Should().NotContain("Sub-issue of an epic");
        capturedRequest.Prompt.Should().Contain("Do NOT propose refactorings similar to rejected items above.");
    }

    [Fact]
    public async Task ExecuteAsync_ClosedScanIssueHasImplementerFeedback_InjectsTheLatestIntoPrompt()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var closedIssues = new PagedResult<IssueSummary>
        {
            Items =
            [
                new IssueSummary
                {
                    Identifier = "3236", Title = "Stop swallowing cancellation", Labels = ["agent:generated", "agent:done"],
                    Description = $"*{RefactoringExecutor.GeneratedIssueFooter}.*"
                }
            ],
            Page = 1,
            PageSize = 50,
            HasMore = false
        };
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(closedIssues);

        static IssueComment Comment(string id, string body, int day) =>
            new() { Id = id, Author = "coding-agent-webui", Body = body, CreatedAt = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc) };
        var older = FeedbackCommentFormatter.FormatComment(new IssueFeedback { Category = "stale", Description = "Older note." })!;
        var latest = FeedbackCommentFormatter.FormatComment(new IssueFeedback
        {
            Category = "partial scope",
            Description = "The outer catch at L777 was missed."
        })!;
        _mockIssueProvider
            .Setup(x => x.ListCommentsAsync(It.Is<IssueIdentifier>(i => i.Value == "3236"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Comment("1", "## 🤖 Agent Analysis\nplan", 1), Comment("2", older, 2), Comment("3", latest, 3) });

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        capturedPrompt.Should().Contain("- #3236 \"Stop swallowing cancellation\" — partial scope: The outer catch at L777 was missed.");
        capturedPrompt.Should().NotContain("Older note.");
    }

    [Fact]
    public async Task ExecuteAsync_CommentsOfAPastIssueCannotBeRead_KeepsTheOutcomeContext()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items =
                [
                    new IssueSummary
                    {
                        Identifier = "7", Title = "Remove dead helper", Labels = ["agent:generated", "agent:done"],
                        Description = $"*{RefactoringExecutor.GeneratedIssueFooter}.*"
                    }
                ],
                Page = 1,
                PageSize = 50,
                HasMore = false
            });
        _mockIssueProvider
            .Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        capturedPrompt.Should().Contain("#7 \"Remove dead helper\"");
        capturedPrompt.Should().NotContain("Implementer Feedback");
    }

    [Fact]
    public void FormatIssueBody_WithNewFields_RendersMetadata()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Extract validation",
            AffectedFiles = ["src/A.cs"],
            Description = "Extract shared logic",
            Rationale = "DRY violation",
            Prerequisites = ["Add tests for A.cs"],
            EstimatedEffort = "medium",
            RiskLevel = "low",
            Technique = "Extract Method"
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("**Effort:** medium");
        body.Should().Contain("**Risk:** low");
        body.Should().Contain("**Technique:** Extract Method");
        body.Should().Contain("## Prerequisites");
        body.Should().Contain("- Add tests for A.cs");
    }

    [Fact]
    public void FormatIssueBody_WithNullFields_OmitsOptionalSections()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Rename methods",
            AffectedFiles = ["src/X.cs"],
            Description = "Inconsistent naming",
            Rationale = "Convention violation"
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().NotContain("**Effort:**");
        body.Should().NotContain("**Risk:**");
        body.Should().NotContain("**Technique:**");
        body.Should().NotContain("## Prerequisites");
        body.Should().NotContain("## Scope");
        body.Should().NotContain("## Evidence");
        body.Should().Contain("## Problem");
        body.Should().Contain("## Affected Components");
    }

    [Fact]
    public void ParseHotspotOutput_WithValidOutput_ReturnsFormattedSummary()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = "COMMIT_DATE:2026-07-19 14:00:00 +0000\nsrc/File1.cs\nsrc/File2.cs\nCOMMIT_DATE:2026-07-18 10:00:00 +0000\nsrc/File1.cs\nsrc/File1.cs\nsrc/File2.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Should().NotBeNull();
        result.Should().Contain("src/File1.cs");
        result.Should().Contain("3 changes");
        result.Should().Contain("src/File2.cs");
        result.Should().Contain("2 changes");
        result.Should().Contain("last 90 days");
        result.Should().Contain("time-decay weighted");
        result.Should().Contain("avg");
        result.Should().Contain("days ago");
    }

    [Fact]
    public void ParseHotspotOutput_WithEmptyOutput_ReturnsNull()
    {
        var result = RefactoringExecutor.ParseHotspotOutput("", TimeSpan.FromDays(90));

        result.Should().BeNull();
    }

    [Fact]
    public void ParseHotspotOutput_WithOnlyWhitespace_ReturnsNull()
    {
        var result = RefactoringExecutor.ParseHotspotOutput("\n\n  \n", TimeSpan.FromDays(90));

        result.Should().BeNull();
    }

    [Fact]
    public void ParseHotspotOutput_CapsAtThirtyFiles()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("COMMIT_DATE:2026-07-19 10:00:00 +0000");
        for (int i = 0; i < 50; i++)
            sb.AppendLine($"src/File{i:D2}.cs");

        var result = RefactoringExecutor.ParseHotspotOutput(sb.ToString(), TimeSpan.FromDays(90), referenceTime);

        result.Should().NotBeNull();
        // Each file appears once, so 30 lines with score data
        var lines = result!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        lines.Should().HaveCount(30);
    }

    [Fact]
    public void ParseHotspotOutput_SortsDescendingByScore()
    {
        // Common.cs has 3 recent changes (high score), Medium.cs has 2 recent changes, Rare.cs has 1
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = "COMMIT_DATE:2026-07-19 10:00:00 +0000\nsrc/Rare.cs\nsrc/Common.cs\nsrc/Common.cs\nsrc/Common.cs\nsrc/Medium.cs\nsrc/Medium.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Should().NotBeNull();
        var lines = result!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        lines[0].Should().Contain("src/Common.cs");
        lines[1].Should().Contain("src/Medium.cs");
        lines[2].Should().Contain("src/Rare.cs");
    }

    [Fact]
    public void ParseHotspotOutput_RecentChangesScoreHigherThanOldChangesWithSameCount()
    {
        // Both files have 3 changes, but RecentFile's changes are 1 day old vs OldFile's 80 days old
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:2026-07-19 12:00:00 +0000\nsrc/RecentFile.cs\nsrc/RecentFile.cs\nsrc/RecentFile.cs\n" +
            "COMMIT_DATE:2026-05-01 12:00:00 +0000\nsrc/OldFile.cs\nsrc/OldFile.cs\nsrc/OldFile.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Should().NotBeNull();
        var lines = result!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        // RecentFile should rank first (higher score due to recency)
        lines[0].Should().Contain("src/RecentFile.cs");
        lines[1].Should().Contain("src/OldFile.cs");
    }

    [Fact]
    public void ParseHotspotOutput_FewerRecentChangesOutrankManyOldChanges()
    {
        // ActiveFile: 3 changes, 1 day old → factor ≈ 1/(1+1/30) ≈ 0.97 each → score ≈ 2.9
        // StaleFile: 10 changes, 80 days old → factor = 1/(1+80/30) ≈ 0.27 each → score ≈ 2.7
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("COMMIT_DATE:2026-07-19 12:00:00 +0000");
        sb.AppendLine("src/ActiveFile.cs");
        sb.AppendLine("src/ActiveFile.cs");
        sb.AppendLine("src/ActiveFile.cs");
        sb.AppendLine("COMMIT_DATE:2026-05-01 12:00:00 +0000");
        for (int i = 0; i < 10; i++)
            sb.AppendLine("src/StaleFile.cs");

        var result = RefactoringExecutor.ParseHotspotOutput(sb.ToString(), TimeSpan.FromDays(90), referenceTime);

        result.Should().NotBeNull();
        var lines = result!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes,")).ToList();
        // ActiveFile should rank first despite fewer changes
        lines[0].Should().Contain("src/ActiveFile.cs");
        lines[1].Should().Contain("src/StaleFile.cs");
    }

    [Fact]
    public void ParseHotspotOutput_MalformedDateLinesDontCrash()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:not-a-date\nsrc/File1.cs\nsrc/File2.cs\n" +
            "COMMIT_DATE:2026-07-19 12:00:00 +0000\nsrc/File3.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        // Should not throw and should produce output
        result.Should().NotBeNull();
        result.Should().Contain("src/File1.cs");
        result.Should().Contain("src/File2.cs");
        result.Should().Contain("src/File3.cs");
    }

    [Fact]
    public void FormatIssueBody_WithNullEntriesInPrerequisites_SkipsNulls()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            Description = "desc",
            Rationale = "rationale",
            AffectedFiles = ["src/File.cs"],
            Prerequisites = ["valid prerequisite", null!, "another valid one"]
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("valid prerequisite");
        body.Should().Contain("another valid one");
    }

    [Fact]
    public void FormatIssueBody_SanitizesMetadataFields()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            EstimatedEffort = "<script>alert('xss')</script>",
            RiskLevel = "@admin injection",
            Technique = "low <!-- hidden -->"
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("**Effort:** &lt;script>alert('xss')&lt;/script>");
        body.Should().Contain("**Risk:** @\u200Badmin injection");
        body.Should().Contain("**Technique:** low &lt;!-- hidden -->");
    }

    [Fact]
    public async Task RunGitCommandAsync_NonZeroExitCode_ThrowsWithStderr()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"git-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var act = () => RefactoringExecutor.RunGitCommandAsync(tempDir, "log --oneline -1", CancellationToken.None);

            var ex = await act.Should().ThrowAsync<InvalidOperationException>();
            ex.Which.Message.Should().Contain("failed with exit code");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AutoDispatchTrue_CreatesIssuesWithAgentNextLabel()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = new ConsolidationJobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            TemplateName = "Test Template",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir },
            AutoDispatch = true
        };

        var proposalsJson = """
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Shared logic.",
                    "rationale": "DRY."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        IReadOnlyList<string>? capturedLabels = null;
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "10", Url = "https://github.com/t/r/issues/10" });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain("agent:generated");
        capturedLabels.Should().Contain("agent:next");
    }

    [Fact]
    public async Task ExecuteAsync_AutoDispatchFalse_CreatesIssuesWithOnlyGeneratedLabel()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob(); // AutoDispatch defaults to false

        var proposalsJson = """
            [
                {
                    "title": "Inline helper method",
                    "affectedFiles": ["src/B.cs"],
                    "description": "Unused abstraction.",
                    "rationale": "Simplicity."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Done."] });

        IReadOnlyList<string>? capturedLabels = null;
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "11", Url = "https://github.com/t/r/issues/11" });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain("agent:generated");
        capturedLabels.Should().NotContain("agent:next");
    }

    // TODO: This test verifies each criterion appears as a checkbox but does not assert positional ordering
    // (i.e., that criteria appear under the "## Acceptance Criteria" heading and before "---"). A section-
    // ordering bug could cause criteria to render in the wrong section without failing this test.
    // TODO: [WARNING] After the S3776 refactoring (#1790), AppendAcceptanceCriteria returns without appending
    // the footer (`---` separator + attribution line); the footer is now appended by the calling method after
    // AppendAcceptanceCriteria returns. A regression that re-orders footer-before-criteria would not be caught
    // by this test. Consider adding an assertion that the `---` separator appears *after* the last criterion.
    [Fact]
    public void FormatIssueBody_WithPopulatedAcceptanceCriteria_RendersAllItemsAsCheckboxes()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = ["Criterion A", "Criterion B", "Criterion C"]
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] Criterion A");
        body.Should().Contain("- [ ] Criterion B");
        body.Should().Contain("- [ ] Criterion C");
        body.Should().NotContain("Refactoring applied without changing observable behavior");
        body.Should().NotContain("All existing tests continue to pass");
    }

    [Fact]
    public void FormatIssueBody_WithNullAcceptanceCriteria_RendersFallbackAC()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = null
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] Refactoring applied without changing observable behavior");
        body.Should().Contain("- [ ] All existing tests continue to pass");
    }

    [Fact]
    public void FormatIssueBody_WithEmptyAcceptanceCriteria_RendersFallbackAC()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = []
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] Refactoring applied without changing observable behavior");
        body.Should().Contain("- [ ] All existing tests continue to pass");
    }

    [Fact]
    public void FormatIssueBody_WithNullEntriesInAcceptanceCriteria_SkipsNulls()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = ["valid one", null!, "another valid"]
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] valid one");
        body.Should().Contain("- [ ] another valid");
    }

    // TODO: No test verifies the boundary condition where AcceptanceCriteria contains exactly 1 item or
    // more than 4 items. The issue specifies "Range: 2-4 criteria per proposal" but the rendering logic
    // doesn't enforce this range. Consider adding tests documenting that single-item lists render correctly
    // without falling through to the fallback.
    [Fact]
    public void FormatIssueBody_SanitizesAcceptanceCriteriaEntries()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Test",
            AffectedFiles = ["src/A.cs"],
            Description = "desc",
            Rationale = "rationale",
            AcceptanceCriteria = ["@admin injection", "<script>xss</script>"]
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] @\u200Badmin injection");
        body.Should().Contain("- [ ] &lt;script>xss&lt;/script>");
    }

    // ─── Autolink escaping in prerequisites (issue #1450) ───────────────────────

    private static RefactoringProposal Proposal(
        string title, IReadOnlyList<string>? prerequisites = null) => new()
    {
        Title = title,
        AffectedFiles = ["x"],
        Description = "d",
        Rationale = "r",
        Prerequisites = prerequisites
    };

    [Fact]
    public async Task ExecuteAsync_ProposalWithDependsOnInJson_CreatesInFileOrderWithNoDependsOnLines()
    {
        // Requirement 6: when the proposals JSON contains a "dependsOn" field (from an older agent
        // run), the field is silently ignored (System.Text.Json drops unknown properties by default),
        // proposals are created in file order, and neither issue body contains a "Depends on" line.
        var executor = CreateExecutor();
        var job = CreateJob();

        var proposalsJson = """
            [
                {
                    "title": "Extract shared validation logic",
                    "affectedFiles": ["src/Validator.cs"],
                    "description": "Duplicated validation logic.",
                    "rationale": "DRY principle."
                },
                {
                    "title": "Simplify callers of validation",
                    "affectedFiles": ["src/Handler.cs"],
                    "description": "Use the extracted validator.",
                    "rationale": "Follows from the extraction.",
                    "dependsOn": ["Extract shared validation logic"]
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var capturedTitles = new List<string>();
        var capturedBodies = new List<string>();
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((title, body, _, _) =>
            {
                capturedTitles.Add(title);
                capturedBodies.Add(body);
            })
            .ReturnsAsync(new CreatedIssueResult { Identifier = "1", Url = "https://github.com/test/repo/issues/1" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — both issues are created in file order, neither body has a "Depends on" line
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(2);
        // TODO: capturedTitles captures the raw title argument passed to CreateIssueAsync, which is
        // the sanitized title (SanitizeTitle is called before CreateIssueAsync). If SanitizeTitle
        // ever transforms these titles (e.g. truncation), this assertion will fail with a misleading
        // message unrelated to the ordering contract being verified. Use SanitizeTitle-aware expected
        // values if the titles become more complex.
        capturedTitles.Should().Equal("Extract shared validation logic", "Simplify callers of validation");
        // TODO: capturedBodies is populated by a separate mock callback, not derived from CreatedIssues.
        // The HaveCount(2) on CreatedIssues above does not guard capturedBodies. If CreateIssueAsync
        // is called fewer than 2 times, the index access below throws ArgumentOutOfRangeException
        // instead of a meaningful FluentAssertions failure. Add capturedBodies.Should().HaveCount(2)
        // here to produce a clear message when the callback count diverges from CreatedIssues.
        capturedBodies[0].Should().NotContain("Depends on");
        capturedBodies[1].Should().NotContain("Depends on");
    }

    [Fact]
    public void FormatIssueBody_PrerequisiteWithHashNumber_EscapesTheAutolink()
    {
        var proposal = Proposal("Extract class",
            prerequisites: ["Complete proposal #1 (dead code removal)", "After #2 is merged"]);

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        // GitHub turns a bare #N into a link to issue N, which is unrelated to the proposal numbering.
        body.Should().Contain("- Complete proposal `#1` (dead code removal)");
        body.Should().Contain("- After `#2` is merged");
    }

    [Fact]
    public void FormatIssueBody_PrerequisiteWithLanguageName_IsNotEscaped()
    {
        var proposal = Proposal("Use primary constructors", prerequisites: ["Requires C# 12 and F#8 support"]);

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- Requires C# 12 and F#8 support");
    }

    [Fact]
    public void FormatIssueBody_PrerequisiteWithoutHashNumber_PassesThrough()
    {
        var proposal = Proposal("Rename", prerequisites: ["Add characterization tests for X before refactoring"]);

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- Add characterization tests for X before refactoring");
    }

    // ── Issue #2681: exception-swallowing / misleading permissions hint ───────────────────────

    private void SetupEmptyClosedIssues()
    {
        var emptyClosedResult = new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 20, HasMore = false };
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyClosedResult);
    }

    private void SetupProposalsFile(string proposalsJson)
    {
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) =>
            {
                RefactoringTestWorkspace.WriteProposals(path, proposalsJson);
            })
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp401_ReturnsFailedResultWithTokenHint()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("401");
        // AC #1: the run summary (not just ErrorMessage) must contain "401" to confirm hint threading
        // through FormatRefactoringSummary. Also verify the old hardcoded string is absent.
        result.Summary.Should().Contain("401");
        result.Summary.Should().NotContain("check GitHub App permissions");
        // TODO [WARNING]: The compound null-or-empty check below accepts a null CreatedIssues, which is a
        // weaker contract than the implementation provides (it always returns a non-null list). Consider
        // replacing with result.CreatedIssues.Should().HaveCount(0) to enforce the non-null contract.
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp403_ReturnsFailedResultWithPermissionsHint()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Remove dead code",
                    "affectedFiles": ["src/B.cs"],
                    "description": "Unused method.",
                    "rationale": "Cleanliness."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("403");
        result.ErrorMessage.Should().NotContain("401");
        // AC #2: the run summary must contain "403" and be distinct from the 401 message to confirm
        // hint threading through FormatRefactoringSummary is intact.
        result.Summary.Should().Contain("403");
        result.Summary.Should().NotContain("401 Unauthorized");
        // TODO [WARNING]: The compound null-or-empty check below accepts a null CreatedIssues, which is a
        // weaker contract than the implementation provides (it always returns a non-null list). Consider
        // replacing with result.CreatedIssues.Should().HaveCount(0) to enforce the non-null contract.
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PartialIssueCreationFailure_ReturnsSuccessTrueWithPartialCountAndErrorMessage()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY."
                },
                {
                    "title": "Remove dead code",
                    "affectedFiles": ["src/B.cs"],
                    "description": "Unused method.",
                    "rationale": "Cleanliness."
                }
            ]
            """);

        _mockIssueProvider
            .SetupSequence(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "10", Url = "https://github.com/test/repo/issues/10" })
            .ThrowsAsync(new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(1);
        // AC #3: ErrorMessage must contain the specific first-failure hint, not just be non-empty.
        // The second proposal throws 403, so the hint must identify that classification.
        result.ErrorMessage.Should().Contain("403");
        // TODO [WARNING]: This test does not verify that CreateIssueAsync was called exactly twice.
        // If MaxRefactoringProposals is configured to 1 in CreateJob(), the second proposal (the
        // failing one) is silently skipped, ErrorMessage ends up null, and the partial-failure path
        // is never actually exercised. Add _mockIssueProvider.Verify(x => x.CreateIssueAsync(...),
        // Times.Exactly(2)) after the act step to make this precondition explicit.
    }

    [Fact]
    public void FormatRefactoringSummary_ZeroCreatedNonZeroProposals_WithHint_IncludesHintInSummary()
    {
        // Act
        var summary = RefactoringExecutor.FormatRefactoringSummary(
            [],
            proposalCount: 3,
            firstFailureHint: "401 Unauthorized — token expired or revoked");

        // Assert
        summary.Should().Contain("3");
        summary.Should().Contain("401 Unauthorized — token expired or revoked");
        summary.Should().NotContain("check GitHub App permissions");
    }

    [Fact]
    public void FormatRefactoringSummary_PartialCreation_WithHint_IncludesHintInSummary()
    {
        // Arrange
        var createdIssues = new List<CreatedIssueInfo>
        {
            new() { Identifier = "42", Title = "Extract validation", Url = "https://example.com/42" }
        };

        // Act
        var summary = RefactoringExecutor.FormatRefactoringSummary(
            createdIssues,
            proposalCount: 3,
            firstFailureHint: "403 Forbidden — app missing 'issues: write' permission");

        // Assert
        summary.Should().Contain("1/3");
        summary.Should().Contain("403 Forbidden");
        summary.Should().Contain("2 failed");
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp422_ReturnsFailedResultWithUnprocessableHint()
    {
        // Arrange — covers ClassifyIssueCreationException HTTP 422 branch
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unprocessable Entity", null, System.Net.HttpStatusCode.UnprocessableEntity));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("422");
        result.Summary.Should().Contain("422");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsHttp429_ReturnsFailedResultWithRateLimitHint()
    {
        // Arrange — covers ClassifyIssueCreationException HTTP 429 branch
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("429");
        result.Summary.Should().Contain("429");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsUnknownHttpStatus_ReturnsFailedResultWithHttpCodeHint()
    {
        // Arrange — covers ClassifyIssueCreationException default HTTP branch ("HTTP {code}")
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        // 503 falls through to the default arm: "HTTP 503"
        result.ErrorMessage.Should().Contain("503");
        result.Summary.Should().Contain("503");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllIssueCreationThrowsNonHttpException_ReturnsFailedResultWithExceptionTypeHint()
    {
        // Arrange — covers ClassifyIssueCreationException non-HTTP fallback branch
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Extract validation helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Duplicated validation.",
                    "rationale": "DRY principle."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        // Non-HTTP exception: "{TypeName}: {Message}"
        result.ErrorMessage.Should().Contain("InvalidOperationException");
        result.Summary.Should().Contain("InvalidOperationException");
        (result.CreatedIssues is null || result.CreatedIssues.Count == 0).Should().BeTrue();
    }

    // ── Proposal validation before issue creation ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ProposalFailsValidation_IsNotFiledAndSummaryCountsIt()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Correct the stale note in the conventions file",
                    "affectedFiles": [".agent/refactoring-conventions.json"],
                    "description": "Update the note.",
                    "rationale": "It is stale."
                },
                {
                    "title": "Remove dead helper",
                    "category": "dead-code",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Delete it.",
                    "rationale": "No callers."
                }
            ]
            """);

        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "7", Url = "https://github.com/test/repo/issues/7" });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Summary.Should().Be("Created 1 refactoring issue(s): #7 (1 proposal(s) dropped by validation)");
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.Is<string>(t => t.Contains("conventions file")), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalDuplicatesOpenIssueTitle_IsNotFiled()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        var openIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "300", Title = "Remove dead helper", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-90) }],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openIssues);
        SetupProposalsFile("""
            [
                {
                    "title": "Remove dead helper",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Delete it.",
                    "rationale": "No callers."
                }
            ]
            """);

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // An open issue blocks a duplicate however old it is
        result.Success.Should().BeTrue();
        result.Summary.Should().Contain("No refactoring opportunities identified");
        result.Summary.Should().Contain("1 proposal(s) dropped by validation");
        _mockIssueProvider.Verify(
            x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WritesIssueContextFileForTheReviewStep()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        var openIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "100", Title = "Extract retry logic", Labels = ["agent:generated"], CreatedAt = DateTime.UtcNow }],
            Page = 1,
            PageSize = 100,
            HasMore = false
        };
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(openIssues);
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? contextAtAggregation = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                var path = Path.Combine(req.WorkspacePath!, AgentWorkspacePaths.RefactoringIssueContextFilePath);
                if (File.Exists(path))
                    contextAtAggregation = File.ReadAllText(path);
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        contextAtAggregation.Should().NotBeNull();
        contextAtAggregation.Should().Contain("#100 \"Extract retry logic\"");
    }

    [Fact]
    public async Task ExecuteAsync_EvidenceAndScopeQueryWrittenAsArrays_AreJoinedInsteadOfFailingTheParse()
    {
        var executor = CreateExecutor();
        var job = CreateJob();
        SetupEmptyClosedIssues();
        SetupProposalsFile("""
            [
                {
                    "title": "Stop swallowing cancellation",
                    "category": "bug",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Add the filter.",
                    "rationale": "Cancellation is logged as a failure.",
                    "evidence": ["src/A.cs:L10", "catch (Exception ex)"],
                    "scopeQuery": ["git grep -n 'catch (Exception ex)' -- src/A.cs"]
                }
            ]
            """);

        string? capturedBody = null;
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, body, _, _) => capturedBody = body)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "8", Url = "https://github.com/test/repo/issues/8" });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        capturedBody!.ReplaceLineEndings("\n").Should().Contain("```\nsrc/A.cs:L10\ncatch (Exception ex)\n```");
        capturedBody.Should().Contain("git grep -n 'catch (Exception ex)' -- src/A.cs");
    }

    // ── Issue body format ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatIssueBody_PutsProblemFromRationaleBeforeApproachFromDescription()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix it",
            AffectedFiles = ["src/A.cs"],
            Description = "THE-CHANGE",
            Rationale = "THE-PROBLEM"
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        var problem = body.IndexOf("## Problem", StringComparison.Ordinal);
        var approach = body.IndexOf("## Suggested Approach", StringComparison.Ordinal);
        problem.Should().BeGreaterThanOrEqualTo(0);
        approach.Should().BeGreaterThan(problem);
        body.IndexOf("THE-PROBLEM", StringComparison.Ordinal).Should().BeInRange(problem, approach);
        body.IndexOf("THE-CHANGE", StringComparison.Ordinal).Should().BeGreaterThan(approach);
    }

    [Fact]
    public void FormatIssueBody_RendersCategoryInMetadataLine()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Remove dead code",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            Category = "dead-code",
            EstimatedEffort = "small"
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("**Category:** dead-code | **Effort:** small");
    }

    [Fact]
    public void FormatIssueBody_RendersEvidenceVerbatimInAFenceItCannotClose()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            Evidence = "src/A.cs:L10\nif (x < 0) { /* ``` */ }",
            EvidenceSources = ["usage-search:Foo<T>:0-callers"]
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal).ReplaceLineEndings("\n");

        body.Should().Contain("## Evidence");
        body.Should().Contain("````\nsrc/A.cs:L10\nif (x < 0) { /* ``` */ }\n````");
        // Code spans render literally, so no HTML escaping that would show up as "&lt;"
        body.Should().Contain("- `usage-search:Foo<T>:0-callers`");
    }

    [Fact]
    public void FormatIssueBody_WithScopeQuery_RendersScopeSection()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            ScopeQuery = "git grep -n 'catch (Exception ex)' -- src"
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal).ReplaceLineEndings("\n");

        body.Should().Contain("## Scope");
        body.Should().Contain("```sh\ngit grep -n 'catch (Exception ex)' -- src\n```");
    }

    [Fact]
    public void FormatIssueBody_WithCommitSha_NamesTheAnalyzedCommit()
    {
        var proposal = new RefactoringProposal { Title = "Fix", AffectedFiles = ["src/A.cs"], Description = "d", Rationale = "r" };

        var body = RefactoringExecutor.FormatIssueBody(proposal, "0123456789abcdef0123");

        body.Should().EndWith(
            "*This issue was automatically generated by the refactoring detection consolidation loop at commit `0123456789ab`. Line numbers refer to that commit.*");
    }

    [Fact]
    public void FormatIssueBody_WithoutCommitSha_KeepsThePlainFooter()
    {
        var proposal = new RefactoringProposal { Title = "Fix", AffectedFiles = ["src/A.cs"], Description = "d", Rationale = "r" };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().EndWith("*This issue was automatically generated by the refactoring detection consolidation loop.*");
    }

    [Fact]
    public void FormatIssueBody_BugWithoutCriteria_DefaultsToAReproductionTestNotUnchangedBehavior()
    {
        var proposal = new RefactoringProposal
        {
            Title = "Fix null dereference",
            AffectedFiles = ["src/A.cs"],
            Description = "d",
            Rationale = "r",
            Category = "bug"
        };

        var body = RefactoringExecutor.FormatIssueBody(proposal);

        body.Should().Contain("- [ ] A test reproduces the failure described under Problem and passes after the fix");
        body.Should().NotContain("without changing observable behavior");
    }

    // ── Hotspot input ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".agent/hotspot-analysis.txt")]
    [InlineData(".brain/projects/x/SKILL.md")]
    [InlineData("docs/configuration.md")]
    [InlineData("src/web/package-lock.json")]
    [InlineData("src/App/packages.lock.json")]
    [InlineData("poetry.lock")]
    public void ParseHotspotOutput_LeavesOutFilesWhoseChurnSaysNothingAboutCode(string excluded)
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = $"COMMIT_DATE:2026-07-19 12:00:00 +0000\n{excluded}\n{excluded}\nsrc/Kept.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90), referenceTime);

        result.Should().Contain("src/Kept.cs");
        result.Should().NotContain(excluded);
    }

    [Fact]
    public void ParseHotspotOutput_WithExistenceCheck_LeavesOutDeletedFiles()
    {
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput = "COMMIT_DATE:2026-07-19 12:00:00 +0000\nsrc/Deleted.cs\nsrc/Deleted.cs\nsrc/Kept.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(
            gitOutput, TimeSpan.FromDays(90), referenceTime, fileExists: f => f != "src/Deleted.cs");

        result.Should().Contain("src/Kept.cs");
        result.Should().NotContain("src/Deleted.cs");
    }

    [Fact]
    public void FormatRefactoringSummary_WithDroppedProposals_AppendsTheCount()
    {
        var summary = RefactoringExecutor.FormatRefactoringSummary([], proposalCount: 0, droppedCount: 2);

        summary.Should().Be("No refactoring opportunities identified (2 proposal(s) dropped by validation)");
    }
}
