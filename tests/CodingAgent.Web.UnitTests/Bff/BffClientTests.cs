using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodingAgent.Web.UnitTests.Bff;

/// <summary>Answers each request with the next queued response (or the last one again) and records the requests.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _responses = new();
    private Func<HttpResponseMessage> _last = () => new HttpResponseMessage(HttpStatusCode.OK);

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public ScriptedHandler Then(Func<HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        if (_responses.TryDequeue(out var next))
            _last = next;
        return Task.FromResult(_last());
    }
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
}

/// <summary>Spec 052 DD8, 3.3–3.5: the browser's session, its heartbeat, the BFF handler and the version check.</summary>
public class BffClientTests
{
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly RecordingNavigationManager _nav = new("http://localhost/runs?page=2");

    internal static BffSession Session(AccessRole role = AccessRole.ReadOnly, string subject = "sub-alice", Dictionary<string, AccessRole>? projects = null) =>
        new(subject, "alice", "Alice", "alice@example.org", ["team-a"], IdentitySources.Oidc,
            new DateTimeOffset(2026, 10, 17, 18, 0, 0, TimeSpan.Zero),
            new AccessGrant(role, projects ?? [], []), "Keycloak", new CodingAgent.Web.Models.BuildInfo());

    internal static HttpResponseMessage Json(BffSession session, string? version = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(session, UiJsonContext.Default.BffSession), Encoding.UTF8, "application/json"),
        };
        if (version is not null)
            response.Headers.Add(BffHttpHandler.AppVersionHeader, version);
        return response;
    }

    private SessionStore Store(ScriptedHandler handler) =>
        new(new StubHttpClientFactory(handler), new LoginNavigator(_nav), _time, NullLogger<SessionStore>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    // ── SessionStore ─────────────────────────────────────────────────────

    [Fact]
    public async Task Load_ReturnsTheSession_ThenTheHeartbeatChecksItEvery30Seconds()
    {
        var handler = new ScriptedHandler().Then(() => Json(Session()));
        using var store = Store(handler);

        var session = await store.LoadAsync();

        session!.Subject.Should().Be("sub-alice");
        store.Current.Should().Be(session);
        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath.Should().Be("/bff/session");

        _time.Advance(SessionStore.HeartbeatInterval);
        await WaitUntilAsync(() => handler.Requests.Count == 2);
        _time.Advance(SessionStore.HeartbeatInterval);
        await WaitUntilAsync(() => handler.Requests.Count == 3);
        (await store.LoadAsync()).Should().BeSameAs(session, "the first load is started once");
    }

    [Fact]
    public async Task Unauthorized_GoesToTheLoginPage_WithTheCurrentPageAsReturnUrl()
    {
        var handler = new ScriptedHandler().Then(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var store = Store(handler);

        (await store.LoadAsync()).Should().BeNull();

        store.Current.Should().BeNull();
        _nav.Navigations.Should().ContainSingle().Which.Should().Be(("login?returnUrl=%2Fruns%3Fpage%3D2", true));
    }

    [Fact]
    public async Task SessionEndingWhileThePageIsOpen_IsCaughtByTheHeartbeat()
    {
        var handler = new ScriptedHandler()
            .Then(() => Json(Session()))
            .Then(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var store = Store(handler);
        await store.LoadAsync();

        _time.Advance(SessionStore.HeartbeatInterval);
        await WaitUntilAsync(() => _nav.Navigations.Count == 1);

        _nav.Navigations.Single().ForceLoad.Should().BeTrue("a full page load lets the static login page render");
        _time.Advance(SessionStore.HeartbeatInterval * 3);
        await Task.Delay(50);
        _nav.Navigations.Should().ContainSingle("the heartbeat stops once the browser is on its way to the login page");
    }

    [Fact]
    public async Task ChangedGrant_RaisesChanged_ButAnUnchangedOneDoesNot()
    {
        var handler = new ScriptedHandler()
            .Then(() => Json(Session()))
            .Then(() => Json(Session()))
            .Then(() => Json(Session(projects: new Dictionary<string, AccessRole> { [PaymentsId] = AccessRole.Operator })));
        using var store = Store(handler);
        var changes = 0;
        store.Changed += () => changes++;

        await store.LoadAsync();
        changes.Should().Be(1, "the first session is a change from none");
        (await store.CheckNowAsync()).Should().BeTrue();
        changes.Should().Be(1, "same user, expiry and grant");
        (await store.CheckNowAsync()).Should().BeTrue();
        changes.Should().Be(2);
        store.Current!.Grant.ProjectRoles.Should().ContainKey(PaymentsId);
    }

    [Fact]
    public async Task NetworkError_KeepsTheLastSession_AndDoesNotNavigate()
    {
        var handler = new ScriptedHandler()
            .Then(() => Json(Session()))
            .Then(() => throw new HttpRequestException("web host restarting"));
        using var store = Store(handler);
        await store.LoadAsync();

        (await store.CheckNowAsync()).Should().BeTrue("the last confirmed session stands until the web host answers");

        store.Current.Should().NotBeNull();
        _nav.Navigations.Should().BeEmpty();
    }

    // ── BffHttpHandler ───────────────────────────────────────────────────

    private (HttpClient Client, ScriptedHandler Api, ScriptedHandler SessionApi, VersionWatch Version) Client()
    {
        var api = new ScriptedHandler();
        var sessionApi = new ScriptedHandler();
        var version = new VersionWatch();
        version.SetBoot("aaaaaaa");
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(sessionApi))
            .AddSingleton(new LoginNavigator(_nav))
            .AddSingleton<TimeProvider>(_time)
            .AddSingleton(NullLogger<SessionStore>.Instance)
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<SessionStore>>(NullLogger<SessionStore>.Instance)
            .AddSingleton<SessionStore>()
            .BuildServiceProvider();
        var handler = new BffHttpHandler(version, services) { InnerHandler = api };
        return (new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, api, sessionApi, version);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task EveryRequest_CarriesTheCsrfHeader(string method)
    {
        var (client, api, _, _) = Client();

        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "api/ui/runs"));

        api.Requests.Single().Headers.GetValues(BffHttpHandler.CsrfHeader).Should().Equal("1");
    }

    [Fact]
    public async Task AnotherAppVersion_MakesTheClientOutdated()
    {
        var (client, api, _, version) = Client();
        api.Then(() => { var r = new HttpResponseMessage(HttpStatusCode.OK); r.Headers.Add(BffHttpHandler.AppVersionHeader, "aaaaaaa"); return r; })
            .Then(() => { var r = new HttpResponseMessage(HttpStatusCode.OK); r.Headers.Add(BffHttpHandler.AppVersionHeader, "bbbbbbb"); return r; });

        await client.GetAsync("api/ui/health");
        version.IsOutdated.Should().BeFalse();
        await client.GetAsync("api/ui/health");
        version.IsOutdated.Should().BeTrue();
    }

    [Fact]
    public async Task ApiUnauthorized_WithAValidSession_FailsTheCall_WithoutALoginLoop()
    {
        var (client, api, sessionApi, _) = Client();
        api.Then(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        sessionApi.Then(() => Json(Session()));

        var response = await client.GetAsync("api/ui/runs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the 401 came from the API, for example during a version skew");
        sessionApi.Requests.Should().ContainSingle("the session is checked first");
        _nav.Navigations.Should().BeEmpty();
    }

    [Fact]
    public async Task ApiUnauthorized_WithTheSessionGone_GoesToTheLoginPage()
    {
        var (client, api, sessionApi, _) = Client();
        api.Then(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        sessionApi.Then(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await client.GetAsync("api/ui/runs");

        _nav.Navigations.Should().ContainSingle().Which.ForceLoad.Should().BeTrue();
    }

    // ── VersionWatch ─────────────────────────────────────────────────────

    [Fact]
    public void VersionWatch_IsOutdatedOnlyForAnotherBuild_AndRaisesChangedOnce()
    {
        var version = new VersionWatch();
        var changes = 0;
        version.Changed += () => changes++;

        version.Report("bbbbbbb");
        version.IsOutdated.Should().BeFalse("without the page's version there is nothing to compare");

        version.SetBoot("aaaaaaa");
        version.Report("aaaaaaa");
        version.Report(null);
        version.IsOutdated.Should().BeFalse();

        version.Report("bbbbbbb");
        version.Report("ccccccc");
        version.IsOutdated.Should().BeTrue();
        changes.Should().Be(1);
    }

    // ── Authentication state and access from the session ────────────────

    [Fact]
    public async Task AuthenticationState_IsTheSessionsPrincipal()
    {
        var handler = new ScriptedHandler().Then(() => Json(Session()));
        using var store = Store(handler);
        using var provider = new BffAuthenticationStateProvider(store);

        var user = (await provider.GetAuthenticationStateAsync()).User;

        user.Identity!.IsAuthenticated.Should().BeTrue();
        AuthPrincipals.GetUsername(user).Should().Be("alice");
        AuthPrincipals.GetGroups(user).Should().Equal("team-a");
        user.FindFirst(AuthClaimTypes.Email)!.Value.Should().Be("alice@example.org");
        user.FindFirst(AuthClaimTypes.IdentitySource)!.Value.Should().Be(IdentitySources.Oidc);
        AuthPrincipals.GetSessionExpiry(user).Should().Be(Session().Expires);
    }

    [Fact]
    public async Task AuthenticationState_IsAnonymous_WithoutASession()
    {
        var handler = new ScriptedHandler().Then(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var store = Store(handler);
        using var provider = new BffAuthenticationStateProvider(store);

        (await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticationState_FollowsASessionChange()
    {
        var handler = new ScriptedHandler()
            .Then(() => Json(Session()))
            .Then(() => Json(Session(AccessRole.Admin)));
        using var store = Store(handler);
        using var provider = new BffAuthenticationStateProvider(store);
        await provider.GetAuthenticationStateAsync();
        var notified = 0;
        provider.AuthenticationStateChanged += _ => notified++;

        await store.CheckNowAsync();

        notified.Should().Be(1, "CurrentAccess re-evaluates on this event");
    }

    [Fact]
    public async Task RbacEvaluator_ReturnsTheSessionsGrant_AndNoneForAnonymous()
    {
        var handler = new ScriptedHandler().Then(() => Json(Session(AccessRole.Operator)));
        using var store = Store(handler);
        var rbac = new SessionRbacEvaluator(store);

        (await rbac.EvaluateAsync(AuthPrincipals.Create(IdentitySources.Oidc, "sub-alice", "alice", null, null, [], DateTimeOffset.UtcNow.AddHours(1))))
            .GlobalRole.Should().Be(AccessRole.Operator);
        (await rbac.EvaluateAsync(new ClaimsPrincipal(new ClaimsIdentity()))).Should().Be(AccessGrant.None);
        new SessionAuthUiInfo(store).OidcProviderName.Should().Be("Keycloak");
    }
}
