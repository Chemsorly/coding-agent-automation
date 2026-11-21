namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>Writes several files at the given workspace-relative paths, creating directories as needed.</summary>
public sealed class WriteFilesScript : AgentScript
{
    public required IReadOnlyDictionary<string, string> Files { get; init; }

    public override void Execute(string workspacePath)
    {
        foreach (var (relativePath, content) in Files)
        {
            var fullPath = Path.Combine(workspacePath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
    }
}
