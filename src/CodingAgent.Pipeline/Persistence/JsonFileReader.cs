using System.Text.Json;
using Serilog;

namespace CodingAgent.Pipeline.Persistence;

/// <summary>
/// Provides a null-returning JSON file read helper that encapsulates the
/// File.Exists / empty-check / deserialize / catch-and-warn sequence.
/// </summary>
internal static class JsonFileReader
{
    /// <summary>
    /// Reads and deserializes a JSON file from disk.
    /// Returns <see langword="null"/> (default) if the file does not exist, is empty/whitespace,
    /// fails to deserialize, or any non-cancellation exception is thrown.
    /// Logs a Debug message on missing file; logs a Warning on parse failure.
    /// Never throws — <see cref="OperationCanceledException"/> propagates normally.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="filePath">Absolute path to the JSON file.</param>
    /// <param name="options">Serializer options to use for deserialization.</param>
    /// <param name="logger">Logger for diagnostic messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized value, or <see langword="null"/> on any error condition.</returns>
    public static async Task<T?> TryReadJsonFileAsync<T>(
        string filePath,
        JsonSerializerOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            logger.Debug("JSON file not found at {FilePath}", filePath);
            return default;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            if (string.IsNullOrWhiteSpace(json))
                return default;

            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Failed to read or parse JSON file at {FilePath}", filePath);
            return default;
        }
    }
}
