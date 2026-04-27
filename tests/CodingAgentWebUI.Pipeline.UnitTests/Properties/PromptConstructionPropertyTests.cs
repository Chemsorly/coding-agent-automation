using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for prompt construction.
/// </summary>
public class PromptConstructionPropertyTests
{
    private static readonly string DefaultImpl = PipelineConfiguration.DefaultImplementationPrompt;
    private static readonly string DefaultAnalysis = PipelineConfiguration.DefaultAnalysisPrompt;

    /// <summary>
    /// Prompt contains the issue title and acceptance criteria inline,
    /// but references the issue context file instead of inlining description.
    /// </summary>
    [Property(MaxTest = 20)]
    public void Prompt_ContainsTitleAndCriteriaButReferencesFile(NonEmptyString title, NonEmptyString description, byte criteriaCount)
    {
        var count = criteriaCount % 6;
        var criteriaOptions = new[] { "Must compile", "Tests pass", "Coverage above 80%", "No security issues", "Handles edge cases" };
        var criteria = criteriaOptions.Take(count).ToList();

        var issue = new IssueDetail
        {
            Identifier = "1",
            Title = title.Get,
            Description = description.Get,
            Labels = Array.Empty<string>()
        };

        var parsed = new ParsedIssue
        {
            RequirementsSection = issue.Description,
            AcceptanceCriteria = criteria.AsReadOnly()
        };

        var prompt = PromptBuilder.BuildPrompt(DefaultImpl, issue, parsed);

        prompt.Should().Contain(issue.Title);
        prompt.Should().Contain(issue.Identifier);
        prompt.Should().Contain(PromptBuilder.IssueContextFilePath);

        foreach (var criterion in parsed.AcceptanceCriteria)
        {
            prompt.Should().Contain(criterion);
        }
    }

    /// <summary>
    /// Prompt does not contain the issue description or comments inline — they are in the file.
    /// </summary>
    [Fact]
    public void Prompt_DoesNotContainDescriptionOrComments()
    {
        var issue = new IssueDetail
        {
            Identifier = "1",
            Title = "Test",
            Description = "Unique-description-content-xyz",
            Labels = Array.Empty<string>()
        };

        var parsed = new ParsedIssue
        {
            RequirementsSection = "Unique-description-content-xyz",
            AcceptanceCriteria = Array.Empty<string>()
        };

        var prompt = PromptBuilder.BuildPrompt(DefaultImpl, issue, parsed);

        prompt.Should().NotContain("Unique-description-content-xyz");
        prompt.Should().NotContain("## Comments");
        prompt.Should().NotContain("## Description");
        prompt.Should().NotContain("## Requirements");
    }

    /// <summary>
    /// Issue context file content includes all comment bodies and author attributions.
    /// </summary>
    [Property(MaxTest = 50)]
    public void IssueContextFile_ContainsAllCommentBodiesAndAuthors(NonEmptyString title, NonEmptyString description, byte commentCount)
    {
        var count = (commentCount % 4) + 1; // 1-4 comments
        var comments = Enumerable.Range(0, count).Select(i => new IssueComment
        {
            Id = i.ToString(),
            Body = $"Comment body {i}",
            Author = $"user{i}",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)
        }).ToList().AsReadOnly();

        var issue = new IssueDetail
        {
            Identifier = "1",
            Title = title.Get,
            Description = description.Get,
            Labels = Array.Empty<string>()
        };

        var parsed = new ParsedIssue
        {
            RequirementsSection = issue.Description,
            AcceptanceCriteria = Array.Empty<string>()
        };

