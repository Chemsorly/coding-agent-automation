using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property 4: JobDistributionRequest JSON Round-Trip
/// Serialize/deserialize via PipelineJsonOptions.Default produces equivalent objects.
/// **Validates: Requirements 1.12, 4.2**
/// </summary>
public class JobDistributionRequestJsonRoundTripPropertyTests
{
    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    /// <summary>
    /// Property 4: Any valid JobDistributionRequest survives JSON round-trip via PipelineJsonOptions.Default.
    /// Asserts that serialize→deserialize→serialize produces identical JSON (stable round-trip).
    /// **Validates: Requirements 1.12, 4.2**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(JobDistributionRequestArbitraries) })]
    public Property JobDistributionRequest_JsonRoundTrip_PreservesEquality(JobDistributionRequest original)
    {
        var json1 = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<JobDistributionRequest>(json1, JsonOptions);
        var json2 = deserialized is not null
            ? JsonSerializer.Serialize(deserialized, JsonOptions)
            : "";

        // Compare via JSON output: serialize→deserialize→serialize must produce identical JSON
        return (deserialized is not null && json1 == json2)
            .ToProperty()
            .Label($"Round-trip failed for IssueIdentifier={original.IssueIdentifier}");
    }
}

/// <summary>
/// FsCheck generators for JobDistributionRequest and its nested types.
/// </summary>
public class JobDistributionRequestArbitraries
{
    private static readonly string[] StringPool =
    [
        "owner/repo#1", "test-config-id", "agent-1", "label:dotnet",
        "user@example.com", "project-alpha", "main", "feature/test",
        "https://github.com/org/repo/pull/42", "my-project"
    ];

    public static Arbitrary<JobDistributionRequest> JobDistributionRequestArb()
    {
        var gen = GenJobDistributionRequest();
        return gen.ToArbitrary();
    }

