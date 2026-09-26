namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Determines the order in which the HTTP and hub channels fire during a
/// <see cref="FakeAgentClient.CompleteLikeProductionAsync"/> call.
/// </summary>
public enum CompletionOrder
{
    /// <summary>HTTP POST first, then SignalR ReportJobCompleted (production ordering).</summary>
    HttpThenHub = 0,

    /// <summary>SignalR ReportJobCompleted first, then HTTP POST (replay / out-of-order testing).</summary>
    HubThenHttp = 1,

    /// <summary>HTTP POST only; no SignalR call (tests the HTTP-only path).</summary>
    HttpOnly = 2,
}
