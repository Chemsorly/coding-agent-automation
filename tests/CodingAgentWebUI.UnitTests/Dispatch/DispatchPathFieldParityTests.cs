using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Bidirectional field-parity drift test for the two dispatch paths that produce
/// <see cref="JobAssignmentMessage"/> instances:
///
/// 1. Legacy/SignalR: <c>AgentJobDispatcher.BuildBaseJobAssignmentMessage(DispatchPipelineContext)</c>
/// 2. K8s/DB: <c>DispatchOrchestrationService.MapToRequest(…)</c> → <c>DbWorkDistributorBase.BuildJobAssignmentMessage(…)</c>
///
/// These paths MUST produce equivalent output (same non-null fields for the same logical input).
/// This test uses reflection to enumerate ALL settable properties on <see cref="JobAssignmentMessage"/>,
/// catching NEW fields automatically without needing manual test updates.
///
/// Prevents regressions like issue #1686 where MapToRequest silently omitted
/// ProjectSteeringContent and RepoSteeringContent.
/// </summary>
public sealed class DispatchPathFieldParityTests
{
    /// <summary>
    /// Fields intentionally different between paths, with documented justification.
    /// </summary>
    private static readonly HashSet<string> ExcludedFields = new()
    {
        // Legacy path uses RunId (from PipelineRun.RunId), K8s path uses WorkItemId (Guid).
        // These are intentionally different identifiers by design.
        "JobId",

        // Legacy path injects ProjectSecrets directly from PipelineProject.Secrets.
        // K8s path does NOT serialize secrets to the WorkItem payload (security) — they are
        // injected at delivery time from IProjectStore via the PendingWorkItemDrainService.
        "ProjectSecrets",
    };

    /// <summary>
    /// Verifies that every non-excluded property on <see cref="JobAssignmentMessage"/> that is
    /// populated by one dispatch path is also populated by the other. Bidirectional: catches
    /// drift in BOTH directions.
    /// </summary>
    [Fact]
    public void DispatchPathParity_AllFieldsPopulatedByBothPaths()
    {
        // 1. Build fully-populated DispatchPreparationResult (all fields non-null)
        var result = BuildFullyPopulatedPreparationResult();

        // 2. K8s path: MapToRequest → BuildJobAssignmentMessage
        var request = InvokeMapToRequest(result, WorkItemTaskType.Implementation, PipelineRunType.Implementation);
        var k8sMessage = DbWorkDistributorBase.BuildJobAssignmentMessage(Guid.NewGuid(), request);

        // 3. Legacy path: BuildBaseJobAssignmentMessage + customize (mimics full dispatch flow)
        var ctx = BuildEquivalentDispatchPipelineContext(result);
        var legacyMessage = InvokeBuildBaseJobAssignmentMessage(ctx);

        // Apply the same customization the legacy dispatch applies after building the base message
        // (see AgentJobDispatcher.PrepareImplementationDispatchAsync customize lambda)
        legacyMessage = legacyMessage with
        {
            ExistingAnalysis = result.ExistingAnalysis,
            ForceRefreshAnalysis = result.ForceRefreshAnalysis,
            StalenessSignal = result.StalenessSignal,
            AnalysisRefreshCount = result.AnalysisRefreshCount,
            QualityGateConfigs = result.QualityGateConfigs,
            ReviewerConfigs = result.ReviewerConfigs,
            // TraceContext is captured from ambient OpenTelemetry Activity in production (CaptureTraceContext()).
            // In tests there's no active span so it returns null. Override to match K8s path which uses pre-captured value.
            TraceContext = result.TraceContext
        };

        // 4. Reflect over ALL properties on JobAssignmentMessage
        var properties = typeof(JobAssignmentMessage)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => !ExcludedFields.Contains(p.Name))
            .ToList();

        // Sanity check: we expect a reasonable number of properties (guards against reflection failure)
        properties.Count.Should().BeGreaterThan(20,
            "Expected 20+ public properties on JobAssignmentMessage — reflection may be broken");

        // 5. Bidirectional assertion
        var legacyOnly = new List<string>();
        var k8sOnly = new List<string>();

        foreach (var prop in properties)
        {
            var legacyValue = prop.GetValue(legacyMessage);
            var k8sValue = prop.GetValue(k8sMessage);

            // If Legacy has it non-null, K8s must too
            if (legacyValue is not null && !IsDefaultValue(legacyValue, prop.PropertyType) && k8sValue is null)
                legacyOnly.Add(prop.Name);

            // If K8s has it non-null, Legacy must too
            if (k8sValue is not null && !IsDefaultValue(k8sValue, prop.PropertyType) && legacyValue is null)
                k8sOnly.Add(prop.Name);
        }

