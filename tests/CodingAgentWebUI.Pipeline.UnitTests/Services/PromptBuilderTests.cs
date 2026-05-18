using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="PromptBuilder"/>.
/// </summary>
public class PromptBuilderTests
{
    private static IssueDetail CreateIssue(string id = "42", string title = "Add feature X",
        string description = "Implement feature X as described.") => new()
    {
        Identifier = id,
        Title = title,
        Description = description,
        Labels = new[] { "enhancement" }
    };

    private static ParsedIssue CreateParsedIssue(string requirements = "Build the thing",
        IReadOnlyList<string>? criteria = null) => new()
    {
        RequirementsSection = requirements,
        AcceptanceCriteria = criteria ?? new[] { "It compiles", "Tests pass" }
    };

    #region BuildAnalysisPrompt

    [Fact]
    public void BuildAnalysisPrompt_ContainsInstructions()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Analyze carefully", CreateIssue(), CreateParsedIssue());
        result.Should().StartWith("Analyze carefully");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsDoNotImplementWarning()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain("Do NOT implement any changes");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsAnalysisFilePath()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain(PromptBuilder.AnalysisFilePath);
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsAssessmentFilePath()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain(PromptBuilder.AnalysisAssessmentFilePath);
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsIssueTitle()
    {
        var issue = CreateIssue(title: "Fix login bug");
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", issue, CreateParsedIssue());
        result.Should().Contain("Fix login bug");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsAcceptanceCriteria()
    {
        var parsed = CreateParsedIssue(criteria: new[] { "Users can log in", "Session persists" });
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), parsed);
        result.Should().Contain("- Users can log in");
        result.Should().Contain("- Session persists");
    }

    [Fact]
    public void BuildAnalysisPrompt_WithBrainContext_ContainsBrainReference()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), CreateParsedIssue(),
            brainContextWritten: true);
        result.Should().Contain(PromptBuilder.BrainContextFilePath);
    }

    [Fact]
    public void BuildAnalysisPrompt_WithoutBrainContext_OmitsBrainReference()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), CreateParsedIssue(),
            brainContextWritten: false);
        result.Should().NotContain("Project knowledge and conventions are at");
    }

    [Fact]
    public void BuildAnalysisPrompt_NullInstructions_Throws()
    {
        var act = () => PromptBuilder.BuildAnalysisPrompt(null!, CreateIssue(), CreateParsedIssue());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildAnalysisPrompt_NullIssue_Throws()
    {
        var act = () => PromptBuilder.BuildAnalysisPrompt("Instructions", null!, CreateParsedIssue());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildAnalysisPrompt_NullParsed_Throws()
    {
        var act = () => PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsRecommendationOptions()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain("\"ready\"");
        result.Should().Contain("\"not_ready\"");
        result.Should().Contain("\"wont_do\"");
    }

    #endregion

    #region BuildPrompt

    [Fact]
    public void BuildPrompt_ContainsImplementationInstructions()
    {
        var result = PromptBuilder.BuildPrompt("Implement now", CreateIssue(), CreateParsedIssue());
        result.Should().StartWith("Implement now");
    }

    [Fact]
    public void BuildPrompt_ContainsGitRestriction()
    {
        var result = PromptBuilder.BuildPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain("Do NOT run git write commands");
    }

    [Fact]
    public void BuildPrompt_ContainsAnalysisFileReference()
    {
        var result = PromptBuilder.BuildPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain(PromptBuilder.AnalysisFilePath);
    }

    [Fact]
    public void BuildPrompt_ContainsIssueContext()
    {
        var issue = CreateIssue(id: "99", title: "Refactor DB layer");
        var result = PromptBuilder.BuildPrompt("Instructions", issue, CreateParsedIssue());
        result.Should().Contain("Issue #99: Refactor DB layer");
    }

    [Fact]
    public void BuildPrompt_WithBrainWriteInstructions_AppendsAtEnd()
    {
        var result = PromptBuilder.BuildPrompt("Instructions", CreateIssue(), CreateParsedIssue(),
            brainWriteInstructions: "Write lessons to .brain/");
        result.Should().Contain("Write lessons to .brain/");
    }

    [Fact]
    public void BuildPrompt_WithBrainContext_ContainsBrainReference()
    {
        var result = PromptBuilder.BuildPrompt("Instructions", CreateIssue(), CreateParsedIssue(),
            brainContextWritten: true);
        result.Should().Contain(PromptBuilder.BrainContextFilePath);
    }

    [Fact]
    public void BuildPrompt_NullInstructions_Throws()
    {
        var act = () => PromptBuilder.BuildPrompt(null!, CreateIssue(), CreateParsedIssue());
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region BuildReviewPrompt

    [Fact]
    public void BuildReviewPrompt_ContainsReviewInstructions()
    {
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("TestAgent");
        var result = PromptBuilder.BuildReviewPrompt("Review this code", CreateIssue(), CreateParsedIssue(), findingsPath);
        result.Should().StartWith("Review this code");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsAgentSpecificFindingsFilePath()
    {
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("Correctness");
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath);
        result.Should().Contain(findingsPath);
        result.Should().Contain(".agent/review-findings-correctness.md");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsGitRestriction()
    {
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("TestAgent");
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath);
        result.Should().Contain("Do NOT run git write commands");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsIssueContext()
    {
        var issue = CreateIssue(title: "Add caching");
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("TestAgent");
        var result = PromptBuilder.BuildReviewPrompt("Review", issue, CreateParsedIssue(), findingsPath);
        result.Should().Contain("Add caching");
    }

    [Fact]
    public void BuildReviewPrompt_NullInstructions_Throws()
    {
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("TestAgent");
        var act = () => PromptBuilder.BuildReviewPrompt(null!, CreateIssue(), CreateParsedIssue(), findingsPath);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildReviewPrompt_NullFindingsFilePath_Throws()
    {
        var act = () => PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildReviewPrompt_Isolated_ContainsIndependentReviewerFraming()
    {
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("TestAgent");
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath, isolated: true);
        result.Should().Contain("reviewing code changes made by another agent");
    }

    [Fact]
    public void BuildReviewPrompt_Isolated_ContainsGitDiffInstruction()
    {
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("TestAgent");
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath, isolated: true);
        result.Should().Contain("git diff");
    }

    [Fact]
    public void BuildReviewPrompt_NotIsolated_NoIsolationFraming()
    {
        var findingsPath = PromptBuilder.GetReviewFindingsFilePath("TestAgent");
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath, isolated: false);
        result.Should().NotContain("reviewing code changes made by another agent");
    }

    #endregion

    #region GetReviewFindingsFilePath

    [Fact]
    public void GetReviewFindingsFilePath_ReturnsExpectedFormat()
    {
        var result = PromptBuilder.GetReviewFindingsFilePath("Correctness");
        result.Should().Be(".agent/review-findings-correctness.md");
    }

    [Fact]
    public void GetReviewFindingsFilePath_SanitizesSpaces()
    {
        var result = PromptBuilder.GetReviewFindingsFilePath("DotNet Specialist");
        result.Should().Be(".agent/review-findings-dotnet-specialist.md");
    }

    [Fact]
    public void GetReviewFindingsFilePath_SanitizesPathSeparators()
    {
        var result = PromptBuilder.GetReviewFindingsFilePath("Agent/Sub\\Name");
        result.Should().Be(".agent/review-findings-agent-sub-name.md");
    }

    [Fact]
    public void GetReviewFindingsFilePath_NullName_Throws()
    {
        var act = () => PromptBuilder.GetReviewFindingsFilePath(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region BuildFixPrompt

    [Fact]
    public void BuildFixPrompt_ContainsFixInstructions()
    {
        var result = PromptBuilder.BuildFixPrompt("Fix the critical issues");
        result.Should().StartWith("Fix the critical issues");
    }

    [Fact]
    public void BuildFixPrompt_ContainsReviewFindingsReference()
    {
        var result = PromptBuilder.BuildFixPrompt("Fix");
        result.Should().Contain(PromptBuilder.ReviewFindingsFilePath);
    }

    [Fact]
    public void BuildFixPrompt_ContainsCriticalOnlyInstruction()
    {
        var result = PromptBuilder.BuildFixPrompt("Fix");
        result.Should().Contain("[CRITICAL]");
    }

    [Fact]
    public void BuildFixPrompt_NullInstructions_Throws()
    {
        var act = () => PromptBuilder.BuildFixPrompt(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region BuildCleanupPrompt

    [Fact]
    public void BuildCleanupPrompt_ContainsCleanupHeader()
    {
        var result = PromptBuilder.BuildCleanupPrompt();
        result.Should().Contain("Pre-Pull Request Cleanup");
    }

    [Fact]
    public void BuildCleanupPrompt_ContainsNoFunctionalChangesWarning()
    {
        var result = PromptBuilder.BuildCleanupPrompt();
        result.Should().Contain("Do NOT make functional changes");
    }

    [Fact]
    public void BuildCleanupPrompt_ContainsGitRestriction()
    {
        var result = PromptBuilder.BuildCleanupPrompt();
        result.Should().Contain("Do NOT run git write commands");
    }

    #endregion

    #region BuildReworkPrompt

    [Fact]
    public void BuildReworkPrompt_NoConflictsNoCommentsNotDraft_ReturnsNull()
    {
        var result = PromptBuilder.BuildReworkPrompt(
            Array.Empty<string>(),
            Array.Empty<PullRequestReviewComment>(),
            isDraft: false);
        result.Should().BeNull();
    }

    [Fact]
    public void BuildReworkPrompt_WithConflictFiles_ContainsConflictSection()
    {
        var result = PromptBuilder.BuildReworkPrompt(
            new[] { "src/Foo.cs", "src/Bar.cs" },
            Array.Empty<PullRequestReviewComment>());

        result.Should().NotBeNull();
        result.Should().Contain("Merge Conflicts");
        result.Should().Contain("`src/Foo.cs`");
        result.Should().Contain("`src/Bar.cs`");
    }

    [Fact]
    public void BuildReworkPrompt_WithReviewComments_ContainsFeedbackSection()
    {
        var comments = new[]
        {
            new PullRequestReviewComment
            {
                Id = "1", Body = "Fix this null check", Author = "reviewer1",
                CreatedAt = DateTime.UtcNow, Path = "src/Service.cs"
            }
        };

        var result = PromptBuilder.BuildReworkPrompt(Array.Empty<string>(), comments);

        result.Should().NotBeNull();
        result.Should().Contain("Review Feedback");
        result.Should().Contain("@reviewer1");
        result.Should().Contain("Fix this null check");
        result.Should().Contain("`src/Service.cs`");
    }

    [Fact]
    public void BuildReworkPrompt_IsDraft_ReturnsPromptEvenWithoutConflictsOrComments()
    {
        var result = PromptBuilder.BuildReworkPrompt(
            Array.Empty<string>(),
            Array.Empty<PullRequestReviewComment>(),
            isDraft: true);

        result.Should().NotBeNull();
        result.Should().Contain("Draft PR");
    }

    [Fact]
    public void BuildReworkPrompt_ContainsIssueContextReference()
    {
        var result = PromptBuilder.BuildReworkPrompt(
            new[] { "file.cs" },
            Array.Empty<PullRequestReviewComment>());

        result.Should().Contain(PromptBuilder.IssueContextFilePath);
    }

    #endregion

    #region BuildIssueContextFileContent

    [Fact]
    public void BuildIssueContextFileContent_ContainsTitle()
    {
        var issue = CreateIssue(title: "My Feature");
        var result = PromptBuilder.BuildIssueContextFileContent(issue, CreateParsedIssue());
        result.Should().Contain("# Issue: My Feature");
    }

    [Fact]
    public void BuildIssueContextFileContent_ContainsDescription()
    {
        var issue = CreateIssue(description: "Detailed description here");
        var result = PromptBuilder.BuildIssueContextFileContent(issue, CreateParsedIssue());
        result.Should().Contain("Detailed description here");
    }

    [Fact]
    public void BuildIssueContextFileContent_ContainsRequirements()
    {
        var parsed = CreateParsedIssue(requirements: "Must support OAuth2");
        var result = PromptBuilder.BuildIssueContextFileContent(CreateIssue(), parsed);
        result.Should().Contain("Must support OAuth2");
    }

    [Fact]
    public void BuildIssueContextFileContent_ContainsAcceptanceCriteria()
    {
        var parsed = CreateParsedIssue(criteria: new[] { "Login works", "Logout works" });
        var result = PromptBuilder.BuildIssueContextFileContent(CreateIssue(), parsed);
        result.Should().Contain("- Login works");
        result.Should().Contain("- Logout works");
    }

    [Fact]
    public void BuildIssueContextFileContent_WithComments_IncludesFilteredComments()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = "Please also handle edge case", Author = "user1", CreatedAt = DateTime.UtcNow }
        };

        var result = PromptBuilder.BuildIssueContextFileContent(CreateIssue(), CreateParsedIssue(), comments);
        result.Should().Contain("@user1");
        result.Should().Contain("Please also handle edge case");
    }

    [Fact]
    public void BuildIssueContextFileContent_ExcludesBotComments()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = "## 🤖 Agent Analysis\nSome analysis", Author = "bot", CreatedAt = DateTime.UtcNow },
            new() { Id = "2", Body = "Human comment", Author = "human", CreatedAt = DateTime.UtcNow }
        };

        var result = PromptBuilder.BuildIssueContextFileContent(CreateIssue(), CreateParsedIssue(), comments);
        result.Should().NotContain("Agent Analysis");
        result.Should().Contain("Human comment");
    }

    [Fact]
    public void BuildIssueContextFileContent_ExcludesGateRejectionComments()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = "<!-- agent:gate-rejection -->Rejected", Author = "bot", CreatedAt = DateTime.UtcNow },
            new() { Id = "2", Body = "Real feedback", Author = "dev", CreatedAt = DateTime.UtcNow }
        };

        var result = PromptBuilder.BuildIssueContextFileContent(CreateIssue(), CreateParsedIssue(), comments);
        result.Should().NotContain("Rejected");
        result.Should().Contain("Real feedback");
    }

    [Fact]
    public void BuildIssueContextFileContent_NullComments_OmitsCommentsSection()
    {
        var result = PromptBuilder.BuildIssueContextFileContent(CreateIssue(), CreateParsedIssue(), null);
        result.Should().NotContain("## Comments");
    }

    [Fact]
    public void BuildIssueContextFileContent_EmptyComments_OmitsCommentsSection()
    {
        var result = PromptBuilder.BuildIssueContextFileContent(CreateIssue(), CreateParsedIssue(),
            new List<IssueComment>());
        result.Should().NotContain("## Comments");
    }

    #endregion

    #region BuildBrainContextSection

    [Fact]
    public void BuildBrainContextSection_NotAvailable_ReturnsEmpty()
    {
        var result = PromptBuilder.BuildBrainContextSection(brainAvailable: false);
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildBrainContextSection_Available_ContainsBrainHeader()
    {
        var result = PromptBuilder.BuildBrainContextSection(brainAvailable: true);
        result.Should().Contain("Brain Repository");
    }

    [Fact]
    public void BuildBrainContextSection_WithProjectName_ContainsProjectPath()
    {
        var result = PromptBuilder.BuildBrainContextSection(brainAvailable: true, projectName: "my-app");
        result.Should().Contain(".brain/projects/my-app/");
    }

    [Fact]
    public void BuildBrainContextSection_WithTechStack_ContainsTechReference()
    {
        var result = PromptBuilder.BuildBrainContextSection(brainAvailable: true, techStack: "dotnet, blazor");
        result.Should().Contain("dotnet, blazor");
    }

    [Fact]
    public void BuildBrainContextSection_ContainsNoGitWarning()
    {
        var result = PromptBuilder.BuildBrainContextSection(brainAvailable: true);
        result.Should().Contain("Do NOT run git commands");
    }

    #endregion

    #region BuildReflectionPrompt

    [Fact]
    public void BuildReflectionPrompt_ContainsRunId()
    {
        var run = CreatePipelineRun("run-123");
        var result = PromptBuilder.BuildReflectionPrompt(run);
        result.Should().Contain("run-123");
    }

    [Fact]
    public void BuildReflectionPrompt_ContainsIssueIdentifier()
    {
        var run = CreatePipelineRun(issueId: "55");
        var result = PromptBuilder.BuildReflectionPrompt(run);
        result.Should().Contain("#55");
    }

    [Fact]
    public void BuildReflectionPrompt_WithIssueTitle_ContainsTitle()
    {
        var run = CreatePipelineRun();
        var result = PromptBuilder.BuildReflectionPrompt(run, issueTitle: "Fix auth bug");
        result.Should().Contain("Fix auth bug");
    }

    [Fact]
    public void BuildReflectionPrompt_WithProjectName_ContainsProject()
    {
        var run = CreatePipelineRun();
        var result = PromptBuilder.BuildReflectionPrompt(run, projectName: "my-service");
        result.Should().Contain("my-service");
    }

    [Fact]
    public void BuildReflectionPrompt_WithRetries_ShowsRetryCount()
    {
        var run = CreatePipelineRun();
        run.RetryCount = 3;
        var result = PromptBuilder.BuildReflectionPrompt(run);
        result.Should().Contain("3");
    }

    [Fact]
    public void BuildReflectionPrompt_WithRetryErrors_ShowsErrors()
    {
        var run = CreatePipelineRun();
        run.RetryErrors.Add("Build failed: missing reference");
        var result = PromptBuilder.BuildReflectionPrompt(run);
        result.Should().Contain("Build failed: missing reference");
    }

    [Fact]
    public void BuildReflectionPrompt_NullRun_Throws()
    {
        var act = () => PromptBuilder.BuildReflectionPrompt(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildReflectionPrompt_ContainsNoSourceCodeWarning()
    {
        var run = CreatePipelineRun();
        var result = PromptBuilder.BuildReflectionPrompt(run);
        result.Should().Contain("Do NOT modify any source code files");
    }

    [Fact]
    public void BuildReflectionPrompt_DoesNotContainFeedbackSection()
    {
        var run = CreatePipelineRun();
        var result = PromptBuilder.BuildReflectionPrompt(run);
        result.Should().NotContain("Feedback Collection");
        result.Should().NotContain("Feedback Questions");
    }

    #endregion

    #region BuildBrainWriteInstructions

    [Fact]
    public void BuildBrainWriteInstructions_NotAvailable_ReturnsEmpty()
    {
        var result = PromptBuilder.BuildBrainWriteInstructions(brainAvailable: false, "run1", "issue1");
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildBrainWriteInstructions_ReadOnly_ReturnsEmpty()
    {
        var result = PromptBuilder.BuildBrainWriteInstructions(brainAvailable: true, "run1", "issue1",
            brainReadOnly: true);
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildBrainWriteInstructions_Available_ContainsWriteHeader()
    {
        var result = PromptBuilder.BuildBrainWriteInstructions(brainAvailable: true, "run1", "issue1");
        result.Should().Contain("Write Back What You Learned");
    }

    [Fact]
    public void BuildBrainWriteInstructions_ContainsAppendWarning()
    {
        var result = PromptBuilder.BuildBrainWriteInstructions(brainAvailable: true, "run1", "issue1");
        result.Should().Contain("APPEND to existing files");
    }

    #endregion

    #region BuildAnalysisReviewPrompt

    [Fact]
    public void BuildAnalysisReviewPrompt_ContainsInstructions()
    {
        var result = PromptBuilder.BuildAnalysisReviewPrompt("Review carefully", CreateIssue(), CreateParsedIssue());
        result.Should().StartWith("Review carefully");
    }

    [Fact]
    public void BuildAnalysisReviewPrompt_ContainsReviewFilePath()
    {
        var result = PromptBuilder.BuildAnalysisReviewPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain(PromptBuilder.AnalysisReviewFilePath);
    }

    [Fact]
    public void BuildAnalysisReviewPrompt_ProhibitsModifyingAnalysis()
    {
        var result = PromptBuilder.BuildAnalysisReviewPrompt("Instructions", CreateIssue(), CreateParsedIssue());
        result.Should().Contain("Do NOT modify `.agent/analysis.md`");
    }

    [Fact]
    public void BuildAnalysisReviewPrompt_ContainsIssueTitle()
    {
        var issue = CreateIssue(title: "Fix login bug");
        var result = PromptBuilder.BuildAnalysisReviewPrompt("Instructions", issue, CreateParsedIssue());
        result.Should().Contain("Fix login bug");
    }

    [Fact]
    public void BuildAnalysisReviewPrompt_ContainsAcceptanceCriteria()
    {
        var parsed = CreateParsedIssue(criteria: new[] { "Users can log in", "Session persists" });
        var result = PromptBuilder.BuildAnalysisReviewPrompt("Instructions", CreateIssue(), parsed);
        result.Should().Contain("- Users can log in");
        result.Should().Contain("- Session persists");
    }

    [Fact]
    public void BuildAnalysisReviewPrompt_NullInstructions_Throws()
    {
        var act = () => PromptBuilder.BuildAnalysisReviewPrompt(null!, CreateIssue(), CreateParsedIssue());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildAnalysisReviewPrompt_NullIssue_Throws()
    {
        var act = () => PromptBuilder.BuildAnalysisReviewPrompt("Instructions", null!, CreateParsedIssue());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildAnalysisReviewPrompt_NullParsed_Throws()
    {
        var act = () => PromptBuilder.BuildAnalysisReviewPrompt("Instructions", CreateIssue(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region BuildAnalysisRefinementPrompt

    [Fact]
    public void BuildAnalysisRefinementPrompt_ContainsInstructions()
    {
        var result = PromptBuilder.BuildAnalysisRefinementPrompt("Refine the analysis");
        result.Should().StartWith("Refine the analysis");
    }

    [Fact]
    public void BuildAnalysisRefinementPrompt_ReferencesReviewFile()
    {
        var result = PromptBuilder.BuildAnalysisRefinementPrompt("Instructions");
        result.Should().Contain(PromptBuilder.AnalysisReviewFilePath);
    }

    [Fact]
    public void BuildAnalysisRefinementPrompt_ReferencesAnalysisFile()
    {
        var result = PromptBuilder.BuildAnalysisRefinementPrompt("Instructions");
        result.Should().Contain(PromptBuilder.AnalysisFilePath);
    }

    [Fact]
    public void BuildAnalysisRefinementPrompt_ReferencesAssessmentFile()
    {
        var result = PromptBuilder.BuildAnalysisRefinementPrompt("Instructions");
        result.Should().Contain(PromptBuilder.AnalysisAssessmentFilePath);
    }

    [Fact]
    public void BuildAnalysisRefinementPrompt_NullInstructions_Throws()
    {
        var act = () => PromptBuilder.BuildAnalysisRefinementPrompt(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    private static PipelineRun CreatePipelineRun(string runId = "test-run", string issueId = "42") => new()
    {
        RunId = runId,
        IssueIdentifier = issueId,
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-provider",
        RepoProviderConfigId = "repo-provider",
        StartedAt = DateTime.UtcNow
    };
}
