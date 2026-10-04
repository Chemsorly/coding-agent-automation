using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodingAgent.Web.IntegrationTests.Helpers;

/// <summary>
/// Spec 049: the web host requires a signed-in user and fails at startup without a login method.
/// Test factories set a local admin password through the same environment variables Helm uses,
/// and tests that request pages sign in through the real login form.
/// </summary>
public static partial class AuthTestEnvironment
{
    public const string AdminPassword = "integration-test-admin-password";

    /// <summary>Sets the admin password and a rate limit high enough for repeated logins.</summary>
    public static void Apply(int loginRateLimitPerMinute = 10_000)
    {
        Environment.SetEnvironmentVariable("Auth__Admin__Password", AdminPassword);
        Environment.SetEnvironmentVariable("Auth__LoginRateLimitPerMinute", loginRateLimitPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void Clear()
    {
        Environment.SetEnvironmentVariable("Auth__Admin__Password", null);
        Environment.SetEnvironmentVariable("Auth__LoginRateLimitPerMinute", null);
    }

    /// <summary>Creates a client and signs it in as the local admin; the client keeps the session cookie.</summary>
    public static async Task<HttpClient> CreateSignedInClientAsync<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory, bool allowAutoRedirect = true)
        where TEntryPoint : class
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = allowAutoRedirect,
            HandleCookies = true,
        });
        // Land on a cheap anonymous endpoint: following the default redirect would render
        // /overview, which costs a full page render (slow when the API is unreachable).
        var response = await PostLoginAsync(client, "admin", AdminPassword, returnUrl: "/healthz");
        if (response.StatusCode != HttpStatusCode.Redirect && response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Test login failed with {(int)response.StatusCode}");
        return client;
    }

    /// <summary>Loads the login page for an antiforgery token, then posts the login form.</summary>
    public static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string username, string password, string? returnUrl = null)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/login");
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Username"] = username,
            ["Password"] = password,
        };
        if (returnUrl is not null)
            fields["ReturnUrl"] = returnUrl;
        return await client.PostAsync("/auth/login", new FormUrlEncodedContent(fields));
    }

    /// <summary>Reads the hidden antiforgery field of a server-rendered form.</summary>
    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = AntiforgeryField().Match(html);
        if (!match.Success)
            throw new InvalidOperationException($"No antiforgery token on {path}");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
