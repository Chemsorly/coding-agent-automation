using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Agent.OpenCode;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// A mock <see cref="HttpMessageHandler"/> that records outbound requests and returns
/// queued responses. Supports both FIFO ordering and URL-pattern-based response matching.
/// Used for testing <see cref="OpenCodeAgentProvider"/> without network I/O.
/// </summary>
public sealed class MockOpenCodeHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<PatternResponse> _patternResponses = [];
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>
    /// All requests sent through this handler, in order.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    /// <summary>
    /// Enqueues a response with the given status code and string content.
    /// Responses are returned in FIFO order unless a URL pattern match takes priority.
    /// </summary>
    public void EnqueueResponse(HttpStatusCode statusCode, string content, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };
        _responses.Enqueue(response);
    }

    /// <summary>
    /// Enqueues a response with the given status code and a JSON-serialized body.
    /// Uses the same <see cref="OpenCodeJson.JsonOptions"/> as the production code.
    /// </summary>
    public void EnqueueJsonResponse<T>(T body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body, OpenCodeJson.JsonOptions);
        EnqueueResponse(statusCode, json);
    }

    /// <summary>
    /// Enqueues a simple 200 OK response with an empty JSON object body.
    /// </summary>
    public void EnqueueOk(string content = "{}")
    {
        EnqueueResponse(HttpStatusCode.OK, content);
    }

    /// <summary>
    /// Enqueues a response with no content (e.g., for POST endpoints that return 204 or 200 with empty body).
    /// </summary>
    public void EnqueueEmpty(HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responses.Enqueue(new HttpResponseMessage(statusCode));
    }

    /// <summary>
    /// Configures a response for requests matching the given URL pattern (regex).
    /// Pattern responses take priority over FIFO-queued responses.
    /// Each pattern can return multiple responses (consumed in order per pattern).
    /// </summary>
    /// <param name="urlPattern">Regex pattern to match against the request URL path (e.g., "/session/.+/message").</param>
    /// <param name="statusCode">HTTP status code to return.</param>
    /// <param name="content">Response body content.</param>
    /// <param name="mediaType">Content-Type media type (default: application/json).</param>
    public void ForUrlPattern(string urlPattern, HttpStatusCode statusCode, string content, string mediaType = "application/json")
    {
        _patternResponses.Add(new PatternResponse(
            urlPattern,
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            }));
    }

    /// <summary>
    /// Configures a JSON response for requests matching the given URL pattern (regex).
    /// </summary>
    public void ForUrlPattern<T>(string urlPattern, T body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body, OpenCodeJson.JsonOptions);
        ForUrlPattern(urlPattern, statusCode, json);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read and store the request body before returning the response
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        _requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            body,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToList() as IReadOnlyList<string>)));

        // Check URL-pattern responses first (priority over FIFO queue)
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;
        for (var i = 0; i < _patternResponses.Count; i++)
        {
            if (Regex.IsMatch(path, _patternResponses[i].UrlPattern))
            {
                var match = _patternResponses[i];
                _patternResponses.RemoveAt(i);
                return match.Response;
            }
        }

        // Fall back to FIFO queue
        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    "{\"error\":\"No queued responses in MockOpenCodeHandler\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        return _responses.Dequeue();
    }

    /// <summary>
    /// Returns the number of remaining FIFO-queued responses.
    /// </summary>
    public int RemainingResponses => _responses.Count;

    /// <summary>
    /// Returns the number of remaining URL-pattern responses.
    /// </summary>
    public int RemainingPatternResponses => _patternResponses.Count;

    /// <summary>
    /// Clears all recorded requests, queued responses, and pattern responses.
    /// </summary>
    public void Reset()
    {
        _requests.Clear();
        _responses.Clear();
        _patternResponses.Clear();
    }

    private sealed record PatternResponse(string UrlPattern, HttpResponseMessage Response);
}

/// <summary>
/// Represents a recorded HTTP request with method, URI, body, and headers.
/// </summary>
public sealed record RecordedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? Body,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers)
{
    /// <summary>
    /// Deserializes the request body as JSON using OpenCode serialization options.
    /// </summary>
    public T? DeserializeBody<T>()
    {
        if (Body is null) return default;
        return JsonSerializer.Deserialize<T>(Body, OpenCodeJson.JsonOptions);
    }

    /// <summary>
    /// Returns the request path (without query string).
    /// </summary>
    public string Path => RequestUri?.AbsolutePath ?? string.Empty;
}
