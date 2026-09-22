using System.Text.Json;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Persistence;
using Serilog;

namespace CodingAgent.Pipeline.Services;

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
            // Use the shared helper for consistent missing-file / empty-file / parse-error handling.
            // A null result (any error) is silently skipped — same skip-corrupt-files semantics as before.
            // TODO: [WARNING] Log.Logger (Serilog static global) is used here for the same reason as GetByIdAsync —
            // no injected ILogger in this class. Thread one in if this class is ever extended.
            var run = await JsonFileReader.TryReadJsonFileAsync<ConsolidationRun>(
                file, PipelineJsonOptions.Default, Log.Logger, ct);
            if (run is not null)
                runs.Add(run);
        }

        return runs;
    }

    public async Task<ConsolidationRun?> GetByIdAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
            return null;

        var filePath = GetFilePath(runId.Value);
        // TODO: [WARNING] Log.Logger (Serilog static global) is used because this class has no injected ILogger.
        // In tests that construct the store directly without bootstrapping Serilog, Log.Logger resolves to
        // SilentLogger and warnings about malformed files will be swallowed silently. If an ILogger is ever
        // threaded into this class, pass it here instead of Log.Logger.
        return await JsonFileReader.TryReadJsonFileAsync<ConsolidationRun>(
            filePath, PipelineJsonOptions.Default, Log.Logger, ct);
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
