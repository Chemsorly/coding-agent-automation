using Microsoft.Extensions.Configuration;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Static factory for building <see cref="DispatchServiceOptions"/> from <see cref="IConfiguration"/>.
/// Eliminates the duplicated InitializeOptions logic across DispatchService,
/// ConsolidationWorkItemDispatchService, and WorkDistributionRegistration.Kubernetes.cs.
/// </summary>
internal static class DispatchServiceOptionsFactory
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

        return options;
    }
}
