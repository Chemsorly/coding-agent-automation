namespace CodingAgentWebUI.Infrastructure.Resilience;

/// <summary>
/// Classifies git push failures into actionable categories.
/// </summary>
public static class PushErrorClassifier
{
    /// <summary>Push failure categories.</summary>
    public enum PushFailureCategory
    {
        /// <summary>Authentication or credentials error.</summary>
        Auth,
        /// <summary>Branch protection rules prevent push.</summary>
        BranchProtection,
        /// <summary>Transient network error (retryable).</summary>
        Network,
        /// <summary>Non-fast-forward / branch diverged.</summary>
        Conflict,
        /// <summary>Unknown error.</summary>
        Unknown
    }

    /// <summary>
    /// Classifies a push error message into a failure category.
    /// </summary>
    public static PushFailureCategory Classify(string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);

        if (errorMessage.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("credentials", StringComparison.OrdinalIgnoreCase))
        {
            return PushFailureCategory.Auth;
        }

        if (errorMessage.Contains("protected branch", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("required status check", StringComparison.OrdinalIgnoreCase))
        {
            return PushFailureCategory.BranchProtection;
        }

        if (errorMessage.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            return PushFailureCategory.Conflict;
        }

        if (errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("DNS", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("503", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("resolve", StringComparison.OrdinalIgnoreCase))
        {
            return PushFailureCategory.Network;
        }

        return PushFailureCategory.Unknown;
    }

    /// <summary>
    /// Returns an actionable error message for the given push failure category.
    /// </summary>
    public static string GetActionableMessage(PushFailureCategory category, string? branchName = null)
    {
        return category switch
        {
            PushFailureCategory.Auth => "Push failed: authentication error. Token may have expired.",
            PushFailureCategory.BranchProtection => $"Push failed: branch '{branchName ?? "unknown"}' is protected.",
            PushFailureCategory.Network => "Push failed: network error after retries exhausted.",
            PushFailureCategory.Conflict => "Push failed: branch has diverged from remote (non-fast-forward).",
            _ => "Push failed: unexpected error."
        };
    }
}
