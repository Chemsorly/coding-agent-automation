using System.Text.Json;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Loads and resolves <see cref="JobTemplate"/> entries from a YAML or JSON file (ConfigMap-mounted).
/// Templates are keyed by normalized (sorted, trimmed) label set for O(1) lookup.
/// Supports both YAML (.yaml/.yml) and JSON (.json) — YAML is the primary format for K8s-native readability.
/// </summary>
public sealed class JobTemplateStore
{
    /// <summary>JSON serializer options used for deserializing job-templates.json (legacy/fallback).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Dictionary<string, JobTemplate> _templates;

    private JobTemplateStore(Dictionary<string, JobTemplate> templates)
    {
        _templates = templates;
    }

    /// <summary>
    /// Loads templates from a YAML string. Normalizes labels for lookup.
    /// Duplicate label sets: last entry wins.
    /// </summary>
    /// <exception cref="YamlDotNet.Core.YamlException">Thrown if YAML is malformed.</exception>
    public static JobTemplateStore LoadFromYaml(string yaml)
    {
        var dtos = YamlDeserializer.Deserialize<List<JobTemplateYamlDto>>(yaml);
        if (dtos is null)
        {
            Log.Error("Deserialized job templates list is null from YAML input");
            throw new InvalidOperationException("Deserialized job templates list is null");
        }

        var templates = dtos.Select(dto => dto.ToJobTemplate()).ToList();
        return BuildLookup(templates);
    }

    /// <summary>
    /// Loads templates from a JSON string. Normalizes labels for lookup.
    /// Duplicate label sets: last entry wins.
    /// </summary>
    /// <exception cref="JsonException">Thrown if JSON is malformed.</exception>
    public static JobTemplateStore LoadFromJson(string json)
    {
        var list = JsonSerializer.Deserialize<List<JobTemplate>>(json, JsonOptions);
        if (list is null)
        {
            Log.Error("Deserialized job templates list is null from JSON input");
            throw new JsonException("Deserialized job templates list is null");
        }

        return BuildLookup(list);
    }

