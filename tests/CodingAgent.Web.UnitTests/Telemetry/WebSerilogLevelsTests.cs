using AwesomeAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Web.UnitTests.Telemetry;

/// <summary>
/// The web host's log levels (<see cref="SerilogRegistration.ApplyWebHostLevels"/>): YARP's per-call lines stay
/// out even at Debug, its forwarding errors pass.
/// </summary>
public sealed class WebSerilogLevelsTests
{
    private const string Forwarder = "Yarp.ReverseProxy.Forwarder.HttpForwarder";

    [Fact]
    public void Yarp_LogsOnlyWarningsAndAbove()
    {
        var sink = new CollectingSink();
        using (var logger = new LoggerConfiguration().ApplyWebHostLevels(LogEventLevel.Debug).WriteTo.Sink(sink).CreateLogger())
        {
            var yarp = logger.ForContext(Constants.SourceContextPropertyName, Forwarder);
            yarp.Information("Proxying to {TargetUrl}", "http://api/api/ui/agents");
            yarp.Debug("RequestCanceled: The request was canceled before receiving a response.");
            yarp.Warning("Request: An error was encountered before receiving a response.");
        }

        sink.Events.Should().ContainSingle()
            .Which.Level.Should().Be(LogEventLevel.Warning, "a 502 to the API must still be logged");
    }

    [Fact]
    public void OtherCategories_KeepTheConfiguredLevel()
    {
        var sink = new CollectingSink();
        using (var logger = new LoggerConfiguration().ApplyWebHostLevels(LogEventLevel.Debug).WriteTo.Sink(sink).CreateLogger())
            logger.ForContext(Constants.SourceContextPropertyName, "CodingAgent.Web.Auth.LoginEndpoints").Debug("Signed in");

        sink.Events.Should().ContainSingle();
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}
