using CodingAgent.Kubernetes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.JobController.Dispatch;

/// <summary>
/// DI registrations for the Job Controller dispatch services.
/// </summary>
public static class DispatchServiceRegistration
{
    /// <summary>
    /// Registers <see cref="DispatchServiceOptions"/> from configuration.
    /// <para>
    /// All poll-based dispatch has been removed. Regular work item dispatch
    /// (<c>DispatchService</c> / <c>DispatchLoop</c>) was removed in issue #2322.
    /// Consolidation dispatch (<c>ConsolidationDispatchService</c> / <c>ConsolidationDispatchLoop</c>)
    /// was removed in issue #2323. Work items now wait in the <c>Pending</c> queue until the
    /// Scheduler's dispatch loop starts them through <c>POST /api/work-items/{id}/dispatch</c>.
    /// </para>
    /// <para>
    /// The only active Job Controller service is <c>ReconciliationService</c> (post-dispatch
    /// lifecycle: timeout enforcement, K8s Job status sync, orphan cleanup).
    /// </para>
    /// </summary>
    public static IServiceCollection AddDispatchService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = DispatchServiceOptionsFactory.Create(configuration);
        services.AddSingleton(options);

        return services;
    }
}
