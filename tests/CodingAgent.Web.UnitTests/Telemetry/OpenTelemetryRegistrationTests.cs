using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;

namespace CodingAgent.Web.UnitTests.Telemetry;

/// <summary>
/// Verifies that <see cref="OpenTelemetryRegistration.AddApplicationTelemetry"/> wires
/// <see cref="OtelNoiseFilter"/> into the web host's trace instrumentation (issue #2972).
/// A rework rebase once dropped this wiring from the web host without any test failing (#3015),
/// and probe spans flooded the traces again. The filter behaviour itself is covered by
/// <c>OtelNoiseFilterTests</c>; these tests cover only the registration.
/// </summary>
public sealed class OpenTelemetryRegistrationTests
{
    [Fact]
    public void AddApplicationTelemetry_FiltersAspNetCoreRequestsThroughOtelNoiseFilter()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        options.Filter.Should().Be(new Func<HttpContext, bool>(OtelNoiseFilter.FilterAspNetCoreRequest),
            "health probes and UI polling must not produce server spans");
    }

    [Fact]
    public void AddApplicationTelemetry_FiltersAndEnrichesHttpClientRequestsThroughOtelNoiseFilter()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        options.FilterHttpRequestMessage.Should().Be(
            new Func<HttpRequestMessage, bool>(OtelNoiseFilter.FilterHttpClientRequest),
            "Kubernetes API server calls must not produce client spans");
        options.EnrichWithHttpRequestMessage.Should().Be(
            new Action<Activity, HttpRequestMessage>(OtelNoiseFilter.EnrichHttpClientRequest),
            "outbound spans must be named by method and host");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddApplicationTelemetry(redisConnectionString: null);
        return services.BuildServiceProvider();
    }
}
