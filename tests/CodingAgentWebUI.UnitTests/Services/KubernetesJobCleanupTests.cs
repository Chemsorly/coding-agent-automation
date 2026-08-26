using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Autorest;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="KubernetesJobCleanup"/>.
/// Validates API lookup + K8s delete + 404 handling + generic exception handling.
/// </summary>
public sealed class KubernetesJobCleanupTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockApiClient;
    private readonly Mock<IKubernetesJobClient> _mockJobClient;
    private readonly Mock<ILogger> _mockLogger;
    private readonly KubernetesJobCleanup _sut;

    private const string K8sNamespace = "coding-agent";

    public KubernetesJobCleanupTests()
    {
        _mockApiClient = new Mock<IPipelineApiWorkItemClient>();
        _mockJobClient = new Mock<IKubernetesJobClient>();
        _mockLogger = new Mock<ILogger>();

        _sut = new KubernetesJobCleanup(
            _mockApiClient.Object,
            _mockJobClient.Object,
            K8sNamespace,
            _mockLogger.Object);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_ValidRunIdWithK8sJobName_DeletesJob()
    {
        var runId = Guid.NewGuid();
        const string jobName = "caa-test-job";

        _mockApiClient
            .Setup(c => c.GetK8sJobNameAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobName);
        _mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        _mockJobClient.Verify(c => c.DeleteJobAsync(jobName, K8sNamespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_NoK8sJobName_DoesNotCallDelete()
    {
        var runId = Guid.NewGuid();

        _mockApiClient
            .Setup(c => c.GetK8sJobNameAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        _mockJobClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_InvalidRunId_NoOp()
    {
        await _sut.TryDeleteJobForRunAsync("not-a-guid", CancellationToken.None);

        _mockApiClient.Verify(c => c.GetK8sJobNameAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockJobClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_ApiReturnsNull_NoOp()
    {
        var runId = Guid.NewGuid();

        _mockApiClient
            .Setup(c => c.GetK8sJobNameAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        _mockJobClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_DeleteReturns404_GracefullyHandled()
    {
        var runId = Guid.NewGuid();
        const string jobName = "caa-already-gone";

        _mockApiClient
            .Setup(c => c.GetK8sJobNameAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobName);

        var response404 = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        _mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException { Response = new HttpResponseMessageWrapper(response404, "") });

        // Should not throw
        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_DeleteThrowsOtherException_GracefullyHandled()
    {
        var runId = Guid.NewGuid();
        const string jobName = "caa-error-job";

        _mockApiClient
            .Setup(c => c.GetK8sJobNameAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobName);
        _mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("K8s API timeout"));

        // Should not throw
        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        _mockJobClient.Verify(c => c.DeleteJobAsync(jobName, K8sNamespace, It.IsAny<CancellationToken>()), Times.Once);
    }
}
