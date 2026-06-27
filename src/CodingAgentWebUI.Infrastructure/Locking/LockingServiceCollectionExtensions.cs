using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.Infrastructure.Locking;

/// <summary>
/// DI registration for IDistributedLockProvider.
/// Postgres provider when DbContextFactory is available, InProcess otherwise.
/// </summary>
public static class LockingServiceCollectionExtensions
{
    /// <summary>
    /// Registers IDistributedLockProvider. Uses PostgresDistributedLockProvider when
    /// <paramref name="connectionString"/> is non-null/non-empty (indicating Postgres is configured),
    /// otherwise falls back to InProcessDistributedLockProvider.
    /// </summary>
    public static IServiceCollection AddDistributedLockProvider(
        this IServiceCollection services,
        string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            services.AddSingleton<IDistributedLockProvider>(sp =>
            {
                var factory = sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
                return new PostgresDistributedLockProvider(factory);
            });
        }
        else
        {
            services.AddSingleton<IDistributedLockProvider, InProcessDistributedLockProvider>();
        }

        return services;
    }
}
