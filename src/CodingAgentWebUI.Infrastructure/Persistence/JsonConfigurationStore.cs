using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.Persistence;

public class JsonConfigurationStore : IConfigurationStore
{
    private readonly string _baseDirectory;
    private readonly SemaphoreSlim _pipelineConfigLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<JsonConfigurationStore>();

    private static JsonSerializerOptions JsonOptions => PipelineJsonOptions.Default;

    public JsonConfigurationStore(string baseDirectory = PipelineConstants.ConfigBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        _baseDirectory = baseDirectory;
    }

    public async Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
    {
        var path = Path.Combine(_baseDirectory, "pipeline-config.json");
        return await LoadJsonAsync<PipelineConfiguration>(path, ct) ?? new PipelineConfiguration();
    }

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var path = Path.Combine(_baseDirectory, "pipeline-config.json");
        await SaveJsonAsync(path, config, ct);
    }

    public async Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transform);

        await _pipelineConfigLock.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_baseDirectory, "pipeline-config.json");

            PipelineConfiguration current;
            if (File.Exists(path))
            {
                var loaded = await LoadJsonAsync<PipelineConfiguration>(path, ct);
                if (loaded is null)
                    throw new InvalidOperationException(
                        $"Pipeline configuration file '{path}' exists but contains invalid JSON. " +
                        "Fix or delete the file before saving.");
                current = loaded;
            }
            else
            {
                current = new PipelineConfiguration();
            }

            var updated = transform(current);
            await SaveJsonAsync(path, updated, ct);
        }
        finally
        {
            _pipelineConfigLock.Release();
        }
    }

    public async Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
    {
        var directory = GetProviderDirectory(kind);
        return await LoadAllFromDirectoryAsync<ProviderConfig>(directory, ct);
    }

    public async Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var path = Path.Combine(GetProviderDirectory(kind), $"{id}.json");
        return await LoadJsonAsync<ProviderConfig>(path, ct);
    }

    public async Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = GetProviderDirectory(config.Kind);
        var path = Path.Combine(directory, $"{config.Id}.json");
        await SaveJsonAsync(path, config, ct);
    }

    public Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var path = Path.Combine(GetProviderDirectory(kind), $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    // --- Agent Profiles ---

    public Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct)
        => LoadEntitiesAsync<AgentProfile>("profiles", ct);

    public Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct)
        => SaveEntityAsync(profile, "profiles", p => p.Id, ct);

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct)
        => DeleteEntityAsync(id, "profiles");

    // --- Quality Gate Configurations ---

    public Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct)
        => LoadEntitiesAsync<QualityGateConfiguration>("quality-gates", ct);

    public Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct)
        => SaveEntityAsync(config, "quality-gates", c => c.Id, ct);

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct)
        => DeleteEntityAsync(id, "quality-gates");

    // --- Reviewer Configurations ---

    public Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct)
        => LoadEntitiesAsync<ReviewerConfiguration>("reviewers", ct);

    public Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct)
        => SaveEntityAsync(config, "reviewers", c => c.Id, ct);

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct)
        => DeleteEntityAsync(id, "reviewers");

    private Task<IReadOnlyList<T>> LoadEntitiesAsync<T>(string subfolder, CancellationToken ct) where T : class
        => LoadAllFromDirectoryAsync<T>(Path.Combine(_baseDirectory, subfolder), ct);

    private async Task SaveEntityAsync<T>(T entity, string subfolder, Func<T, string> idSelector, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var path = Path.Combine(_baseDirectory, subfolder, $"{idSelector(entity)}.json");
        await SaveJsonAsync(path, entity, ct);
    }

    private Task DeleteEntityAsync(string id, string subfolder)
    {
        ArgumentNullException.ThrowIfNull(id);
        var path = Path.Combine(_baseDirectory, subfolder, $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    private string GetProviderDirectory(ProviderKind kind)
    {
        var subfolder = kind switch
        {
            ProviderKind.Issue => "issue",
            ProviderKind.Repository => "repository",
            ProviderKind.Brain => "repository", // Brain repos are stored alongside work repos
            ProviderKind.Agent => "agent",
            ProviderKind.Pipeline => "pipeline",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown provider kind")
        };
        return Path.Combine(_baseDirectory, "providers", subfolder);
    }

    private async Task<T?> LoadJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Return null so callers fall back to defaults
            return null;
        }
    }

    private async Task<IReadOnlyList<T>> LoadAllFromDirectoryAsync<T>(string directory, CancellationToken ct) where T : class
    {
        if (!Directory.Exists(directory))
            return Array.Empty<T>();

        var items = new List<T>();
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var item = await LoadJsonAsync<T>(file, ct);
            if (item is not null)
            {
                items.Add(item);
            }
            else
            {
                _logger.Warning("Skipping corrupted configuration file: {FilePath}", file);
            }
        }

        return items.AsReadOnly();
    }

    private async Task SaveJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(value, JsonOptions);
            await File.WriteAllTextAsync(path, json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to write configuration file '{path}': {ex.Message}", ex);
        }
    }

}
