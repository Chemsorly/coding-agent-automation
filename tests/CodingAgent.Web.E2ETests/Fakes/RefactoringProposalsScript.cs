using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>Writes the affected files and .agent/refactoring-proposals.json, as the aggregation step does.</summary>
public sealed class RefactoringProposalsScript : AgentScript
{
    public required IReadOnlyList<RefactoringProposal> Proposals { get; init; }

    public override void Execute(string workspacePath)
    {
        // The proposal validator accepts a proposal only when at least one of its affected files exists.
        foreach (var path in Proposals.SelectMany(p => p.AffectedFiles))
        {
            var fullPath = Path.Combine(workspacePath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "// file\n");
        }

        var proposalsPath = Path.Combine(workspacePath, AgentWorkspacePaths.RefactoringProposalsFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(proposalsPath)!);
        File.WriteAllText(proposalsPath, JsonSerializer.Serialize(Proposals, PipelineJsonOptions.Default));
    }
}
