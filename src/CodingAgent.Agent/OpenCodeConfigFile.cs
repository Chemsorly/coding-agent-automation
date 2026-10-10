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

    // OpenCode accepts JSONC; an existing file with comments or trailing commas is kept, minus its comments.
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Applies <paramref name="update"/> to the file's root object and writes it back. Every write also
    /// sets <c>experimental.continue_loop_on_deny</c>: no one is there to answer the question tool, so
    /// the pipeline rejects its questions, and without the setting a rejection ends the turn with no
    /// error, which reads as finished work.
    /// </summary>
    public static void Update(string fullPath, Action<JsonObject> update)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(update);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var root = Read(fullPath);
        root["$schema"] = "https://opencode.ai/config.json";
        if (root["experimental"] is not JsonObject experimental)
        {
            experimental = new JsonObject();
            root["experimental"] = experimental;
        }
        experimental["continue_loop_on_deny"] = true;
        update(root);
        File.WriteAllText(fullPath, root.ToJsonString(WriteOptions));
    }

    private static JsonObject Read(string fullPath)
    {
        if (!File.Exists(fullPath))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(fullPath), documentOptions: ReadOptions) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject(); // a broken file is replaced rather than kept
        }
    }
}