        var content = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments);

        content.Should().Contain("## Comments");
        foreach (var comment in comments)
        {
            content.Should().Contain(comment.Body);
            content.Should().Contain($"@{comment.Author}");
        }
    }

    /// <summary>
    /// Issue context file omits the Comments section when no comments are provided.
    /// </summary>
    [Fact]
    public void IssueContextFile_OmitsCommentsSection_WhenNoComments()
    {
        var issue = new IssueDetail
        {
            Identifier = "1",
            Title = "Test",
            Description = "Desc",
            Labels = Array.Empty<string>()
        };

        var parsed = new ParsedIssue
        {
            RequirementsSection = "Desc",
            AcceptanceCriteria = Array.Empty<string>()
        };

        var content = PromptBuilder.BuildIssueContextFileContent(issue, parsed);

        content.Should().NotContain("## Comments");
    }

    /// <summary>
    /// Agent analysis comments are excluded from the issue context file.
    /// </summary>
    [Fact]
    public void IssueContextFile_ExcludesAgentAnalysisComments()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = "Please also handle edge cases", Author = "alice", CreatedAt = DateTime.UtcNow },
            new() { Id = "2", Body = "## 🤖 Agent Analysis\n\nPlanned approach...", Author = "bot", CreatedAt = DateTime.UtcNow },
            new() { Id = "3", Body = "Looks good, one more thing", Author = "bob", CreatedAt = DateTime.UtcNow },
        }.AsReadOnly();

        var issue = new IssueDetail
        {
            Identifier = "1", Title = "Test", Description = "Desc",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue { RequirementsSection = "Desc", AcceptanceCriteria = Array.Empty<string>() };

        var content = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments);

        content.Should().Contain("@alice");
        content.Should().Contain("@bob");
        content.Should().NotContain("@bot");
        content.Should().NotContain("Agent Analysis");
    }

    /// <summary>
    /// Only the last 10 comments are included in the issue context file.
    /// </summary>
    [Fact]
    public void IssueContextFile_LimitsToLast10Comments()
    {
        var comments = Enumerable.Range(0, 15).Select(i => new IssueComment
        {
            Id = i.ToString(),
            Body = $"Unique-comment-body-{i:D3}",
            Author = $"author-{i:D3}",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)
        }).ToList().AsReadOnly();

        var issue = new IssueDetail
        {
            Identifier = "1", Title = "Test", Description = "Desc",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue { RequirementsSection = "Desc", AcceptanceCriteria = Array.Empty<string>() };

        var content = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments);

        // First 5 should be excluded, last 10 included
        for (var i = 0; i < 5; i++)
            content.Should().NotContain($"Unique-comment-body-{i:D3}");
        for (var i = 5; i < 15; i++)
            content.Should().Contain($"Unique-comment-body-{i:D3}");
    }

    /// <summary>
    /// Issue context file contains all issue fields (title, description, requirements, criteria).
    /// </summary>
    [Fact]
    public void IssueContextFile_ContainsAllIssueFields()
    {
        var issue = new IssueDetail
        {
            Identifier = "42", Title = "Add caching layer", Description = "We need Redis caching",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue
        {
            RequirementsSection = "We need Redis caching",
            AcceptanceCriteria = new[] { "Cache hit rate > 90%" }.ToList().AsReadOnly()
        };

        var content = PromptBuilder.BuildIssueContextFileContent(issue, parsed);

        content.Should().Contain("Add caching layer");
        content.Should().Contain("We need Redis caching");
        content.Should().Contain("Cache hit rate > 90%");
        content.Should().Contain("## Description");
    }

    // --- BuildAnalysisPrompt tests ---

    /// <summary>
    /// Analysis prompt contains the configurable instructions, issue title, acceptance criteria,
    /// and references the issue context file.
    /// </summary>
    [Fact]
    public void AnalysisPrompt_ContainsConfigurableInstructionsAndIssueFields()
    {
        var issue = new IssueDetail
        {
            Identifier = "42", Title = "Add caching layer", Description = "We need Redis caching",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue
        {
            RequirementsSection = "We need Redis caching",
            AcceptanceCriteria = new[] { "Cache hit rate > 90%" }.ToList().AsReadOnly()
        };

        var prompt = PromptBuilder.BuildAnalysisPrompt(DefaultAnalysis, issue, parsed);

        prompt.Should().Contain("Planned Approach");
        prompt.Should().Contain("Test Coverage");
        prompt.Should().Contain("Add caching layer");
        prompt.Should().Contain("Cache hit rate > 90%");
        prompt.Should().Contain(PromptBuilder.IssueContextFilePath);
    }

    /// <summary>
    /// Analysis prompt always includes pipeline mechanics regardless of configurable text.
    /// </summary>
    [Fact]
    public void AnalysisPrompt_IncludesPipelineMechanics()
    {
        var issue = new IssueDetail
        {
            Identifier = "1", Title = "Test", Description = "Desc",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue { RequirementsSection = "Desc", AcceptanceCriteria = Array.Empty<string>() };

        var prompt = PromptBuilder.BuildAnalysisPrompt("Custom analysis instructions", issue, parsed);

        prompt.Should().Contain("Custom analysis instructions");
        prompt.Should().Contain("Do NOT implement any changes");
        prompt.Should().Contain(PromptBuilder.AnalysisFilePath);
        prompt.Should().Contain("sub-agents");
    }

    /// <summary>
    /// Implementation prompt includes pipeline mechanics (git prohibition, analysis reference).
    /// </summary>
    [Fact]
    public void ImplementationPrompt_IncludesPipelineMechanics()
    {
        var issue = new IssueDetail
        {
            Identifier = "1", Title = "Test", Description = "Desc",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue { RequirementsSection = "Desc", AcceptanceCriteria = Array.Empty<string>() };

        var prompt = PromptBuilder.BuildPrompt("Custom impl instructions", issue, parsed);

        prompt.Should().Contain("Custom impl instructions");
        prompt.Should().Contain("Do NOT run git write commands");
        prompt.Should().Contain(PromptBuilder.AnalysisFilePath);
        prompt.Should().Contain("Implement these changes now.");
    }

    /// <summary>
    /// Implementation prompt no longer contains the file-write-retry workaround.
    /// </summary>
    [Fact]
    public void ImplementationPrompt_DoesNotContainFileWriteRetryWorkaround()
    {
        var issue = new IssueDetail
        {
            Identifier = "1", Title = "Test", Description = "Desc",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue { RequirementsSection = "Desc", AcceptanceCriteria = Array.Empty<string>() };

        var prompt = PromptBuilder.BuildPrompt(DefaultImpl, issue, parsed);

        prompt.Should().NotContain("file write is rejected");
    }

    /// <summary>
    /// Prompts reference brain context file when brainContextWritten is true.
    /// </summary>
    [Fact]
    public void Prompt_ReferencesBrainContextFile_WhenBrainContextWritten()
    {
        var issue = new IssueDetail
        {
            Identifier = "1", Title = "Test", Description = "Desc",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue { RequirementsSection = "Desc", AcceptanceCriteria = Array.Empty<string>() };

        var analysisPrompt = PromptBuilder.BuildAnalysisPrompt(DefaultAnalysis, issue, parsed, brainContextWritten: true);
        var implPrompt = PromptBuilder.BuildPrompt(DefaultImpl, issue, parsed, brainContextWritten: true);

        analysisPrompt.Should().Contain(PromptBuilder.BrainContextFilePath);
        implPrompt.Should().Contain(PromptBuilder.BrainContextFilePath);
    }

    /// <summary>
    /// Prompts omit brain context file reference when brainContextWritten is false.
    /// </summary>
    [Fact]
    public void Prompt_OmitsBrainContextFileReference_WhenNotWritten()
    {
        var issue = new IssueDetail
        {
            Identifier = "1", Title = "Test", Description = "Desc",
            Labels = Array.Empty<string>()
        };
        var parsed = new ParsedIssue { RequirementsSection = "Desc", AcceptanceCriteria = Array.Empty<string>() };

        var analysisPrompt = PromptBuilder.BuildAnalysisPrompt(DefaultAnalysis, issue, parsed);
        var implPrompt = PromptBuilder.BuildPrompt(DefaultImpl, issue, parsed);

        analysisPrompt.Should().NotContain(PromptBuilder.BrainContextFilePath);
        implPrompt.Should().NotContain(PromptBuilder.BrainContextFilePath);
    }

    // --- BuildFixPrompt tests ---

    /// <summary>
    /// BuildFixPrompt references the review findings file instead of inlining content.
    /// </summary>
    [Fact]
    public void BuildFixPrompt_ReferencesReviewFindingsFile()
    {
        var prompt = PromptBuilder.BuildFixPrompt("Fix the issues.");

        prompt.Should().Contain(PromptBuilder.ReviewFindingsFilePath);
        prompt.Should().Contain("Read the file");
        prompt.Should().Contain("[CRITICAL]");
    }

    /// <summary>
    /// BuildFixPrompt includes the configurable fix instructions.
    /// </summary>
    [Fact]
    public void BuildFixPrompt_IncludesFixInstructions()
    {
        var prompt = PromptBuilder.BuildFixPrompt("Custom fix instructions here.");

        prompt.Should().Contain("Custom fix instructions here.");
        prompt.Should().Contain("Do NOT run git write commands");
    }

    /// <summary>
    /// BuildFixPrompt does not inline any raw findings content.
    /// </summary>
    [Fact]
    public void BuildFixPrompt_DoesNotContainReviewFindingsHeader()
    {
        var prompt = PromptBuilder.BuildFixPrompt("Fix the issues.");

        prompt.Should().NotContain("## Review Findings");
    }

    // --- QualityGatesOutputDirectory constant tests ---

    [Fact]
    public void QualityGatesOutputDirectory_IsKiroSubdirectory()
    {
        PromptBuilder.QualityGatesOutputDirectory.Should().StartWith(".kiro/");
    }

    // --- BuildQualityGateRetryPrompt tests ---

    [Fact]
    public void BuildQualityGateRetryPrompt_IncludesAllGatesWithStatus()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build failed with exit code 1. 3 error(s), 2 warning(s)." },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "Tests passed: 42 passed, 0 failed, 0 skipped" },
            Coverage = new GateResult { GateName = "Coverage", Passed = true, Details = "Coverage 85.0% meets threshold 40.0%" }
        };

        var prompt = QualityGateOrchestrator.BuildQualityGateRetryPrompt(report, 1, 3);

        prompt.Should().Contain("Quality gates failed (attempt 1/3):");
        prompt.Should().Contain("- Compilation: FAILED");
        prompt.Should().Contain("- Tests: PASSED");
        prompt.Should().Contain("- Coverage: PASSED");
        prompt.Should().Contain(PromptBuilder.QualityGatesOutputDirectory);
        prompt.Should().Contain("List the files there and read the relevant ones");
    }

    [Fact]
    public void BuildQualityGateRetryPrompt_OmitsNullOptionalGates()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
            Tests = new GateResult { GateName = "Tests", Passed = false, Details = "Tests failed: 10 passed, 2 failed, 0 skipped." }
        };

        var prompt = QualityGateOrchestrator.BuildQualityGateRetryPrompt(report, 2, 3);

        prompt.Should().Contain("- Compilation: PASSED");
        prompt.Should().Contain("- Tests: FAILED");
        prompt.Should().NotContain("Coverage");
        prompt.Should().NotContain("Security");
        prompt.Should().NotContain("External CI");
    }
}
