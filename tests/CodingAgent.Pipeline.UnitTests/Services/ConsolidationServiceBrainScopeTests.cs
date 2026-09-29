using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// What a consolidation run works on, and which settings it runs with:
/// <list type="bullet">
///   <item>Brain consolidation works on a brain, which several templates can share, so one runs per brain at a time.</item>
///   <item>Brain consolidation writes to the brain, so it does not run from a template whose brain is read-only.</item>
///   <item>The work item's timeout comes from the live settings with the project's overrides (#3147).</item>
/// </list>
/// </summary>
public sealed class ConsolidationServiceBrainScopeTests
{
    private const string SharedBrain = "brain-shared";

    private static readonly PipelineJobTemplate TemplateA = new()
    {
        Id = "tmpl-a", Name = "Repo A", IssueProviderId = "ip-a", RepoProviderId = "rp-a", BrainProviderId = SharedBrain
    };

    private static readonly PipelineJobTemplate TemplateB = new()
    {
        Id = "tmpl-b", Name = "Repo B", IssueProviderId = "ip-b", RepoProviderId = "rp-b", BrainProviderId = SharedBrain
    };

    private readonly Mock<IProjectStore> _projectStore = new();
    private readonly Mock<IPipelineConfigStore> _configStore = new();
    private readonly Mock<IWorkDistributor> _distributor = new();
    private readonly Mock<IConsolidationSelectorResolver> _selectorResolver = new();
    private readonly Mock<IConsolidationRunStore> _runStore = new();
    private readonly List<JobDistributionRequest> _requests = [];

    private PipelineConfiguration _liveConfig = new() { AgentTimeout = TimeSpan.FromMinutes(60) };
    private PipelineProject _project = new() { Id = WellKnownIds.DefaultProjectId, Name = "Product", TemplateIds = [TemplateA.Id, TemplateB.Id] };
    private IReadOnlyList<PipelineJobTemplate> _templates = [TemplateA, TemplateB];

