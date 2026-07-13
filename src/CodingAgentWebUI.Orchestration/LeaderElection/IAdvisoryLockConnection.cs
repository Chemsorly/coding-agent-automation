using System.Data;

namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Abstraction over the database connection used for advisory lock operations.
/// Allows unit testing without a real PostgreSQL connection.
/// </summary>
internal interface IAdvisoryLockConnection : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The current state of the underlying connection.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Opens the connection to the database.
    /// </summary>
    Task OpenAsync(CancellationToken ct);

    /// <summary>
    /// Attempts to acquire the advisory lock. Returns true if acquired.
    /// </summary>
    Task<bool> TryAcquireLockAsync(long lockKey, CancellationToken ct);

    /// <summary>
    /// Verifies the advisory lock is still held by this session.
    /// </summary>
    Task<bool> VerifyLockIsHeldAsync(long lockKey, CancellationToken ct);

    /// <summary>
    /// Explicitly releases the advisory lock.
    /// </summary>
    Task ReleaseLockAsync(long lockKey);

    /// <summary>
    /// Closes the underlying connection.
    /// </summary>
    Task CloseAsync();
}
