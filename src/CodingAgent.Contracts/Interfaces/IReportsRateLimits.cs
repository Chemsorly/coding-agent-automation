using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// An agent provider that reports subscription usage readings while a call runs (Claude Code's rate-limit events), not
/// only with its result, so a long call that crosses a usage threshold is seen before it ends.
/// </summary>
public interface IReportsRateLimits
{
    /// <summary>Receives every window's latest reading each time the provider sees a new rate-limit event.</summary>
    Action<IReadOnlyList<AgentRateLimitObservation>>? RateLimitsObserved { get; set; }
}
