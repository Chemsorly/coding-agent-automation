using KiroCliLib.Configuration;
using KiroCliLib.Core;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

public class DiContainerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DiContainerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(typeof(IConfigurationStore))]
    [InlineData(typeof(IProviderFactory))]
    [InlineData(typeof(IKiroCliOrchestrator))]
    [InlineData(typeof(IQualityGateValidator))]
    [InlineData(typeof(Configuration))]
    [InlineData(typeof(PipelineOrchestrationService))]
    [InlineData(typeof(PipelineLoopService))]
    [InlineData(typeof(IBrainUpdateService))]
    [InlineData(typeof(IPipelineRunHistoryService))]
    [InlineData(typeof(CiLogWriter))]
    [InlineData(typeof(IssueDescriptionParser))]
    [InlineData(typeof(GitHubValidationService))]
    public void Key_Service_Resolves_Without_Error(Type serviceType)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(service);
    }

    [Fact]
    public void Scoped_Service_Resolves_Without_Error()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService<KiroExecutionService>();

        Assert.NotNull(service);
    }

    [Theory]
    [InlineData(typeof(PipelineOrchestrationService))]
    [InlineData(typeof(PipelineLoopService))]
    [InlineData(typeof(IConfigurationStore))]
    [InlineData(typeof(IProviderFactory))]
    public void Singleton_Services_Return_Same_Instance(Type serviceType)
    {
        var first = _factory.Services.GetRequiredService(serviceType);
        var second = _factory.Services.GetRequiredService(serviceType);

        Assert.Same(first, second);
    }

    [Fact]
    public void Scoped_Service_Returns_Different_Instances_Across_Scopes()
    {
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();

        var first = scope1.ServiceProvider.GetRequiredService<KiroExecutionService>();
        var second = scope2.ServiceProvider.GetRequiredService<KiroExecutionService>();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Transient_Service_Returns_Different_Instances()
    {
        using var scope = _factory.Services.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<IssueDescriptionParser>();
        var second = scope.ServiceProvider.GetRequiredService<IssueDescriptionParser>();

        Assert.NotSame(first, second);
    }
}
