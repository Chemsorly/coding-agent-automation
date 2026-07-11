using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="DispatchResolutionService"/>.
/// </summary>
public class DispatchResolutionServiceTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly DispatchResolutionService _service;

    public DispatchResolutionServiceTests()
    {
        // TODO: All 6 sub-interface parameters receive the same mock instance. Use distinct mocks per sub-interface to detect cross-wiring bugs.
        _service = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task ResolveProfileAsync_ReturnsMatchingProfile()
    {
        var profile = new AgentProfile
        {
            Id = "p1",
            DisplayName = "Test",
            AgentProviderConfigId = "ap1",
            Enabled = true,
            MatchLabels = ["dotnet"]
        };
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { profile });

        var registry = new AgentRegistryService(_mockLogger.Object);
        var agent = registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = ["dotnet"]
        }, "conn-1");

        var result = await _service.ResolveProfileAsync(agent, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be("p1");
    }

    [Fact]
    public async Task ResolveProfileAsync_NoMatch_ReturnsNull()
    {
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        var registry = new AgentRegistryService(_mockLogger.Object);
        var agent = registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = ["dotnet"]
        }, "conn-1");

        var result = await _service.ResolveProfileAsync(agent, CancellationToken.None);

        result.Should().BeNull();
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
