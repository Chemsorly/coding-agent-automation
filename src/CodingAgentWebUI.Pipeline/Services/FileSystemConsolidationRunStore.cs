using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Persistence;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Filesystem-backed implementation of <see cref="IConsolidationRunStore"/>.
/// Stores each run as a JSON file: {directory}/{runId}.json.
/// Used in legacy (non-DB) mode.
/// </summary>
public sealed class FileSystemConsolidationRunStore : IConsolidationRunStore
{
    private readonly string _directory;

    public FileSystemConsolidationRunStore(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public async Task SaveRunAsync(ConsolidationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (!Directory.Exists(_directory))
            Directory.CreateDirectory(_directory);

        var filePath = GetFilePath(run.RunId);
        var json = JsonSerializer.Serialize(run, PipelineJsonOptions.Default);
        await AtomicFileWriter.WriteAsync(filePath, json, ct);
    }

    public async Task<IReadOnlyList<ConsolidationRun>> LoadAllRunsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
            return [];

        var files = Directory.GetFiles(_directory, "*.json");
        var runs = new List<ConsolidationRun>(files.Length);

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var run = JsonSerializer.Deserialize<ConsolidationRun>(json, PipelineJsonOptions.Default);
                if (run is not null)
                    runs.Add(run);
            }
            catch
            {
                // Skip corrupt files — same behavior as original inline code
            }
        }

        return runs;
    }

    public async Task<ConsolidationRun?> GetByIdAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (!Guid.TryParse(runId, out _))
            return null;

        var filePath = GetFilePath(runId);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<ConsolidationRun>(json, PipelineJsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }

    public Task DeleteRunAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (!Guid.TryParse(runId, out _))
            return Task.CompletedTask;

        var filePath = GetFilePath(runId);
        if (File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    private string GetFilePath(string runId) => Path.Combine(_directory, $"{runId}.json");
}
