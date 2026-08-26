using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Autorest;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s-mode implementation of <see cref="IJobCleanupStrategy"/>.
/// Looks up the K8s Job name via the Pipeline API and deletes the Job to prevent
/// the Job controller from retrying (backoffLimit).
/// </summary>
public sealed class KubernetesJobCleanup : IJobCleanupStrategy
{
    private readonly IPipelineApiWorkItemClient _apiClient;
    private readonly IKubernetesJobClient _jobClient;
    private readonly string _k8sNamespace;
    private readonly ILogger _logger;

    public KubernetesJobCleanup(
        IPipelineApiWorkItemClient apiClient,
        IKubernetesJobClient jobClient,
        string k8sNamespace,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(jobClient);
        ArgumentNullException.ThrowIfNull(k8sNamespace);
        ArgumentNullException.ThrowIfNull(logger);

        _apiClient = apiClient;
        _jobClient = jobClient;
        _k8sNamespace = k8sNamespace;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task TryDeleteJobForRunAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out var workItemId))
            return;

        try
        {
            var jobName = await _apiClient.GetK8sJobNameAsync(workItemId, ct);

            if (string.IsNullOrEmpty(jobName))
                return;

            await _jobClient.DeleteJobAsync(jobName, _k8sNamespace, ct);
            _logger.Information(
                "KubernetesJobCleanup: deleted K8s Job {JobName} for cancelled run {RunId}",
                jobName, runId);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Job already deleted (e.g., by ReconciliationService race) — expected, not a warning
            _logger.Debug(
                "KubernetesJobCleanup: K8s Job for run {RunId} already deleted (404)",
                runId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "KubernetesJobCleanup: failed to delete K8s Job for run {RunId} (non-fatal, Job will expire via TTL)",
                runId);
        }
    }
}
