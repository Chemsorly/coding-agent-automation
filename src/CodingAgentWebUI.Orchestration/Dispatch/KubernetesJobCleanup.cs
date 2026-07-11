using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using k8s.Autorest;
using Microsoft.EntityFrameworkCore;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s-mode implementation of <see cref="IJobCleanupStrategy"/>.
/// Queries the database for the K8s Job name associated with a work item,
/// then deletes the Job to prevent the Job controller from retrying (backoffLimit).
/// </summary>
public sealed class KubernetesJobCleanup : IJobCleanupStrategy
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IKubernetesJobClient _jobClient;
    private readonly string _k8sNamespace;
    private readonly ILogger _logger;

    public KubernetesJobCleanup(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IKubernetesJobClient jobClient,
        string k8sNamespace,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(jobClient);
        ArgumentNullException.ThrowIfNull(k8sNamespace);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _jobClient = jobClient;
        _k8sNamespace = k8sNamespace;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task TryDeleteJobForRunAsync(string runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId, out var workItemId))
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var jobName = await db.WorkItems
                .Where(w => w.Id == workItemId)
                .Select(w => w.K8sJobName)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrEmpty(jobName))
                return;

            await _jobClient.DeleteJobAsync(jobName, _k8sNamespace, ct);
            _logger.Information(
                "KubernetesJobCleanup: deleted K8s Job {JobName} for cancelled run {RunId}",
                jobName, runId);
        }
        // TODO: httpEx.Response could theoretically be null — filter would evaluate to false (not a crash),
        // but the exception would fall through to the generic catch instead of being handled as a 404 gracefully.
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
