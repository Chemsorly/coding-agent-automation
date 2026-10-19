using System.Net;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.ApiClient;

/// <summary>
/// Coverage tests for <see cref="PipelineApiWorkItemClient.GetActiveDecompositionCountAsync"/>
/// targeting the null-guard path introduced in issue #3774.
///
/// These tests MUST live in <c>CodingAgent.Web.UnitTests</c> (not in the integration test
/// project) because Sonar only captures coverage from this project for the
/// <c>CodingAgent.Api.Client</c> assembly — see coverlet.runsettings and the comment in
/// <see cref="PipelineApiWorkItemClientSetPriorityTests"/> for context.
/// </summary>
public sealed class PipelineApiWorkItemClientDecompositionCountTests
{
    private static HttpResponseMessage NullJsonResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task GetActiveDecompositionCountAsync_WhenNullDeserialization_ThrowsInvalidOperationException()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>(MockBehavior.Default);
        // ForContext<T>() returns null on a Loose mock unless set up explicitly.
        // Without this, _logger is null and Warning() throws NullReferenceException
        // rather than the InvalidOperationException asserted below.
        mockLogger
            .Setup(l => l.ForContext<PipelineApiWorkItemClient>())
            .Returns(mockLogger.Object);

        var handler = new FixedResponseHandler(NullJsonResponse());
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var sut = new PipelineApiWorkItemClient(http, mockLogger.Object);

        // Act
        var act = () => sut.GetActiveDecompositionCountAsync();

        // Assert — null deserialization must throw, not return 0
        await act.Should().ThrowAsync<InvalidOperationException>(
            "a null deserialization result must throw so the Scheduler's catch block treats it " +
            "as a transient error rather than a valid zero count");
    }

    [Fact]
    public async Task GetActiveDecompositionCountAsync_WhenNullDeserialization_LogsWarningBeforeThrowing()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>(MockBehavior.Default);
        mockLogger
            .Setup(l => l.ForContext<PipelineApiWorkItemClient>())
            .Returns(mockLogger.Object);

        var handler = new FixedResponseHandler(NullJsonResponse());
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var sut = new PipelineApiWorkItemClient(http, mockLogger.Object);

        // Act — swallow the expected exception so we can verify the log call
        try { await sut.GetActiveDecompositionCountAsync(); } catch (InvalidOperationException) { }

        // Assert — Warning must be logged; the call uses two structured properties
        // ({Endpoint}, {Type}), so the invoked Serilog overload is Warning<T0,T1>(string, T0, T1).
        mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("null") || s.Contains("deserialized")),
                It.IsAny<string>(),   // {Endpoint}
                It.IsAny<string>()), // {Type}
            Times.Once,
            "a Warning must be logged before throwing so the failure is visible in telemetry");
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public FixedResponseHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
