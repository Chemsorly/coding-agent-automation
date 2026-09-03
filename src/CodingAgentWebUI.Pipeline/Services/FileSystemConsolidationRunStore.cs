using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Persistence;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Filesystem-backed implementation of <see cref="IConsolidationRunStore"/>.
/// Stores each run as a JSON file: {directory}/{runId}.json.
/// <para>
/// <b>Deprecated:</b> No longer registered in any production DI container.
/// All deployments route through <c>PostgresConsolidationRunStore</c> (direct DB) or
/// <c>ApiBackedConsolidationRunStore</c> (API-backed). This class is retained for
/// unit/integration tests that construct stores directly (contract tests,
/// ConsolidationServiceTests, ConsolidationFeedbackCacheTests, etc.).
/// </para>
/// </summary>
[Obsolete("Not registered in any production DI container. Use PostgresConsolidationRunStore or ApiBackedConsolidationRunStore. This class exists only for test infrastructure.")]
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

    public async Task<ConsolidationRun?> GetByIdAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
            return null;

        var filePath = GetFilePath(runId.Value);
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

    public Task DeleteRunAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
            return Task.CompletedTask;

        var filePath = GetFilePath(runId.Value);
        if (File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    private string GetFilePath(string runId) => Path.Combine(_directory, $"{runId}.json");
}
