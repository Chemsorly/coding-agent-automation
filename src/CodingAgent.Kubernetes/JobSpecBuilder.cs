using CodingAgent.Pipeline;
using System.Text.Json;
using k8s.Models;
using Serilog;

namespace CodingAgent.Kubernetes;

/// <summary>
/// Builds a K8s <see cref="V1Job"/> spec from a <see cref="JobTemplate"/> and per-dispatch context.
/// Extracted from <see cref="DispatchService.BuildJobSpec"/> to enable unit testing
/// and template-driven pod spec construction.
/// </summary>
public static class JobSpecBuilder
{
    private const string AgentApiKeyVolumeName = "agent-api-key";

    private static readonly JsonSerializerOptions K8sDeserializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Per-dispatch context that varies per work item (not from the template).
    /// </summary>
    public sealed record BuildContext
    {
        /// <summary>
        /// Work item ID. When non-null, the <c>--work-item-id</c> CLI arg and
        /// <c>caa/work-item-id</c> label are emitted. Null for jobs that do not
        /// correspond to a work item (e.g. model-fetch jobs).
        /// </summary>
        public Guid? WorkItemId { get; init; }
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

        /// <summary>
        /// W3C traceparent captured at WorkItem creation time (API span).
        /// When non-null, injected as TRACEPARENT env var so the worker can restore the trace context.
        /// </summary>
        public string? TraceParent { get; init; }

        /// <summary>
        /// Name of the per-Job K8s Secret that holds the per-job agent API key (Spec 043 Req 8a).
        /// When set, the Job container receives <c>AGENT_API_KEY</c> from this Secret instead of
        /// mounting the master <c>agent-api-key</c> Secret. This prevents work-item agent pods
        /// from holding the master key — they receive only <c>HMAC-SHA256(masterKey, jobName)</c>
        /// which cannot be used to compute credentials for any other job.
        ///
        /// The value stored in this Secret is pre-computed by <c>DispatchLifecycleService</c>
        /// before the K8s Job is created. <c>HubConnectionManager</c> and <c>WorkItemHttpClient</c>
        /// use the key directly (no further derivation); the server-side <c>AgentApiKeyAuthHandler</c>
        /// validates by computing <c>HMAC(masterKey, agentId)</c> and comparing with the presented token.
        ///
        /// Null for non-work-item jobs (model-fetch, consolidation) which are not yet migrated.
        /// </summary>
        public string? DerivedKeySecretName { get; init; }
    }

    /// <summary>
    /// Builds a complete <see cref="V1Job"/> by merging template-defined pod spec fields
    /// with per-dispatch dynamic fields (work item ID, PVC claim, secrets).
    /// </summary>
    public static V1Job Build(JobTemplate template, BuildContext ctx)
    {
        var isKiroAgent = IsKiroAgent(template.ProviderType);

        var envVars = BuildEnvVars(template, ctx);
        var (volumeMounts, volumes) = BuildVolumeMountsAndVolumes(isKiroAgent, ctx);

        // ── Container ───────────────────────────────────────────────────────
        var container = new V1Container
        {
            Name = "agent",
            Image = template.Image,
            ImagePullPolicy = template.ImagePullPolicy,
            Args = ctx.WorkItemId is not null
                ? ["--mode=workitem", $"--work-item-id={ctx.WorkItemId}"]
                : [],
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
            podSecurityContext = DeserializeK8s<V1PodSecurityContext>(pscElement);
        else
            podSecurityContext = new V1PodSecurityContext
            {
                RunAsNonRoot = true,
                SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
            };

        // ── Init containers ─────────────────────────────────────────────────
        IList<V1Container>? initContainers = template.InitContainers is { } icElement
            ? DeserializeK8s<List<V1Container>>(icElement) : null;

        // ── Tolerations ─────────────────────────────────────────────────────
        IList<V1Toleration>? tolerations = template.Tolerations is { } tolElement
            ? DeserializeK8s<List<V1Toleration>>(tolElement) : null;

        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = ctx.JobName,
                NamespaceProperty = ctx.Namespace,
                Labels = new Dictionary<string, string>(
                    new[]
                    {
                        KeyValuePair.Create("app.kubernetes.io/managed-by", "caa-orchestrator"),
                        KeyValuePair.Create("app.kubernetes.io/component", "agent-job"),
                        KeyValuePair.Create("caa/agent-selector", ctx.AgentSelector.Replace(',', '.'))
                    }
                    .Concat(ctx.WorkItemId is not null
                        ? [KeyValuePair.Create("caa/work-item-id", ctx.WorkItemId.ToString()!)]
                        : []))
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
    /// Builds the environment variable list for the agent container.
    /// Includes static orchestrator/agent vars, OTEL propagation, log level, and agent labels.
    /// </summary>
    private static List<V1EnvVar> BuildEnvVars(JobTemplate template, BuildContext ctx)
    {
        var envVars = new List<V1EnvVar>
        {
            new() { Name = "ORCHESTRATOR_URL", Value = ctx.OrchestratorUrl },
        };

        // Spec 043 Req 8a: vend derived key via per-Job Secret when available.
        // Non-work-item jobs (model-fetch, consolidation) fall back to the file-based
        // master key until they are migrated.
        if (!string.IsNullOrEmpty(ctx.DerivedKeySecretName))
        {
            // Derived key path: AGENT_API_KEY env var sourced from per-Job Secret.
            // No AGENT_API_KEY_FILE — agent reads AGENT_API_KEY directly.
            envVars.Add(new V1EnvVar
            {
                Name = "AGENT_API_KEY",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = ctx.DerivedKeySecretName,
                        Key = "agent-api-key"
                    }
                }
            });
        }
        else
        {
            // Legacy path: master Secret file mount (non-work-item jobs).
            envVars.Add(new V1EnvVar
            {
                Name = "AGENT_API_KEY_FILE",
                Value = "/var/run/secrets/agent-api-key/agent-api-key"
            });
        }

        envVars.Add(new V1EnvVar
        {
            // Set AGENT_ID to the Job name (not the pod name, which has a random suffix).
            // Key derivation uses the job name: HMAC(masterKey, jobName).
            // Using metadata.name (pod name) would give caa-xxx-<random> which won't match.
            Name = "AGENT_ID",
            Value = ctx.JobName
        });

        var otelEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrEmpty(otelEndpoint))
            envVars.Add(new V1EnvVar { Name = "OTEL_EXPORTER_OTLP_ENDPOINT", Value = otelEndpoint });

