using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Persistence;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.Persistence;

public class JsonConfigurationStore : IConfigurationStore
{
    private readonly string _baseDirectory;
    private readonly SemaphoreSlim _pipelineConfigLock = new(1, 1);
    private readonly SemaphoreSlim _projectLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<JsonConfigurationStore>();

    private static JsonSerializerOptions JsonOptions => PipelineJsonOptions.Default;

    // Write-through caches — populated on first read, invalidated on write/delete
    private PipelineConfiguration? _pipelineConfigCache;
    private IReadOnlyList<ProviderConfig>?[] _providerConfigsCache = new IReadOnlyList<ProviderConfig>?[5]; // indexed by (int)ProviderKind — update size if enum grows
    private IReadOnlyList<AgentProfile>? _agentProfilesCache;
    private IReadOnlyList<QualityGateConfiguration>? _qualityGateConfigsCache;
    private IReadOnlyList<ReviewerConfiguration>? _reviewerConfigsCache;
    private IReadOnlyList<PipelineProject>? _projectsCache;
    private IReadOnlyList<PipelineJobTemplate>? _allTemplatesCache;

    public JsonConfigurationStore(string baseDirectory = PipelineConstants.ConfigBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        _baseDirectory = baseDirectory;
        CleanupOrphanedTempFiles();
        EnsureDefaultProjectExists();
    }

    /// <summary>
    /// Deletes any orphaned .tmp files left by crashed atomic write operations.
    /// </summary>
    private void CleanupOrphanedTempFiles()
    {
        if (!Directory.Exists(_baseDirectory))
            return;

        try
        {
            var tmpFiles = Directory.GetFiles(_baseDirectory, "*.tmp", SearchOption.AllDirectories);
            foreach (var tmpFile in tmpFiles)
            {
                try
                {
                    File.Delete(tmpFile);
                    _logger.Information("Cleaned up orphaned temp file: {FilePath}", tmpFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warning("Failed to clean up orphaned temp file: {FilePath}: {Error}", tmpFile, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Failed to enumerate temp files in config directory: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Ensures the Default project file exists on disk. Creates it with all-null overrides
    /// if missing. This guarantees the invariant that the Default project always exists,
    /// including on fresh deployments where no migration runs.
    /// </summary>
    private void EnsureDefaultProjectExists()
    {
        var projectsDir = Path.Combine(_baseDirectory, "projects");
        var defaultProjectPath = Path.Combine(projectsDir, $"{WellKnownIds.DefaultProjectId}.json");

        if (File.Exists(defaultProjectPath))
            return;

        Directory.CreateDirectory(projectsDir);

        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            Enabled = true,
            TemplateIds = []
        };

        var json = JsonSerializer.Serialize(defaultProject, JsonOptions);
        File.WriteAllText(defaultProjectPath, json);
        _logger.Information("Created Default project at {Path}", defaultProjectPath);
    }

    public async Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
    {
        if (_pipelineConfigCache is not null)
            return _pipelineConfigCache;

        var path = Path.Combine(_baseDirectory, "pipeline-config.json");
        var result = await LoadJsonAsync<PipelineConfiguration>(path, ct) ?? new PipelineConfiguration();
        _pipelineConfigCache = result;
        return result;
    }

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _pipelineConfigLock.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_baseDirectory, "pipeline-config.json");
            await SaveJsonAsync(path, config, ct);
            _pipelineConfigCache = null;
        }
        finally
        {
            _pipelineConfigLock.Release();
        }
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
            _pipelineConfigCache = updated;
        }
        finally
        {
            _pipelineConfigLock.Release();
        }
    }

    public async Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
    {
        var index = (int)kind;
        var cached = _providerConfigsCache[index];
        if (cached is not null)
            return cached;

        var directory = GetProviderDirectory(kind);
        var result = await LoadAllFromDirectoryAsync<ProviderConfig>(directory, ct);
        _providerConfigsCache[index] = result;
        return result;
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
        _providerConfigsCache[(int)config.Kind] = null;
    }

    public Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var path = Path.Combine(GetProviderDirectory(kind), $"{id}.json");
        if (File.Exists(path))
            File.Delete(path);

        _providerConfigsCache[(int)kind] = null;
        return Task.CompletedTask;
    }

    // --- Agent Profiles ---

