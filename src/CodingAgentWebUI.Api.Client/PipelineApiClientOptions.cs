namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Configuration options for the Pipeline API typed HTTP clients and hub connection.
/// </summary>
public sealed class PipelineApiClientOptions
{
    /// <summary>Base URL of the Pipeline API service, e.g. "https://api:8080".</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Bearer token (master API key) used to authenticate all HTTP and SignalR requests.
    /// Must not be null or empty — a missing key produces silent 401s on every call.
    /// </summary>
    public required string AgentApiKey { get; init; }
}
