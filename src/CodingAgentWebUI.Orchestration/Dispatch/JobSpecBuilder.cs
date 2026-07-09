using System.Text.Json;
using CodingAgentWebUI.Agent;
using k8s.Models;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Builds a K8s <see cref="V1Job"/> spec from a <see cref="JobTemplate"/> and per-dispatch context.
/// Extracted from <see cref="DispatchService.BuildJobSpec"/> to enable unit testing
/// and template-driven pod spec construction.
/// </summary>
public static class JobSpecBuilder
{
    /// <summary>
    /// Per-dispatch context that varies per work item (not from the template).
    /// </summary>
    public sealed record BuildContext
    {
        public required Guid WorkItemId { get; init; }
        public required string AgentSelector { get; init; }
        public required int TimeoutSeconds { get; set; }
        public required string JobName { get; set; }
        public required string? ClaimedPvc { get; init; }
        public required string OrchestratorUrl { get; init; }
        public required string AgentApiKeySecretName { get; init; }
        public required string AgentServiceAccountName { get; init; }
        public required string Namespace { get; init; }
        public string? OpencodeConfigSecretName { get; init; }
        public Dictionary<string, string>? ProjectSecrets { get; init; }
    }

    /// <summary>
    /// Builds a complete <see cref="V1Job"/> by merging template-defined pod spec fields
    /// with per-dispatch dynamic fields (work item ID, PVC claim, secrets).
    /// </summary>
    public static V1Job Build(JobTemplate template, BuildContext ctx)
    {
        var isKiroAgent = IsKiroAgent(template.ProviderType);
        var isOpencodeAgent = IsOpencodeAgent(template.ProviderType);

        // ── Env vars ────────────────────────────────────────────────────────
        var envVars = new List<V1EnvVar>
        {
            new() { Name = "ORCHESTRATOR_URL", Value = ctx.OrchestratorUrl },
            new() { Name = "AGENT_API_KEY_FILE", Value = "/var/run/secrets/agent-api-key/agent-api-key" },
            new()
            {
                Name = "AGENT_ID",
                ValueFrom = new V1EnvVarSource
                {
                    FieldRef = new V1ObjectFieldSelector { FieldPath = "metadata.name" }
                }
            }
        };

        var otelEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrEmpty(otelEndpoint))
            envVars.Add(new V1EnvVar { Name = "OTEL_EXPORTER_OTLP_ENDPOINT", Value = otelEndpoint });

        var otelHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        if (!string.IsNullOrEmpty(otelHeaders))
            envVars.Add(new V1EnvVar { Name = "OTEL_EXPORTER_OTLP_HEADERS", Value = otelHeaders });

        // Propagate agent labels from the template so WorkItemAgentService can read them
        if (!string.IsNullOrEmpty(template.Labels))
            envVars.Add(new V1EnvVar { Name = AgentDefaults.EnvAgentLabels, Value = template.Labels });

        // ── Volumes & mounts ────────────────────────────────────────────────
        var volumeMounts = new List<V1VolumeMount>
        {
            new()
            {
                Name = "agent-api-key",
                MountPath = "/var/run/secrets/agent-api-key",
                ReadOnlyProperty = true
            }
        };

