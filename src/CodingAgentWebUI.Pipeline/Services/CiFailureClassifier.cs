using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Classifies CI failures into categories to determine whether auto-retry is appropriate.
/// Infrastructure failures can be retried without consuming the agent retry budget.
/// </summary>
public static class CiFailureClassifier
{
    /// <summary>CI failure categories.</summary>
    public enum CiFailureCategory
    {
        /// <summary>Code-related failure — agent should fix.</summary>
        CodeFailure,
        /// <summary>Transient infrastructure failure — safe to auto-retry.</summary>
        Infrastructure,
        /// <summary>Rate limit hit — retryable with backoff.</summary>
        RateLimited,
        /// <summary>Unknown failure — treated conservatively as CodeFailure.</summary>
        Unknown
    }

    private static readonly string[] InfrastructurePatterns =
    {
        "lost communication with the server",
        "Process completed with exit code 143",
        "The runner has received a shutdown signal",
        "Unable to write data to the transport connection",
        "Unable to resolve action",
        "Could not resolve host",
        "Name or service not known",
        "Connection reset by peer",
        "Connection timed out",
        "No space left on device",
        "Error downloading",
        "failed to create shim task",
        "connection refused",
        "Cache service responded with",
        "Unable to load the service index"
    };

    private static readonly string[] CodeFailurePatterns =
    {
        "error CS",
        "Build FAILED",
        "Failed!  - Failed:",
        "Error Message:\n"
    };

    private static readonly string[] RateLimitPatterns =
    {
        "rate limit",
        "API rate limit exceeded",
        "429 Too Many Requests"
    };

    /// <summary>
    /// Classifies a CI failure based on job log content.
    /// </summary>
    public static CiFailureCategory Classify(PipelineRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var failedJobs = status.Jobs.Where(j => j.State == PipelineRunState.Failed).ToList();
        if (failedJobs.Count == 0)
            return CiFailureCategory.Unknown;

        var hasInfrastructure = false;
        var hasCodeFailure = false;
        var hasRateLimit = false;
        var hasAnyLogs = false;

        foreach (var job in failedJobs)
        {
            if (string.IsNullOrEmpty(job.LogContent))
                continue;

            hasAnyLogs = true;
            var log = job.LogContent.ReplaceLineEndings("\n");

            if (CodeFailurePatterns.Any(p => log.Contains(p, StringComparison.OrdinalIgnoreCase)))
                hasCodeFailure = true;

            if (InfrastructurePatterns.Any(p => log.Contains(p, StringComparison.OrdinalIgnoreCase)))
                hasInfrastructure = true;

            if (RateLimitPatterns.Any(p => log.Contains(p, StringComparison.OrdinalIgnoreCase)))
                hasRateLimit = true;
        }

        // Code failure indicators take priority — never auto-retry if code is broken
        if (hasCodeFailure)
            return CiFailureCategory.CodeFailure;

        if (hasRateLimit)
            return CiFailureCategory.RateLimited;

        if (hasInfrastructure)
            return CiFailureCategory.Infrastructure;

        // If no logs are available for any failed job, this is almost certainly a log-availability
        // race (BlobNotFound from GitHub Actions storage). Treat as infrastructure so we use the
        // lightweight infra-retry path instead of wasting agent retry budget on a problem the agent
        // cannot diagnose without logs.
        if (!hasAnyLogs)
            return CiFailureCategory.Infrastructure;

        return CiFailureCategory.Unknown;
    }
}
