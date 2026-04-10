using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog.Events;

namespace KiroCliPoc.Configuration;

/// <summary>
/// JSON converter for TimeSpan that handles "HH:MM:SS" format strings.
/// </summary>
internal sealed class TimeSpanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return TimeSpan.TryParse(value, out var result) ? result : TimeSpan.FromMinutes(30);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Manages loading and merging configuration from files and command-line arguments.
/// </summary>
public static class ConfigurationManager
{
    /// <summary>
    /// Loads configuration from a JSON file asynchronously.
    /// If the file doesn't exist or is invalid, returns default configuration.
    /// </summary>
    /// <param name="configPath">Path to the configuration file. If null, uses default path.</param>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>A Configuration object with values from the file or defaults.</returns>
    public static async Task<Configuration> LoadAsync(string? configPath = null, CancellationToken cancellationToken = default)
    {
        var path = configPath ?? "config/appsettings.json";

        if (!File.Exists(path))
        {
            // Return default configuration if file doesn't exist
            return new Configuration();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true),
                    new TimeSpanConverter()
                }
            };

            var config = await JsonSerializer.DeserializeAsync<Configuration>(stream, options, cancellationToken);
            return config ?? new Configuration();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse configuration file '{path}'. Please ensure it contains valid JSON.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to load configuration file '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Merges configuration from a file with command-line arguments.
    /// Command-line arguments take precedence over file configuration.
    /// </summary>
    /// <param name="fileConfig">Configuration loaded from file.</param>
    /// <param name="cliArgs">Command-line arguments.</param>
    /// <returns>A merged Configuration object.</returns>
    /// <exception cref="ArgumentNullException">Thrown when fileConfig or cliArgs is null.</exception>
    public static Configuration Merge(Configuration fileConfig, CommandLineArgs cliArgs)
    {
        ArgumentNullException.ThrowIfNull(fileConfig);
        ArgumentNullException.ThrowIfNull(cliArgs);

        var merged = new Configuration
        {
            KiroCliPath = fileConfig.KiroCliPath,
            UseWsl = fileConfig.UseWsl,
            WorkspaceDirectory = cliArgs.WorkspaceDirectory ?? fileConfig.WorkspaceDirectory,
            AgentName = cliArgs.AgentName ?? fileConfig.AgentName,
            Timeout = cliArgs.Timeout ?? fileConfig.Timeout,
            LogLevel = cliArgs.LogLevel ?? fileConfig.LogLevel,
            LogFilePath = fileConfig.LogFilePath
        };

        return merged;
    }
}
