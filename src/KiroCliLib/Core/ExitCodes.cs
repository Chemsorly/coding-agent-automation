namespace KiroCliLib.Core;

/// <summary>
/// Well-known process exit codes used by the pipeline and agent containers.
/// </summary>
public static class ExitCodes
{
    /// <summary>Process completed successfully.</summary>
    public const int Success = 0;

    /// <summary>General (non-specific) failure.</summary>
    public const int GeneralFailure = 1;

    /// <summary>Process was killed due to timeout.</summary>
    public const int Timeout = 124;

    /// <summary>Process was cancelled (SIGINT / user cancellation).</summary>
    public const int Cancelled = 130;
}
