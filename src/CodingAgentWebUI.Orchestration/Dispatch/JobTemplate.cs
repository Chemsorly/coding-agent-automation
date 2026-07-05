using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Represents a K8s Job pod template for a specific agent label set.
/// Deserialized from the job-templates.json ConfigMap.
/// Fields like <see cref="InitContainers"/>, <see cref="PodSecurityContext"/>, and
/// <see cref="Tolerations"/> use <see cref="JsonElement"/> for pass-through to the k8s API
/// without modeling every K8s spec field.
/// </summary>
public sealed record JobTemplate
{
    /// <summary>Comma-separated agent labels (e.g., "kiro,dotnet,dotnet10"). Normalized to sorted form on load.</summary>
    [JsonPropertyName("labels")]
    public required string Labels { get; init; }

    /// <summary>Container image including tag (e.g., "chemsorly/coding-agent:kiro-dotnet10").</summary>
    [JsonPropertyName("image")]
    public required string Image { get; init; }

    /// <summary>Agent provider type: "kiro" or "opencode". Determines volume mount profile.</summary>
    [JsonPropertyName("providerType")]
    public required string ProviderType { get; init; }

    /// <summary>Max concurrent Jobs for this label set. 0 = no limit.</summary>
    [JsonPropertyName("maxConcurrent")]
    public int MaxConcurrent { get; init; }

    /// <summary>Resource requests/limits for the agent container.</summary>
    [JsonPropertyName("resources")]
    public JobTemplateResources? Resources { get; init; }

    /// <summary>Pod-level security context (runAsUser, fsGroup, etc.). Pass-through to V1PodSecurityContext.</summary>
    [JsonPropertyName("podSecurityContext")]
    public JsonElement? PodSecurityContext { get; init; }

    /// <summary>Node selector key-value pairs for pod scheduling.</summary>
    [JsonPropertyName("nodeSelector")]
    public Dictionary<string, string>? NodeSelector { get; init; }

    /// <summary>Init containers injected before the agent container. Pass-through to List&lt;V1Container&gt;.</summary>
    [JsonPropertyName("initContainers")]
    public JsonElement? InitContainers { get; init; }

    /// <summary>Pod tolerations for scheduling. Pass-through to List&lt;V1Toleration&gt;.</summary>
    [JsonPropertyName("tolerations")]
    public JsonElement? Tolerations { get; init; }
}

/// <summary>
/// Resource requests and limits for a Job container.
/// </summary>
public sealed record JobTemplateResources
{
    [JsonPropertyName("requests")]
    public Dictionary<string, string>? Requests { get; init; }

    [JsonPropertyName("limits")]
    public Dictionary<string, string>? Limits { get; init; }
}
