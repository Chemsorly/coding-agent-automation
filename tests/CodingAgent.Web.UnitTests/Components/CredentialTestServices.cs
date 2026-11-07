using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>Spec 056: pages with the paused-credential banner or card need the credentials client.</summary>
internal static class CredentialTestServices
{
    /// <summary>Registers a credentials client that reports no credential in use.</summary>
    public static IServiceCollection AddNoCredentials(this IServiceCollection services)
    {
        var client = new Mock<IUiCredentialsClient>();
        client.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiAgentCredentialsPage([], new Dictionary<string, int>()));
        return services.AddSingleton(client.Object);
    }
}