    private static Gen<JobDistributionRequest> GenJobDistributionRequest() =>
        from issueId in GenString()
        from issueProviderConfigId in GenString()
        from repoProviderConfigId in GenString()
        from brainProviderConfigId in GenNullableString()
        from pipelineProviderConfigId in GenNullableString()
        from initiatedBy in GenString()
        from taskType in Gen.Elements(
            WorkItemTaskType.Implementation,
            WorkItemTaskType.Review,
            WorkItemTaskType.Decomposition,
            WorkItemTaskType.Consolidation)
        from agentSelector in GenString()
        from timeoutSeconds in Gen.Choose(60, 7200)
        from projectId in GenNullableString()
        from projectName in GenNullableString()
        from runType in Gen.Elements(
            PipelineRunType.Implementation,
            PipelineRunType.Review,
            PipelineRunType.DecompositionAnalysis,
            PipelineRunType.Decomposition)
        from issueDetail in GenNullable(GenIssueDetail())
        from parsedIssue in GenNullable(GenParsedIssue())
        from issueComments in GenNullableList(GenIssueComment())
        from existingAnalysis in GenNullableString()
        from providerConfigs in GenNullableList(GenProviderConfig())
        from pipelineConfiguration in GenNullable(GenPipelineConfiguration())
        from resolvedProfileId in GenNullableString()
        from qualityGateConfigs in GenNullableList(GenQualityGateConfiguration())
        from reviewerConfigs in GenNullableList(GenReviewerConfiguration())
        from mcpServers in GenNullableList(GenMcpServerConfig())
        from linkedPr in GenNullable(GenLinkedPullRequest())
        from reviewPrTargetBranch in GenNullableString()
        from reviewPrDescription in GenNullableString()
        from reviewPrAuthor in GenNullableString()
        from projectContext in GenNullable(GenDecompositionProjectContext())
        from decompositionSource in GenNullableString()
        select new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            BrainProviderConfigId = brainProviderConfigId,
            PipelineProviderConfigId = pipelineProviderConfigId,
            InitiatedBy = initiatedBy,
            TaskType = taskType,
            AgentSelector = agentSelector,
            TimeoutSeconds = timeoutSeconds,
            ProjectId = projectId,
            ProjectName = projectName,
            RunType = runType,
            IssueDetail = issueDetail,
            ParsedIssue = parsedIssue,
            IssueComments = issueComments,
            ExistingAnalysis = existingAnalysis,
            ProviderConfigs = providerConfigs,
            PipelineConfiguration = pipelineConfiguration,
            ResolvedProfileId = resolvedProfileId,
            QualityGateConfigs = qualityGateConfigs,
            ReviewerConfigs = reviewerConfigs,
            McpServers = mcpServers,
            LinkedPullRequest = linkedPr,
            ReviewPrTargetBranch = reviewPrTargetBranch,
            ReviewPrDescription = reviewPrDescription,
            ReviewPrAuthor = reviewPrAuthor,
            ProjectContext = projectContext,
            DecompositionSource = decompositionSource
        };

    private static Gen<string> GenString() =>
        Gen.Elements(StringPool);

    private static Gen<string?> GenNullableString() =>
        Gen.Frequency(
            (1, Gen.Constant<string?>(null)),
            (3, Gen.Elements(StringPool).Select<string, string?>(s => s)));

    private static Gen<T?> GenNullable<T>(Gen<T> gen) where T : class =>
        Gen.Frequency(
            (1, Gen.Constant<T?>(null)),
            (3, gen.Select<T, T?>(x => x)));

    private static Gen<IReadOnlyList<T>?> GenNullableList<T>(Gen<T> itemGen) =>
        Gen.Frequency(
            (1, Gen.Constant<IReadOnlyList<T>?>(null)),
            (3, Gen.ArrayOf(itemGen).Resize(2).Select<T[], IReadOnlyList<T>?>(a => a)));

    private static Gen<IssueDetail> GenIssueDetail() =>
        from identifier in GenString()
        from title in GenString()
        from description in GenString()
        from labels in Gen.ArrayOf(GenString()).Resize(2)
        select new IssueDetail
        {
            Identifier = identifier,
            Title = title,
            Description = description,
            Labels = labels
        };

    private static Gen<ParsedIssue> GenParsedIssue() =>
        from requirements in GenString()
        from criteria in Gen.ArrayOf(GenString()).Resize(2)
        select new ParsedIssue
        {
            RequirementsSection = requirements,
            AcceptanceCriteria = criteria
        };

    private static Gen<IssueComment> GenIssueComment() =>
        from author in GenString()
        from body in GenString()
        from id in GenString()
        select new IssueComment
        {
            Author = author,
            Body = body,
            Id = id,
            CreatedAt = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc)
        };

    private static Gen<ProviderConfig> GenProviderConfig() =>
        from kind in Gen.Elements(ProviderKind.Repository, ProviderKind.Issue, ProviderKind.Agent, ProviderKind.Brain)
        from providerType in Gen.Elements("GitHub", "GitLab", "Jira")
        from displayName in GenString()
        select new ProviderConfig
        {
            Kind = kind,
            ProviderType = providerType,
            DisplayName = displayName,
            Settings = new Dictionary<string, string> { ["key"] = "value" }
        };

    private static Gen<PipelineConfiguration> GenPipelineConfiguration() =>
        from maxRetries in Gen.Choose(0, 5)
        select new PipelineConfiguration
        {
            MaxRetries = maxRetries
        };

    private static Gen<QualityGateConfiguration> GenQualityGateConfiguration() =>
        from displayName in GenString()
        select new QualityGateConfiguration
        {
            DisplayName = displayName,
            Enabled = true
        };

    private static Gen<ReviewerConfiguration> GenReviewerConfiguration() =>
        from displayName in GenString()
        select new ReviewerConfiguration
        {
            DisplayName = displayName,
            Enabled = true,
            Agents = [new ReviewAgent { Name = "Reviewer1", Prompt = "Review code" }]
        };

    private static Gen<McpServerConfig> GenMcpServerConfig() =>
        from name in Gen.Elements("context7", "web-search", "github-mcp")
        from type in Gen.Elements("stdio", "http")
        from command in GenNullableString()
        select new McpServerConfig
        {
            Name = name,
            Type = type,
            Command = command,
            Args = ["--arg1"],
            Env = new Dictionary<string, string> { ["KEY"] = "val" }
        };

    private static Gen<LinkedPullRequest> GenLinkedPullRequest() =>
        from branchName in GenString()
        from isDraft in Gen.Elements(true, false)
        from number in Gen.Choose(1, 999)
        from url in GenString()
        select new LinkedPullRequest
        {
            BranchName = branchName,
            IsDraft = isDraft,
            Number = number,
            Url = url,
            ReviewComments = []
        };

    private static Gen<DecompositionProjectContext> GenDecompositionProjectContext() =>
        from projectName in GenString()
        from repos in Gen.ArrayOf(GenRepositoryTarget()).Resize(2)
        select new DecompositionProjectContext
        {
            ProjectName = projectName,
            Repositories = repos
        };

    private static Gen<RepositoryTarget> GenRepositoryTarget() =>
        from templateName in GenString()
        from description in GenString()
        from available in Gen.Elements(true, false)
        from decompositionEnabled in Gen.Elements(true, false)
        select new RepositoryTarget
        {
            TemplateName = templateName,
            Description = description,
            Available = available,
            DecompositionEnabled = decompositionEnabled,
            Labels = ["csharp", "dotnet"]
        };
}
