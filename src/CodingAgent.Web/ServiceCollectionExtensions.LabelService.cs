using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using Serilog;

namespace CodingAgent.Web;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the label service. Token vending went with the consolidation job preparation in
    /// spec 051 2.4a: only the API prepares agent jobs, so nothing in this host vends tokens.
    /// </summary>
    private static void RegisterLabelService(IServiceCollection services)
    {
        services.AddSingleton<ILabelService>(sp => new LabelService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));
    }
}
