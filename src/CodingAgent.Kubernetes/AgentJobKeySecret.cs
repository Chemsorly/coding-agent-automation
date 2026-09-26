using System.Net;
using CodingAgent.Pipeline;
using k8s.Autorest;
using k8s.Models;

namespace CodingAgent.Kubernetes;

/// <summary>
/// The per-Job agent API key (Spec 043 Req 8a). Every agent Job — work item, consolidation, chat
/// and model fetch — gets its own Secret holding <c>HMAC-SHA256(master key, job name)</c>, which
/// <see cref="JobSpecBuilder"/> exposes to the pod as <c>AGENT_API_KEY</c>. That key authenticates
/// only as the Job's own agent ID; the master key never enters an agent pod. The Secret is owned
/// by its Job, so Kubernetes deletes it together with the Job.
/// </summary>
public static class AgentJobKeySecret
{
    /// <summary>Data key of the credential inside the Secret.</summary>
    public const string DataKey = "agent-api-key";

    private static readonly TimeSpan[] DefaultUidReadRetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)];

    /// <summary>Name of the Secret that holds the key for <paramref name="jobName"/>.</summary>
    public static string NameFor(string jobName) => $"caa-key-{jobName}";

    /// <summary>
    /// Creates the key Secret for a Job that already exists, owned by that Job when
    /// <paramref name="jobUid"/> is known. A Secret with the same name can only be left over from an
    /// earlier Job with the same name (a re-dispatched work item), and garbage collection of that
    /// Job would delete it under the new Job's pod — so it is replaced. Throws when the Secret cannot
    /// be created: the Job's pod cannot start without it.
    /// </summary>
    public static async Task CreateForJobAsync(
        IKubernetesJobClient client, string ns, string jobName, string? jobUid, string masterKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(jobName);

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = NameFor(jobName),
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["app.kubernetes.io/component"] = "agent-key"
                },
                OwnerReferences = string.IsNullOrEmpty(jobUid)
                    ? null
                    : [new V1OwnerReference { ApiVersion = "batch/v1", Kind = "Job", Name = jobName, Uid = jobUid }]
            },
            StringData = new Dictionary<string, string>
            {
                [DataKey] = AgentKeyDerivation.DeriveAgentKey(masterKey, jobName)
            }
        };

        try
        {
            await client.CreateSecretAsync(secret, ns, ct);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            await client.DeleteSecretAsync(secret.Metadata.Name, ns, ct);
            await client.CreateSecretAsync(secret, ns, ct);
        }
    }

    /// <summary>
    /// Reads the UID of a Job that was just created, retrying briefly while the API server catches
    /// up. Returns <see langword="null"/> when it cannot be read; the key Secret is then created
    /// without an owner reference and is not garbage-collected with the Job.
    /// </summary>
    public static async Task<string?> ReadJobUidAsync(
        IKubernetesJobClient client, string ns, string jobName, CancellationToken ct,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var delays = retryDelays ?? DefaultUidReadRetryDelays;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var job = await client.ReadJobAsync(jobName, ns, ct);
                return job?.Metadata?.Uid;
            }
            catch (Exception) when (!ct.IsCancellationRequested && attempt < delays.Count)
            {
                await Task.Delay(delays[attempt], ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
