namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Role of a repository provider instance.
/// Work repositories contain the code the pipeline operates on.
/// Brain repositories contain persistent cross-session knowledge for AI agents.
/// </summary>
public enum RepositoryRole
{
    Work,
    Brain
}
