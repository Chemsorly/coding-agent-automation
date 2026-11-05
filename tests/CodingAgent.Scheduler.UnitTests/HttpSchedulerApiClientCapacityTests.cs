using System.Net;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>Spec 057 req 7.2: <see cref="HttpSchedulerApiClient.GetAgentCapacityAsync"/> route, payload and null body.</summary>
public sealed class HttpSchedulerApiClientCapacityTests
{
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public Uri? RequestedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task GetAgentCapacityAsync_ReadsTheCapacityRoute()
    {
        const string json = """
            {"all":{"templateKey":null,"runningRuns":2,"runningChats":1,"effectiveLimit":6,"effectiveSource":"helm"},
             "templates":[{"templateKey":"dotnet,kiro","runningRuns":2,"runningChats":1,"effectiveLimit":3,"effectiveSource":"ui"}],
             "unknownTemplateRuns":0,"unknownTemplateChats":0,"countsAvailable":true}
            """;
        var handler = new StubHandler(json);
        var client = new HttpSchedulerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://api") });

        var status = await client.GetAgentCapacityAsync();

        handler.RequestedUri!.AbsolutePath.Should().Be("/api/agents/capacity");
        status!.All.EffectiveLimit.Should().Be(6);
        status.Templates.Single().TemplateKey.Should().Be("dotnet,kiro");
        status.Templates.Single().EffectiveSource.Should().Be("ui");
    }

    [Fact]
    public async Task GetAgentCapacityAsync_NullBody_ReturnsNull()
    {
        var client = new HttpSchedulerApiClient(new HttpClient(new StubHandler("null")) { BaseAddress = new Uri("http://api") });

        (await client.GetAgentCapacityAsync()).Should().BeNull();
    }
}
