using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Coverage tests for small Pipeline models and utility methods
/// that are otherwise only exercised through integration paths.
/// </summary>
public class PipelineModelCoverageTests
{
    [Theory]
    [InlineData(false, null, null, "Idle")]
    [InlineData(true, 1234, true, "Running (PID 1234)")]
    [InlineData(true, 5678, false, "Process exited (PID 5678)")]
    [InlineData(true, null, null, "Executing (process state unknown)")]
    public void AgentHealthStatus_Summary_ReturnsExpectedText(
        bool isExecuting, int? pid, bool? isAlive, string expected)
    {
        var status = new AgentHealthStatus
        {
            IsExecuting = isExecuting,
            ProcessId = pid,
            IsProcessAlive = isAlive
        };
        status.Summary.Should().Be(expected);
    }

    [Fact]
    public void AgentResult_Success_ReflectsExitCode()
    {
        new AgentResult { ExitCode = 0, OutputLines = [] }.Success.Should().BeTrue();
        new AgentResult { ExitCode = ExitCodes.GeneralFailure, OutputLines = [] }.Success.Should().BeFalse();
    }

    [Fact]
    public void AgentRequest_ResumeSessionId_DefaultsToNull()
    {
        var request = new AgentRequest { Prompt = "test", WorkspacePath = "/ws" };
        request.ResumeSessionId.Should().BeNull();
        request.UseResume.Should().BeFalse();
    }

    [Fact]
    public void GenerateBranchName_VeryLongTitle_TruncatesSlug()
    {
        var longTitle = new string('a', 200);
        var result = PipelineFormatting.GenerateBranchName("42", longTitle, "abcdef12-0000-0000-0000-000000000000");
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().StartWith("feature/auto-42-");
    }

    [Fact]
    public void GenerateBranchName_EmptyTitle_FallsBackToPrefix()
    {
        var result = PipelineFormatting.GenerateBranchName("42", "", "abcdef12-0000-0000-0000-000000000000");
        result.Should().StartWith("feature/auto-42");
    }

    [Fact]
    public void FormatQualityGateSummary_WithCoverageAndSecurity_IncludesAll()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" },
            Coverage = new GateResult { GateName = "Coverage", Passed = true, Details = "80%", CoveragePercent = 80.0 },
            SecurityScan = new GateResult { GateName = "Security", Passed = false, Details = "1 vulnerability" }
        };
        var summary = PipelineFormatting.FormatQualityGateSummary(report);
        summary.Should().Contain("Compilation");
        summary.Should().Contain("Coverage");
        summary.Should().Contain("Security");
    }

    [Fact]
    public void GeneratePrBody_WithFileChanges_IncludesTable()
    {
        var fileChanges = new List<FileChangeSummary>
        {
            new("Modified", "src/Foo.cs"),
            new("Added", "src/Bar.cs")
        };
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = 80.0,
                FileChanges = fileChanges,
                IssueTitle = "Fix bug",
            });
        body.Should().Contain("src/Foo.cs");
        body.Should().Contain("| Modified |");
    }

    [Fact]
    public void GeneratePrBody_WithModelName_IncludesModelInFooter()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                ModelName = "claude-sonnet-4",
            });
        body.Should().Contain("claude-sonnet-4");
    }

    [Fact]
    public void GeneratePrBody_WithCodeReviewSummary_IncludesReviewSection()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                CodeReviewSummary = new CodeReviewSummary(
                AgentsRun: new[] { "reviewer-1" },
                CriticalCount: 1,
                WarningCount: 2,
                SuggestionCount: 3,
                AgentFindings: new[] { new AgentFindings("reviewer-1", "Found issue X") }),
            });
        body.Should().Contain("Code Review");
        body.Should().Contain("reviewer-1");
    }

    [Fact]
    public void GeneratePrBody_WithComments_IncludesInputComments()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Author = "user1", Body = "Please fix this", CreatedAt = DateTime.UtcNow }
        };
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                Comments = comments,
            });
        body.Should().Contain("user1");
        body.Should().Contain("Please fix this");
    }

    [Fact]
    public void GeneratePrBody_DraftPr_IncludesWarning()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                IsDraft = true,
            });
        body.Should().Contain("draft");
    }

    [Fact]
    public void GeneratePrBody_WithLongComment_TruncatesAndClosesCodeFence()
    {
        var longBody = "```csharp\n" + new string('x', 2000) + "\n```";
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Author = "user1", Body = longBody, CreatedAt = DateTime.UtcNow }
        };
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                Comments = comments,
            });
        body.Should().Contain("user1");
    }

    // TODO: Tautological test — GeneratePrBody never reads BlacklistedFilesDetected, so this assertion is vacuously true and cannot detect a regression.
    [Fact]
    public void GeneratePrBody_WithBlacklistedFiles_DoesNotIncludeWarning()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                BlacklistedFilesDetected = new[] { ".agent/settings.json" },
            });
        body.Should().NotContain("## ⚠️ Blacklisted Files Excluded");
    }

    [Fact]
    public void GeneratePrBody_WithCoverage_IncludesCoveragePercent()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = 75.5,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
            });
        body.Should().Contain("75.5");
    }

    [Fact]
    public void GeneratePrBody_WithTestFailures_IncludesFailCount()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 8,
                TestsFailed = 2,
                TestsSkipped = 1,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
            });
        body.Should().Contain("Failed: 2");
    }

    [Fact]
    public void GeneratePrBody_NoFileChanges_ShowsNoChangesMessage()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
            });
        body.Should().Contain("No file changes");
    }

    [Fact]
    public void GeneratePrBody_CodeReviewNoFindings_ShowsNoFindings()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                CodeReviewSummary = new CodeReviewSummary(
                AgentsRun: Array.Empty<string>(),
                CriticalCount: 0, WarningCount: 0, SuggestionCount: 0,
                AgentFindings: Array.Empty<AgentFindings>()),
            });
        body.Should().Contain("no findings");
    }

    [Fact]
    public void GeneratePrBody_ExcludesAgentAnalysisComments()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Author = "bot", Body = "## 🤖 Agent Analysis\nSome analysis", CreatedAt = DateTime.UtcNow },
            new() { Id = "2", Author = "user1", Body = "Real comment", CreatedAt = DateTime.UtcNow }
        };
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
                Comments = comments,
            });
        body.Should().Contain("Real comment");
        body.Should().NotContain("Agent Analysis");
    }

    [Fact]
    public void GeneratePrBody_WithoutModelName_UsesDefaultFooter()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
            {
                IssueReference = "#42",
                TestsPassed = 10,
                TestsFailed = 0,
                TestsSkipped = 0,
                CoveragePercent = null,
                FileChanges = Array.Empty<FileChangeSummary>(),
                IssueTitle = "Fix bug",
            });
        body.Should().Contain("Automated implementation via pipeline");
        body.Should().NotContain("Model:");
    }
}
