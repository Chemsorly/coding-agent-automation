using Microsoft.Extensions.Configuration;

namespace CodingAgentWebUI.Kubernetes;

/// <summary>
/// Static factory for building <see cref="DispatchServiceOptions"/> from <see cref="IConfiguration"/>.
/// Eliminates the duplicated InitializeOptions logic across DispatchService,
/// ConsolidationWorkItemDispatchService, and WorkDistributionRegistration.Kubernetes.cs.
/// Made public (was internal) because three assemblies consume it: JobController, Api, and Orchestration.
/// </summary>
public static class DispatchServiceOptionsFactory
{
    /// <summary>
    /// Creates a fully-populated <see cref="DispatchServiceOptions"/> from the given configuration.
    /// Reads 7 config keys with appropriate fallback logic.
    /// </summary>
    public static DispatchServiceOptions Create(IConfiguration configuration)
    {
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

        // Master API key for HMAC key derivation (Spec 043 Req 8a.1).
        // Comes from the AGENT_API_KEY env var (same secret as the orchestrator uses).
        // IConfiguration exposes env vars directly.
        options.AgentMasterApiKey = configuration.GetValue<string>("AGENT_API_KEY") ?? "";

        return options;
    }
}
