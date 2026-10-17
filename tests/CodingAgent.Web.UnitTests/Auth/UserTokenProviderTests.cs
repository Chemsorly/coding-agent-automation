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

/// <summary>
/// Spec 051 D2: the per-scope token cache in front of <see cref="UserTokenMinter"/> (the minting rules
/// are <see cref="UserTokenMinterTests"/>). Spec 052 deletes the provider with the circuits.
/// </summary>
public class UserTokenProviderTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly SwitchableAuthenticationStateProvider _auth = new();
    private readonly AccessGrant _grant = new(AccessRole.ReadOnly, new Dictionary<string, AccessRole>(), []);

    private UserTokenProvider Provider() =>
        new(_auth, new UserTokenMinter(new MappedRbacEvaluator(_ => _grant), new UserTokenSigningKey(UserTokenMinterTests.Key), _time,
            new FakeLogger<UserTokenMinter>()), _time);

    private void SignIn(string username = "alice", TimeSpan? session = null) =>
        _auth.SetUser(AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{username}", username, email: null, displayName: null,
            groups: ["team-a"], _time.GetUtcNow() + (session ?? TimeSpan.FromHours(8))));

    [Fact]
    public async Task HubToken_IsKeptApartFromTheHttpToken_AndReused()
    {
        SignIn();
        var provider = Provider();

        var hubToken = await provider.GetHubTokenAsync();
        var httpToken = await provider.GetTokenAsync();

        hubToken.Should().NotBe(httpToken);
        (await UserTokenMinterTests.ValidateAsync(hubToken!)).FindFirst(UserTokenClaimTypes.Use)?.Value.Should().Be(UserTokenClaimTypes.HubUse);
        (await provider.GetHubTokenAsync()).Should().Be(hubToken, "a hub token is reused while it is the same user's");
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
        (await UserTokenMinterTests.ValidateAsync(renewed!)).FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-alice");
    }

    [Fact]
    public async Task UserChange_MintsATokenForTheNewUser()
    {
        SignIn("alice");
        var provider = Provider();
        var alice = await provider.GetTokenAsync();

        SignIn("bob");
        var bob = await provider.GetTokenAsync();

        (await UserTokenMinterTests.ValidateAsync(bob!)).FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-bob");
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
        services.AddUiClients("http://api.test", UserTokenMinterTests.Key);
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
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, UserTokens.CreateValidationParameters(UserTokenMinterTests.Key));
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
