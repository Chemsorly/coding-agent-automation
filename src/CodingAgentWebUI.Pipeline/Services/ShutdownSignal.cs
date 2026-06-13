using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Thread-safe implementation of <see cref="IShutdownSignal"/>.
/// Registered as a singleton — shared between <c>ShutdownService</c> (writer) and
/// dispatch paths (readers).
/// </summary>
public sealed class ShutdownSignal : IShutdownSignal
{
    private volatile bool _isShuttingDown;

    /// <inheritdoc />
    public bool IsShuttingDown => _isShuttingDown;

    /// <inheritdoc />
    public void SignalShutdown() => _isShuttingDown = true;
}
