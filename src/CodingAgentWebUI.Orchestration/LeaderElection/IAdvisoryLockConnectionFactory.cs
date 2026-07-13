namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Factory for creating <see cref="IAdvisoryLockConnection"/> instances.
/// Allows the service to create new connections after a disconnect without
/// depending on <c>new NpgsqlConnection(...)</c> directly.
/// </summary>
internal interface IAdvisoryLockConnectionFactory
{
    /// <summary>
    /// Creates a new advisory lock connection (not opened yet).
    /// </summary>
    IAdvisoryLockConnection Create();
}
