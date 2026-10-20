using AwesomeAssertions;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Orchestration.UnitTests;

/// <summary>
/// Verifies that <see cref="TokenVendingServiceCollectionExtensions.AddTokenVendingCore"/>
/// registers exactly the four services it is documented to register, and does not
/// register <see cref="ILabelSwapService"/> (which is an API-only service).
/// </summary>
public sealed class TokenVendingCoreRegistrationTests
{
    /// <summary>
    /// Registers the minimum dependencies required by <see cref="TokenVendingServiceCollectionExtensions.AddTokenVendingCore"/>:
    /// an HTTP client factory, a mock <see cref="IProviderConfigStore"/>, and a mock
    /// <see cref="IProviderFactory"/>.
    /// </summary>
    private static ServiceCollection BuildMinimalPrerequisites()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new Mock<IProviderConfigStore>().Object);
        services.AddSingleton(new Mock<IProviderFactory>().Object);
        return services;
    }

    [Fact]
    public async Task AddTokenVendingCore_RegistersTokenVendingService_Concrete()
    {
        var services = BuildMinimalPrerequisites();
        services.AddTokenVendingCore();

        await using var provider = services.BuildServiceProvider();

        provider.GetService<TokenVendingService>()
            .Should().NotBeNull("TokenVendingService concrete singleton must be registered by AddTokenVendingCore");
    }

    [Fact]
    public async Task AddTokenVendingCore_RegistersITokenVendingService_Alias()
    {
        var services = BuildMinimalPrerequisites();
        services.AddTokenVendingCore();

        await using var provider = services.BuildServiceProvider();

        provider.GetService<ITokenVendingService>()
            .Should().NotBeNull("ITokenVendingService alias must be registered by AddTokenVendingCore");
    }

    [Fact]
    public async Task AddTokenVendingCore_ITokenVendingService_ResolvesToSameInstanceAsTokenVendingService()
    {
        var services = BuildMinimalPrerequisites();
        services.AddTokenVendingCore();

        await using var provider = services.BuildServiceProvider();

        var concrete = provider.GetService<TokenVendingService>();
        var alias = provider.GetService<ITokenVendingService>();

        alias.Should().BeSameAs(concrete,
            "ITokenVendingService must be an alias for the same TokenVendingService singleton");
    }

    [Fact]
    public async Task AddTokenVendingCore_RegistersTokenCacheHousekeepingService_AsHostedService()
    {
        var services = BuildMinimalPrerequisites();
        services.AddTokenVendingCore();

        await using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>();
        hostedServices.Should().ContainSingle(
            svc => svc is TokenCacheHousekeepingService,
            "TokenCacheHousekeepingService must be registered as an IHostedService by AddTokenVendingCore");
    }

    [Fact]
    public async Task AddTokenVendingCore_RegistersILabelService()
    {
        var services = BuildMinimalPrerequisites();
        services.AddTokenVendingCore();

        await using var provider = services.BuildServiceProvider();

        provider.GetService<ILabelService>()
            .Should().NotBeNull("ILabelService must be registered by AddTokenVendingCore");
    }

    [Fact]
    public async Task AddTokenVendingCore_DoesNotRegisterILabelSwapService()
    {
        // Regression guard: ILabelSwapService is an API-only service (asymmetry invariant).
        // AddTokenVendingCore must not register it — only the API's AddLabelServices does.
        var services = BuildMinimalPrerequisites();
        services.AddTokenVendingCore();

        await using var provider = services.BuildServiceProvider();

        provider.GetService<ILabelSwapService>()
            .Should().BeNull("ILabelSwapService is API-only and must never be registered by AddTokenVendingCore");
    }

    [Fact]
    public void AddTokenVendingCore_ThrowsArgumentNullException_WhenServicesIsNull()
    {
        IServiceCollection? nullServices = null;

        var act = () => nullServices!.AddTokenVendingCore();

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("services");
    }
}
