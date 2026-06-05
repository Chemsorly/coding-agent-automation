using AwesomeAssertions;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests.Executors;

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
            PipelineConfiguration = new PipelineConfiguration()
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
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((path, _) =>
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
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((path, _) =>
            {
                var agentDir = Path.Combine(path, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(Path.Combine(agentDir, "refactoring-proposals.json"), proposalsJson);
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

        var refactoringIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "100", Title = "Extract retry logic", Labels = ["agent:generated"], CreatedAt = DateTime.UtcNow.AddDays(-5) }],
            Page = 1, PageSize = 30, HasMore = false
        };
        var allIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "200", Title = "Add caching layer", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-2) }],
            Page = 1, PageSize = 50, HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 30, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refactoringIssues);
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 50, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(allIssues);

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        capturedPrompt.Should().Contain("Extract retry logic");
        capturedPrompt.Should().Contain("Add caching layer");
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
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
    public async Task ExecuteAsync_IssueQueryReturnsOldIssues_FilteredOut()
    {
        // Arrange
        var executor = CreateExecutor();
        var job = CreateJob();

        var emptyRefactoring = new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 30, HasMore = false };
        var oldIssues = new PagedResult<IssueSummary>
        {
            Items = [new IssueSummary { Identifier = "50", Title = "Old issue", Labels = [], CreatedAt = DateTime.UtcNow.AddDays(-60) }],
            Page = 1, PageSize = 50, HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 30, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyRefactoring);
        _mockIssueProvider
            .Setup(x => x.ListOpenIssuesAsync(1, 50, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldIssues);

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? capturedPrompt = null;
        _mockAgentProvider
            .Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        // Act
        await executor.ExecuteAsync(job, _mockRepoProvider.Object, null, _mockIssueProvider.Object, _mockAgentProvider.Object, CancellationToken.None);

        // Assert — old issue should be filtered out, no issue context in prompt
        capturedPrompt.Should().NotContain("Old issue");
        capturedPrompt.Should().NotContain("Do Not Duplicate");
    }

    [Fact]
    public async Task ExecuteAsync_ClosedIssueQueryFails_ContinuesWithoutContext()
    {
        var executor = CreateExecutor();
        var job = CreateJob();

        _mockRepoProvider
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(x => x.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var closedIssues = new PagedResult<IssueSummary>
        {
            Items = new[]
            {
                new IssueSummary { Identifier = "100", Title = "Implemented refactoring", Labels = new[] { "agent:generated", "agent:done" } },
                new IssueSummary { Identifier = "101", Title = "Rejected refactoring", Labels = new[] { "agent:generated", "agent:wont-do" } }
            },
            Page = 1,
            PageSize = 20,
            HasMore = false
        };

        _mockIssueProvider
            .Setup(x => x.ListClosedIssuesAsync(1, 20,
                It.Is<IReadOnlyList<string>>(l => l.Contains("agent:generated")),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(closedIssues);

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
        capturedRequest.Prompt.Should().Contain("Do NOT propose refactorings similar to rejected items above.");
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
        body.Should().Contain("## Summary");
        body.Should().Contain("## Affected Components");
    }

    [Fact]
    public void ParseHotspotOutput_WithValidOutput_ReturnsFormattedSummary()
    {
        var gitOutput = "src/File1.cs\nsrc/File2.cs\nsrc/File1.cs\nsrc/File1.cs\nsrc/File2.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90));

        result.Should().NotBeNull();
        result.Should().Contain("3 changes — src/File1.cs");
        result.Should().Contain("2 changes — src/File2.cs");
        result.Should().Contain("last 90 days");
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
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 50; i++)
            sb.AppendLine($"src/File{i:D2}.cs");

        var result = RefactoringExecutor.ParseHotspotOutput(sb.ToString(), TimeSpan.FromDays(90));

        result.Should().NotBeNull();
        // Each file appears once, so 30 lines of "1 changes — ..."
        var lines = result!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes —")).ToList();
        lines.Should().HaveCount(30);
    }

    [Fact]
    public void ParseHotspotOutput_SortsDescendingByCount()
    {
        var gitOutput = "src/Rare.cs\nsrc/Common.cs\nsrc/Common.cs\nsrc/Common.cs\nsrc/Medium.cs\nsrc/Medium.cs\n";

        var result = RefactoringExecutor.ParseHotspotOutput(gitOutput, TimeSpan.FromDays(90));

        result.Should().NotBeNull();
        var lines = result!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("changes —")).ToList();
        lines[0].Should().Contain("src/Common.cs");
        lines[1].Should().Contain("src/Medium.cs");
        lines[2].Should().Contain("src/Rare.cs");
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
}