    public async Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct)
    {
        if (_agentProfilesCache is not null)
            return _agentProfilesCache;

        var result = await LoadEntitiesAsync<AgentProfile>("profiles", ct);
        _agentProfilesCache = result;
        return result;
    }

    public async Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct)
    {
        await SaveEntityAsync(profile, "profiles", p => p.Id, ct);
        _agentProfilesCache = null;
    }

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct)
    {
        var result = DeleteEntityAsync(id, "profiles");
        _agentProfilesCache = null;
        return result;
    }

    // --- Quality Gate Configurations ---

    public async Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct)
    {
        if (_qualityGateConfigsCache is not null)
            return _qualityGateConfigsCache;

        var result = await LoadEntitiesAsync<QualityGateConfiguration>("quality-gates", ct);
        _qualityGateConfigsCache = result;
        return result;
    }

    public async Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct)
    {
        await SaveEntityAsync(config, "quality-gates", c => c.Id, ct);
        _qualityGateConfigsCache = null;
    }

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct)
    {
        var result = DeleteEntityAsync(id, "quality-gates");
        _qualityGateConfigsCache = null;
        return result;
    }

    // --- Reviewer Configurations ---

    public async Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct)
    {
        if (_reviewerConfigsCache is not null)
            return _reviewerConfigsCache;

        var result = await LoadEntitiesAsync<ReviewerConfiguration>("reviewers", ct);
        _reviewerConfigsCache = result;
        return result;
    }

    public async Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct)
    {
        await SaveEntityAsync(config, "reviewers", c => c.Id, ct);
        _reviewerConfigsCache = null;
    }

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct)
    {
        var result = DeleteEntityAsync(id, "reviewers");
        _reviewerConfigsCache = null;
        return result;
    }

    // --- Projects ---

    public async Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct)
    {
        if (_projectsCache is not null)
            return _projectsCache;

        var result = await LoadEntitiesAsync<PipelineProject>("projects", ct);
        _projectsCache = result;
        return result;
    }

    public async Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!IsValidGuidFormat(id))
            return null;

        var path = Path.Combine(_baseDirectory, "projects", $"{id}.json");
        return await LoadJsonAsync<PipelineProject>(path, ct);
    }

    public async Task SaveProjectAsync(PipelineProject project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateProjectId(project.Id);
        ValidateProjectFields(project);

        await _projectLock.WaitAsync(ct);
        try
        {
            await SaveEntityAsync(project, "projects", p => p.Id, ct);
            _projectsCache = null;
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task DeleteProjectAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ValidateProjectId(id);

        if (string.Equals(id, WellKnownIds.DefaultProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Default project cannot be deleted.");

        await _projectLock.WaitAsync(ct);
        try
        {
            var projectToDelete = await GetProjectByIdAsync(id, ct);
            if (projectToDelete is null)
                return;

            // Move orphaned templates to the Default project
            if (projectToDelete.TemplateIds.Count > 0)
            {
                var defaultProject = await GetProjectByIdAsync(WellKnownIds.DefaultProjectId, ct);
                if (defaultProject is not null)
                {
                    var updatedTemplateIds = defaultProject.TemplateIds.ToList();
                    updatedTemplateIds.AddRange(projectToDelete.TemplateIds);
                    var updatedDefault = defaultProject with { TemplateIds = updatedTemplateIds };
                    await SaveEntityAsync(updatedDefault, "projects", p => p.Id, ct);

                    _logger.Information(
                        "Moved {Count} templates from deleted project '{ProjectName}' to Default project",
                        projectToDelete.TemplateIds.Count, projectToDelete.Name);
                }
                else
                {
                    _logger.Warning(
                        "Default project not found when deleting project '{ProjectName}' — orphaned templates: {TemplateIds}",
                        projectToDelete.Name, projectToDelete.TemplateIds);
                }
            }

            await DeleteEntityAsync(id, "projects");
            _projectsCache = null;
            _allTemplatesCache = null;
        }
        finally
        {
            _projectLock.Release();
        }
    }

    // ── Template CRUD ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ValidateProjectId(projectId);
        var project = await GetProjectByIdAsync(projectId, ct);
        if (project is null)
            return [];

        var templatesDir = Path.Combine(_baseDirectory, "projects", projectId, "templates");
        if (!Directory.Exists(templatesDir))
            return [];

        var templates = new Dictionary<string, PipelineJobTemplate>();
        foreach (var file in Directory.GetFiles(templatesDir, "*.json"))
        {
            var template = await LoadJsonAsync<PipelineJobTemplate>(file, ct);
            if (template is not null)
                templates[template.Id] = template;
        }

        // Order by TemplateIds position
        var ordered = new List<PipelineJobTemplate>();
        foreach (var id in project.TemplateIds)
        {
            if (templates.TryGetValue(id, out var t))
                ordered.Add(t);
        }
        return ordered.AsReadOnly();
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct)
    {
        if (_allTemplatesCache is not null)
            return _allTemplatesCache;

        var projectsDir = Path.Combine(_baseDirectory, "projects");
        if (!Directory.Exists(projectsDir))
            return [];

        var all = new List<PipelineJobTemplate>();
        foreach (var projDir in Directory.GetDirectories(projectsDir))
        {
            var templatesDir = Path.Combine(projDir, "templates");
            if (!Directory.Exists(templatesDir))
                continue;
            foreach (var file in Directory.GetFiles(templatesDir, "*.json"))
            {
                var template = await LoadJsonAsync<PipelineJobTemplate>(file, ct);
                if (template is not null)
                    all.Add(template);
            }
        }

        var result = all.AsReadOnly();
        _allTemplatesCache = result;
        return result;
    }

    public async Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(template);
        ValidateProjectId(projectId);

        await _projectLock.WaitAsync(ct);
        try
        {
            // Verify project exists before writing the template file
            var project = await GetProjectByIdAsync(projectId, ct);
            if (project is null)
                return;

            var templatesDir = Path.Combine(_baseDirectory, "projects", projectId, "templates");
            Directory.CreateDirectory(templatesDir);
            var path = Path.Combine(templatesDir, $"{template.Id}.json");
            await SaveJsonAsync(path, template, ct);

            // Add template ID to project's TemplateIds if not present
            if (!project.TemplateIds.Contains(template.Id))
            {
                var updatedIds = project.TemplateIds.ToList();
                updatedIds.Add(template.Id);
                var updated = project with { TemplateIds = updatedIds };
                await SaveEntityAsync(updated, "projects", p => p.Id, ct);
            }

            _allTemplatesCache = null;
            _projectsCache = null;
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task DeleteTemplateAsync(string projectId, string templateId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(templateId);
        ValidateProjectId(projectId);
        ValidateTemplateId(templateId);

        await _projectLock.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_baseDirectory, "projects", projectId, "templates", $"{templateId}.json");
            if (File.Exists(path))
                File.Delete(path);

            // Remove template ID from project's TemplateIds
            var project = await GetProjectByIdAsync(projectId, ct);
            if (project is not null && project.TemplateIds.Contains(templateId))
            {
                var updatedIds = project.TemplateIds.ToList();
                updatedIds.Remove(templateId);
                var updated = project with { TemplateIds = updatedIds };
                await SaveEntityAsync(updated, "projects", p => p.Id, ct);
            }

            _allTemplatesCache = null;
            _projectsCache = null;
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, string templateId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceProjectId);
        ArgumentNullException.ThrowIfNull(targetProjectId);
        ArgumentNullException.ThrowIfNull(templateId);
        ValidateProjectId(sourceProjectId);
        ValidateProjectId(targetProjectId);
        ValidateTemplateId(templateId);

        await _projectLock.WaitAsync(ct);
        try
        {
            // Move template file
            var sourcePath = Path.Combine(_baseDirectory, "projects", sourceProjectId, "templates", $"{templateId}.json");
            var targetDir = Path.Combine(_baseDirectory, "projects", targetProjectId, "templates");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, $"{templateId}.json");

            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                File.Delete(sourcePath);
            }

            // Remove from source project's TemplateIds
            var sourceProject = await GetProjectByIdAsync(sourceProjectId, ct);
            if (sourceProject is not null && sourceProject.TemplateIds.Contains(templateId))
            {
                var updatedIds = sourceProject.TemplateIds.ToList();
                updatedIds.Remove(templateId);
                var updated = sourceProject with { TemplateIds = updatedIds };
                await SaveEntityAsync(updated, "projects", p => p.Id, ct);
            }

            // Add to target project's TemplateIds
            var targetProject = await GetProjectByIdAsync(targetProjectId, ct);
            if (targetProject is not null && !targetProject.TemplateIds.Contains(templateId))
            {
                var updatedIds = targetProject.TemplateIds.ToList();
                updatedIds.Add(templateId);
                var updated = targetProject with { TemplateIds = updatedIds };
                await SaveEntityAsync(updated, "projects", p => p.Id, ct);
            }

            _allTemplatesCache = null;
            _projectsCache = null;
        }
        finally
        {
            _projectLock.Release();
        }
    }

    private static void ValidateProjectId(string id)
    {
        if (!IsValidGuidFormat(id))
            throw new ArgumentException(
                $"Project ID '{id}' is not a valid GUID format. Only GUID-formatted IDs are accepted to prevent path traversal.",
                nameof(id));
    }

    private static void ValidateTemplateId(string id)
    {
        if (!IsValidGuidFormat(id))
            throw new ArgumentException(
                $"Template ID '{id}' is not a valid GUID format. Only GUID-formatted IDs are accepted to prevent path traversal.",
                nameof(id));
    }

    private static bool IsValidGuidFormat(string id)
        => Guid.TryParse(id, out _);

    private static void ValidateProjectFields(PipelineProject project)
    {
        if (string.IsNullOrWhiteSpace(project.Name))
            throw new ArgumentException("Project name must be non-empty.", nameof(project));

        if (project.Name.Length > 128)
            throw new ArgumentException(
                $"Project name must not exceed 128 characters (was {project.Name.Length}).", nameof(project));

        if (project.Description is not null && project.Description.Length > 512)
            throw new ArgumentException(
                $"Project description must not exceed 512 characters (was {project.Description.Length}).", nameof(project));
    }

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
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning("Failed to delete configuration file '{FilePath}': {ErrorMessage}", path, ex.Message);
                throw new InvalidOperationException(
                    $"Unable to delete configuration file '{path}': {ex.Message}", ex);
            }
        }

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
            _logger.Warning("Configuration file corrupt or empty: {Path}", path);
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
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await AtomicFileWriter.WriteAsync(path, json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to write configuration file '{path}': {ex.Message}", ex);
        }
    }

}
