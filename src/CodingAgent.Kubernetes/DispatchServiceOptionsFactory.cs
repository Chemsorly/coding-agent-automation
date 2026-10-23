using Microsoft.Extensions.Configuration;

namespace CodingAgent.Kubernetes;

/// <summary>
/// Static factory for building <see cref="DispatchServiceOptions"/> from <see cref="IConfiguration"/>.
/// Eliminates the duplicated InitializeOptions logic across DispatchService
/// and WorkDistributionRegistration.Kubernetes.cs.
/// Made public (was internal) because multiple assemblies consume it: JobController, Api, and Orchestration.
/// </summary>
public static class DispatchServiceOptionsFactory
{
    /// <summary>
    /// Creates a fully-populated <see cref="DispatchServiceOptions"/> from the given configuration,
    /// with all numeric values clamped to their safe minimums via
    /// <see cref="DispatchServiceOptions.ValidateAndClamp"/>.
    /// </summary>
    /// <param name="configuration">The configuration root to bind from.</param>
    /// <param name="logger">
    /// Optional Serilog logger. When provided, <see cref="DispatchServiceOptions.ValidateAndClamp"/>
    /// emits a warning for each value that was raised to its minimum. Pass <c>null</c> to clamp
    /// silently (e.g. in pre-container startup checks where the logger is not yet available).
    /// </param>
    public static DispatchServiceOptions Create(IConfiguration configuration, Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new DispatchServiceOptions();
        configuration.GetSection("WorkDistribution:Dispatch").Bind(options);

        var pvcList = configuration.GetSection("WorkDistribution:CredentialPools:Kiro").Get<List<string>>();
        if (pvcList is not null)
            options.KiroPvcPool = pvcList;

        options.OrchestratorUrl = configuration.GetValue<string>("WorkDistribution:OrchestratorUrl") ?? "";
        options.AgentApiKeySecretName = configuration.GetValue<string>("WorkDistribution:AgentApiKeySecretName") ?? "";
        options.AgentServiceAccountName = configuration.GetValue<string>("WorkDistribution:AgentServiceAccountName") ?? "";
        options.Namespace = configuration.GetValue<string>("WorkDistribution:Namespace")
            ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
            ?? "default";
        options.OpencodeConfigSecretName = configuration.GetValue<string>("WorkDistribution:OpencodeConfigSecretName") ?? "";
        options.ClaudeAuthSecretName = configuration.GetValue<string>("WorkDistribution:ClaudeAuthSecretName") ?? "";

        // Populate the master key value so DispatchLifecycleService can pre-compute per-job credentials
        // (HMAC-SHA256(masterKey, jobName)) stored in per-job K8s Secrets (Spec 043 Req 8a).
        // Never injected into agent pods — only the per-job derived value enters pod memory.
        options.AgentApiKeyValue = configuration.GetValue<string>("AGENT_API_KEY") ?? "";
        // Empty means no agent key can be issued: every Job dispatcher then fails the dispatch (and
        // logs an error) instead of starting a pod that could not authenticate.

        // Clamp all numeric options to their safe minimums. Every consumer — including the
        // JobController's ReconciliationLoop — receives values that will not cause immediate
        // false timeouts or negative durations. The logger parameter allows the warning to be
        // surfaced at startup when one is available, and silenced in pre-container checks.
        options.ValidateAndClamp(logger);

        return options;
    }
}