// Feature: 035a-postgres-work-queue
// Property 13: Payload Contains No Secrets
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Property-based test asserting that serialized JobDistributionRequest payloads
/// never contain JSON property names indicative of secret/credential material.
/// **Validates: Requirements 1.13, 12.4, 12.5**
/// </summary>
public class JobDistributionRequestNoSecretsPropertyTests
{
    /// <summary>
    /// Forbidden JSON property names that must never appear in a serialized payload.
    /// Checked case-insensitively against all keys in the JSON document tree.
    /// </summary>
    private static readonly string[] ForbiddenPropertyNames =
    [
        "privatekeybase64",
        "projectsecrets",
        "password",
        "secret",
        "credential",
        "token"
    ];

    /// <summary>
    /// Property 13: Payload Contains No Secrets
    /// For any generated JobDistributionRequest, serializing to JSON SHALL produce
    /// a document where no property name (key) matches forbidden secret field names.
    /// **Validates: Requirements 1.13, 12.4, 12.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(JobDistributionRequestArbitraries) })]
    public void SerializedPayload_ContainsNoSecretPropertyNames(JobDistributionRequest request)
    {
        var json = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);
        using var doc = JsonDocument.Parse(json);

        var violations = new List<string>();
        CollectForbiddenKeys(doc.RootElement, "", violations);

        if (violations.Count > 0)
        {
            throw new Exception(
                $"Forbidden secret property names found in serialized payload: [{string.Join(", ", violations)}]");
        }
    }

    /// <summary>
    /// Recursively walks the JSON element tree and collects property names that match
    /// any forbidden term (case-insensitive exact match on the property name).
    /// </summary>
    private static void CollectForbiddenKeys(JsonElement element, string path, List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyNameLower = property.Name.ToLowerInvariant();
                    if (ForbiddenPropertyNames.Contains(propertyNameLower))
                    {
                        violations.Add($"{path}.{property.Name}");
                    }
                    CollectForbiddenKeys(property.Value, $"{path}.{property.Name}", violations);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectForbiddenKeys(item, $"{path}[{index}]", violations);
                    index++;
                }
                break;
        }
    }
}

/// <summary>
/// FsCheck arbitrary generators for JobDistributionRequest used by Property 13.
/// Generates instances with realistic field values — importantly including nested
/// objects that could potentially carry secret-named fields if the model were wrong.
/// </summary>
public class JobDistributionRequestArbitraries
{
    private static readonly string[] IssueIdentifiers =
        ["owner/repo#1", "org/project#42", "team/service#100", "user/lib#7"];

    private static readonly string[] ConfigIds =
        ["cfg-aaa", "cfg-bbb", "cfg-ccc", "cfg-ddd"];

    private static readonly string[] Initiators =
        ["user@example.com", "pipeline-scheduler", "manual-trigger", "webhook"];

    private static readonly string[] Selectors =
        ["dotnet,kiro", "java,opencode", "python,kiro", "dotnet,opencode"];

    private static readonly string[] ProjectNames =
        ["Alpha", "Beta", "Gamma", null!];

    private static readonly string[] AnalysisTexts =
        ["Previous analysis found issues", "No prior analysis", null!];

    private static readonly string[] BranchNames =
        ["feature/foo", "fix/bar", "main", "develop"];

    private static readonly string[] Urls =
        ["https://github.com/org/repo/pull/1", "https://github.com/org/repo/pull/99"];

    private static readonly string[] ProviderTypes =
        ["GitHub", "KiroCli", "OpenCode", "AzureDevOps"];

    private static readonly string[] DisplayNames =
        ["My Provider", "Work Repo", "Issue Tracker", "Brain AI"];

    private static readonly string[] McpNames =
        ["context7", "web-search", "sequential-thinking"];

    private static readonly string[] McpCommands =
        ["uvx", "npx", "node"];

