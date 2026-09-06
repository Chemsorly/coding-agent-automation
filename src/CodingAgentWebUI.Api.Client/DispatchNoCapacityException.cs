using System.Net;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Thrown by <see cref="IPipelineApiWorkItemClient.DispatchAsync"/> when the API returns
/// HTTP 409 (concurrency limit) or 503 (no PVC available / K8s failure).
/// The caller (<c>KubernetesWorkDistributor</c>) maps this to a failed <c>DistributionResult</c>
/// so the Scheduler reverts the GitHub label to <c>agent:next</c> and re-queues the item.
/// </summary>
public sealed class DispatchNoCapacityException : Exception
{
    /// <summary>The HTTP status code returned by the dispatch endpoint (409 or 503).</summary>
    public HttpStatusCode StatusCode { get; }

    public DispatchNoCapacityException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
