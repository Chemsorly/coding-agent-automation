using System.Net;

namespace CodingAgent.Api.Client;

/// <summary>
/// Thrown by <see cref="PipelineApiChatClient.DispatchChatPodAsync"/> when the API returns a
/// non-2xx status code. Carries the structured error detail from the API response body and,
/// for 504 responses, the timeout in seconds reported by the API.
/// </summary>
public sealed class ChatDispatchFailedException(string message, HttpStatusCode statusCode, int? timeoutSeconds)
    : HttpRequestException(message, inner: null, statusCode)
{
    /// <summary>
    /// The timeout value from the API's 504 response body, or <c>null</c> when the response did
    /// not include a <c>timeoutSeconds</c> field (e.g. non-timeout errors or non-JSON bodies).
    /// </summary>
    public int? TimeoutSeconds { get; } = timeoutSeconds;
}
