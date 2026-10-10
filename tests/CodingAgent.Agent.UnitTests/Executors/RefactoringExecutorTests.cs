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

        // Default: return empty closed-issue pages so CollectScanIssueReferencesAsync and
        // TryBuildOutcomeContextAsync both succeed without null-task NullReferenceExceptions.
        // ListClosedIssuesAsync is a Default Interface Method (DIM); Moq does not invoke DIM bodies
        // on mock objects — without an explicit setup the call returns null for Task<…> return types,
        // causing a NullReferenceException that is silently swallowed by the catch blocks in those
        // callers.  The setup here ensures all ExecuteAsync tests exercise the happy path.
        var emptyClosedResult = new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 100, HasMore = false };
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyClosedResult);

        // FormatIssueReference is also a DIM; set up the default "#N" implementation so
        // CollectScanIssueReferencesAsync can format identifiers correctly in all tests.
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns((IssueIdentifier id) => $"#{id}");
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

    [Fact]
    public async Task ExecuteAsync_DependentProposal_InjectedBodyContainsDependsOnLine()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        // Proposal A has no dependencies; proposal B depends on A by exact title match
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

        // Capture bodies for each call and return distinct identifiers per call.
        // callIndex is incremented exclusively in the Callback (which Moq fires before the
        // ReturnsAsync factory), so the factory reads identifiers[callIndex - 1] after the
        // increment. This avoids relying on the internal Callback-before-Returns ordering of
        // the increment side-effect in the factory lambda.
        var capturedBodies = new List<string>();
        var callIndex = 0;
        var identifiers = new[] { "10", "11" };
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, body, _, _) =>
            {
                capturedBodies.Add(body);
                callIndex++;
            })
            // TODO: The clamped guard (callIndex - 1 < identifiers.Length ? ... : identifiers.Length - 1)
            // silently returns the last identifier on any unexpected extra call instead of throwing.
            // This masks a production regression where CreateIssueAsync is called more times than expected.
            // Replace the guard with an unclamped identifiers[callIndex - 1] (or an explicit throw) so
            // that an over-call surfaces as a test failure rather than returning a duplicate identifier.
            .ReturnsAsync(() => new CreatedIssueResult
            {
                Identifier = identifiers[callIndex - 1 < identifiers.Length ? callIndex - 1 : identifiers.Length - 1],
                Url = "https://github.com/test/repo/issues/x"
            });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(2);

        // Proposal B's body (second call) must contain "Depends on #10"
        capturedBodies.Should().HaveCount(2);
        // TODO: Tighten this assertion to capturedBodies[1].Should().StartWith("Depends on #10") to
        // pin the prepend contract (production code does $"{depSection}\n\n{body}"). The current
        // Contain check passes even if the dependency line is appended or embedded rather than prepended.
        capturedBodies[1].Should().Contain("Depends on #10");
        // Proposal A's body (first call) must NOT contain any dependency line
        capturedBodies[0].Should().NotContain("Depends on #");
        // TODO: Add a captured-title assertion to verify ordering: the first CreateIssueAsync call
        // was for the prerequisite proposal ("Extract shared validation logic") and the second for
        // the dependent ("Simplify callers of validation"). Without this, if proposal processing
        // order changes incorrectly but the resolver still resolves the reference, the test may
        // pass for the wrong reason or fail with a misleading message.
    }

    [Fact]
    public async Task ExecuteAsync_DependsOnUnresolvableTitle_OmitsDependsOnLine()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        // Proposal B references a title that doesn't exist in the batch
        var proposalsJson = """
            [
                {
                    "title": "Simplify callers of validation",
                    "affectedFiles": ["src/Handler.cs"],
                    "description": "Use the extracted validator.",
                    "rationale": "Follows from extraction.",
                    "dependsOn": ["Nonexistent proposal title"]
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

        string? capturedBody = null;
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((_, body, _, _) => capturedBody = body)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "20", Url = "https://github.com/test/repo/issues/20" });

        // Act
        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — unresolvable title should be silently omitted, not cause failure
        result.Success.Should().BeTrue();
        result.CreatedIssues.Should().HaveCount(1);
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().NotContain("Depends on #");
    }

    [Fact]
    public async Task ExecuteAsync_ProposalDependsOnALaterProposal_CreatesTheDependencyFirst()
    {
        // Proposal A lists B, which comes later in the batch. Proposals are created in dependency
        // order (issue #1450), so B exists when A's dependency line is resolved.
        var executor = CreateExecutor();
        var job = CreateJob();

        var proposalsJson = """
            [
                {
                    "title": "Extract class from service",
                    "affectedFiles": ["src/Service.cs"],
                    "description": "Extract the dispatch run creator.",
                    "rationale": "Too many responsibilities.",
                    "dependsOn": ["Remove dead code cluster"]
                },
                {
                    "title": "Remove dead code cluster",
                    "affectedFiles": ["src/Service.cs"],
                    "description": "Delete unused methods.",
                    "rationale": "285 lines of dead code."
                }
            ]
            """;

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((path, _) => RefactoringTestWorkspace.WriteProposals(path, proposalsJson))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Analysis complete."] });

        var createdTitles = new List<string>();
        var capturedBodies = new List<string>();
        _mockIssueProvider
            .Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>?, CancellationToken>((title, body, _, _) =>
            {
                createdTitles.Add(title);
                capturedBodies.Add(body);
            })
            .ReturnsAsync(() => new CreatedIssueResult
            {
                Identifier = (1000 + capturedBodies.Count).ToString(CultureInfo.InvariantCulture),
                Url = $"https://github.com/test/repo/issues/{1000 + capturedBodies.Count}"
            });

        var result = await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        createdTitles.Should().Equal("Remove dead code cluster", "Extract class from service");
        capturedBodies[0].Should().NotContain("Depends on #");
        capturedBodies[1].Should().StartWith("Depends on #1001");
    }

    // ─── Topological sort of proposals (issue #1450) ────────────────────────────

    private static RefactoringProposal Proposal(
        string title, IReadOnlyList<string>? dependsOn = null, IReadOnlyList<string>? prerequisites = null) => new()
        {
            Title = title,
            AffectedFiles = ["x"],
            Description = "d",
            Rationale = "r",
            DependsOn = dependsOn,
            Prerequisites = prerequisites
        };

    [Fact]
    public void TopologicalSortProposals_IndependentProposals_KeepsTheirOrder()
    {
        var sorted = RefactoringExecutor.TopologicalSortProposals([Proposal("A"), Proposal("B"), Proposal("C")]);

        sorted.Select(p => p.Title).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void TopologicalSortProposals_DependentProposal_MovesAfterItsDependency()
    {
        var sorted = RefactoringExecutor.TopologicalSortProposals(
            [Proposal("B depends on A", ["A is independent"]), Proposal("A is independent")]);

        sorted.Select(p => p.Title).Should().Equal("A is independent", "B depends on A");
    }

    [Fact]
    public void TopologicalSortProposals_Cycle_KeepsTheOriginalOrder()
    {
        var sorted = RefactoringExecutor.TopologicalSortProposals([Proposal("A", ["B"]), Proposal("B", ["A"])]);

        sorted.Select(p => p.Title).Should().Equal("A", "B");
    }

    [Fact]
    public void TopologicalSortProposals_DependencyOutsideTheBatch_IsIgnored()
    {
        var sorted = RefactoringExecutor.TopologicalSortProposals(
            [Proposal("A", ["External issue not in batch"]), Proposal("B")]);

        sorted.Select(p => p.Title).Should().Equal("A", "B");
    }

    [Fact]
    public void TopologicalSortProposals_DependencyTitleWithOtherCaseAndSpaces_MovesAfterItsDependency()
    {
        var sorted = RefactoringExecutor.TopologicalSortProposals(
            [Proposal("B", ["  a  "]), Proposal("A")]);

        sorted.Select(p => p.Title).Should().Equal("A", "B");
    }

    [Fact]
    public void TopologicalSortProposals_SelfReference_IsIgnored()
    {
        var sorted = RefactoringExecutor.TopologicalSortProposals(
            [Proposal("Y", ["X"]), Proposal("X", [" x "])]);

        sorted.Select(p => p.Title).Should().Equal("X", "Y");
    }

    // ─── Autolink escaping in prerequisites (issue #1450) ───────────────────────

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

    // TODO: Add a test covering the case where the first proposal fails to be created (mock throws)
    // and a later proposal lists its title in DependsOn. Because the catch block swallows per-proposal
    // exceptions and Register is only called after success, the failed title is never registered and
    // the dependent proposal silently receives no dependency line. A test would document this
    // behavior and prevent a future change from accidentally registering titles for failed creations.

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

    // ── ParseHotspotOutput: isExcludedCommit predicate (issue #3540) ──────────────────────────

    [Fact]
    public void ParseHotspotOutput_WithPredicate_ExcludesMatchingCommitFiles()
    {
        // AC 1: two commits both change src/A.cs; one has subject "feat: x (#5) (#9)" and is excluded;
        // only the other commit (non-matching) contributes, so the output shows 1 changes.
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:2026-07-19 10:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: x (#5) (#9)\n" +
            "src/A.cs\n" +
            "COMMIT_DATE:2026-07-18 10:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: y (#7)\n" +
            "src/A.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(
            gitOutput, TimeSpan.FromDays(90), referenceTime,
            isExcludedCommit: s => s.Contains("(#5)"));

        result.Should().NotBeNull();
        result.Should().Contain("src/A.cs");
        // TODO [WARNING]: Asserting Contains("1 changes") is a weak check — the test would still pass
        // if both commits contributed and the output showed "2 changes" while also containing the
        // substring "1" elsewhere. A stronger assertion (e.g. asserting "2 changes" is absent, or
        // asserting the exact score line) would better lock down the exclusion behaviour.
        result.Should().Contain("1 changes");
    }

    [Fact]
    public void ParseHotspotOutput_NonScanCommit_IsCounted_AndNoCommitSubjectLineInOutput()
    {
        // AC 2: a non-scan commit (subject does not match predicate) is counted;
        // and no output line starts with "COMMIT_SUBJECT:" whether the predicate is provided or not.
        // TODO [WARNING]: This test uses a single commit, so it does not exercise the case where
        // skipCurrentCommit is not reset on COMMIT_DATE: (i.e. a skip-state leak across commits).
        // A bug where the skip flag was never cleared would not be caught here. Consider adding a
        // second commit (non-excluded) after an excluded commit to verify the reset behaviour.
        var referenceTime = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var gitOutput =
            "COMMIT_DATE:2026-07-19 10:00:00 +0000\n" +
            "COMMIT_SUBJECT:feat: y (#7)\n" +
            "src/B.cs\n";

        // With predicate (excludes #5 only — does not match "feat: y (#7)")
        var resultWithPredicate = RefactoringExecutor.ParseHotspotOutput(
            gitOutput, TimeSpan.FromDays(90), referenceTime,
            isExcludedCommit: s => s.Contains("(#5)"));

        resultWithPredicate.Should().NotBeNull();
        resultWithPredicate.Should().Contain("src/B.cs");
        resultWithPredicate!.Split('\n')
            .Should().NotContain(l => l.StartsWith("COMMIT_SUBJECT:", StringComparison.Ordinal));

        // Without predicate — same: COMMIT_SUBJECT: lines are never emitted
        var resultNoPredicate = RefactoringExecutor.ParseHotspotOutput(
            gitOutput, TimeSpan.FromDays(90), referenceTime);

        resultNoPredicate.Should().NotBeNull();
        resultNoPredicate.Should().Contain("src/B.cs");
        resultNoPredicate!.Split('\n')
            .Should().NotContain(l => l.StartsWith("COMMIT_SUBJECT:", StringComparison.Ordinal));
    }

    // ── CollectScanIssueReferencesAsync (issue #3540) ─────────────────────────────────────────

    [Fact]
    public async Task CollectScanIssueReferences_WhenHasMoreOnPage1_RequestsPage2()
    {
        // AC 3a: HasMore=true on page 1 leads to a page-2 request; both identifiers appear in the result.
        var executor = CreateExecutor();
        // TODO [WARNING]: The "*<footer>.*" pattern works only because IsScanIssue uses Contains and the
        // footer is still a substring. The literal * characters are not wildcards. Use GeneratedIssueFooter
        // directly (or a string that simply contains it) to make the fixture's intent unambiguous.
        var scanFooter = $"*{RefactoringExecutor.GeneratedIssueFooter}.*";

        // FormatIssueReference is a default interface method — Moq does not invoke DIM bodies;
        // must be set up explicitly so the mock returns the expected "#N" string.
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns((IssueIdentifier id) => $"#{id}");

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(1, 100,
                It.Is<IReadOnlyList<string>>(l => l.Contains("agent:generated")),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [new IssueSummary { Identifier = "10", Title = "Scan A", Labels = ["agent:generated"], Description = scanFooter }],
                Page = 1,
                PageSize = 100,
                HasMore = true
            });
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(2, 100,
                It.Is<IReadOnlyList<string>>(l => l.Contains("agent:generated")),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [new IssueSummary { Identifier = "11", Title = "Scan B", Labels = ["agent:generated"], Description = scanFooter }],
                Page = 2,
                PageSize = 100,
                HasMore = false
            });

        var result = await executor.CollectScanIssueReferencesAsync(
            _mockIssueProvider.Object, [], TimeSpan.FromDays(90), CancellationToken.None);

        result.Should().Contain("#10");
        result.Should().Contain("#11");

        _mockIssueProvider.Verify(x => x.ListClosedIssuesAsync(1, 100,
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockIssueProvider.Verify(x => x.ListClosedIssuesAsync(2, 100,
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CollectScanIssueReferences_NonScanIssues_AreNotIncluded()
    {
        // AC 3b: non-scan issues (without the footer) are not included in the result.
        var executor = CreateExecutor();
        // TODO [WARNING]: The "*<footer>.*" pattern works only because IsScanIssue uses Contains and the
        // footer is still a substring. The literal * characters are not wildcards. Use GeneratedIssueFooter
        // directly (or a string that simply contains it) to make the fixture's intent unambiguous.
        var scanFooter = $"*{RefactoringExecutor.GeneratedIssueFooter}.*";

        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns((IssueIdentifier id) => $"#{id}");

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), 100,
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items =
                [
                    new IssueSummary { Identifier = "20", Title = "Scan issue", Labels = ["agent:generated"], Description = scanFooter },
                    new IssueSummary { Identifier = "21", Title = "Non-scan issue", Labels = ["agent:generated"], Description = "Some other description." }
                ],
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        var result = await executor.CollectScanIssueReferencesAsync(
            _mockIssueProvider.Object, [], TimeSpan.FromDays(90), CancellationToken.None);

        result.Should().Contain("#20");
        result.Should().NotContain("#21");
    }

    [Fact]
    public async Task CollectScanIssueReferences_ClosedIssueQueryThrows_ReturnsOpenIssueRefs()
    {
        // AC 3c: exception from ListClosedIssuesAsync still returns open scan issue references.
        var executor = CreateExecutor();
        // TODO [WARNING]: The "*<footer>.*" pattern works only because IsScanIssue uses Contains and the
        // footer is still a substring. The literal * characters are not wildcards. Use GeneratedIssueFooter
        // directly (or a string that simply contains it) to make the fixture's intent unambiguous.
        var scanFooter = $"*{RefactoringExecutor.GeneratedIssueFooter}.*";

        // FormatIssueReference is a DIM — must be set up explicitly on the Moq mock.
        _mockIssueProvider
            .Setup(x => x.FormatIssueReference(It.IsAny<IssueIdentifier>()))
            .Returns((IssueIdentifier id) => $"#{id}");

        var openIssues = new List<IssueSummary>
        {
            new() { Identifier = "5", Title = "Open scan issue", Labels = ["agent:generated"], Description = scanFooter }
        };

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API failure"));

        var result = await executor.CollectScanIssueReferencesAsync(
            _mockIssueProvider.Object, openIssues, TimeSpan.FromDays(90), CancellationToken.None);

        result.Should().Contain("#5");
    }

    // ── ListOpenIssuesAsync called once per ExecuteAsync (issue #3540) ────────────────────────

    [Fact]
    public async Task ExecuteAsync_ListOpenIssuesAsync_IsCalledOnce()
    {
        // AC 4: CollectScanIssueReferencesAsync reuses the open issues fetched by TryBuildIssueContextAsync;
        // ListOpenIssuesAsync must be called exactly once per ExecuteAsync run.
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // ListClosedIssuesAsync is set up in the constructor with an empty PagedResult so both
        // CollectScanIssueReferencesAsync and TryBuildOutcomeContextAsync exercise the happy path.
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        _mockIssueProvider.Verify(
            x => x.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── WriteHotspotAnalysisAsync: predicate path with real git repo (issue #3540) ──────────

    [Fact]
    public async Task ExecuteAsync_WithScanIssueReferences_HotspotFileContainsOnlyNonExcludedCommits()
    {
        // Exercises the isExcludedCommit predicate code path inside WriteHotspotAnalysisAsync
        // (lines that require a real git repo to reach). A scan issue reference "#42" is returned
        // by CollectScanIssueReferencesAsync; a commit with "(#42)" in its subject must be excluded
        // from the hotspot output, while a commit without it must be included.
        var executor = CreateExecutor();
        var job = CreateJob();

        // Arrange: the clone callback initialises a real git repo with two commits.
        // Commit 1 — subject references the scan issue and should be excluded.
        // Commit 2 — ordinary subject, its files should appear in the hotspot file.
        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspacePath, CancellationToken>((workspacePath, _) =>
            {
                // Initialise git repo so WriteHotspotAnalysisAsync can run git log successfully.
                RunGit(workspacePath, "init");
                RunGit(workspacePath, "config user.email \"test@example.com\"");
                RunGit(workspacePath, "config user.name \"Test\"");

                // Commit 1: the scan commit — should be excluded.
                var scanFile = Path.Combine(workspacePath, "scan-file.cs");
                File.WriteAllText(scanFile, "// scan");
                RunGit(workspacePath, "add scan-file.cs");
                RunGit(workspacePath, "commit --allow-empty -m \"Refactoring scan (#42)\"");

                // Commit 2: a normal commit — files should appear in the hotspot list.
                var normalFile = Path.Combine(workspacePath, "important.cs");
                File.WriteAllText(normalFile, "// important");
                RunGit(workspacePath, "add important.cs");
                RunGit(workspacePath, "commit -m \"Normal feature commit\"");
            })
            .Returns(Task.CompletedTask);

        // CollectScanIssueReferencesAsync will return "#42" from the closed issues page.
        var scanFooter = RefactoringExecutor.GeneratedIssueFooter;
        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items =
                [
                    new IssueSummary
                    {
                        Identifier = "42",
                        Title = "Scan issue",
                        Labels = ["agent:generated"],
                        Description = $"Some description. {scanFooter}"
                    }
                ],
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(
            job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert: the hotspot file should exist and contain the normal file but not the scan file.
        var hotspotPath = Path.Combine(_tempDir, job.JobId, ".agent", "hotspot-analysis.txt");
        File.Exists(hotspotPath).Should().BeTrue("WriteHotspotAnalysisAsync should produce the hotspot file");

        var content = await File.ReadAllTextAsync(hotspotPath);
        content.Should().Contain("important.cs", "the normal commit's file should appear in the hotspot analysis");
        content.Should().NotContain("scan-file.cs", "the scan commit's file should be excluded by the isExcludedCommit predicate");
    }

    /// <summary>Runs a git command in the given directory; throws on non-zero exit code.</summary>
    private static void RunGit(string workingDir, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {arguments} failed: {process.StandardError.ReadToEnd()}");
    }
}
