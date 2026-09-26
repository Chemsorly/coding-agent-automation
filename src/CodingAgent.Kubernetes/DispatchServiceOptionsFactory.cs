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

        // Populate the master key value so DispatchLifecycleService can pre-compute per-job credentials
        // (HMAC-SHA256(masterKey, jobName)) stored in per-job K8s Secrets (Spec 043 Req 8a).
        // Never injected into agent pods — only the per-job derived value enters pod memory.
        options.AgentApiKeyValue = configuration.GetValue<string>("AGENT_API_KEY") ?? "";
        // TODO: When AgentApiKeyValue is empty, DispatchLifecycleService silently degrades to the
        // legacy master-key-mount path for work-item pods (the vulnerability this change was designed
        // to fix). In a K8s dispatch context, AgentApiKeyAuthHandler.ResolveApiKey generates a *random*
        // master key when AGENT_API_KEY is unset, making the dispatch signer and the auth handler
        // operate with different keys. Consider emitting a startup warning (or even throwing) here when
        // running in K8s dispatch mode (e.g., Namespace is non-empty) and AgentApiKeyValue is absent,
        // so operators are alerted immediately rather than discovering the degraded state via pod audits.

        return options;
    }
}