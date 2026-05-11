using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// In-memory implementation of IConfigurationStore for E2E tests.
/// Pre-seeded with default pipeline config and provider configs.
/// </summary>
public sealed class InMemoryConfigurationStore : IConfigurationStore
{
    private PipelineConfiguration _pipelineConfig = new()
    {
        WorkspaceBaseDirectory = Path.Combine(Path.GetTempPath(), "e2e-workspaces"),
        MaxRetries = 3,
        AgentTimeout = TimeSpan.FromMinutes(2),
        ExternalCiEnabled = false,
    };

    private readonly List<ProviderConfig> _providerConfigs = new();
    private readonly List<AgentProfile> _agentProfiles = new();
    private readonly List<QualityGateConfiguration> _qualityGateConfigs = new();
    private readonly List<ReviewerConfiguration> _reviewerConfigs = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Reset()
    {
        _pipelineConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.Combine(Path.GetTempPath(), "e2e-workspaces"),
            MaxRetries = 3,
            AgentTimeout = TimeSpan.FromMinutes(2),
            ExternalCiEnabled = false,
        };
        _providerConfigs.Clear();
        _agentProfiles.Clear();
        _qualityGateConfigs.Clear();
        _reviewerConfigs.Clear();
        SeedDefaults();
    }

    public void SeedDefaults()
    {
        // Only seed if not already seeded (prevent duplicates)
        if (_providerConfigs.Count > 0) return;

        _providerConfigs.AddRange(new[]
        {
            new ProviderConfig { Id = "issue-e2e", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "E2E Issue Provider" },
            new ProviderConfig { Id = "repo-e2e", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "E2E Repo Provider" },
            new ProviderConfig { Id = "agent-e2e", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "E2E Agent Provider",
                Settings = new Dictionary<string, string> { ["model"] = "test-model" } }
        });

        _qualityGateConfigs.Add(new QualityGateConfiguration
        {
            Id = "qg-e2e",
            DisplayName = "E2E Quality Gate",
            CompilationCommand = "echo",
            CompilationArguments = ["ok"],
            TestCommand = "echo",
            TestArguments = ["ok"],
            Enabled = true
        });

        _pipelineConfig = _pipelineConfig with
        {
            LastUsedProviderIds = new Dictionary<string, string>
            {
                ["issue"] = "issue-e2e",
                ["repository"] = "repo-e2e",
                ["agent"] = "agent-e2e"
            }
        };
    }

    // Pipeline config
    public Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct) =>
        Task.FromResult(_pipelineConfig);

    public Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
    {
        _pipelineConfig = config;
        return Task.CompletedTask;
    }

    public async Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { _pipelineConfig = transform(_pipelineConfig); }
        finally { _lock.Release(); }
    }

    // Provider configs
    public Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderConfig>>(_providerConfigs.Where(c => c.Kind == kind).ToList());

    public Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct)
    {
        _providerConfigs.RemoveAll(c => c.Id == config.Id);
        _providerConfigs.Add(config);
        return Task.CompletedTask;
    }

    public Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        _providerConfigs.RemoveAll(c => c.Id == id && c.Kind == kind);
        return Task.CompletedTask;
    }

    // Agent profiles
    public Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentProfile>>(_agentProfiles.ToList());

    public Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct)
    {
        _agentProfiles.RemoveAll(p => p.Id == profile.Id);
        _agentProfiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct)
    {
        _agentProfiles.RemoveAll(p => p.Id == id);
        return Task.CompletedTask;
    }

    // Quality gate configs
    public Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QualityGateConfiguration>>(_qualityGateConfigs.ToList());

    public Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct)
    {
        _qualityGateConfigs.RemoveAll(c => c.Id == config.Id);
        _qualityGateConfigs.Add(config);
        return Task.CompletedTask;
    }

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct)
    {
        _qualityGateConfigs.RemoveAll(c => c.Id == id);
        return Task.CompletedTask;
    }

    // Reviewer configs
    public Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ReviewerConfiguration>>(_reviewerConfigs.ToList());

    public Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct)
    {
        _reviewerConfigs.RemoveAll(c => c.Id == config.Id);
        _reviewerConfigs.Add(config);
        return Task.CompletedTask;
    }

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct)
    {
        _reviewerConfigs.RemoveAll(c => c.Id == id);
        return Task.CompletedTask;
    }
}
