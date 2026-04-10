using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiroCliLib.Configuration;

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
    public static async Task<Configuration> LoadAsync(string? configPath = null, CancellationToken cancellationToken = default)
    {
        var path = configPath ?? "config/appsettings.json";

        if (!File.Exists(path))
            return new Configuration();

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

    public static Configuration Merge(Configuration fileConfig, CommandLineArgs cliArgs)
    {
        ArgumentNullException.ThrowIfNull(fileConfig);
        ArgumentNullException.ThrowIfNull(cliArgs);

        return new Configuration
        {
            KiroCliPath = fileConfig.KiroCliPath,
            UseWsl = fileConfig.UseWsl,
            WorkspaceDirectory = cliArgs.WorkspaceDirectory ?? fileConfig.WorkspaceDirectory,
            AgentName = cliArgs.AgentName ?? fileConfig.AgentName,
            Timeout = cliArgs.Timeout ?? fileConfig.Timeout,
            LogLevel = cliArgs.LogLevel ?? fileConfig.LogLevel,
            LogFilePath = fileConfig.LogFilePath
        };
    }
}
