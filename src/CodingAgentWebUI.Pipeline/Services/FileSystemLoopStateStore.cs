using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Persistence;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Filesystem-backed implementation of <see cref="ILoopStateStore"/>.
/// Stores loop state as a single JSON file at the configured path.
/// Used in legacy (non-DB) mode.
/// </summary>
public sealed class FileSystemLoopStateStore : ILoopStateStore
{
    private readonly string _filePath;

    public FileSystemLoopStateStore(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        _filePath = filePath;
    }

    public async Task<LoopState?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            return JsonSerializer.Deserialize<LoopState>(json, PipelineJsonOptions.Default);
        }
        catch (JsonException)
        {
            // Corrupt file — treat as absent
            return null;
        }
    }

    public async Task WriteAsync(LoopState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(state, PipelineJsonOptions.Default);
        await AtomicFileWriter.WriteAsync(_filePath, json, ct);
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        return Task.CompletedTask;
    }
}