        var volumes = new List<V1Volume>
        {
            new()
            {
                Name = "agent-api-key",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = ctx.AgentApiKeySecretName,
                    Items = [new V1KeyToPath { Key = "agent-api-key", Path = "agent-api-key" }]
                }
            }
        };

        if (isKiroAgent && ctx.ClaimedPvc is not null)
        {
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "kiro-cli-data",
                MountPath = "/home/ubuntu/.local/share/kiro-cli"
            });
            volumes.Add(new V1Volume
            {
                Name = "kiro-cli-data",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName = ctx.ClaimedPvc
                }
            });
        }

        if (isOpencodeAgent && !string.IsNullOrEmpty(ctx.OpencodeConfigSecretName))
        {
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "opencode-config",
                MountPath = "/home/ubuntu/.config/opencode",
                ReadOnlyProperty = true
            });
            volumes.Add(new V1Volume
            {
                Name = "opencode-config",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = ctx.OpencodeConfigSecretName
                }
            });
        }

        if (ctx.ProjectSecrets is not null && ctx.ProjectSecrets.Count > 0)
        {
            var secretName = $"caa-secrets-{ctx.WorkItemId.ToString("N")[..8]}";
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "project-secrets",
                MountPath = "/var/run/secrets/project-secrets",
                ReadOnlyProperty = true
            });
            volumes.Add(new V1Volume
            {
                Name = "project-secrets",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = secretName,
                    Optional = true
                }
            });
        }

        // ── Container ───────────────────────────────────────────────────────
        var container = new V1Container
        {
            Name = "agent",
            Image = template.Image,
            ImagePullPolicy = template.ImagePullPolicy,
            Args = [$"--work-item-id={ctx.WorkItemId}"],
            Env = envVars,
            VolumeMounts = volumeMounts,
            SecurityContext = new V1SecurityContext
            {
                Capabilities = new V1Capabilities { Drop = ["ALL"] }
            }
        };

        // Apply resources from template
        if (template.Resources is not null)
        {
            container.Resources = new V1ResourceRequirements
            {
                Requests = template.Resources.Requests?
                    .ToDictionary(kv => kv.Key, kv => new ResourceQuantity(kv.Value)),
                Limits = template.Resources.Limits?
                    .ToDictionary(kv => kv.Key, kv => new ResourceQuantity(kv.Value))
            };
        }

        // ── Pod security context ────────────────────────────────────────────
        V1PodSecurityContext podSecurityContext;
        if (template.PodSecurityContext is { } pscElement)
        {
            podSecurityContext = DeserializeK8s<V1PodSecurityContext>(pscElement);
        }
        else
        {
            // Hardened defaults when no template override
            podSecurityContext = new V1PodSecurityContext
            {
                RunAsNonRoot = true,
                SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
            };
        }

        // ── Init containers ─────────────────────────────────────────────────
        IList<V1Container>? initContainers = null;
        if (template.InitContainers is { } icElement)
        {
            initContainers = DeserializeK8s<List<V1Container>>(icElement);
        }

        // ── Tolerations ─────────────────────────────────────────────────────
        IList<V1Toleration>? tolerations = null;
        if (template.Tolerations is { } tolElement)
        {
            tolerations = DeserializeK8s<List<V1Toleration>>(tolElement);
        }

        // ── Build Job ───────────────────────────────────────────────────────
        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = ctx.JobName,
                NamespaceProperty = ctx.Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["app.kubernetes.io/component"] = "agent-job",
                    ["caa/work-item-id"] = ctx.WorkItemId.ToString(),
                    ["caa/agent-selector"] = ctx.AgentSelector.Replace(',', '.')
                }
            },
            Spec = new V1JobSpec
            {
                Parallelism = 1,
                Completions = 1,
                BackoffLimit = 2,
                ActiveDeadlineSeconds = ctx.TimeoutSeconds + 60,
                TtlSecondsAfterFinished = 3600,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        ServiceAccountName = ctx.AgentServiceAccountName,
                        RestartPolicy = "Never",
                        TerminationGracePeriodSeconds = 30,
                        SecurityContext = podSecurityContext,
                        Containers = [container],
                        InitContainers = initContainers,
                        Volumes = volumes,
                        NodeSelector = template.NodeSelector,
                        Tolerations = tolerations
                    }
                }
            }
        };
    }

    /// <summary>
    /// Deserializes a <see cref="JsonElement"/> to a k8s model type using the k8s client's
    /// default serializer options (camelCase property names, nullable handling).
    /// </summary>
    private static T DeserializeK8s<T>(JsonElement element)
    {
        // k8s client models use System.Text.Json with camelCase property names
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var result = element.Deserialize<T>(options);
        if (result is null)
        {
            Log.Error("Failed to deserialize JsonElement to {TypeName}", typeof(T).Name);
            throw new InvalidOperationException($"Failed to deserialize JsonElement to {typeof(T).Name}");
        }
        return result;
    }

    private static bool IsKiroAgent(string providerType) =>
        string.Equals(providerType, "kiro", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpencodeAgent(string providerType) =>
        string.Equals(providerType, "opencode", StringComparison.OrdinalIgnoreCase);
}
