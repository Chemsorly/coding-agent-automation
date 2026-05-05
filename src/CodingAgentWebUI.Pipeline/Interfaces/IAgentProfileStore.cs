using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IAgentProfileStore
{
    Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct);
    Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct);
    Task DeleteAgentProfileAsync(string id, CancellationToken ct);
}
