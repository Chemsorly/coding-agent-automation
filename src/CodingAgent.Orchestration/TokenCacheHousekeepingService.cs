using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

/// <summary>
/// Background service that periodically evicts expired entries from
/// <see cref="TokenVendingService"/>'s token and semaphore caches.
///
/// Registered as a hosted service in all three hosts that register
/// <see cref="TokenVendingService"/> as a singleton (Api, Web, Scheduler).
/// The sweep interval defaults to 5 minutes — well within the 1-hour token
/// lifetime — so stale entries are collected long before memory pressure
/// could occur in normal deployments.
/// </summary>
internal sealed class TokenCacheHousekeepingService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly TokenVendingService _tokenVendingService;
    private readonly ILogger _logger;
    private readonly TimeSpan _sweepInterval;
    private readonly TimeProvider _timeProvider;

    public TokenCacheHousekeepingService(
        TokenVendingService tokenVendingService,
        ILogger logger,
        TimeSpan? sweepInterval = null,
        TimeProvider? timeProvider = null)
    {
        // TODO [WARNING]: tokenVendingService and logger are not null-checked here.
        // A null tokenVendingService would surface as a NullReferenceException on the background
        // thread inside ExecuteAsync, swallowed by the catch(Exception) sweep handler and logged
        // only as a generic "sweep error", masking a DI misconfiguration. Add
        // ArgumentNullException.ThrowIfNull(tokenVendingService) and
        // ArgumentNullException.ThrowIfNull(logger) to fail fast at composition time, consistent
        // with TokenVendingService's own constructors. (.NET Specialist warning)
        _tokenVendingService = tokenVendingService;
        _logger = logger.ForContext<TokenCacheHousekeepingService>();
        _sweepInterval = sweepInterval ?? DefaultInterval;
        // TODO [WARNING]: _timeProvider here governs only the PeriodicTimer cadence (when sweeps fire),
        // not the expiry comparison inside TrimExpiredCacheEntries, which uses TokenVendingService's own
        // independent TimeProvider. If callers inject different TimeProvider instances into the two
        // services, sweeps will tick on one time source while expiry is judged on another, causing
        // entries to never evict or evict prematurely. Ensure both services share the same TimeProvider
        // instance (e.g., via DI) or add a clarifying comment at the injection site. (.NET Specialist warning)
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_sweepInterval, _timeProvider);

        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _tokenVendingService.TrimExpiredCacheEntries();
                _logger.Debug("TokenCacheHousekeepingService: eviction sweep complete " +
                    "(cacheCount={CacheCount}, semaphoreCount={SemaphoreCount})",
                    _tokenVendingService.CacheCount, _tokenVendingService.SemaphoreCount);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "TokenCacheHousekeepingService: sweep error");
            }
        }
    }
}
