using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Moq;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// Spec 053 task 3.2 (review-2 M4): records what agents received before spec 053, from the real pre-053 code, while it
/// still exists. Over the req 7.14 fixture, per template × task type:
/// <list type="bullet">
///   <item>the dispatch: the composition of <c>DispatchOrchestrationService.ResolveRequiredLabelsInternalAsync</c> and
///   <c>ResolveProfileByLabelsAsync</c> (and the same for <c>TriageService.BuildDistributionRequestAsync</c>):
///   <see cref="RepositorySettings"/>, <see cref="LabelResolver"/>, <see cref="ProfileResolver"/>;</item>
///   <item>consolidation: <see cref="ConsolidationSelectorResolver"/> as the API registers it (static configuration
///   <c>new PipelineConfiguration()</c>, the stored configuration live, the job template filter on);</item>
///   <item>the claim: the real <see cref="AssignmentEnricher"/> (pinned profile, or re-resolution from the selector
///   for an operator triage and a bare consolidation selector), the quality gates and reviewers for the labels it
///   passes, the agent provider config it names, and the MCP servers it merges.</item>
/// </list>
/// Run with <c>GOLDEN_WRITE=1</c> to write the files; otherwise it compares and fails on a difference. Deleted with the
/// old code in task 4.12; the files stay and are never regenerated.
/// </summary>
public sealed class TodayRoutesGoldenTests
{
    private static readonly WorkItemTaskType[] DispatchedTaskTypes =
        [WorkItemTaskType.Implementation, WorkItemTaskType.Review, WorkItemTaskType.Decomposition];

    [Fact]
    public async Task TodaysRoutes_EqualTheGoldenFiles_Req7_14()
    {
        var cells = new List<GoldenCell>();
        var configs = new List<GoldenAgentConfig>();
        foreach (var scenario in AgentRoutingLegacyFixture.Scenarios)
            await ComputeAsync(scenario, cells, configs);

        if (GoldenFiles.WriteMode)
        {
            GoldenFiles.Write(GoldenFiles.TodayRoutes, cells);
            GoldenFiles.Write(GoldenFiles.Assignment, configs);
            return;
        }

        cells.Should().BeEquivalentTo(GoldenFiles.Read<GoldenCell>(GoldenFiles.TodayRoutes), o => o.WithStrictOrdering());
        configs.Should().BeEquivalentTo(GoldenFiles.Read<GoldenAgentConfig>(GoldenFiles.Assignment), o => o.WithStrictOrdering());
    }

    private static async Task ComputeAsync(LegacyRoutingScenario scenario, List<GoldenCell> cells, List<GoldenAgentConfig> configs)
    {
        var profiles = scenario.ProfileJson
            .Select(json => JsonSerializer.Deserialize<AgentProfile>(json, PipelineJsonOptions.Lenient)!)
            .ToList();
        var storedConfig = new PipelineConfiguration { DefaultRequiredAgentLabels = scenario.DefaultRequiredAgentLabels };
        var dispatchable = scenario.ToolsByKey.Keys.ToHashSet(StringComparer.Ordinal);
        var claim = new Claim(scenario, profiles);

        foreach (var template in scenario.Templates.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var repository = scenario.ProviderConfigs.Single(p => p.Id == template.RepoProviderId);

            // Dispatch: the same composition in DispatchOrchestrationService and TriageService.
            var enrichedRepository = RepositorySettings.WithTemplateSettings(
                repository, RepositorySettings.TemplateFor(template.RepoProviderId, scenario.Templates));
            var labels = LabelResolver.ResolveRequiredLabels(enrichedRepository, storedConfig);
            var dispatchProfile = ProfileResolver.ResolveByRequiredLabels(profiles, labels);

            foreach (var taskType in DispatchedTaskTypes)
                Add(scenario, template, taskType, await claim.PinnedAsync(template, dispatchProfile), cells, configs);

            // Triage: the loop pins the profile like the other task types; an operator triage pins none
            // (TriageService sets only the selector), so its claim re-resolves from the selector.
            var loopTriage = await claim.PinnedAsync(template, dispatchProfile);
            var operatorTriage = dispatchProfile is null ? null : await claim.UnpinnedAsync(template, dispatchProfile.JobTemplateKey());
            Add(scenario, template, WorkItemTaskType.Triage, loopTriage, cells, configs);
            cells[^1] = cells[^1] with
            {
                OperatorTriageClaimProfileId = operatorTriage?.Profile.Id,
                OperatorTriageQualityGates = operatorTriage?.QualityGates,
            };

            // Consolidation: the selector resolver as the API registers it, then the consolidation claim.
            var selection = ConsolidationSelectorResolver.Resolve(
                RepositorySettings.WithTemplateSettings(repository, template),
                new PipelineConfiguration(),
                storedConfig,
                profiles,
                dispatchable);
            var consolidation = selection is null ? null : await claim.ConsolidationAsync(template, selection);
            Add(scenario, template, WorkItemTaskType.Consolidation, consolidation, cells, configs);
        }
    }

