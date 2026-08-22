using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Static helpers for loading <see cref="JobTemplateStore"/> from configuration.
/// Previously a static method on <c>DispatchService</c>; extracted here (arch-audit 2026-08-22).
/// </summary>
public static class JobTemplateProviderLoader
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(JobTemplateProviderLoader));

    /// <summary>Default path for job templates ConfigMap mount.</summary>
    internal const string DefaultJobTemplatesPath = "/app/config/job-templates.yaml";

    /// <summary>
    /// Loads <see cref="JobTemplateStore"/> from the path specified in
    /// <c>WorkDistribution:JobTemplatesPath</c>, with a YAML→JSON fallback.
    /// </summary>
    public static JobTemplateStore LoadTemplateProvider(IConfiguration configuration)
    {
        var templatesPath = configuration.GetValue<string>("WorkDistribution:JobTemplatesPath") ?? DefaultJobTemplatesPath;
        // Also check .json path for format flexibility
        if (!File.Exists(templatesPath) && templatesPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            var jsonFallback = Path.ChangeExtension(templatesPath, ".json");
            if (File.Exists(jsonFallback))
                templatesPath = jsonFallback;
        }
        var provider = JobTemplateStore.LoadFromFile(templatesPath);
        Log.Information("Loaded {Count} job template(s) from {Path}",
            provider.GetAllTemplates().Count, templatesPath);
        return provider;
    }
}
