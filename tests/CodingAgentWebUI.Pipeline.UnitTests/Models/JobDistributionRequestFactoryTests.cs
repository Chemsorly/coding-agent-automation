using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for the static factory methods on <see cref="JobDistributionRequest"/>.
/// Verifies field mapping for implementation, review, and decomposition overloads.
/// </summary>
public class JobDistributionRequestFactoryTests
{
    private static PipelineJobTemplate MakeTemplate(
        string issueProviderId = "ip-1",
        string repoProviderId = "rp-1",
        string? brainProviderId = "bp-1",
        string? pipelineProviderId = "pp-1") =>
        new()
        {
            Id = "t-1",
            Name = "Test Template",
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
            BrainProviderId = brainProviderId,
            PipelineProviderId = pipelineProviderId
        };

    private static IssueSummary MakeIssue(
        string identifier = "123",
        string title = "Test Issue",
        string? description = "Issue body",
        IReadOnlyList<string>? labels = null) =>
        new()
        {
            Identifier = identifier,
            Title = title,
            Description = description,
            Labels = labels ?? ["bug", "priority"]
        };

    private static PullRequestSummary MakePr(
        string identifier = "42",
        string title = "Test PR",
        string description = "PR body",
        string branchName = "feature/test",
        string targetBranch = "main",
        string url = "https://github.com/org/repo/pull/42",
        bool isDraft = false,
        int number = 42,
        string? author = "test-user") =>
        new()
        {
            Identifier = identifier,
            Title = title,
            Description = description,
            Labels = ["review"],
            BranchName = branchName,
            TargetBranch = targetBranch,
            Url = url,
            IsDraft = isDraft,
            Number = number,
            Author = author
        };

    // ── Implementation overload ──

    [Fact]
    public void FromTemplate_Implementation_SetsAllCommonFields()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(template, issue, initiatedBy: "loop");

