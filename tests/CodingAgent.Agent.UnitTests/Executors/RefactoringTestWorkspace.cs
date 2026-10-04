using System.Text.Json;

namespace CodingAgent.Agent.UnitTests.Executors;

/// <summary>
/// Seeds a mocked clone for <see cref="CodingAgent.Agent.Executors.RefactoringExecutor"/> tests.
/// </summary>
internal static class RefactoringTestWorkspace
{
    /// <summary>
    /// Writes the proposals file and creates every file the proposals name in <c>affectedFiles</c>,
    /// so the proposals pass the "affected files exist in the repository" validation.
    /// </summary>
    public static void WriteProposals(string workspacePath, string proposalsJson)
    {
        var agentDir = Path.Combine(workspacePath, ".agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "refactoring-proposals.json"), proposalsJson);

        using var document = JsonDocument.Parse(proposalsJson);
        foreach (var proposal in document.RootElement.EnumerateArray())
        {
            if (!proposal.TryGetProperty("affectedFiles", out var files))
                continue;

            foreach (var file in files.EnumerateArray())
                CreateFile(workspacePath, file.GetString()!);
        }
    }

    /// <summary>Creates an empty source file at <paramref name="relativePath"/> inside the workspace.</summary>
    public static void CreateFile(string workspacePath, string relativePath)
    {
        var fullPath = Path.Combine(workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "// test file");
    }
}