        var otelProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        if (!string.IsNullOrEmpty(otelProtocol))
            envVars.Add(new V1EnvVar { Name = "OTEL_EXPORTER_OTLP_PROTOCOL", Value = otelProtocol });

        // OTEL headers may contain auth tokens — read from Secret rather than propagating plaintext
        envVars.Add(new V1EnvVar
        {
            Name = "OTEL_EXPORTER_OTLP_HEADERS",
            ValueFrom = new V1EnvVarSource
            {
                SecretKeyRef = new V1SecretKeySelector
                {
                    Name = ctx.AgentApiKeySecretName,
                    Key = "otel-headers",
                    Optional = true
                }
            }
        });

        // Stable service name for all agent pods — run identity lives in service.instance.id, not service.name.
        // TODO: OTEL_SERVICE_NAME is added unconditionally without checking whether it already exists in
        // envVars. Kubernetes uses the first occurrence when there are duplicates, so a future change that
        // forwards OTEL_SERVICE_NAME from the parent env earlier in this list would silently override this
        // value. Same risk applies to K8S_NAMESPACE_NAME, K8S_POD_NAME, and OTEL_RESOURCE_ATTRIBUTES.
        // Consider guarding with: if (!envVars.Any(e => e.Name == "OTEL_SERVICE_NAME")) ...
        // See review finding (issue #2969).
        envVars.Add(new V1EnvVar { Name = "OTEL_SERVICE_NAME", Value = "coding-agent-worker" });

        // Downward API: expose namespace and pod name so OTEL_RESOURCE_ATTRIBUTES can reference them
        // via $(K8S_NAMESPACE_NAME) / $(K8S_POD_NAME) Kubernetes env-var substitution.
        // These MUST be added before OTEL_RESOURCE_ATTRIBUTES — K8s only resolves $(VAR) references
        // to variables that appear earlier in the same env list.
        envVars.Add(new V1EnvVar
        {
            Name = "K8S_NAMESPACE_NAME",
            ValueFrom = new V1EnvVarSource
            {
                FieldRef = new V1ObjectFieldSelector { FieldPath = "metadata.namespace" }
            }
        });
        envVars.Add(new V1EnvVar
        {
            Name = "K8S_POD_NAME",
            ValueFrom = new V1EnvVarSource
            {
                FieldRef = new V1ObjectFieldSelector { FieldPath = "metadata.name" }
            }
        });

        // Compose OTEL_RESOURCE_ATTRIBUTES: start from any inherited parent value (trim trailing comma
        // to avoid double-comma), then append per-job identity and Kubernetes attributes.
        // OTEL_RESOURCE_ATTRIBUTES is always set (even when parent is empty) because service.instance.id
        // and k8s.job.name are unconditionally needed for per-run attribution.
        var otelResourceAttrs = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        var resourceAttrsBase = string.IsNullOrEmpty(otelResourceAttrs)
            ? string.Empty
            : otelResourceAttrs.TrimEnd(',') + ",";
        // TODO: ctx.JobName is interpolated directly without validation. A malformed job name
        // containing a comma or equals sign would inject spurious key-value pairs into
        // OTEL_RESOURCE_ATTRIBUTES (e.g. "foo,service.name=evil" → attacker-chosen service.name).
        // Kubernetes DNS label rules (alphanumeric + '-', ≤63 chars) naturally prevent this, but
        // the safety relies entirely on callers enforcing the naming convention. Consider adding an
        // explicit assertion here (e.g. Regex.IsMatch(ctx.JobName, @"^[a-z0-9\-]{1,63}$")) or at
        // minimum documenting the assumed invariant. See review finding (issue #2969).
        envVars.Add(new V1EnvVar
        {
            Name = "OTEL_RESOURCE_ATTRIBUTES",
            Value = $"{resourceAttrsBase}service.instance.id={ctx.JobName},k8s.job.name={ctx.JobName},k8s.namespace.name=$(K8S_NAMESPACE_NAME),k8s.pod.name=$(K8S_POD_NAME)"
        });

