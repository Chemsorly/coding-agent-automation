using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// K8s-mode implementation of <see cref="IJobCleanupStrategy"/>.
/// Looks up the K8s Job name through <see cref="IK8sJobNameLookup"/> and deletes the Job to prevent
/// the Job controller from retrying (backoffLimit).
/// </summary>
public sealed class KubernetesJobCleanup : IJobCleanupStrategy
{
    private readonly IK8sJobNameLookup _jobNameLookup;
    private readonly IKubernetesJobClient _jobClient;
    private readonly string _k8sNamespace;
    private readonly ILogger _logger;

    public KubernetesJobCleanup(
        IK8sJobNameLookup jobNameLookup,
        IKubernetesJobClient jobClient,
        string k8sNamespace,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(jobNameLookup);
        ArgumentNullException.ThrowIfNull(jobClient);
        ArgumentNullException.ThrowIfNull(k8sNamespace);
        ArgumentNullException.ThrowIfNull(logger);

        _jobNameLookup = jobNameLookup;
        _jobClient = jobClient;
        _k8sNamespace = k8sNamespace;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task TryDeleteJobForRunAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out var workItemId))
            return;

        string? jobName;
        try
        {
            jobName = await _jobNameLookup.GetK8sJobNameAsync(workItemId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "KubernetesJobCleanup: failed to look up K8s Job name for run {RunId} (non-fatal)",
                runId);
            return;
        }

        if (string.IsNullOrEmpty(jobName))
            return;

        await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            _jobClient, jobName, _k8sNamespace, _logger,
            $"KubernetesJobCleanup: cancelled run {runId}", ct);
    }
}
