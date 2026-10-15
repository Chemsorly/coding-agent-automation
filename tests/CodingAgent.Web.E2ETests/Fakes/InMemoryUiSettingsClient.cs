using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// Settings for the E2E web host, over the same in-memory store as <see cref="InMemoryPipelineApiConfigClient"/>.
/// The harness keeps configuration in that store, which the API host's database-backed handlers
/// (template listing, export, import) do not read. The wire format of <c>/api/ui/settings</c> is
/// covered by the API contract tests instead.
/// </summary>
public sealed class InMemoryUiSettingsClient(InMemoryPipelineApiConfigClient config) : IUiSettingsClient
{
    private const string FirstRunBannerDismissedKey = "first_run_banner_dismissed";

    public Task<PipelineConfiguration> GetPipelineConfigAsync(CancellationToken ct = default) => config.GetPipelineConfigAsync(ct);

    public Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct = default) =>
        config.UpdatePipelineConfigAsync(transform, ct);

    public Task<IReadOnlyList<AgentProfile>> GetAgentProfilesAsync(CancellationToken ct = default) => config.GetAgentProfilesAsync(ct);
    public Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct = default) => config.SaveAgentProfileAsync(profile, ct);
    public Task DeleteAgentProfileAsync(string id, CancellationToken ct = default) => config.DeleteAgentProfileAsync(id, ct);

    public Task<IReadOnlyList<QualityGateConfiguration>> GetQualityGateConfigsAsync(CancellationToken ct = default) => config.GetQualityGateConfigsAsync(ct);
    public Task SaveQualityGateConfigAsync(QualityGateConfiguration qualityGate, CancellationToken ct = default) => config.SaveQualityGateConfigAsync(qualityGate, ct);
    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct = default) => config.DeleteQualityGateConfigAsync(id, ct);

    public Task<IReadOnlyList<ReviewerConfiguration>> GetReviewerConfigsAsync(CancellationToken ct = default) => config.GetReviewerConfigsAsync(ct);
    public Task SaveReviewerConfigAsync(ReviewerConfiguration reviewer, CancellationToken ct = default) => config.SaveReviewerConfigAsync(reviewer, ct);
    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct = default) => config.DeleteReviewerConfigAsync(id, ct);
    public Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct = default) => config.ResetReviewerConfigsToDefaultAsync(ct);

    public Task<IReadOnlyList<PipelineProject>> GetProjectsAsync(CancellationToken ct = default) => config.GetProjectsAsync(ct);
    public Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct = default) => config.GetProjectByIdAsync(id, ct);
    public Task SaveProjectAsync(PipelineProject project, CancellationToken ct = default) => config.SaveProjectAsync(project, ct);
    public Task DeleteProjectAsync(string id, CancellationToken ct = default) => config.DeleteProjectAsync(id, ct);

    public Task<IReadOnlyList<PipelineJobTemplate>> GetAllTemplatesAsync(CancellationToken ct = default) => config.GetAllTemplatesAsync(ct);

    public Task MoveTemplateAsync(ProjectId sourceProjectId, ProjectId targetProjectId, string templateId, CancellationToken ct = default) =>
        config.MoveTemplateAsync(sourceProjectId, targetProjectId, templateId, ct);

    public Task SaveTemplateAsync(ProjectId projectId, PipelineJobTemplate template, CancellationToken ct = default) =>
        config.SaveTemplateAsync(projectId.Value, template, ct);

    public Task DeleteTemplateAsync(ProjectId projectId, TemplateId templateId, CancellationToken ct = default) =>
        config.DeleteTemplateAsync(projectId.Value, templateId.Value, ct);

    public Task<byte[]> ExportConfigAsync(CancellationToken ct = default) => config.ExportConfigAsync(ct);
    public Task ImportConfigAsync(Stream jsonStream, string fileName, CancellationToken ct = default) => config.ImportConfigAsync(jsonStream, fileName, ct);

    public async Task<UiFirstRunState> GetFirstRunStateAsync(CancellationToken ct = default) =>
        new(await config.HasEnabledTemplatesAsync(ct), await config.GetKeyValueAsync(FirstRunBannerDismissedKey, ct) == "true");

    public Task DismissFirstRunBannerAsync(CancellationToken ct = default) =>
        config.SetKeyValueAsync(FirstRunBannerDismissedKey, "true", ct);
}
