using Serilog.Events;

namespace CodingAgent.Api;

/// <summary>
/// A request the client aborted (the browser navigated away mid-call) is no server error: its handler
/// throws <see cref="OperationCanceledException"/> once <see cref="HttpContext.RequestAborted"/> fires, and
/// nobody reads the answer. Such a request ends as 499 Client Closed Request, which the request log keeps
/// at Debug instead of a 500 Warning.
/// </summary>
internal static class ClientAbortedRequests
{
    /// <summary>
    /// Ends a request the client aborted as 499 instead of rethrowing. Goes right after the request
    /// logging, so the log sees the 499 and no exception.
    /// </summary>
    public static IApplicationBuilder UseClientAbortedRequests(this IApplicationBuilder app) =>
        app.Use(EndAbortedRequestAsync);

    internal static async Task EndAbortedRequestAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // A response that has started cannot change its status; the connection is gone anyway.
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
    }

    /// <summary>
    /// The request log's level: Warning for an exception or an error status, Debug for everything else,
    /// including a request the client aborted (499).
    /// </summary>
    public static LogEventLevel GetRequestLogLevel(HttpContext context, Exception? ex) =>
        ex is not null
        || (context.Response.StatusCode >= 400 && context.Response.StatusCode != StatusCodes.Status499ClientClosedRequest)
            ? LogEventLevel.Warning
            : LogEventLevel.Debug;
}