    public ConsolidationServiceBrainScopeTests()
    {
        _projectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<PipelineProject> { _project });
        _projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _templates);
        _configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _liveConfig);
        _selectorResolver
            .Setup(r => r.ResolveAsync(It.IsAny<ProviderConfig?>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["dotnet"]);
        _distributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((request, _) => _requests.Add(request))
            .ReturnsAsync(() => new DistributionResult(Success: true, WorkItemId: Guid.NewGuid().ToString(), ErrorMessage: null, Queued: true));
        _runStore.Setup(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _runStore.Setup(s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private ConsolidationService CreateSut()
    {
        // The bootstrap configuration deliberately differs from the live one: it must not be read.
        var bootstrap = new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath(), AgentTimeout = TimeSpan.FromMinutes(30) };
        return new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            bootstrap,
            _projectStore.Object,
            new Mock<IPipelineRunHistoryService>().Object,
            _runStore.Object,
            new Mock<IHarnessSuggestionStore>().Object,
            new Mock<IProviderConfigStore>().Object,
            WorkspaceManager: new ConsolidationWorkspaceManager(new LoggerConfiguration().CreateLogger(), bootstrap),
            WorkDistributor: _distributor.Object,
            SelectorResolver: _selectorResolver.Object,
            PipelineConfigStore: _configStore.Object));
    }

    // ── One brain consolidation per brain ───────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_BrainConsolidation_TemplatesSharingABrainShareTheKey()
    {
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateA.Id), CancellationToken.None);
        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateB.Id), CancellationToken.None);

        _requests.Select(r => r.IssueIdentifier.Value).Should().Equal(
            $"BrainConsolidation:{SharedBrain}", $"BrainConsolidation:{SharedBrain}");
        _requests.Select(r => r.ConsolidationTemplateId).Should().Equal(new[] { TemplateA.Id, TemplateB.Id },
            "the run still records the template it was triggered from, whose settings it uses");
    }

    [Fact]
    public async Task TriggerAsync_RefactoringScan_KeyIsTheTemplate()
    {
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(TemplateA.Id), CancellationToken.None);

        _requests.Should().ContainSingle().Which.IssueIdentifier.Value.Should().Be($"RefactoringDetection:{TemplateA.Id}");
    }

    // ── A read-only brain is not consolidated ───────────────────────────────────

    [Fact]
    public async Task TriggerAsync_TemplateMarksItsBrainReadOnly_BrainConsolidationIsRejected()
    {
        _templates = [TemplateA with { BrainReadOnly = true }, TemplateB];
        var sut = CreateSut();

        var run = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateA.Id), CancellationToken.None);

        run.Should().BeNull();
        _requests.Should().BeEmpty("no work item is created for a read-only brain");
        _runStore.Verify(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TriggerAsync_BrainReadOnlyGlobally_BrainConsolidationIsRejected()
    {
        _liveConfig = _liveConfig with { BrainReadOnly = true };
        var sut = CreateSut();

        var run = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateA.Id), CancellationToken.None);

        run.Should().BeNull();
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TriggerAsync_ProjectReenablesBrainWrites_BrainConsolidationRuns()
    {
        _liveConfig = _liveConfig with { BrainReadOnly = true };
        _project = _project with { BrainReadOnly = false };
        var sut = CreateSut();

        var run = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateA.Id), CancellationToken.None);

        run.Should().NotBeNull();
        _requests.Should().ContainSingle();
    }

    [Fact]
    public async Task TriggerAsync_ReadOnlyBrain_RefactoringScanStillRuns()
    {
        _templates = [TemplateA with { BrainReadOnly = true }, TemplateB];
        var sut = CreateSut();

        var run = await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(TemplateA.Id), CancellationToken.None);

        run.Should().NotBeNull("only brain consolidation writes to the brain");
    }

    // ── The work item's timeout comes from the live settings (#3147) ────────────

    [Fact]
    public async Task TriggerAsync_TimeoutComesFromTheLiveGlobalSettings()
    {
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateA.Id), CancellationToken.None);

        _requests.Should().ContainSingle().Which.TimeoutSeconds.Should().Be(3600);
    }

    [Fact]
    public async Task TriggerAsync_TimeoutAppliesTheProjectOverride()
    {
        _project = _project with { AgentTimeout = TimeSpan.FromMinutes(90) };
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(TemplateA.Id), CancellationToken.None);

        _requests.Should().ContainSingle().Which.TimeoutSeconds.Should().Be(5400);
    }

    [Fact]
    public async Task TriggerAsync_HarnessSuggestions_UseTheGlobalTimeout()
    {
        _project = _project with { AgentTimeout = TimeSpan.FromMinutes(90) };
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        _requests.Should().ContainSingle().Which.TimeoutSeconds.Should().Be(3600,
            "harness suggestions have no template, so no project override applies");
    }

    [Fact]
    public async Task TriggerAsync_WorkspaceLiesUnderTheLiveWorkspaceBaseDirectory()
    {
        var liveBase = Path.Combine(Path.GetTempPath(), "live-workspaces");
        _liveConfig = _liveConfig with { WorkspaceBaseDirectory = liveBase };
        var sut = CreateSut();

        var run = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateA.Id), CancellationToken.None);

        _requests.Should().ContainSingle().Which.ConsolidationWorkspacePath
            .Should().Be(Path.Combine(liveBase, "consolidation", run!.RunId),
                "the agent works in this directory, so it follows the settings the agent runs with, not the web's startup copy");
    }

    [Fact]
    public async Task TriggerAsync_SelectorLabelsAreResolvedWithTheLiveSettings()
    {
        _liveConfig = _liveConfig with { DefaultRequiredAgentLabels = "live-label" };
        var sut = CreateSut();

        await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, new TemplateId(TemplateA.Id), CancellationToken.None);

        _selectorResolver.Verify(r => r.ResolveAsync(
            It.IsAny<ProviderConfig?>(),
            It.Is<PipelineConfiguration>(c => c.DefaultRequiredAgentLabels == "live-label"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
