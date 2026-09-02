using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// No-op configuration store for agent-side execution where configs come from the job assignment.
/// <para>
/// Steps that read from this store use pre-resolved configs from
/// <see cref="CodingAgentWebUI.Pipeline.Services.Steps.PipelineStepContext"/> instead:
/// <list type="bullet">
///   <item><see cref="CodingAgentWebUI.Pipeline.Services.Steps.ReviewCodeStep"/> — uses
///     <c>PreResolvedReviewerConfigs</c> from the job assignment; the store is only reached on the
///     orchestrator path where reviewer resolution happens live.</item>
///   <item><see cref="CodingAgentWebUI.Pipeline.Services.Steps.RunQualityGatesStep"/> — uses
///     <c>PreResolvedQualityGateConfigs</c> from the job assignment.</item>
///   <item><see cref="CodingAgentWebUI.Pipeline.Services.Steps.VerifyBaselineStep"/> — uses
///     <c>PreResolvedQualityGateConfigs</c> from the job assignment; returns a safe skip when the
///     store returns an empty list.</item>
///   <item><see cref="CodingAgentWebUI.Pipeline.Services.Steps.RunEnvironmentSetupStep"/> — calls
///     <c>GetProviderConfigByIdAsync</c>; a <c>null</c> result is handled by null-safe operators and
///     an early <c>return Continue</c> — no secrets or setup steps are run.</item>
/// </list>
/// </para>
/// <para>
/// All four reads are safe when this store is active. If this store is ever injected in a context
/// where pre-resolved configs are <c>null</c>, <c>ReviewCodeStep</c> will silently resolve zero
/// reviewers and <c>RunQualityGatesStep</c> will silently skip quality gates with no observable log
/// output. Inject a real <see cref="IConfigurationStore"/> in any context other than the agent-side
/// execution path established in
/// <see cref="CodingAgentWebUI.Agent.PipelineExecutionContextBuilder"/>.
/// </para>
/// </summary>
internal sealed class NullConfigurationStore : IConfigurationStore
{

    public Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct) =>
        Task.FromResult(new PipelineConfiguration());

    public Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct) =>
        Task.CompletedTask;

    public Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderConfig>>([]);

    public Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct) =>
        Task.FromResult<ProviderConfig?>(null);

    public Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentProfile>>([]);

    public Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<QualityGateConfiguration>>([]);
    }

    public Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ReviewerConfiguration>>([]);
    }

    public Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct) =>
        Task.CompletedTask;

    public Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PipelineProject>>([]);

    public Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct) =>
        Task.FromResult<PipelineProject?>(null);

    public Task SaveProjectAsync(PipelineProject project, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteProjectAsync(string id, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PipelineJobTemplate>>([]);

    public Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PipelineJobTemplate>>([]);

    public Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteTemplateAsync(string projectId, TemplateId templateId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, TemplateId templateId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<bool> HasEnabledTemplatesAsync(CancellationToken ct) =>
        Task.FromResult(false);
}