    private static void Add(
        LegacyRoutingScenario scenario,
        PipelineJobTemplate template,
        WorkItemTaskType taskType,
        ClaimResult? result,
        List<GoldenCell> cells,
        List<GoldenAgentConfig> configs)
    {
        var settings = result?.AgentConfig?.Settings;
        cells.Add(new GoldenCell(
            scenario.Name,
            template.Name,
            taskType.ToString(),
            result?.Profile.Id,
            result?.Profile.DisplayName,
            result?.Profile.JobTemplateKey(),
            settings?.GetValueOrDefault(ProviderSettingKeys.Model),
            settings?.GetValueOrDefault(ProviderSettingKeys.Effort),
            result?.QualityGates ?? [],
            result?.Reviewers ?? [],
            result?.McpServers ?? []));
        configs.Add(new GoldenAgentConfig(
            scenario.Name,
            template.Name,
            taskType.ToString(),
            result?.AgentConfig?.ProviderType,
            settings is null ? null : new SortedDictionary<string, string>(settings, StringComparer.Ordinal)));
    }

    private sealed record ClaimResult(
        AgentProfile Profile,
        ProviderConfig? AgentConfig,
        IReadOnlyList<string> QualityGates,
        IReadOnlyList<string> Reviewers,
        IReadOnlyList<string> McpServers);

    /// <summary>The real <see cref="AssignmentEnricher"/> over the scenario, with the dispatch infrastructure stubbed.</summary>
    private sealed class Claim
    {
        private readonly LegacyRoutingScenario _scenario;
        private readonly IReadOnlyList<AgentProfile> _profiles;
        private readonly CapturingInfrastructure _infra = new();
        private readonly CapturingConsolidationPreparer _preparer = new();
        private readonly AssignmentEnricher _enricher;

        public Claim(LegacyRoutingScenario scenario, IReadOnlyList<AgentProfile> profiles)
        {
            _scenario = scenario;
            _profiles = profiles;
            var profileStore = new Mock<IAgentProfileStore>();
            profileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles);
            var projectStore = new Mock<IProjectStore>();
            projectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([scenario.Project]);
            projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(scenario.Templates);
            _enricher = new AssignmentEnricher(
                _infra, profileStore.Object, _preparer, projectStore.Object,
                new ConsolidationTemplateResolver(projectStore.Object), Serilog.Log.Logger);
        }

        public Task<ClaimResult?> PinnedAsync(PipelineJobTemplate template, AgentProfile? dispatchProfile) =>
            dispatchProfile is null
                ? Task.FromResult<ClaimResult?>(null)
                : EnrichAsync(Identity(template, WorkItemTaskType.Implementation, dispatchProfile.JobTemplateKey(), dispatchProfile.Id));

        public Task<ClaimResult?> UnpinnedAsync(PipelineJobTemplate template, string selector) =>
            EnrichAsync(Identity(template, WorkItemTaskType.Implementation, selector, resolvedProfileId: null));

        public async Task<ClaimResult?> ConsolidationAsync(PipelineJobTemplate template, ConsolidationAgentSelection selection)
        {
            _preparer.Captured = null;
            var identity = Identity(template, WorkItemTaskType.Consolidation, selection.AgentSelector, selection.ResolvedProfileId) with
            {
                ConsolidationTemplateId = template.Id,
                ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            };
            var enriched = await _enricher.EnrichAsync(identity, _scenario.Project, CancellationToken.None);
            if (enriched is null || _preparer.Captured is null)
                return null;
            // Consolidation runs have no quality gates or reviewers.
            return new ClaimResult(_preparer.Captured, AgentConfig(enriched.AgentProviderConfigId), [], [], McpNames(enriched));
        }

