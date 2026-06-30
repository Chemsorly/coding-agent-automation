using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Persistence;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Filesystem-backed implementation of <see cref="IHarnessSuggestionStore"/>.
/// Stores suggestions as a single JSON file at the configured path.
/// Used in legacy (non-DB) mode.
/// </summary>
public sealed class FileSystemHarnessSuggestionStore : IHarnessSuggestionStore
{
    private readonly string _filePath;

    public FileSystemHarnessSuggestionStore(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        _filePath = filePath;
    }

    public async Task<HarnessSuggestions?> GetAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            return JsonSerializer.Deserialize<HarnessSuggestions>(json, PipelineJsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(suggestions, PipelineJsonOptions.Default);
        await AtomicFileWriter.WriteAsync(_filePath, json, ct);
    }
}
