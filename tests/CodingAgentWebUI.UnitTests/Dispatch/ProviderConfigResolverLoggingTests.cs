using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Verifies that ProviderConfigResolver logs at Error level before throwing
/// when a required config is not found.
/// </summary>
public class ProviderConfigResolverLoggingTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly Mock<IConfigurationStore> _mockStore;

    public ProviderConfigResolverLoggingTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);

        _mockStore = new Mock<IConfigurationStore>();
        // Return null from DB fallback to trigger the throw
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);
    }

    [Fact]
    public async Task ResolveAsync_RequiredConfigNotFound_LogsErrorBeforeThrowing()
    {
        var cachedList = new List<ProviderConfig>(); // empty = cache miss

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderConfigResolver.ResolveAsync(
                _mockStore.Object,
                "missing-config-id",
                ProviderKind.Repository,
                cachedList,
                required: true,
                _mockLogger.Object,
                CancellationToken.None));

        ex.Message.Should().Contain("missing-config-id");

        _mockLogger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<ProviderKind>()), Times.AtLeastOnce);
    }
}
