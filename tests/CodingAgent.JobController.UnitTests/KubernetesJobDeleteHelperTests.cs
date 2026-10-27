using AwesomeAssertions;
using k8s.Autorest;
using ILogger = Serilog.ILogger;

namespace CodingAgent.JobController.UnitTests;

/// <summary>
/// Unit tests for <see cref="KubernetesJobDeleteHelper"/>.
/// Covers the three outcomes of <see cref="KubernetesJobDeleteHelper.TryDeleteJobAsync"/>
/// and the typed 404 detection of <see cref="KubernetesJobDeleteHelper.IsNotFound"/>.
/// </summary>
public sealed class KubernetesJobDeleteHelperTests
{
    private const string TestJobName = "caa-test-job";
    private const string TestNamespace = "test-ns";
    private const string TestContext = "Test: job cleanup";

    private static HttpOperationException NotFound404() => new("Job not found")
    {
        Response = new HttpResponseMessageWrapper(
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), "")
    };

    private static HttpOperationException StatusCode(System.Net.HttpStatusCode code) => new(code.ToString())
    {
        Response = new HttpResponseMessageWrapper(
            new System.Net.Http.HttpResponseMessage(code), "")
    };

    // ─── TryDeleteJobAsync: 404 → swallowed, logged at Debug with exception attached ───

    [Fact]
    public async Task TryDeleteJobAsync_WhenJobNotFound_SwallowsAndLogsDebugWithException()
    {
        var notFoundEx = NotFound404();
        var clientMock = new Mock<IKubernetesJobClient>();
        clientMock
            .Setup(c => c.DeleteJobAsync(TestJobName, TestNamespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(notFoundEx);

        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(loggerMock.Object);

        // Must not throw; must return false (swallowed)
        var result = await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            clientMock.Object, TestJobName, TestNamespace, loggerMock.Object, TestContext, CancellationToken.None);
        result.Should().BeFalse("404 is a swallowed failure, not a success");

        // The caught HttpOperationException must be attached to the Debug log entry (S6667)
        // Serilog resolves Debug(ex, "template", string, string) to Debug<string,string>(Exception?, ...)
        loggerMock.Verify(l => l.Debug(
            It.Is<HttpOperationException>(e => ReferenceEquals(e, notFoundEx)),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);

        // No Warning must be emitted — check both params and generic 2-string overloads
        loggerMock.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object[]>()), Times.Never);
        loggerMock.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    // ─── TryDeleteJobAsync: success → returns true, no log ───────────────────

    [Fact]
    public async Task TryDeleteJobAsync_WhenDeleteSucceeds_ReturnsTrueAndLogsNothing()
    {
        var clientMock = new Mock<IKubernetesJobClient>();
        clientMock
            .Setup(c => c.DeleteJobAsync(TestJobName, TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loggerMock = new Mock<ILogger>();

        var result = await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            clientMock.Object, TestJobName, TestNamespace, loggerMock.Object, TestContext, CancellationToken.None);

        result.Should().BeTrue();
        // No log emitted on success — call sites own their success logs
        loggerMock.Verify(l => l.Debug(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        loggerMock.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ─── TryDeleteJobAsync: other exception → swallowed, logged at Warning, returns false ───

    [Fact]
    public async Task TryDeleteJobAsync_WhenOtherException_SwallowsAndLogsWarning()
    {
        var clientMock = new Mock<IKubernetesJobClient>();
        clientMock
            .Setup(c => c.DeleteJobAsync(TestJobName, TestNamespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network failure"));

        var loggerMock = new Mock<ILogger>();

        // Must not throw; must return false (swallowed)
        var result = await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            clientMock.Object, TestJobName, TestNamespace, loggerMock.Object, TestContext, CancellationToken.None);
        result.Should().BeFalse("non-404 error is swallowed, not a success");

        // A Warning must be emitted
        // Serilog resolves Warning(ex, "template", string, string) to Warning<string,string>
        loggerMock.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
    }

    // ─── TryDeleteJobAsync: OCE with cancelled token → rethrows ─────────────

    [Fact]
    public async Task TryDeleteJobAsync_WhenCancelledTokenAndOce_Rethrows()
    {
        using var cts = new CancellationTokenSource();

        var clientMock = new Mock<IKubernetesJobClient>();
        clientMock
            .Setup(c => c.DeleteJobAsync(TestJobName, TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken _) =>
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            });

        var loggerMock = new Mock<ILogger>();

        var act = async () => await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            clientMock.Object, TestJobName, TestNamespace, loggerMock.Object, TestContext, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // No Warning must be emitted for a cancelled-token OCE — check both overloads
        loggerMock.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object[]>()), Times.Never);
        loggerMock.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    // ─── TryDeleteJobAsync: OCE when token is NOT cancelled → swallowed ──────

    [Fact]
    public async Task TryDeleteJobAsync_WhenOceButTokenNotCancelled_SwallowsAndLogsWarning()
    {
        var clientMock = new Mock<IKubernetesJobClient>();
        // K8s client can throw OCE internally (e.g. on a client-side timeout) without the
        // caller's token being cancelled. This must be swallowed like any other non-404 error.
        clientMock
            .Setup(c => c.DeleteJobAsync(TestJobName, TestNamespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("k8s client internal timeout"));

        var loggerMock = new Mock<ILogger>();

        // Must not throw — token is not cancelled
        await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            clientMock.Object, TestJobName, TestNamespace, loggerMock.Object, TestContext, CancellationToken.None);

        // Warning must be emitted for the unexpected OCE
        // Serilog resolves Warning(ex, "template", string, string) to Warning<string,string>
        loggerMock.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
    }

    // ─── IsNotFound ──────────────────────────────────────────────────────────

    [Fact]
    public void IsNotFound_WithHttpOperationException404_ReturnsTrue()
        => KubernetesJobDeleteHelper.IsNotFound(NotFound404()).Should().BeTrue();

    [Fact]
    public void IsNotFound_WithHttpOperationException500_ReturnsFalse()
        => KubernetesJobDeleteHelper.IsNotFound(StatusCode(System.Net.HttpStatusCode.InternalServerError))
            .Should().BeFalse();

    [Fact]
    public void IsNotFound_WithGenericExceptionContaining404InMessage_ReturnsFalse()
        => KubernetesJobDeleteHelper.IsNotFound(new Exception("404 Not Found"))
            .Should().BeFalse(
                "message-string matches must not trigger IsNotFound — only a typed 404 HttpOperationException qualifies");

    [Fact]
    public void IsNotFound_WithPlainException_ReturnsFalse()
        => KubernetesJobDeleteHelper.IsNotFound(new InvalidOperationException("Connection refused"))
            .Should().BeFalse();

    // ─── TryDeleteJobAsync: null guard ───────────────────────────────────────

    [Fact]
    public async Task TryDeleteJobAsync_NullClient_ThrowsArgumentNullException()
    {
        var act = async () => await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            null!, TestJobName, TestNamespace, new Mock<ILogger>().Object, TestContext, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("client");
    }

    [Fact]
    public async Task TryDeleteJobAsync_NullLogger_ThrowsArgumentNullException()
    {
        var clientMock = new Mock<IKubernetesJobClient>();
        var act = async () => await KubernetesJobDeleteHelper.TryDeleteJobAsync(
            clientMock.Object, TestJobName, TestNamespace, null!, TestContext, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("logger");
    }
}
