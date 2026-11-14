using CodingAgent.Pipeline;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>Writes the findings file of one review agent, as the review prompt asks.</summary>
public sealed class AgentReviewFindingsScript : AgentScript
{
    public required string AgentName { get; init; }
    public required string Findings { get; init; }

    public override void Execute(string workspacePath)
    {
        var path = Path.Combine(workspacePath, AgentWorkspacePaths.GetReviewFindingsFilePath(AgentName));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Findings);
    }
}