        // Propagate the originating W3C traceparent so the worker's spans attach to the upstream
        // API trace rather than starting a disconnected root trace.
        if (!string.IsNullOrEmpty(ctx.TraceParent))
            envVars.Add(new V1EnvVar { Name = "TRACEPARENT", Value = ctx.TraceParent });

        // Propagate log level so worker pods use the same verbosity as the orchestrator
        var logLevel = Environment.GetEnvironmentVariable(AgentDefaults.EnvLogLevel);
        if (!string.IsNullOrEmpty(logLevel))
            envVars.Add(new V1EnvVar { Name = AgentDefaults.EnvLogLevel, Value = logLevel });

        // Propagate agent labels from the template so WorkItemAgentService can read them
        if (!string.IsNullOrEmpty(template.Labels))
            envVars.Add(new V1EnvVar { Name = AgentDefaults.EnvAgentLabels, Value = template.Labels });

        // OpenCode agents: inject OPENCODE_CONFIG_CONTENT from the secret so entrypoint.sh
        // writes it to opencode.json at startup. Using an env var (not a directory volume mount)
        // so the entrypoint can write to /home/ubuntu/.config/opencode/opencode.json normally.
        // Security note: env var values are visible in `kubectl describe pod` and etcd.
        // A directory volume mount is not usable here because mounting the secret dir replaces
        // /home/ubuntu/.config/opencode/ entirely, preventing entrypoint.sh from writing the
        // correctly-named opencode.json file. Accepted tradeoff: port 4096 is container-internal
        // only and the secret is already in the K8s Secret object (same etcd exposure level).
        if (IsOpencodeAgent(template.ProviderType) && !string.IsNullOrEmpty(ctx.OpencodeConfigSecretName))
        {
            envVars.Add(new V1EnvVar
            {
                Name = "OPENCODE_CONFIG_CONTENT",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = ctx.OpencodeConfigSecretName,
                        Key = "opencode-config-content",
                        Optional = true
                    }
                }
            });
        }

        return envVars;
    }

    /// <summary>
    /// Builds the volume mounts and volumes for the agent container.
    /// When <see cref="JobSpecBuilder.BuildContext.DerivedKeySecretName"/> is set (work-item mode),
    /// the master <c>agent-api-key</c> Secret is NOT mounted — the derived key is vended via an
    /// env var from the per-Job Secret instead. For legacy non-work-item jobs the master Secret
    /// is still mounted via file.
    /// </summary>
    private static (List<V1VolumeMount> mounts, List<V1Volume> volumes) BuildVolumeMountsAndVolumes(
        bool isKiroAgent, BuildContext ctx)
    {
        var volumeMounts = new List<V1VolumeMount>();
        var volumes = new List<V1Volume>();

        // Only mount the master secret for non-work-item jobs (legacy path).
        // Work-item dispatch provides the derived key via DerivedKeySecretName (Spec 043 Req 8a).
        if (string.IsNullOrEmpty(ctx.DerivedKeySecretName))
        {
            volumeMounts.Add(new V1VolumeMount
            {
                Name = AgentApiKeyVolumeName,
                MountPath = "/var/run/secrets/agent-api-key",
                ReadOnlyProperty = true
            });
            volumes.Add(new V1Volume
            {
                Name = AgentApiKeyVolumeName,
                Secret = new V1SecretVolumeSource
                {
                    SecretName = ctx.AgentApiKeySecretName,
                    Items = [new V1KeyToPath { Key = AgentApiKeyVolumeName, Path = AgentApiKeyVolumeName }]
                }
            });
        }

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

        // OpenCode config is now injected via OPENCODE_CONFIG_CONTENT env var (see BuildEnvVars).
        // No directory volume mount needed — entrypoint.sh writes the file at startup.

        if (ctx.WorkItemId.HasValue && ctx.ProjectSecrets is not null && ctx.ProjectSecrets.Count > 0)
        {
            var secretName = $"caa-secrets-{ctx.WorkItemId.Value.ToString("N")[..8]}";
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

        return (volumeMounts, volumes);
    }

    /// <summary>
    /// Deserializes a <see cref="JsonElement"/> to a k8s model type using the k8s client's
    /// default serializer options (camelCase property names, nullable handling).
    /// </summary>
    private static T DeserializeK8s<T>(JsonElement element)
    {
        // k8s client models use System.Text.Json with camelCase property names
        var result = element.Deserialize<T>(K8sDeserializerOptions);
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
