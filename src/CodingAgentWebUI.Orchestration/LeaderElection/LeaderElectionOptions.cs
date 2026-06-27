namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Configuration options for K8s Lease-based leader election.
/// Bound from configuration section "LeaderElection".
/// </summary>
public sealed class LeaderElectionOptions
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
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Deadline for the leader to renew the lease before it expires.
    /// Must be less than LeaseDuration.
    /// </summary>
    public TimeSpan RenewDeadline { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval between attempts to acquire or renew the lease.
    /// </summary>
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
}
