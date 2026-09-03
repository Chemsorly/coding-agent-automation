using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;

namespace CodingAgentWebUI.UnitTests.ApiClient;

/// <summary>
/// Tests for <see cref="PipelineApiWorkItemClient.SetPriorityAsync"/> using a
/// fake <see cref="HttpMessageHandler"/>. These tests live in CodingAgentWebUI.UnitTests
/// (not Infrastructure.UnitTests) so that coverlet captures coverage for the
/// CodingAgentWebUI.Api.Client assembly, which is included in this project's
/// coverlet.runsettings but excluded from Infrastructure.UnitTests.
/// </summary>
public sealed class PipelineApiWorkItemClientSetPriorityTests
{
    private static PipelineApiWorkItemClient CreateSut(HttpStatusCode statusCode)
    {
        var handler = new FixedStatusHandler(statusCode);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new PipelineApiWorkItemClient(http);
    }

    [Fact]
    public async Task SetPriorityAsync_Success_DoesNotThrow()
    {
        var sut = CreateSut(HttpStatusCode.OK);
        var workItemId = Guid.NewGuid();

        var act = () => sut.SetPriorityAsync(workItemId, 500);

        // TODO: This test does not capture or inspect the outgoing HttpRequestMessage.
        // The body serialisation path (new { priorityWeight } via PipelineJsonOptions.Default) is
        // never verified. If camelCase serialisation is disabled in PipelineJsonOptions.Default, the
        // backend would receive {"PriorityWeight": 500} instead of {"priorityWeight": 500} and the
        // API would return 400, but this test would still pass against the fake handler.
        // Add a capturing handler that records the request and assert the JSON body contains
        // {"priorityWeight": 500} to lock in payload serialization.
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetPriorityAsync_BadRequest_ThrowsHttpRequestException()
    {
        var sut = CreateSut(HttpStatusCode.BadRequest);
        var workItemId = Guid.NewGuid();

        var act = () => sut.SetPriorityAsync(workItemId, -1);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SetPriorityAsync_Conflict_ThrowsHttpRequestException()
    {
        var sut = CreateSut(HttpStatusCode.Conflict);
        var workItemId = Guid.NewGuid();

        var act = () => sut.SetPriorityAsync(workItemId, 100);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> that always returns a fixed status code.
    /// </summary>
    private sealed class FixedStatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public FixedStatusHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_statusCode));
    }
}
