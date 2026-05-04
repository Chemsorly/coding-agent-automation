using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
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
    [InlineData(typeof(IQualityGateValidator))]
    [InlineData(typeof(PipelineOrchestrationService))]
    [InlineData(typeof(PipelineLoopService))]
    [InlineData(typeof(IBrainUpdateService))]
    [InlineData(typeof(IPipelineRunHistoryService))]
    [InlineData(typeof(IAgentPhaseExecutor))]
    [InlineData(typeof(IQualityGateExecutor))]
    [InlineData(typeof(IssueDescriptionParser))]
    [InlineData(typeof(GitHubValidationService))]
    public void Key_Service_Resolves_Without_Error(Type serviceType)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService(serviceType);

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
    public void Transient_Service_Returns_Different_Instances()
    {
        using var scope = _factory.Services.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<IssueDescriptionParser>();
        var second = scope.ServiceProvider.GetRequiredService<IssueDescriptionParser>();

        Assert.NotSame(first, second);
    }
}
