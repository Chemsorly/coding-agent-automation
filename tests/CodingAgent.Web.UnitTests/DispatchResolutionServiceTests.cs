using AwesomeAssertions;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Web.UnitTests;

/// <summary>
/// Unit tests for <see cref="DispatchResolutionService"/>.
/// </summary>
public class DispatchResolutionServiceTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly DispatchResolutionService _service;

    public DispatchResolutionServiceTests()
    {
        _service = new DispatchResolutionService(
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object);
    }

    [Fact]
    public async Task ResolveQualityGatesAsync_ReturnsMatchingConfigs()
    {
        var qgc = new QualityGateConfiguration
        {
            Id = "qgc1",
            DisplayName = "Build",
            Enabled = true,
            MatchLabels = ["dotnet"],
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            TestCommand = "dotnet",
            TestArguments = ["test"]
        };
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { qgc });

        var result = await _service.ResolveQualityGatesAsync(["dotnet"], CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("qgc1");
    }

    [Fact]
    public async Task ResolveQualityGatesAsync_NoMatch_ReturnsEmpty()
    {
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());

        var result = await _service.ResolveQualityGatesAsync(["dotnet"], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveReviewersAsync_ReturnsMatchingConfigs()
    {
        var rc = new ReviewerConfiguration
        {
            Id = "rc1",
            DisplayName = "Security",
            Enabled = true,
            MatchLabels = ["dotnet"],
            Agents = []
        };
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { rc });

        var result = await _service.ResolveReviewersAsync(["dotnet"], CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("rc1");
    }

    [Fact]
    public async Task ResolveReviewersAsync_NoMatch_ReturnsEmpty()
    {
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        var result = await _service.ResolveReviewersAsync(["dotnet"], CancellationToken.None);

        result.Should().BeEmpty();
    }
}