        private async Task<ClaimResult?> EnrichAsync(JobDistributionRequest identity)
        {
            _infra.Captured = null;
            var enriched = await _enricher.EnrichAsync(identity, _scenario.Project, CancellationToken.None);
            if (enriched is null || _infra.Captured is not { } core)
                return null;

            var profile = _profiles.Single(p => p.Id == enriched.ResolvedProfileId);
            var gates = new QualityGateResolver().Resolve(_scenario.QualityGates, core.RequiredLabels).Select(g => g.Id);
            var reviewers = new ReviewerResolver().Resolve(_scenario.Reviewers, core.RequiredLabels).Select(r => r.Id);
            return new ClaimResult(
                profile,
                AgentConfig(core.AgentProviderId),
                gates.Order(StringComparer.Ordinal).ToList(),
                reviewers.Order(StringComparer.Ordinal).ToList(),
                McpNames(enriched));
        }

        /// <summary>The kind-Agent config <c>DispatchInfrastructure</c> loads by Id; none means the claim fails.</summary>
        private ProviderConfig? AgentConfig(string? id) =>
            _scenario.ProviderConfigs.FirstOrDefault(p => p.Kind == ProviderKind.Agent && p.Id == id);

        private static List<string> McpNames(JobDistributionRequest enriched) =>
            (enriched.McpServers ?? []).Select(s => s.Name).Order(StringComparer.Ordinal).ToList();

        private static JobDistributionRequest Identity(
            PipelineJobTemplate template, WorkItemTaskType taskType, string selector, string? resolvedProfileId) => new()
        {
            IssueIdentifier = new IssueIdentifier("example/repo#1"),
            IssueProviderConfigId = template.IssueProviderId,
            RepoProviderConfigId = template.RepoProviderId,
            InitiatedBy = "golden",
            TaskType = taskType,
            AgentSelector = selector,
            ResolvedProfileId = resolvedProfileId,
            TimeoutSeconds = 3600,
        };
    }

    /// <summary>Captures the request the enricher passes on; returns an empty preparation.</summary>
    private sealed class CapturingInfrastructure : DispatchInfrastructure
    {
        public DispatchCoreRequest? Captured { get; set; }

        internal override Task<(IReadOnlyList<QualityGateConfiguration> QualityGates,
            IReadOnlyList<ReviewerConfiguration> Reviewers,
            IssueContextResult IssueContext,
            IReadOnlyList<ProviderConfig> ProviderConfigs,
            PipelineConfiguration Config,
            bool ForceRefresh,
            string? StalenessSignal,
            int RefreshCount)?> PrepareDispatchCoreAsync(DispatchCoreRequest request, CancellationToken ct)
        {
            Captured = request;
            var issue = new IssueContextResult(
                IssueDetail: new IssueDetail { Identifier = "example/repo#1", Title = "t", Description = "", Labels = [] },
                ParsedIssue: new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
                IssueComments: [],
                ExistingAnalysis: null,
                ForceRefreshAnalysis: false,
                StalenessSignal: null,
                RefreshCount: 0);
            return Task.FromResult<(IReadOnlyList<QualityGateConfiguration>, IReadOnlyList<ReviewerConfiguration>,
                IssueContextResult, IReadOnlyList<ProviderConfig>, PipelineConfiguration, bool, string?, int)?>(
                ([], [], issue, [], new PipelineConfiguration(), false, null, 0));
        }

        internal override Task<IReadOnlyList<RepositoryTarget>> BuildProjectReviewRepositoriesAsync(
            PipelineProject project, string ownRepoProviderId, Serilog.ILogger logger, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepositoryTarget>>([]);
    }

    /// <summary>Captures the profile the consolidation claim resolved.</summary>
    private sealed class CapturingConsolidationPreparer : IConsolidationJobPreparationService
    {
        public AgentProfile? Captured { get; set; }

        public Task<ConsolidationJobPreparationResult> PrepareAsync(
            ConsolidationRunType type, TemplateId? templateId, AgentProfile? profile, CancellationToken ct)
        {
            Captured = profile;
            return Task.FromResult(new ConsolidationJobPreparationResult
            {
                ProviderConfigs = [],
                RepoProviderConfigId = "",
                PipelineConfiguration = new PipelineConfiguration(),
            });
        }
    }
}
