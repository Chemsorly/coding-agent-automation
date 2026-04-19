namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Controls how the pipeline handles agent modifications to blacklisted paths.
/// </summary>
public enum BlacklistMode
{
    /// <summary>Unstage blacklisted files, log a warning, and continue the pipeline.</summary>
    WarnAndExclude,

    /// <summary>Fail the pipeline when blacklisted files are detected.</summary>
    Fail
}