    public static Arbitrary<JobDistributionRequest> JobDistributionRequestArb()
    {
        var gen =
            from issueId in Gen.Elements(IssueIdentifiers)
            from issueProviderConfigId in Gen.Elements(ConfigIds)
            from repoProviderConfigId in Gen.Elements(ConfigIds)
            from hasBrain in Gen.Elements(true, false)
            from brainConfigId in Gen.Elements(ConfigIds)
            from hasPipeline in Gen.Elements(true, false)
            from pipelineConfigId in Gen.Elements(ConfigIds)
            from initiatedBy in Gen.Elements(Initiators)
            from taskType in Gen.Elements(
                WorkItemTaskType.Implementation,
                WorkItemTaskType.Review,
                WorkItemTaskType.Decomposition,
                WorkItemTaskType.Consolidation)
            from agentSelector in Gen.Elements(Selectors)
            from timeout in Gen.Choose(300, 7200)
            from hasProject in Gen.Elements(true, false)
            from projectName in Gen.Elements(ProjectNames)
            from runType in Gen.Elements(
                PipelineRunType.Implementation,
                PipelineRunType.Review,
                PipelineRunType.Decomposition)
            from hasIssueDetail in Gen.Elements(true, false)
            from issueDetail in GenIssueDetail()
            from hasParsedIssue in Gen.Elements(true, false)
            from parsedIssue in GenParsedIssue()
            from commentCount in Gen.Choose(0, 3)
            from comments in Gen.ArrayOf(GenIssueComment(), commentCount)
            from hasAnalysis in Gen.Elements(true, false)
            from analysis in Gen.Elements(AnalysisTexts)
            from providerCount in Gen.Choose(0, 3)
            from providers in Gen.ArrayOf(GenProviderConfig(), providerCount)
            from hasProfileId in Gen.Elements(true, false)
            from profileId in Gen.Elements(ConfigIds)
            from qgCount in Gen.Choose(0, 2)
            from qgs in Gen.ArrayOf(GenQualityGateConfig(), qgCount)
            from reviewerCount in Gen.Choose(0, 2)
            from reviewers in Gen.ArrayOf(GenReviewerConfig(), reviewerCount)
            from mcpCount in Gen.Choose(0, 2)
            from mcps in Gen.ArrayOf(GenMcpServerConfig(), mcpCount)
            from hasLinkedPr in Gen.Elements(true, false)
            from linkedPr in GenLinkedPullRequest()
            from hasReviewBranch in Gen.Elements(true, false)
            from reviewBranch in Gen.Elements(BranchNames)
            from hasDecompContext in Gen.Elements(true, false)
            from decompSource in Gen.Elements("epic#1", "epic#2", (string?)null)
            select new JobDistributionRequest
            {
                IssueIdentifier = issueId,
                IssueProviderConfigId = issueProviderConfigId,
                RepoProviderConfigId = repoProviderConfigId,
                BrainProviderConfigId = hasBrain ? brainConfigId : null,
                PipelineProviderConfigId = hasPipeline ? pipelineConfigId : null,
                InitiatedBy = initiatedBy,
                TaskType = taskType,
                AgentSelector = agentSelector,
                TimeoutSeconds = timeout,
                ProjectId = hasProject ? Guid.NewGuid().ToString() : null,
                ProjectName = hasProject ? projectName : null,
                RunType = runType,
                IssueDetail = hasIssueDetail ? issueDetail : null,
                ParsedIssue = hasParsedIssue ? parsedIssue : null,
                IssueComments = commentCount > 0 ? comments.ToList() : null,
                ExistingAnalysis = hasAnalysis ? analysis : null,
                ProviderConfigs = providerCount > 0 ? providers.ToList() : null,
                ResolvedProfileId = hasProfileId ? profileId : null,
                QualityGateConfigs = qgCount > 0 ? qgs.ToList() : null,
                ReviewerConfigs = reviewerCount > 0 ? reviewers.ToList() : null,
                McpServers = mcpCount > 0 ? mcps.ToList() : null,
                LinkedPullRequest = hasLinkedPr ? linkedPr : null,
                ReviewPrTargetBranch = hasReviewBranch ? reviewBranch : null,
                ReviewPrDescription = hasReviewBranch ? "PR description text" : null,
                ReviewPrAuthor = hasReviewBranch ? "author-user" : null,
                ProjectContext = hasDecompContext ? new DecompositionProjectContext
                {
                    ProjectName = "Test Project",
                    Repositories = [new RepositoryTarget
                    {
                        TemplateName = "dotnet-service",
                        Description = "Main service repo",
                        RepoProviderId = repoProviderConfigId
                    }]
                } : null,
                DecompositionSource = hasDecompContext ? decompSource : null
            };

        return gen.ToArbitrary();
    }