    /// <summary>
    /// Loads templates from a file path. Auto-detects format from extension (.yaml/.yml → YAML, .json → JSON).
    /// Fails fast if file does not exist.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown if file does not exist.</exception>
    /// <exception cref="JsonException">Thrown if JSON content is malformed.</exception>
    /// <exception cref="YamlDotNet.Core.YamlException">Thrown if YAML content is malformed.</exception>
    public static JobTemplateStore LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Log.Error("Job templates file not found: {FilePath}", filePath);
            throw new FileNotFoundException($"Job templates file not found: {filePath}", filePath);
        }

        var content = File.ReadAllText(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".yaml" or ".yml" => LoadFromYaml(content),
            ".json" => LoadFromJson(content),
            _ => LoadFromYaml(content) // default to YAML for extensionless or unknown
        };
    }

    /// <summary>
    /// Resolves a template by agent selector. Normalizes the selector before lookup.
    /// Returns null if no template matches.
    /// </summary>
    public JobTemplate? Resolve(string agentSelector)
    {
        var key = NormalizeLabels(agentSelector);
        return _templates.TryGetValue(key, out var template) ? template : null;
    }

    /// <summary>
    /// Returns the max concurrent pod count for a selector. Returns 0 if no template matches or value is 0 (no limit).
    /// </summary>
    public int GetMaxConcurrent(string agentSelector)
    {
        var template = Resolve(agentSelector);
        return template?.MaxConcurrent ?? 0;
    }

    /// <summary>
    /// Returns all loaded templates as a read-only collection.
    /// </summary>
    public IReadOnlyCollection<JobTemplate> GetAllTemplates()
    {
        return _templates.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Normalizes a comma-separated label string: splits, trims, sorts, re-joins.
    /// </summary>
    public static string NormalizeLabels(string labels)
    {
        if (string.IsNullOrWhiteSpace(labels))
            return "";

        var parts = labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(parts, StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private static JobTemplateStore BuildLookup(List<JobTemplate> templates)
    {
        var dict = new Dictionary<string, JobTemplate>(StringComparer.Ordinal);
        foreach (var template in templates)
        {
            if (string.IsNullOrWhiteSpace(template.Image))
            {
                Log.Error("Job template '{Labels}' has empty Image — each template must specify a container image", template.Labels);
                throw new InvalidOperationException(
                    $"Job template '{template.Labels}' has empty Image — each template must specify a container image");
            }

            var key = NormalizeLabels(template.Labels);
            dict[key] = template;
        }
        return new JobTemplateStore(dict);
    }
}

/// <summary>
/// YAML-friendly DTO for deserialization. Maps to <see cref="JobTemplate"/> after conversion.
/// Uses simple types that YamlDotNet can natively deserialize (no JsonElement).
/// Pass-through fields (initContainers, podSecurityContext, tolerations) are deserialized as
/// object graphs and converted to JsonElement for downstream k8s API compatibility.
/// </summary>
internal sealed class JobTemplateYamlDto
{
    public string Labels { get; set; } = "";
    public string Image { get; set; } = "";
    public string ImagePullPolicy { get; set; } = "Always";
    public string ProviderType { get; set; } = "";
    public int MaxConcurrent { get; set; }
    public JobTemplateResourcesYamlDto? Resources { get; set; }
    public Dictionary<string, object>? PodSecurityContext { get; set; }
    public Dictionary<string, string>? NodeSelector { get; set; }
    public List<object>? InitContainers { get; set; }
    public List<object>? Tolerations { get; set; }

    public JobTemplate ToJobTemplate()
    {
        return new JobTemplate
        {
            Labels = Labels,
            Image = Image,
            ImagePullPolicy = ImagePullPolicy,
            ProviderType = ProviderType,
            MaxConcurrent = MaxConcurrent,
            Resources = Resources?.ToResources(),
            PodSecurityContext = ToJsonElement(PodSecurityContext),
            NodeSelector = NodeSelector,
            InitContainers = ToJsonElement(InitContainers),
            Tolerations = ToJsonElement(Tolerations)
        };
    }

    private static JsonElement? ToJsonElement(object? value)
    {
        if (value is null) return null;
        // YamlDotNet deserializes untyped mappings into Dictionary<object, object> with
        // values as boxed primitives (int, bool, string). System.Text.Json won't
        // recursively inspect runtime types of object-typed dictionary values, so we
        // convert the object graph to a JsonNode tree which preserves types correctly.
        var node = ToJsonNode(value);
        using var doc = JsonDocument.Parse(node!.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Converts a YamlDotNet-deserialized object graph to a <see cref="System.Text.Json.Nodes.JsonNode"/>
    /// that preserves numeric/boolean types from boxed primitives.
    /// YamlDotNet's ScalarNodeDeserializer defaults all scalars to strings when the target type
    /// is <c>object</c> (even with WithAttemptingUnquotedStringTypeDeserialization in nested contexts).
    /// We detect numeric/boolean strings and emit proper JSON types so downstream k8s model
    /// deserialization (V1PodSecurityContext, V1Container, etc.) succeeds on Int32/Int64/bool fields.
    /// This is safe because k8s API string-typed fields (toleration.value, container.name)
    /// never contain bare integers — they always include non-digit characters.
    /// </summary>
    private static System.Text.Json.Nodes.JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        int i => System.Text.Json.Nodes.JsonValue.Create(i),
        long l => System.Text.Json.Nodes.JsonValue.Create(l),
        double d => System.Text.Json.Nodes.JsonValue.Create(d),
        bool b => System.Text.Json.Nodes.JsonValue.Create(b),
        string s when long.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var lv) =>
            System.Text.Json.Nodes.JsonValue.Create(lv),
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var dv) =>
            System.Text.Json.Nodes.JsonValue.Create(dv),
        string s when s.Equals("true", StringComparison.OrdinalIgnoreCase) =>
            System.Text.Json.Nodes.JsonValue.Create(true),
        string s when s.Equals("false", StringComparison.OrdinalIgnoreCase) =>
            System.Text.Json.Nodes.JsonValue.Create(false),
        string s => System.Text.Json.Nodes.JsonValue.Create(s),
        IDictionary<object, object> dict => new System.Text.Json.Nodes.JsonObject(
            dict.Select(kvp => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(
                kvp.Key.ToString()!, ToJsonNode(kvp.Value)))),
        IDictionary<string, object> dict => new System.Text.Json.Nodes.JsonObject(
            dict.Select(kvp => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(
                kvp.Key, ToJsonNode(kvp.Value)))),
        IList<object> list => new System.Text.Json.Nodes.JsonArray(
            list.Select(ToJsonNode).ToArray()),
        _ => System.Text.Json.Nodes.JsonValue.Create(value.ToString()!)
    };
}

internal sealed class JobTemplateResourcesYamlDto
{
    public Dictionary<string, string>? Requests { get; set; }
    public Dictionary<string, string>? Limits { get; set; }

    public JobTemplateResources ToResources() => new()
    {
        Requests = Requests,
        Limits = Limits
    };
}
