using System.Net;
using k8s.Autorest;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Kubernetes;

/// <summary>
/// Shared helpers for best-effort K8s Job deletion used across every dispatch path.
/// Centralises the canonical pattern: 404 → Debug (expected end state), OCE with a
/// cancelled token → rethrow, any other failure → Warning.
/// </summary>
public static class KubernetesJobDeleteHelper
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> is an
    /// <see cref="HttpOperationException"/> whose HTTP status code is 404 (Not Found).
    /// Unlike a message-string search, this check is unambiguous: it cannot be triggered
    /// by an error whose text happens to contain "404" or "not found".
    /// </summary>
    public static bool IsNotFound(Exception ex)
        => ex is HttpOperationException httpEx
           && httpEx.Response?.StatusCode == HttpStatusCode.NotFound;

    /// <summary>
    /// Best-effort deletion of a K8s Job. The three outcomes are:
    /// <list type="bullet">
    ///   <item><see langword="true"/> — Job was deleted successfully. The caller is responsible for any success log.</item>
    ///   <item><see langword="false"/> — Delete failed and the exception was swallowed:
    ///     404 Not Found is logged at Debug (the desired end state is already reached);
    ///     any other error is logged at Warning with <paramref name="context"/>.</item>
    ///   <item><see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled — rethrown so the
    ///     caller's cancellation path is respected. Callers that pass <see cref="CancellationToken.None"/>
    ///     are unaffected because <see cref="CancellationToken.None"/> is never cancelled.</item>
    /// </list>
    /// The method never logs on success; call-site success logs must be gated on the <see langword="true"/> return value.
    /// </summary>
    /// <param name="client">The K8s job client to delete through.</param>
    /// <param name="jobName">Name of the K8s Job to delete.</param>
    /// <param name="ns">Kubernetes namespace.</param>
    /// <param name="logger">Serilog logger for failure messages.</param>
    /// <param name="context">Short context string included in Warning messages (e.g. "ChatJobDispatcher: force delete").</param>
    /// <param name="ct">Cancellation token. When cancelled and <see cref="OperationCanceledException"/> is thrown, the exception propagates.</param>
    /// <returns><see langword="true"/> if the Job was deleted; <see langword="false"/> if the exception was swallowed.</returns>
    public static async Task<bool> TryDeleteJobAsync(
        IKubernetesJobClient client,
        string jobName,
        string ns,
        ILogger logger,
        string context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(jobName);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await client.DeleteJobAsync(jobName, ns, ct);
            return true;
        }
        catch (HttpOperationException httpEx) when (httpEx.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            logger.Debug(httpEx, "K8s Job {JobName} already deleted (404) — {Context}", jobName, context);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "{Context}: failed to delete K8s Job {JobName} (non-fatal, Job will expire via TTL)",
                context, jobName);
            return false;
        }
    }
}
