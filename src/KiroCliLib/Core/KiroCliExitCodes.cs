namespace KiroCliLib.Core;

/// <summary>
/// Well-known process exit codes returned by Kiro CLI operations.
/// </summary>
public static class KiroCliExitCodes
{
    /// <summary>Process completed successfully.</summary>
    public const int Success = 0;

    /// <summary>General (non-specific) failure.</summary>
    public const int GeneralFailure = 1;

    /// <summary>Process was cancelled (SIGINT / user cancellation).</summary>
    public const int Cancelled = 130;
}
