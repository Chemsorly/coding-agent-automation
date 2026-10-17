using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Web.IntegrationTests.Bff;

/// <summary>A request the stub API received through the BFF.</summary>
public sealed record RecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body, bool WebSocket);

/// <summary>
/// Spec 052 Req 9.1: stands in for the Pipeline API behind the BFF, on a real Kestrel port. It
/// records every request it receives and answers by path:
/// <list type="bullet">
/// <item><c>/api/ui/status/{code}</c>: that status with the JSON string body <c>"reason-{code}"</c>,
/// plus a <c>Set-Cookie</c> the BFF must drop;</item>
/// <item><c>/api/ui/slow</c>: 200 after 3 s (longer than the tests' forwarder timeout);</item>
/// <item><c>/api/ui/abort</c>: drops the connection;</item>
/// <item><c>/hubs/ui</c>: accepts a WebSocket, sends <c>hello</c> and closes;</item>
/// <item>anything else: 200 with <c>{"ok":true}</c>.</item>
/// </list>
/// </summary>
public sealed class StubPipelineApi : IAsyncDisposable
{
    public const string HubGreeting = "hello";

    private readonly WebApplication _app;

    public StubPipelineApi()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Run(HandleAsync);
        _app.StartAsync().GetAwaiter().GetResult();
        BaseUrl = _app.Urls.Single();
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        Requests.Enqueue(new RecordedRequest(
            request.Method,
            request.Path + request.QueryString,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            body,
            context.WebSockets.IsWebSocketRequest));

        var path = request.Path.Value ?? "";
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(Encoding.UTF8.GetBytes(HubGreeting), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            return;
        }

        if (path.StartsWith("/api/ui/status/", StringComparison.Ordinal) && int.TryParse(path["/api/ui/status/".Length..], out var status))
        {
            context.Response.StatusCode = status;
            context.Response.Headers.SetCookie = "stub-session=leak; Path=/";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($"\"reason-{status}\"");
            return;
        }

        if (path == "/api/ui/slow")
        {
            await Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted);
            await context.Response.WriteAsync("late");
            return;
        }

        if (path == "/api/ui/abort")
        {
            context.Abort();
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"ok\":true}");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
