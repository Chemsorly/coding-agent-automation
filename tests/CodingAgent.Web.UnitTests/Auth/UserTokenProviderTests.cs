using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>A user the test can switch, like a circuit whose session ends.</summary>
internal sealed class SwitchableAuthenticationStateProvider : AuthenticationStateProvider
{
    public ClaimsPrincipal User { get; private set; } = new(new ClaimsIdentity());

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));

    public void SetUser(ClaimsPrincipal user)
    {
        User = user;
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}

internal sealed class MappedRbacEvaluator(Func<ClaimsPrincipal, AccessGrant> map) : IRbacEvaluator
{
    public Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default) => Task.FromResult(map(user));
}

/// <summary>Spec 051 T8: minting user tokens in the web host.</summary>
public class UserTokenProviderTests
{
    internal static readonly SecurityKey Key = UserTokens.CreateSigningKey("unit-test-user-token-signing-key-0123456789");
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly SwitchableAuthenticationStateProvider _auth = new();
    private readonly FakeLogger<UserTokenProvider> _logger = new();
    private AccessGrant _grant = new(AccessRole.ReadOnly, new Dictionary<string, AccessRole> { [PaymentsId] = AccessRole.Operator }, []);

    private UserTokenProvider Provider() =>
        new(_auth, new MappedRbacEvaluator(_ => _grant), new UserTokenSigningKey(Key), _time, _logger);

