using System.ComponentModel.DataAnnotations;

namespace CodingAgentWebUI.Pipeline.LeaderElection;

/// <summary>
/// Configuration options for K8s Lease-based leader election.
/// Bound from configuration section "LeaderElection".
///
/// Intentionally keeps the namespace <c>CodingAgentWebUI.Pipeline.LeaderElection</c> even
/// though the class lives in <c>CodingAgentWebUI.Orchestration</c>. This avoids touching
/// the ~50 files that import that namespace for types that remain in Pipeline.
/// </summary>
public sealed class LeaderElectionOptions : IValidatableObject
{
    public const string SectionName = "LeaderElection";

    /// <summary>
    /// Name of the Lease resource in Kubernetes.
    /// Default: "caa-leader"
    /// </summary>
    public string LeaseName { get; set; } = "caa-leader";

    /// <summary>
    /// Kubernetes namespace for the Lease. If null/empty, read from
    /// POD_NAMESPACE env var or the mounted service account namespace file.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Duration that non-leader candidates must wait before attempting to acquire leadership.
    /// Must be greater than <see cref="RenewDeadline"/>.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:02", "01:00:00",
        ErrorMessage = "LeaseDuration must be between 2 seconds and 1 hour.")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Deadline for the leader to renew the lease before it expires.
    /// Must be less than LeaseDuration.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:59:59",
        ErrorMessage = "RenewDeadline must be between 1 second and 59 minutes 59 seconds.")]
    public TimeSpan RenewDeadline { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval between attempts to acquire or renew the lease.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00",
        ErrorMessage = "RetryPeriod must be between 1 second and 10 minutes.")]
    public TimeSpan RetryPeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Pod identity override. If null/empty, read from POD_NAME or HOSTNAME env var.
    /// </summary>
    public string? Identity { get; set; }

    /// <summary>
    /// If true, the service will fail startup when running outside a K8s cluster.
    /// If false (default), it logs a warning and stays as non-leader (graceful degradation).
    /// </summary>
    public bool FailOnNonKubernetesEnvironment { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RenewDeadline >= LeaseDuration)
            yield return new ValidationResult(
                $"RenewDeadline ({RenewDeadline}) must be less than LeaseDuration ({LeaseDuration}). " +
                "Kubernetes requires RenewDeadline < LeaseDuration for leader election to function correctly.",
                [nameof(RenewDeadline), nameof(LeaseDuration)]);
    }
}
