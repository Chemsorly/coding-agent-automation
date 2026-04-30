using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new TimeSpanJsonConverter(), new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public JsonConfigurationStore(string baseDirectory = "config/pipeline")
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
        if (!Directory.Exists(directory))
            return Array.Empty<ProviderConfig>();

        var configs = new List<ProviderConfig>();
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var config = await LoadJsonAsync<ProviderConfig>(file, ct);
            if (config is not null)
                configs.Add(config);
        }

        return configs.AsReadOnly();
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

    public async Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct)
    {
        var directory = Path.Combine(_baseDirectory, "profiles");
        if (!Directory.Exists(directory))
            return Array.Empty<AgentProfile>();

        var profiles = new List<AgentProfile>();
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var profile = await LoadJsonAsync<AgentProfile>(file, ct);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
            else
            {
                _logger.Warning("Skipping corrupted agent profile file: {FilePath}", file);
            }
        }

        return profiles.AsReadOnly();
    }

    public async Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var directory = Path.Combine(_baseDirectory, "profiles");
        var path = Path.Combine(directory, $"{profile.Id}.json");
        await SaveJsonAsync(path, profile, ct);
    }

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var path = Path.Combine(_baseDirectory, "profiles", $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    // --- Quality Gate Configurations ---

    public async Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct)
    {
        var directory = Path.Combine(_baseDirectory, "quality-gates");
        if (!Directory.Exists(directory))
            return Array.Empty<QualityGateConfiguration>();

        var configs = new List<QualityGateConfiguration>();
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var config = await LoadJsonAsync<QualityGateConfiguration>(file, ct);
            if (config is not null)
            {
                configs.Add(config);
            }
            else
            {
                _logger.Warning("Skipping corrupted quality gate configuration file: {FilePath}", file);
            }
        }

        return configs.AsReadOnly();
    }

    public async Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = Path.Combine(_baseDirectory, "quality-gates");
        var path = Path.Combine(directory, $"{config.Id}.json");
        await SaveJsonAsync(path, config, ct);
    }

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var path = Path.Combine(_baseDirectory, "quality-gates", $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    // --- Reviewer Configurations ---

    public async Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct)
    {
        var directory = Path.Combine(_baseDirectory, "reviewers");
        if (!Directory.Exists(directory))
            return Array.Empty<ReviewerConfiguration>();

        var configs = new List<ReviewerConfiguration>();
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var config = await LoadJsonAsync<ReviewerConfiguration>(file, ct);
            if (config is not null)
            {
                configs.Add(config);
            }
            else
            {
                _logger.Warning("Skipping corrupted reviewer configuration file: {FilePath}", file);
            }
        }

        return configs.AsReadOnly();
    }

    public async Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = Path.Combine(_baseDirectory, "reviewers");
        var path = Path.Combine(directory, $"{config.Id}.json");
        await SaveJsonAsync(path, config, ct);
    }

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var path = Path.Combine(_baseDirectory, "reviewers", $"{id}.json");
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

    /// <summary>
    /// Custom converter for TimeSpan since System.Text.Json doesn't natively support it.
    /// Serializes as ISO 8601 duration string.
    /// </summary>
    private sealed class TimeSpanJsonConverter : System.Text.Json.Serialization.JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value is not null ? TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : default;
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