    private ClaimsPrincipal SignIn(string username = "alice", TimeSpan? session = null)
    {
        var user = AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{username}", username, email: null, displayName: null,
            groups: ["team-a"], _time.GetUtcNow() + (session ?? TimeSpan.FromHours(8)));
        _auth.SetUser(user);
        return user;
    }

    private async Task<ClaimsIdentity> ValidateAsync(string token)
    {
        var parameters = UserTokens.CreateValidationParameters(Key);
        parameters.LifetimeValidator = (_, _, _, _) => true; // the fake clock is checked through the exp claim instead
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        result.IsValid.Should().BeTrue(result.Exception?.Message);
        return result.ClaimsIdentity;
    }

    private static TimeSpan Lifetime(string token)
    {
        var jwt = new JsonWebToken(token);
        return jwt.ValidTo - jwt.IssuedAt;
    }

    [Fact]
    public async Task Token_CarriesTheUserAndExactlyTheirGrant()
    {
        SignIn();

        var token = await Provider().GetTokenAsync();

        var identity = await ValidateAsync(token!);
        identity.FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-alice");
        identity.FindFirst(UserTokenClaimTypes.Username)!.Value.Should().Be("alice");
        identity.FindFirst(UserTokenClaimTypes.IdentitySource)!.Value.Should().Be(IdentitySources.Oidc);
        var readBack = AccessGrantClaims.FromClaims(identity.Claims);
        readBack.GlobalRole.Should().Be(_grant.GlobalRole);
        readBack.ProjectRoles.Should().BeEquivalentTo(_grant.ProjectRoles);
        identity.FindAll(AuthClaimTypes.Group).Should().BeEmpty("the token carries the grant, not the IdP's groups");
    }

    [Fact]
    public async Task Lifetime_IsTheHttpLifetime_WhenTheSessionLastsLonger()
    {
        SignIn();

        var token = await Provider().GetTokenAsync();

        Lifetime(token!).Should().Be(UserTokens.HttpLifetime);
    }

    [Fact]
    public async Task Lifetime_EndsWithTheSession()
    {
        SignIn(session: TimeSpan.FromMinutes(2));

        var token = await Provider().GetTokenAsync();

        Lifetime(token!).Should().BeCloseTo(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Spec 051 D7: the UI hub's token lasts the session, so a connection older than the HTTP lifetime keeps
    /// receiving; the hub closes it when the session ends. It is the same user and grant as the HTTP token.
    /// </summary>
    [Fact]
    public async Task HubToken_LastsTheSession_AndIsKeptApartFromTheHttpToken()
    {
        SignIn(session: TimeSpan.FromHours(8));
        var provider = Provider();

        var hubToken = await provider.GetHubTokenAsync();
        var httpToken = await provider.GetTokenAsync();

        Lifetime(hubToken!).Should().BeCloseTo(TimeSpan.FromHours(8), TimeSpan.FromSeconds(1));
        Lifetime(httpToken!).Should().Be(UserTokens.HttpLifetime);
        (await ValidateAsync(hubToken!)).FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-alice");
        (await ValidateAsync(hubToken!)).FindFirst(UserTokenClaimTypes.Use)?.Value.Should().Be(UserTokenClaimTypes.HubUse, "the API accepts it only on the hub");
        (await ValidateAsync(httpToken!)).FindFirst(UserTokenClaimTypes.Use).Should().BeNull("HTTP tokens carry no use restriction");
        (await provider.GetHubTokenAsync()).Should().Be(hubToken, "a hub token is reused while it is the same user's");
    }

    [Fact]
    public async Task HubToken_EndedSession_GetsNone()
    {
        SignIn(session: TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(2));

        (await Provider().GetHubTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task EndedSession_GetsNoToken()
    {
        SignIn(session: TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(2));

        (await Provider().GetTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Anonymous_GetsNoToken()
    {
        (await Provider().GetTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task UserWithoutAnyRole_GetsNoToken()
    {
        SignIn();
        _grant = AccessGrant.None;

        (await Provider().GetTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task LocalAdmin_GetsAnAdminToken()
    {
        _auth.SetUser(AuthPrincipals.CreateLocalAdmin(_time.GetUtcNow().AddHours(1)));
        _grant = new AccessGrant(AccessRole.Admin, new Dictionary<string, AccessRole>(), []);

        var identity = await ValidateAsync((await Provider().GetTokenAsync())!);

        identity.FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be(AuthPrincipals.LocalAdminSubject);
        identity.FindFirst(UserTokenClaimTypes.IdentitySource)!.Value.Should().Be(IdentitySources.Local);
        AccessGrantClaims.FromClaims(identity.Claims).GlobalRole.Should().Be(AccessRole.Admin);
    }

    [Fact]
    public async Task Token_IsReused_UntilShortlyBeforeItExpires()
    {
        SignIn();
        var provider = Provider();

        var first = await provider.GetTokenAsync();
        _time.Advance(UserTokens.HttpLifetime - UserTokenProvider.RenewBefore - TimeSpan.FromSeconds(1));
        var stillFirst = await provider.GetTokenAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var renewed = await provider.GetTokenAsync();

        stillFirst.Should().Be(first);
        renewed.Should().NotBe(first);
        (await ValidateAsync(renewed!)).FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-alice");
    }

    [Fact]
    public async Task UserChange_MintsATokenForTheNewUser()
    {
        SignIn("alice");
        var provider = Provider();
        var alice = await provider.GetTokenAsync();

        SignIn("bob");
        var bob = await provider.GetTokenAsync();

        (await ValidateAsync(bob!)).FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-bob");
        bob.Should().NotBe(alice);
    }

    [Fact]
    public async Task SessionEndingMidCircuit_StopsHandingOutTheToken()
    {
        SignIn();
        var provider = Provider();
        (await provider.GetTokenAsync()).Should().NotBeNull();

        _auth.SetUser(new ClaimsPrincipal(new ClaimsIdentity()));

        (await provider.GetTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TokenValue_IsNeverLogged()
    {
        SignIn();

        var token = await Provider().GetTokenAsync();

        var records = _logger.Collector.GetSnapshot();
        records.Should().NotBeEmpty("minting is logged, so the assertion below has something to check");
        foreach (var record in records)
        {
            record.Message.Should().NotContain(token!);
            (record.StructuredState ?? []).Should().NotContain(pair => pair.Value != null && pair.Value.Contains(token!));
        }
    }
}

/// <summary>
/// Spec 051 D6: UI clients share pooled HTTP handlers, so each request must carry the token of its
/// own DI scope (one per circuit). Two signed-in users call the API at the same time through the
/// real registration; every response must come back for the user who asked.
/// </summary>
public class UiClientUserIsolationTests
{
    [Fact]
    public async Task ConcurrentCircuitsOfTwoUsers_EachRequestCarriesItsOwnUser()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SwitchableAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<SwitchableAuthenticationStateProvider>());
        services.AddSingleton<IRbacEvaluator>(new MappedRbacEvaluator(_ =>
            new AccessGrant(AccessRole.ReadOnly, new Dictionary<string, AccessRole>(), [])));
        services.AddUiClients("http://api.test", UserTokenProviderTests.Key);
        var api = new EchoMeApi();
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => api));
        await using var provider = services.BuildServiceProvider();

        await using var alice = SignedInScope(provider, "alice");
        await using var bob = SignedInScope(provider, "bob");

        var calls = Enumerable.Range(0, 25)
            .SelectMany(_ => new[] { CallAsync(alice, "sub-alice"), CallAsync(bob, "sub-bob") })
            .ToList();
        await Task.WhenAll(calls);

        api.Requests.Should().Be(50);
    }

    private static AsyncServiceScope SignedInScope(IServiceProvider provider, string username)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SwitchableAuthenticationStateProvider>().SetUser(
            AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{username}", username, null, null, [], DateTimeOffset.UtcNow.AddHours(1)));
        return scope;
    }

    private static async Task CallAsync(AsyncServiceScope scope, string expectedSubject)
    {
        var me = await scope.ServiceProvider.GetRequiredService<IUiMeClient>().GetAsync();
        me.Subject.Should().Be(expectedSubject);
    }

    /// <summary>Stands in for <c>GET /api/ui/me</c>: validates the token and echoes its subject.</summary>
    private sealed class EchoMeApi : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(Random.Shared.Next(0, 3), cancellationToken); // interleave the two users
            var token = request.Headers.Authorization?.Parameter;
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, UserTokens.CreateValidationParameters(UserTokenProviderTests.Key));
            if (!result.IsValid)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var subject = result.ClaimsIdentity.FindFirst(UserTokenClaimTypes.Subject)!.Value;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new UiMeResponse(subject, null, null, "readonly", [])),
            };
        }
    }
}