        Assert.Equal("123", result.IssueIdentifier);
        Assert.Equal("ip-1", result.IssueProviderConfigId);
        Assert.Equal("rp-1", result.RepoProviderConfigId);
        Assert.Equal("bp-1", result.BrainProviderConfigId);
        Assert.Equal("pp-1", result.PipelineProviderConfigId);
        Assert.Equal("loop", result.InitiatedBy);
        Assert.Equal(WorkItemTaskType.Implementation, result.TaskType);
        Assert.Equal("", result.AgentSelector);
        Assert.Equal(0, result.TimeoutSeconds);
        Assert.Equal(PipelineRunType.Implementation, result.RunType);
    }

    [Fact]
    public void FromTemplate_Implementation_SetsProjectFields()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(
            template, issue, initiatedBy: "loop",
            projectId: "proj-1", projectName: "My Project");

        Assert.Equal("proj-1", result.ProjectId);
        Assert.Equal("My Project", result.ProjectName);
    }

    [Fact]
    public void FromTemplate_Implementation_OmitsProjectFieldsWhenNull()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(template, issue, initiatedBy: "loop");

        Assert.Null(result.ProjectId);
        Assert.Null(result.ProjectName);
    }

    [Fact]
    public void FromTemplate_Implementation_SetsIssueDetail()
    {
        var template = MakeTemplate();
        var issue = MakeIssue(identifier: "55", title: "My Title", description: "My Description", labels: ["a", "b"]);

        var result = JobDistributionRequest.FromTemplate(template, issue, initiatedBy: "manual");

        Assert.NotNull(result.IssueDetail);
        Assert.Equal("55", result.IssueDetail.Identifier);
        Assert.Equal("My Title", result.IssueDetail.Title);
        Assert.Equal("My Description", result.IssueDetail.Description);
        Assert.Equal(["a", "b"], result.IssueDetail.Labels);
    }

    [Fact]
    public void FromTemplate_Implementation_HandlesNullDescription()
    {
        var template = MakeTemplate();
        var issue = MakeIssue(description: null);

        var result = JobDistributionRequest.FromTemplate(template, issue, initiatedBy: "loop");

        Assert.NotNull(result.IssueDetail);
        Assert.Equal("", result.IssueDetail.Description);
    }

    [Fact]
    public void FromTemplate_Implementation_SetsTimeoutSeconds()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(
            template, issue, initiatedBy: "manual", timeoutSeconds: 3600);

        Assert.Equal(3600, result.TimeoutSeconds);
    }

    // ── Review overload ──

    [Fact]
    public void FromTemplate_Review_FullMetadata_SetsLinkedPullRequest()
    {
        var template = MakeTemplate();
        var pr = MakePr(isDraft: true, number: 99);

        var result = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "manual", useFullPrMetadata: true);

        Assert.NotNull(result.LinkedPullRequest);
        Assert.Equal("https://github.com/org/repo/pull/42", result.LinkedPullRequest.Url);
        Assert.Equal("feature/test", result.LinkedPullRequest.BranchName);
        Assert.True(result.LinkedPullRequest.IsDraft);
        Assert.Equal(99, result.LinkedPullRequest.Number);
    }

    [Fact]
    public void FromTemplate_Review_MinimalMetadata_SetsHardcodedDefaults()
    {
        var template = MakeTemplate();
        var pr = MakePr(isDraft: true, number: 99);

        var result = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "loop", useFullPrMetadata: false);

        Assert.NotNull(result.LinkedPullRequest);
        Assert.Equal("https://github.com/org/repo/pull/42", result.LinkedPullRequest.Url);
        Assert.Equal("feature/test", result.LinkedPullRequest.BranchName);
        Assert.False(result.LinkedPullRequest.IsDraft);
        Assert.Equal(0, result.LinkedPullRequest.Number);
    }

    [Fact]
    public void FromTemplate_Review_FullMetadata_SetsIssueDetailDescription()
    {
        var template = MakeTemplate();
        var pr = MakePr(description: "Full PR description");

        var result = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "manual", useFullPrMetadata: true);

        Assert.NotNull(result.IssueDetail);
        Assert.Equal("Full PR description", result.IssueDetail.Description);
        Assert.Empty(result.IssueDetail.Labels);
    }

    [Fact]
    public void FromTemplate_Review_MinimalMetadata_SetsEmptyIssueDetailDescription()
    {
        var template = MakeTemplate();
        var pr = MakePr(description: "Full PR description");

        var result = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "loop", useFullPrMetadata: false);

        Assert.NotNull(result.IssueDetail);
        Assert.Equal("", result.IssueDetail.Description);
    }

    [Fact]
    public void FromTemplate_Review_SetsReviewFields()
    {
        var template = MakeTemplate();
        var pr = MakePr(targetBranch: "develop", description: "PR desc", author: "octocat");

        var result = JobDistributionRequest.FromTemplate(template, pr, initiatedBy: "loop");

        Assert.Equal("develop", result.ReviewPrTargetBranch);
        Assert.Equal("PR desc", result.ReviewPrDescription);
        Assert.Equal("octocat", result.ReviewPrAuthor);
    }

    [Fact]
    public void FromTemplate_Review_SetsTaskTypeAndRunType()
    {
        var template = MakeTemplate();
        var pr = MakePr();

        var result = JobDistributionRequest.FromTemplate(template, pr, initiatedBy: "loop");

        Assert.Equal(WorkItemTaskType.Review, result.TaskType);
        Assert.Equal(PipelineRunType.Review, result.RunType);
    }

    [Fact]
    public void FromTemplate_Review_SetsProjectFields()
    {
        var template = MakeTemplate();
        var pr = MakePr();

        var result = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "manual",
            projectId: "proj-2", projectName: "Review Project");

        Assert.Equal("proj-2", result.ProjectId);
        Assert.Equal("Review Project", result.ProjectName);
    }

    // ── Decomposition overload ──

    [Fact]
    public void FromTemplate_Decomposition_SetsPhaseAsRunType()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(
            template, issue, PipelineRunType.DecompositionAnalysis, initiatedBy: "loop");

        Assert.Equal(PipelineRunType.DecompositionAnalysis, result.RunType);
        Assert.Equal(WorkItemTaskType.Decomposition, result.TaskType);
    }

    [Fact]
    public void FromTemplate_Decomposition_SetsDecompositionSource()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(
            template, issue, PipelineRunType.Decomposition,
            initiatedBy: "loop", decompositionSource: "project-level");

        Assert.Equal("project-level", result.DecompositionSource);
    }

    [Fact]
    public void FromTemplate_Decomposition_OmitsDecompositionSourceByDefault()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(
            template, issue, PipelineRunType.Decomposition, initiatedBy: "loop");

        Assert.Null(result.DecompositionSource);
    }

    [Fact]
    public void FromTemplate_Decomposition_SetsProjectFields()
    {
        var template = MakeTemplate();
        var issue = MakeIssue();

        var result = JobDistributionRequest.FromTemplate(
            template, issue, PipelineRunType.DecompositionAnalysis,
            initiatedBy: "manual", projectId: "proj-3", projectName: "Epic Project");

        Assert.Equal("proj-3", result.ProjectId);
        Assert.Equal("Epic Project", result.ProjectName);
    }

    [Fact]
    public void FromTemplate_Decomposition_SetsIssueDetail()
    {
        var template = MakeTemplate();
        var issue = MakeIssue(identifier: "77", title: "Epic", description: "Epic body", labels: ["epic"]);

        var result = JobDistributionRequest.FromTemplate(
            template, issue, PipelineRunType.DecompositionAnalysis, initiatedBy: "loop");

        Assert.NotNull(result.IssueDetail);
        Assert.Equal("77", result.IssueDetail.Identifier);
        Assert.Equal("Epic", result.IssueDetail.Title);
        Assert.Equal("Epic body", result.IssueDetail.Description);
        Assert.Equal(["epic"], result.IssueDetail.Labels);
    }

    // ── Regression tests (compare against old construction patterns) ──

    /// <summary>
    /// Asserts that two JobDistributionRequest instances have equal field values.
    /// Uses field-by-field comparison because IssueDetail and LinkedPullRequest are
    /// sealed classes (reference equality) rather than records (value equality).
    /// </summary>
    private static void AssertRequestsEqual(JobDistributionRequest expected, JobDistributionRequest actual)
    {
        Assert.Equal(expected.IssueIdentifier, actual.IssueIdentifier);
        Assert.Equal(expected.IssueProviderConfigId, actual.IssueProviderConfigId);
        Assert.Equal(expected.RepoProviderConfigId, actual.RepoProviderConfigId);
        Assert.Equal(expected.BrainProviderConfigId, actual.BrainProviderConfigId);
        Assert.Equal(expected.PipelineProviderConfigId, actual.PipelineProviderConfigId);
        Assert.Equal(expected.InitiatedBy, actual.InitiatedBy);
        Assert.Equal(expected.TaskType, actual.TaskType);
        Assert.Equal(expected.AgentSelector, actual.AgentSelector);
        Assert.Equal(expected.TimeoutSeconds, actual.TimeoutSeconds);
        Assert.Equal(expected.ProjectId, actual.ProjectId);
        Assert.Equal(expected.ProjectName, actual.ProjectName);
        Assert.Equal(expected.RunType, actual.RunType);
        Assert.Equal(expected.DecompositionSource, actual.DecompositionSource);
        Assert.Equal(expected.ReviewPrTargetBranch, actual.ReviewPrTargetBranch);
        Assert.Equal(expected.ReviewPrDescription, actual.ReviewPrDescription);
        Assert.Equal(expected.ReviewPrAuthor, actual.ReviewPrAuthor);

        // IssueDetail comparison (sealed class — no value equality)
        if (expected.IssueDetail is null)
        {
            Assert.Null(actual.IssueDetail);
        }
        else
        {
            Assert.NotNull(actual.IssueDetail);
            Assert.Equal(expected.IssueDetail.Identifier, actual.IssueDetail.Identifier);
            Assert.Equal(expected.IssueDetail.Title, actual.IssueDetail.Title);
            Assert.Equal(expected.IssueDetail.Description, actual.IssueDetail.Description);
            Assert.Equal(expected.IssueDetail.Labels, actual.IssueDetail.Labels);
        }

        // LinkedPullRequest comparison (sealed class — no value equality)
        if (expected.LinkedPullRequest is null)
        {
            Assert.Null(actual.LinkedPullRequest);
        }
        else
        {
            Assert.NotNull(actual.LinkedPullRequest);
            Assert.Equal(expected.LinkedPullRequest.Url, actual.LinkedPullRequest.Url);
            Assert.Equal(expected.LinkedPullRequest.BranchName, actual.LinkedPullRequest.BranchName);
            Assert.Equal(expected.LinkedPullRequest.IsDraft, actual.LinkedPullRequest.IsDraft);
            Assert.Equal(expected.LinkedPullRequest.Number, actual.LinkedPullRequest.Number);
        }
    }

    [Fact]
    // TODO: This test uses Description = null and Labels = [] which masks a behavioral divergence.
    // The old loop code hardcoded Description = "" and Labels = [] regardless of issue data.
    // The factory uses issue.Description ?? "" and issue.Labels ?? [], so a non-null description
    // or non-empty labels would produce different results than the old code. Add companion test
    // cases with non-null description and non-empty labels to document whether this is intentional.
    public void FromTemplate_Implementation_MatchesLegacyLoopConstruction()
    {
        var template = MakeTemplate();
        var issue = new IssueSummary { Identifier = "10", Title = "Bug fix", Labels = [], Description = null };

        // Old construction pattern from PipelineLoopService.MultiTemplateLoop.cs
        var legacy = new JobDistributionRequest
        {
            IssueIdentifier = issue.Identifier,
            IssueProviderConfigId = template.IssueProviderId,
            RepoProviderConfigId = template.RepoProviderId,
            BrainProviderConfigId = template.BrainProviderId,
            PipelineProviderConfigId = template.PipelineProviderId,
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 0,
            ProjectId = "proj-x",
            ProjectName = "Proj X",
            IssueDetail = new IssueDetail { Identifier = issue.Identifier, Title = issue.Title ?? "", Description = "", Labels = [] }
        };

        var factory = JobDistributionRequest.FromTemplate(
            template, issue, initiatedBy: "loop",
            projectId: "proj-x", projectName: "Proj X");

        AssertRequestsEqual(legacy, factory);
    }

    [Fact]
    // TODO: This test uses pipelineProviderId: null which masks a behavioral divergence.
    // The old loop review code never set PipelineProviderConfigId (defaulting to null).
    // The factory always sets it from template.PipelineProviderId. With a non-null value,
    // the factory would produce a different result. Add a companion test to document this.
    public void FromTemplate_Review_MatchesLegacyLoopConstruction()
    {
        var template = MakeTemplate(pipelineProviderId: null);
        var pr = MakePr(identifier: "7", title: "Fix typo", description: "Small fix",
            branchName: "fix/typo", targetBranch: "main",
            url: "https://github.com/org/repo/pull/7",
            isDraft: true, number: 7, author: "dev");

        // Old construction pattern from loop (minimal metadata)
        var legacy = new JobDistributionRequest
        {
            IssueIdentifier = pr.Identifier,
            IssueProviderConfigId = template.IssueProviderId,
            RepoProviderConfigId = template.RepoProviderId,
            BrainProviderConfigId = template.BrainProviderId,
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "",
            TimeoutSeconds = 0,
            RunType = PipelineRunType.Review,
            IssueDetail = new IssueDetail { Identifier = pr.Identifier, Title = pr.Title ?? "", Description = "", Labels = [] },
            LinkedPullRequest = new LinkedPullRequest { Url = pr.Url, BranchName = pr.BranchName, IsDraft = false, Number = 0 },
            ReviewPrTargetBranch = pr.TargetBranch,
            ReviewPrDescription = pr.Description,
            ReviewPrAuthor = pr.Author
        };

        var factory = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "loop", useFullPrMetadata: false);

        AssertRequestsEqual(legacy, factory);
    }

    [Fact]
    public void FromTemplate_Review_MatchesLegacyManualConstruction()
    {
        var template = MakeTemplate(pipelineProviderId: null);
        var pr = MakePr(identifier: "7", title: "Fix typo", description: "Small fix",
            branchName: "fix/typo", targetBranch: "main",
            url: "https://github.com/org/repo/pull/7",
            isDraft: true, number: 7, author: "dev");

        // Old construction pattern from manual dispatch (full metadata)
        var legacy = new JobDistributionRequest
        {
            IssueIdentifier = pr.Identifier,
            IssueProviderConfigId = template.IssueProviderId,
            RepoProviderConfigId = template.RepoProviderId,
            BrainProviderConfigId = template.BrainProviderId,
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            ProjectId = "proj-m",
            ProjectName = "Manual Project",
            RunType = PipelineRunType.Review,
            IssueDetail = new IssueDetail
            {
                Title = pr.Title ?? "",
                Identifier = pr.Identifier,
                Description = pr.Description ?? "",
                Labels = []
            },
            LinkedPullRequest = new LinkedPullRequest
            {
                BranchName = pr.BranchName,
                Url = pr.Url,
                IsDraft = pr.IsDraft,
                Number = pr.Number
            },
            ReviewPrTargetBranch = pr.TargetBranch,
            ReviewPrDescription = pr.Description,
            ReviewPrAuthor = pr.Author
        };

        var factory = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "manual", timeoutSeconds: 3600,
            projectId: "proj-m", projectName: "Manual Project");

        AssertRequestsEqual(legacy, factory);
    }

    [Fact]
    // TODO: This test uses pipelineProviderId: null, Description = null, and Labels = [] which masks
    // behavioral divergences. The old loop decomposition code never set PipelineProviderConfigId and
    // hardcoded Description = "" and Labels = []. The factory sets PipelineProviderConfigId from the
    // template and uses issue.Description ?? "" / issue.Labels ?? []. Add companion tests with non-null
    // values to document whether these behavioral changes are intentional.
    public void FromTemplate_Decomposition_MatchesLegacyLoopConstruction()
    {
        var template = MakeTemplate(pipelineProviderId: null);
        var issue = new IssueSummary { Identifier = "50", Title = "Epic task", Labels = [], Description = null };

        // Old construction pattern from loop decomposition
        var legacy = new JobDistributionRequest
        {
            IssueIdentifier = issue.Identifier,
            IssueProviderConfigId = template.IssueProviderId,
            RepoProviderConfigId = template.RepoProviderId,
            BrainProviderConfigId = template.BrainProviderId,
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Decomposition,
            AgentSelector = "",
            TimeoutSeconds = 0,
            RunType = PipelineRunType.DecompositionAnalysis,
            IssueDetail = new IssueDetail { Identifier = issue.Identifier, Title = issue.Title ?? "", Description = "", Labels = [] }
        };

        var factory = JobDistributionRequest.FromTemplate(
            template, issue, PipelineRunType.DecompositionAnalysis, initiatedBy: "loop");

        AssertRequestsEqual(legacy, factory);
    }

    // ── Null-guard tests ──

    [Fact]
    public void FromTemplate_Implementation_ThrowsOnNullTemplate()
    {
        var issue = MakeIssue();
        Assert.Throws<ArgumentNullException>(() =>
            JobDistributionRequest.FromTemplate(null!, issue, initiatedBy: "loop"));
    }

    [Fact]
    public void FromTemplate_Implementation_ThrowsOnNullIssue()
    {
        var template = MakeTemplate();
        Assert.Throws<ArgumentNullException>(() =>
            JobDistributionRequest.FromTemplate(template, (IssueSummary)null!, initiatedBy: "loop"));
    }

    [Fact]
    public void FromTemplate_Review_ThrowsOnNullPr()
    {
        var template = MakeTemplate();
        Assert.Throws<ArgumentNullException>(() =>
            JobDistributionRequest.FromTemplate(template, (PullRequestSummary)null!, initiatedBy: "loop"));
    }
}
