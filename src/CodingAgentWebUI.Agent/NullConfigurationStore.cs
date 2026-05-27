using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// No-op configuration store for agent-side execution where configs come from the job assignment.
/// Steps that need configs (ReviewCodeStep, RunQualityGatesStep) use pre-resolved configs
/// from <see cref="CodingAgentWebUI.Pipeline.Services.Steps.PipelineStepContext"/> instead.
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

    public Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QualityGateConfiguration>>([]);

    public Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ReviewerConfiguration>>([]);

    public Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct) =>
        Task.CompletedTask;
}
