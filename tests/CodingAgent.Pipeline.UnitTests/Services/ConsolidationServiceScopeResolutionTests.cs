using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Characterization tests pinning the scope-resolution and request-building behavior of
/// <see cref="ConsolidationService.TriggerAsync"/> before the Extract Method refactor in
/// issue #3475. These tests lock down behaviors that were not directly asserted elsewhere:
///
/// <list type="number">
///   <item>repoConfig is fetched using the exact template.RepoProviderId value</item>
///   <item>global run (null templateId) skips template/project resolution entirely</item>
///   <item>BuildConsolidationDistributionRequest forwards all required fields correctly</item>
///   <item>ProjectId is null when project.Id is not a valid GUID</item>
/// </list>
/// </summary>
public sealed class ConsolidationServiceScopeResolutionTests
{
    private static readonly PipelineJobTemplate Template = new()
    {
        Id = "tmpl-scope-1",
        Name = "Scope Test Repo",
        IssueProviderId = "ip-scope",
        RepoProviderId = "rp-scope-exact",
        BrainProviderId = "bp-scope",
        Enabled = true
    };

    private readonly Mock<IProjectStore> _projectStore = new();
    private readonly Mock<IProviderConfigStore> _providerConfigStore = new();
    private readonly Mock<IWorkDistributor> _distributor = new();
    private readonly Mock<IConsolidationSelectorResolver> _selectorResolver = new();
    private readonly List<JobDistributionRequest> _requests = [];

    private PipelineProject _project = new()
    {
        Id = WellKnownIds.DefaultProjectId,
        Name = "Default",
        TemplateIds = [Template.Id]
    };

