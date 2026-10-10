using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodingAgent.Agent;

/// <summary>
/// Read-merge-write access to an OpenCode config file (<c>~/.opencode/opencode.json</c>), which both
/// the MCP config writer (<c>mcp</c>) and the steering writer (<c>instructions</c>) fill, so neither
/// overwrites the other's section. OpenCode reads the file when it first serves a workspace.
/// </summary>
internal static class OpenCodeConfigFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Applies <paramref name="update"/> to the file's root object and writes it back.</summary>
    public static void Update(string fullPath, Action<JsonObject> update)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(update);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var root = Read(fullPath);
        root["$schema"] = "https://opencode.ai/config.json";
        update(root);
        File.WriteAllText(fullPath, root.ToJsonString(WriteOptions));
    }

    private static JsonObject Read(string fullPath)
    {
        if (!File.Exists(fullPath))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(fullPath)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject(); // a broken file is replaced rather than kept
        }
    }
}
