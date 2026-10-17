using System.Net;
using CodingAgent.Web.IntegrationTests.Auth;
using Microsoft.AspNetCore.Hosting;

namespace CodingAgent.Web.IntegrationTests.Bff;

/// <summary>A browser-like client of the BFF: its own cookie jar, which a WebSocket can share.</summary>
public sealed record BffClient(HttpClient Http, CookieContainer Cookies) : IDisposable
{
    public void Dispose() => Http.Dispose();
}

/// <summary>
/// Spec 052 Req 9.1: the web host on a real Kestrel port (YARP proxies WebSockets only through a
/// real server), forwarding to <see cref="StubPipelineApi"/>, with the role bindings and test sign-in
/// of <see cref="RbacWebApplicationFactory"/> ("reader" is global read-only, group "team-a" operates
/// one project, anyone else has no access). The forwarder's activity timeout is cut to 1 s so the
/// timeout test needs no real wait (m6).
/// </summary>
public sealed class BffWebApplicationFactory : RbacWebApplicationFactory
{
    public BffWebApplicationFactory() => UseKestrel(0);

    public StubPipelineApi Api { get; } = new();

    /// <summary>The web host's base address, e.g. <c>http://127.0.0.1:12345</c>; starts the server.</summary>
    public Uri ServerAddress
    {
        get
        {
            if (_serverAddress is null)
            {
                CreateClient().Dispose(); // Kestrel binds its port on the first client
                _serverAddress = ClientOptions.BaseAddress;
            }
            return _serverAddress;
        }
    }

    private Uri? _serverAddress;

    /// <summary>The own origin as a browser sends it in <c>Origin</c>.</summary>
    public string Origin => ServerAddress.GetLeftPart(UriPartial.Authority);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable("PipelineApi__BaseUrl", Api.BaseUrl);
        Environment.SetEnvironmentVariable("Bff__ActivityTimeout", "00:00:01");
    }

    /// <summary>A client without a session.</summary>
    public BffClient Anonymous()
    {
        var cookies = new CookieContainer();
        var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false })
        {
            BaseAddress = ServerAddress,
        };
        return new BffClient(http, cookies);
    }

    /// <summary>A client signed in as <paramref name="username"/> with <paramref name="groups"/>.</summary>
    public Task<BffClient> SignInClientAsync(string username, params string[] groups) =>
        SignInWithQueryAsync($"user={Uri.EscapeDataString(username)}&groups={Uri.EscapeDataString(string.Join(',', groups))}");

    /// <summary>A client signed in through <c>/test/sign-in?{query}</c> (user, groups, ttl, email, name, canary).</summary>
    public async Task<BffClient> SignInWithQueryAsync(string query)
    {
        var client = Anonymous();
        var response = await client.Http.GetAsync($"/test/sign-in?{query}");
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable("Bff__ActivityTimeout", null);
            Api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}
