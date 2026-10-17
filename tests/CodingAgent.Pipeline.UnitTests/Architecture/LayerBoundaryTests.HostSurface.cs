using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Spec 052 Req 14.10 (Task 5.5–5.7): source scans of what every host exposes. The health-only hosts map their
/// probes and nothing else, no host turns on a framework extra (OpenAPI, Swagger UI, a Prometheus scrape endpoint,
/// the developer exception page, directory browsing, a Blazor Server circuit), and every host listens on 8080 only.
/// The web host and the API pin their full endpoint lists in their own inventory snapshots (T7, T1).
/// </summary>
public partial class LayerBoundaryTests
{
    // An endpoint-mapping call: the routing builders' Map* methods and any Map…Endpoints extension.
    [GeneratedRegex(@"\.(Map(?:Get|Post|Put|Delete|Patch|Methods|Group|Hub|Controllers|ControllerRoute|DefaultControllerRoute|Fallback\w*|StaticAssets|RazorPages|RazorComponents|OpenApi|HealthChecks|Forwarder|ReverseProxy|Prometheus\w*|BlazorHub|\w+Endpoints))\((?:""([^""]*)"")?")]
    private static partial Regex EndpointMappingCall();

    // A framework feature that must never be on: API description, Swagger UI, a metrics scrape endpoint, the
    // developer exception page, directory browsing, Blazor Server.
    [GeneratedRegex(@"\b(UseSwagger|UseSwaggerUI|AddSwaggerGen|MapOpenApi|AddOpenApi|UseDeveloperExceptionPage|UseDirectoryBrowser|AddDirectoryBrowser|MapPrometheusScrapingEndpoint|UseOpenTelemetryPrometheusScrapingEndpoint|AddPrometheusExporter|AddPrometheusHttpListener|MapBlazorHub|AddServerSideBlazor|AddInteractiveServerComponents|AddInteractiveServerRenderMode)\b")]
    private static partial Regex FrameworkExtra();

    [GeneratedRegex(@"\b(UseUrls|Listen|ListenAnyIP|ListenLocalhost|ConfigureKestrel)\s*\(")]
    private static partial Regex ListenerCall();

    /// <summary>Every endpoint-mapping call of the health-only hosts, as <c>file: Method("route")</c>.</summary>
    private static readonly Dictionary<string, string[]> HealthOnlyHostMappings = new(StringComparer.Ordinal)
    {
        ["CodingAgent.Scheduler"] =
        [
            "Program.cs: MapSchedulerHealthEndpoints",
            "SchedulerHealthEndpoints.cs: MapGet(\"/healthz\")",
            "SchedulerHealthEndpoints.cs: MapGet(\"/readyz\")",
            "SchedulerHealthEndpoints.cs: MapGet(\"/health\")",
        ],
        ["CodingAgent.JobController"] =
        [
            "Program.cs: MapJobControllerHealthEndpoints",
            "JobControllerHealthEndpoints.cs: MapGet(\"/healthz\")",
            "JobControllerHealthEndpoints.cs: MapGet(\"/readyz\")",
        ],
        ["CodingAgent.Agent"] =
        [
            "Program.cs: MapHealthEndpoints",
            "HealthEndpoints.cs: MapGet(\"/healthz\")",
            "HealthEndpoints.cs: MapGet(\"/readyz\")",
            "HealthEndpoints.cs: MapGet(\"/startupz\")",
        ],
    };

    [Theory]
    [InlineData("CodingAgent.Scheduler")]
    [InlineData("CodingAgent.JobController")]
    [InlineData("CodingAgent.Agent")]
    public void HealthOnlyHosts_MapTheirProbes_AndNothingElse(string host)
    {
        var calls = CodeLines(Path.Combine(RepoRoot, "src", host))
            .SelectMany(l => EndpointMappingCall().Matches(l.Text).Select(m =>
                $"{Path.GetFileName(l.File)}: {m.Groups[1].Value}" + (m.Groups[2].Success ? $"(\"{m.Groups[2].Value}\")" : "")))
            .ToList();

        calls.Should().BeEquivalentTo(HealthOnlyHostMappings[host],
            "a health-only host serves its probes and nothing else; a new endpoint needs a classification (Task 5.5)");
    }

    [Fact]
    public void NoHost_TurnsOnAFrameworkExtra()
    {
        var src = Path.Combine(RepoRoot, "src");
        var offenders = CodeLines(src)
            .Where(l => FrameworkExtra().IsMatch(l.Text))
            .Select(l => $"{Path.GetRelativePath(RepoRoot, l.File)}: {l.Text.Trim()}")
            .ToList();

        CodeLines(src).Should().NotBeEmpty("positive control: the scan sees the sources");
        offenders.Should().BeEmpty("no host serves OpenAPI, Swagger UI, a metrics scrape endpoint, the developer " +
            "exception page, directory browsing or a Blazor Server circuit (Task 5.6)");
    }

    [Fact]
    public void TheChart_NeverRunsAHostInTheDevelopmentEnvironment()
    {
        // ASP.NET Core turns on the developer exception page by itself in Development.
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot, "helm"), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".tpl", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(l => (File: f, Text: l)))
            .Where(l => l.Text.Contains("Development", StringComparison.Ordinal)
                && (l.Text.Contains("ENVIRONMENT", StringComparison.Ordinal) || l.Text.Contains("value:", StringComparison.Ordinal)))
            .Select(l => $"{Path.GetRelativePath(RepoRoot, l.File)}: {l.Text.Trim()}")
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void EveryHost_ListensOnPort8080_Only()
    {
        // The API, the Scheduler and the Job Controller set the URL in code, which wins over ASPNETCORE_URLS.
        foreach (var host in new[] { "CodingAgent.Api", "CodingAgent.Scheduler", "CodingAgent.JobController" })
        {
            File.ReadAllText(Path.Combine(RepoRoot, "src", host, "Program.cs"))
                .Should().Contain("UseUrls(\"http://+:8080\")", $"{host} listens on 8080");
        }

        // The web host takes ASPNETCORE_URLS from its image; the agent keeps the .NET image default
        // (ASPNETCORE_HTTP_PORTS=8080), so neither its code nor its image sets a listener.
        File.ReadAllText(Path.Combine(RepoRoot, "dockerfiles", "web.Dockerfile"))
            .Should().Contain("ENV ASPNETCORE_URLS=http://+:8080");
        File.ReadAllText(Path.Combine(RepoRoot, "dockerfiles", "agent.Dockerfile"))
            .Should().NotContainAny(["ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS"]);

        var otherListeners = CodeLines(Path.Combine(RepoRoot, "src"))
            .Where(l => ListenerCall().IsMatch(l.Text) && !l.Text.Contains("UseUrls(\"http://+:8080\")", StringComparison.Ordinal))
            .Select(l => $"{Path.GetRelativePath(RepoRoot, l.File)}: {l.Text.Trim()}")
            .ToList();
        otherListeners.Should().BeEmpty("no host opens a second listener (Task 5.7)");
    }

    /// <summary>The non-comment source lines of the C# files under <paramref name="directory"/>.</summary>
    private static IEnumerable<(string File, string Text)> CodeLines(string directory)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(l => (File: f, Text: l)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith('*'));
    }
}