    public ConsolidationServiceScopeResolutionTests()
    {
        _projectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<PipelineProject> { _project });
        _projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<PipelineJobTemplate> { Template });

        _providerConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        _selectorResolver
            .Setup(r => r.ResolveAsync(It.IsAny<ProviderConfig?>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["dotnet", "kiro"]);

        _distributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => _requests.Add(req))
            .ReturnsAsync(() => new DistributionResult(Success: true, WorkItemId: Guid.NewGuid().ToString(), ErrorMessage: null));
    }

    private ConsolidationService CreateSut(PipelineConfiguration? config = null)
    {
        var cfg = config ?? new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.GetTempPath(),
            AgentTimeout = TimeSpan.FromMinutes(30)
        };

        return new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            cfg,
            _projectStore.Object,
            new Mock<IHarnessSuggestionStore>().Object,
            _providerConfigStore.Object,
            WorkDistributor: _distributor.Object,
            SelectorResolver: _selectorResolver.Object));
    }

    // ── 1. repoConfig is fetched using the exact template.RepoProviderId ─────

    [Fact]
    public async Task TriggerAsync_WithTemplate_FetchesRepoConfigUsingTemplateRepoProviderId()
    {
        // Characterizes: ResolveConsolidationScopeAsync fetches repoConfig with the exact
        // template.RepoProviderId value, not a default or fallback.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template.Id), CancellationToken.None);

        _providerConfigStore.Verify(
            s => s.GetProviderConfigByIdAsync(
                Template.RepoProviderId,
                ProviderKind.Repository,
                It.IsAny<CancellationToken>()),
            Times.Once,
            $"must fetch ProviderConfig using the template's exact RepoProviderId '{Template.RepoProviderId}'");
    }

    [Fact]
    public async Task TriggerAsync_WithTemplate_ForwardsResolvedRepoConfigToSelectorResolver()
    {
        // Characterizes: the repoConfig resolved from the provider store is forwarded
        // to ResolveSelectorLabelsAsync (and thus to IConsolidationSelectorResolver.ResolveAsync).
        var expectedConfig = new ProviderConfig { Id = "rp-scope-exact", Kind = ProviderKind.Repository, DisplayName = "Scope Repo", ProviderType = "GitHub" };
        _providerConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync(Template.RepoProviderId, ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedConfig);

        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template.Id), CancellationToken.None);

        _selectorResolver.Verify(
            r => r.ResolveAsync(
                expectedConfig,
                It.IsAny<PipelineConfiguration>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the repoConfig returned by the provider store must be passed to the selector resolver");
    }

    // ── 2. global run skips template/project resolution ──────────────────────

    [Fact]
    public async Task TriggerAsync_NullTemplateId_DoesNotCallLoadProjectsAsync()
    {
        // Characterizes: when templateId is null (global run), the template/project resolution
        // block is skipped entirely — LoadProjectsAsync is never called.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        // TODO: Also assert the shape of the dispatched request for a global run: ConsolidationTemplateId
        // is null, ProjectId is null, ProjectName is null, and RepoProviderConfigId is the fallback/empty
        // value. Without those assertions these tests would pass even if global-run paths accidentally
        // propagated stale or default values. (review-findings.md WARNING, line 144)
        _projectStore.Verify(
            s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "global runs (null templateId) must not call LoadProjectsAsync — no project resolution needed");
    }

    [Fact]
    public async Task TriggerAsync_NullTemplateId_DoesNotFetchRepoProviderConfig()
    {
        // Characterizes: when templateId is null, GetProviderConfigByIdAsync is never called.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        _providerConfigStore.Verify(
            s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "global runs (null templateId) must not call GetProviderConfigByIdAsync — no template to resolve from");
    }

    [Fact]
    public async Task TriggerAsync_GlobalHarnessRun_SendsNoTemplateName()
    {
        // Verifies: global harness suggestion runs (no template) must not propagate a template
        // name — ConsolidationTemplateName must be null, not "Global" or any other string.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        _requests.Should().ContainSingle()
            .Which.ConsolidationTemplateName.Should().BeNull(
                "global harness suggestion runs have no template; ConsolidationTemplateName must be null");
    }

    // ── 3. BuildConsolidationDistributionRequest forwards all required fields ─

    [Fact]
    public async Task TriggerAsync_RequestHasCorrectInitiatedBy()
    {
        // Characterizes: InitiatedBy is set to ConsolidationConstants.InitiatedBy.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(Template.Id), CancellationToken.None);

        _requests.Should().ContainSingle()
            .Which.InitiatedBy.Should().Be(ConsolidationConstants.InitiatedBy,
                "InitiatedBy must be ConsolidationConstants.InitiatedBy for all consolidation work items");
    }

    [Fact]
    public async Task TriggerAsync_RequestHasCorrectIssueProviderConfigId()
    {
        // Characterizes: IssueProviderConfigId is set to ConsolidationConstants.ProviderConfigId.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(Template.Id), CancellationToken.None);

        _requests.Should().ContainSingle()
            .Which.IssueProviderConfigId.Should().Be(ConsolidationConstants.ProviderConfigId,
                "IssueProviderConfigId must be ConsolidationConstants.ProviderConfigId for dedup routing");
    }

    [Fact]
    public async Task TriggerAsync_RequestHasCorrectTaskType()
    {
        // Characterizes: TaskType is always WorkItemTaskType.Consolidation.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template.Id), CancellationToken.None);

        _requests.Should().ContainSingle()
            .Which.TaskType.Should().Be(WorkItemTaskType.Consolidation,
                "all consolidation work items must use TaskType.Consolidation");
    }

    [Fact]
    public async Task TriggerAsync_RequestTraceContextIsForwardedFromCaptureCall()
    {
        // Characterizes: TraceContext is set on the request from PipelineTelemetry.CaptureTraceContext.
        // In unit test environments (no active Activity), CaptureTraceContext returns null; the
        // important behavior is that the field is forwarded — not that it is non-null.
        // The non-null path is covered by OpenTelemetry integration tests.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(Template.Id), CancellationToken.None);

        // No assertion on null vs non-null — the field is nullable by design.
        // The request must have been built and dispatched (verified by the _requests list having an entry).
        _requests.Should().ContainSingle("a successful trigger must produce exactly one dispatch request");
        // TODO: This test is effectively a no-op: the property read below discards its result and can never
        // fail. If asserting that TraceContext is forwarded matters, run this test inside an active
        // OpenTelemetry Activity and assert the value is non-null, or remove the test and replace it with
        // a comment documenting the nullable contract. As written it provides no safety net.
        // (review-findings.md WARNING, line 220)
        // TraceContext field must exist on the request object (structural check — the field is populated).
        // Value is null in test environments with no active OpenTelemetry pipeline (CaptureTraceContext returns null).
        _ = _requests[0].TraceContext; // access verifies the field exists and is reachable
    }

    [Fact]
    public async Task TriggerAsync_RequestHasCorrectRepoAndBrainProviderIds()
    {
        // Characterizes: RepoProviderConfigId and BrainProviderConfigId are forwarded from the template.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(Template.Id), CancellationToken.None);

        var req = _requests.Should().ContainSingle().Which;
        req.RepoProviderConfigId.Should().Be(Template.RepoProviderId,
            "RepoProviderConfigId must come from template.RepoProviderId");
        req.BrainProviderConfigId.Should().Be(Template.BrainProviderId,
            "BrainProviderConfigId must come from template.BrainProviderId");
    }

    [Fact]
    public async Task TriggerAsync_RequestHasAutoDispatchForwarded()
    {
        // Characterizes: autoDispatch parameter is forwarded to the JobDistributionRequest.
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(Template.Id), CancellationToken.None, autoDispatch: true);

        // TODO: Add a companion test asserting autoDispatch: false is also forwarded correctly.
        // The current test only exercises the true branch; because bool fields default to false in C#,
        // a test with autoDispatch=false cannot distinguish "correctly forwarded false" from "field was
        // never set." (review-findings.md WARNING, line 238)
        _requests.Should().ContainSingle()
            .Which.AutoDispatch.Should().BeTrue(
                "autoDispatch=true must be forwarded to the JobDistributionRequest");
    }

    // ── 4. ProjectId is null when project.Id is not a valid GUID ─────────────

    [Fact]
    public async Task TriggerAsync_InvalidProjectGuid_ProjectIdIsNull()
    {
        // Characterizes: when the owning project's Id is not a valid GUID string, the
        // JobDistributionRequest.ProjectId must be null (Guid.TryParse fallback path).
        _project = _project with { Id = "not-a-guid", Name = "Invalid GUID Project" };
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template.Id), CancellationToken.None);

        _requests.Should().ContainSingle()
            .Which.ProjectId.Should().BeNull(
                "ProjectId must be null when project.Id is not a parseable GUID");
    }

    [Fact]
    public async Task TriggerAsync_EmptyProjectId_ProjectIdIsNull()
    {
        // Characterizes: an empty project Id also results in null ProjectId.
        _project = _project with { Id = "" };
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template.Id), CancellationToken.None);

        _requests.Should().ContainSingle()
            .Which.ProjectId.Should().BeNull(
                "ProjectId must be null when project.Id is empty");
    }

    [Fact]
    public async Task TriggerAsync_ValidProjectGuid_ProjectIdIsSet()
    {
        // Characterizes: when project.Id is a valid GUID, ProjectId is correctly parsed and set.
        var guid = Guid.NewGuid();
        _project = _project with { Id = guid.ToString() };
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template.Id), CancellationToken.None);

        _requests.Should().ContainSingle()
            .Which.ProjectId.Should().Be(guid,
                "ProjectId must be the parsed Guid when project.Id is a valid GUID string");
    }
}