    private static Gen<IssueDetail> GenIssueDetail() =>
        from desc in Gen.Elements("Fix bug in parser", "Add new endpoint", "Refactor module")
        from id in Gen.Elements(IssueIdentifiers)
        from title in Gen.Elements("Bug: crash on null", "Feature: add auth", "Chore: update deps")
        from labelCount in Gen.Choose(0, 3)
        from labels in Gen.ArrayOf(Gen.Elements("bug", "feature", "chore", "priority:high"), labelCount)
        select new IssueDetail
        {
            Description = desc,
            Identifier = id,
            Title = title,
            Labels = labels.ToList()
        };

    private static Gen<ParsedIssue> GenParsedIssue() =>
        from reqSection in Gen.Elements("## Requirements\n- Must handle null", "## Spec\n- Support pagination")
        from acCount in Gen.Choose(1, 3)
        from acs in Gen.ArrayOf(Gen.Elements("Tests pass", "No regressions", "Handles edge cases"), acCount)
        select new ParsedIssue
        {
            RequirementsSection = reqSection,
            AcceptanceCriteria = acs.ToList()
        };

    private static Gen<IssueComment> GenIssueComment() =>
        from author in Gen.Elements("user1", "bot", "reviewer")
        from body in Gen.Elements("LGTM", "Please fix the null check", "Needs tests")
        from id in Gen.Elements("comment-1", "comment-2", "comment-3")
        select new IssueComment
        {
            Author = author,
            Body = body,
            CreatedAt = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc),
            Id = id
        };

    private static Gen<ProviderConfig> GenProviderConfig() =>
        from kind in Gen.Elements(ProviderKind.Issue, ProviderKind.Repository, ProviderKind.Agent)
        from providerType in Gen.Elements(ProviderTypes)
        from displayName in Gen.Elements(DisplayNames)
        select new ProviderConfig
        {
            Kind = kind,
            ProviderType = providerType,
            DisplayName = displayName,
            Id = Guid.NewGuid().ToString(),
            Settings = new Dictionary<string, string>
            {
                ["repoOwner"] = "test-org",
                ["repoName"] = "test-repo"
            }
        };

    private static Gen<QualityGateConfiguration> GenQualityGateConfig() =>
        from displayName in Gen.Elements("Build Gate", "Test Gate", "Lint Gate")
        from enabled in Gen.Elements(true, false)
        select new QualityGateConfiguration
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Enabled = enabled,
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            TestCommand = "dotnet",
            TestArguments = ["test"]
        };

    private static Gen<ReviewerConfiguration> GenReviewerConfig() =>
        from displayName in Gen.Elements("PR Reviewer", "Security Reviewer", "Style Reviewer")
        from enabled in Gen.Elements(true, false)
        select new ReviewerConfiguration
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Enabled = enabled,
            Agents = [new ReviewAgent { Name = "Reviewer1", Prompt = "Review for correctness" }]
        };

    private static Gen<McpServerConfig> GenMcpServerConfig() =>
        from name in Gen.Elements(McpNames)
        from command in Gen.Elements(McpCommands)
        select new McpServerConfig
        {
            Name = name,
            Type = "stdio",
            Command = command,
            Args = ["--server"]
        };

    private static Gen<LinkedPullRequest> GenLinkedPullRequest() =>
        from branch in Gen.Elements(BranchNames)
        from isDraft in Gen.Elements(true, false)
        from number in Gen.Choose(1, 500)
        from url in Gen.Elements(Urls)
        select new LinkedPullRequest
        {
            BranchName = branch,
            IsDraft = isDraft,
            Number = number,
            Url = url
        };
}