        // Report all failures at once for clear diagnostics
        var failures = new List<string>();
        if (legacyOnly.Count > 0)
            failures.Add($"Legacy path populates but K8s path does NOT: [{string.Join(", ", legacyOnly)}]");
        if (k8sOnly.Count > 0)
            failures.Add($"K8s path populates but Legacy path does NOT: [{string.Join(", ", k8sOnly)}]");

        failures.Should().BeEmpty(
            "Dispatch paths must produce equivalent JobAssignmentMessage fields. " +
            "If a field is intentionally different, add it to ExcludedFields with justification.");
    }

    /// <summary>
    /// Guard test: the exclusion list MUST stay minimal. Adding a new exclusion requires
    /// documented justification that the field is genuinely different between paths by design.
    /// </summary>
    [Fact]
    public void DispatchPathParity_ExclusionListIsMinimal()
    {
        ExcludedFields.Should().HaveCount(2,
            "Only JobId and ProjectSecrets are intentionally different between paths. " +
            "If you need to add a new exclusion, document WHY in the ExcludedFields set " +
            "and update this count assertion.");
    }

    /// <summary>
    /// Verify all excluded fields actually exist on <see cref="JobAssignmentMessage"/>.
    /// Prevents stale exclusions from hiding when properties are renamed/removed.
    /// </summary>
    [Fact]
    public void DispatchPathParity_AllExcludedFieldsExistOnMessage()
    {
        var propertyNames = typeof(JobAssignmentMessage)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .ToHashSet();

        foreach (var excluded in ExcludedFields)
        {
            propertyNames.Should().Contain(excluded,
                $"Excluded field '{excluded}' no longer exists on JobAssignmentMessage — remove it from ExcludedFields");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the private static <c>DispatchOrchestrationService.MapToRequest</c> via reflection.
    /// </summary>
    private static JobDistributionRequest InvokeMapToRequest(
        DispatchPreparationResult result,
        WorkItemTaskType taskType,
        PipelineRunType runType)
    {
        var method = typeof(DispatchOrchestrationService)
            .GetMethod("MapToRequest", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("MapToRequest must exist as a private static method on DispatchOrchestrationService");

        var returned = method!.Invoke(null, [result, taskType, runType]);
        returned.Should().NotBeNull();
        return (JobDistributionRequest)returned!;
    }

    /// <summary>
    /// Invokes the private static <c>AgentJobDispatcher.BuildBaseJobAssignmentMessage</c> via reflection.
    /// </summary>
    private static JobAssignmentMessage InvokeBuildBaseJobAssignmentMessage(
        AgentJobDispatcher.DispatchPipelineContext ctx)
    {
        var method = typeof(AgentJobDispatcher)
            .GetMethod("BuildBaseJobAssignmentMessage", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("BuildBaseJobAssignmentMessage must exist as a private static method on AgentJobDispatcher");

        var returned = method!.Invoke(null, [ctx]);
        returned.Should().NotBeNull();
        return (JobAssignmentMessage)returned!;
    }

    /// <summary>
    /// Builds a fully-populated <see cref="DispatchPreparationResult"/> with ALL fields non-null/non-default,
    /// so that any omission in a dispatch path is detectable as a null vs non-null difference.
    /// </summary>
    private static DispatchPreparationResult BuildFullyPopulatedPreparationResult()
    {
        var repoProviderId = "repo-provider-1";
        var agentProviderId = "agent-provider-1";
        var brainProviderId = "brain-provider-1";
        var pipelineProviderId = "pipeline-provider-1";
        var issueProviderId = "issue-provider-1";
        var runId = Guid.NewGuid().ToString();

        var repoProviderConfig = new ProviderConfig
        {
            Id = repoProviderId,
            DisplayName = "Test Repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            SteeringContent = "repo steering content for parity test"
        };

        return new DispatchPreparationResult
        {
            ResolvedProfile = new AgentProfile
            {
                Id = "profile-1",
                DisplayName = "Test Profile",
                AgentProviderConfigId = agentProviderId,
                MatchLabels = ["dotnet", "kiro"],
                McpServers = [new McpServerConfig { Name = "test-mcp", Command = "test-cmd" }]
            },
            QualityGateConfigs = [new QualityGateConfiguration { Id = "qg-1", DisplayName = "Build" }],
            ReviewerConfigs = [new ReviewerConfiguration { Id = "rc-1", DisplayName = "Correctness", Agents = [new ReviewAgent { Name = "Correctness", Prompt = "Review for correctness" }] }],
            ProviderConfigs = [repoProviderConfig],
            PipelineConfiguration = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(30)
            },
            IssueDetail = new IssueDetail
            {
                Identifier = "owner/repo#42",
                Title = "Test issue",
                Description = "Test description",
                Labels = ["bug", "agent:next"]
            },
            ParsedIssue = new ParsedIssue
            {
                AcceptanceCriteria = ["criterion 1"],
                RequirementsSection = "Requirements section content"
            },
            IssueComments = [new IssueComment { Id = "comment-1", Author = "user1", Body = "comment body", CreatedAt = DateTime.UtcNow }],
            ExistingAnalysis = "existing analysis content",
            ForceRefreshAnalysis = true,
            StalenessSignal = "commit-ahead",
            AnalysisRefreshCount = 2,
            CreatedRun = CreateFullyPopulatedRun(
                runId, issueProviderId, repoProviderId, agentProviderId,
                brainProviderId, pipelineProviderId),
            Project = new PipelineProject
            {
                Id = "project-1",
                Name = "Test Project",
                SteeringContent = "project steering content for parity test",
                Secrets = new Dictionary<string, string> { ["SECRET_KEY"] = "secret-value" }
            },
            McpServers = [new McpServerConfig { Name = "test-mcp", Command = "test-cmd" }],
            TraceContext = new Dictionary<string, string>
            {
                ["traceparent"] = "00-trace-id-span-id-01",
                ["tracestate"] = "vendor=state"
            }
        };
    }

    /// <summary>
    /// Builds an equivalent <see cref="AgentJobDispatcher.DispatchPipelineContext"/> from a
    /// <see cref="DispatchPreparationResult"/>, mimicking how the legacy path constructs its context.
    /// </summary>
    private static AgentJobDispatcher.DispatchPipelineContext BuildEquivalentDispatchPipelineContext(
        DispatchPreparationResult result)
    {
        return new AgentJobDispatcher.DispatchPipelineContext
        {
            Agent = new AgentEntry { AgentId = "agent-1", ConnectionId = "conn-1", Hostname = "localhost", Labels = ["dotnet", "kiro"], RegisteredAt = DateTimeOffset.UtcNow },
            Run = result.CreatedRun,
            Profile = result.ResolvedProfile,
            IssueIdentifier = result.IssueDetail.Identifier,
            IssueDetail = result.IssueDetail,
            ParsedIssue = result.ParsedIssue,
            IssueComments = result.IssueComments,
            RepoProviderId = result.CreatedRun.RepoProviderConfigId,
            AgentProviderId = result.ResolvedProfile.AgentProviderConfigId,
            BrainProviderId = result.CreatedRun.BrainProviderConfigId,
            PipelineProviderId = result.CreatedRun.PipelineProviderConfigId,
            IssueProviderId = result.CreatedRun.IssueProviderConfigId,
            ProviderConfigs = result.ProviderConfigs,
            Config = result.PipelineConfiguration,
            InitiatedBy = result.CreatedRun.InitiatedBy ?? "test-user",
            Project = result.Project
        };
    }

    /// <summary>
    /// Determines whether a value is the default for its type (0 for int, false for bool, etc.).
    /// Used to avoid false positives on value types that can't be null.
    /// </summary>
    private static bool IsDefaultValue(object value, Type type)
    {
        if (!type.IsValueType)
            return false;

        var defaultValue = Activator.CreateInstance(type);
        return Equals(value, defaultValue);
    }

    /// <summary>
    /// Creates a PipelineRun with all provider-related fields populated.
    /// </summary>
    private static PipelineRun CreateFullyPopulatedRun(
        string runId,
        string issueProviderId,
        string repoProviderId,
        string agentProviderId,
        string brainProviderId,
        string pipelineProviderId)
    {
        var run = PipelineRun.CreateImplementation(
            runId: runId,
            issueIdentifier: "owner/repo#42",
            issueTitle: "Test issue",
            issueProviderConfigId: issueProviderId,
            repoProviderConfigId: repoProviderId,
            initiatedBy: "test-user",
            agentProviderConfigId: agentProviderId,
            brainProviderConfigId: brainProviderId);

        // Set mutable properties not available via factory
        run.PipelineProviderConfigId = pipelineProviderId;
        run.ResolvedProfileId = "profile-1";
        run.ProjectId = "project-1";

        return run;
    }
}
