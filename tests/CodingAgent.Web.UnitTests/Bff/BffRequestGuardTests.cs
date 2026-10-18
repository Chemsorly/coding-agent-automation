using System.Net.WebSockets;
using AwesomeAssertions;
using CodingAgent.Web.Bff;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CodingAgent.Web.UnitTests.Bff;

/// <summary>Spec 052 Req 2.1, 2.6, 2.7, 2.11: the rule table of <see cref="BffRequestGuard"/> (design 2.4).</summary>
public class BffRequestGuardTests
{
    private const string OwnOrigin = "https://coding-agent.example";

    private static DefaultHttpContext Request(
        string path, string method = "GET", bool csrf = true, string? origin = null, string? fetchSite = null,
        bool webSocket = false, string? rawTarget = null, string scheme = "https", string host = "coding-agent.example")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget ?? path;
        if (csrf)
            context.Request.Headers[BffRequestGuard.CsrfHeader] = "1";
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        if (fetchSite is not null)
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        context.Features.Set<IHttpWebSocketFeature>(new WebSocketFeature(webSocket));
        return context;
    }

    [Theory]
    [InlineData("/api/ui/runs")]
    [InlineData("/bff/session")]
    [InlineData("/hubs/ui")]
    [InlineData("/api")]
    public void AppliesTo_TheForwardedAndBffPaths(string path) =>
        BffRequestGuard.AppliesTo(path).Should().BeTrue();

    [Theory]
    [InlineData("/overview")]
    [InlineData("/auth/login")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/apiary")]
    public void DoesNotApply_ToPagesAndAuthForms(string path) =>
        BffRequestGuard.AppliesTo(path).Should().BeFalse();

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void SameOriginRequest_WithTheCsrfHeader_Proceeds(string method) =>
        BffRequestGuard.Refusal(Request("/api/ui/runs", method, origin: OwnOrigin, fetchSite: "same-origin")).Should().BeNull();

    [Fact]
    public void RequestWithoutAnOrigin_Proceeds() =>
        BffRequestGuard.Refusal(Request("/api/ui/runs")).Should().BeNull("same-origin GETs need not send Origin");

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public void MissingCsrfHeader_Is400_WhateverTheMethod(string method) =>
        BffRequestGuard.Refusal(Request("/api/ui/settings/models", method, csrf: false)).Should().Be(400);

    [Fact]
    public void WrongCsrfHeaderValue_Is400()
    {
        var context = Request("/api/ui/runs");
        context.Request.Headers[BffRequestGuard.CsrfHeader] = "true";

        BffRequestGuard.Refusal(context).Should().Be(400);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://coding-agent.example")]
    [InlineData("https://coding-agent.example:8443")]
    [InlineData("null")]
    public void ForeignOrigin_Is400(string origin) =>
        BffRequestGuard.Refusal(Request("/api/ui/runs", "POST", origin: origin)).Should().Be(400);

    [Theory]
    [InlineData("https://CODING-AGENT.example")]
    [InlineData("https://coding-agent.example:443")]
    public void OwnOrigin_IsMatchedCaseInsensitively_AndWithTheDefaultPort(string origin) =>
        BffRequestGuard.Refusal(Request("/api/ui/runs", "POST", origin: origin)).Should().BeNull();

    [Fact]
    public void OwnOrigin_OnANonDefaultPort_Matches() =>
        BffRequestGuard.Refusal(Request("/api/ui/runs", "POST", origin: "http://localhost:18090", scheme: "http", host: "localhost:18090"))
            .Should().BeNull();

    [Fact]
    public void CrossSiteFetch_Is400() =>
        BffRequestGuard.Refusal(Request("/api/ui/runs", fetchSite: "cross-site")).Should().Be(400);

    [Theory]
    [InlineData("/api/ui/..%2Fconfig/export")]
    [InlineData("/api/ui/..%2fconfig/export")]
    [InlineData("/api/ui/..%5Cconfig")]
    [InlineData("/api/ui/runs%2F1")]
    public void EncodedSlashOrBackslash_Is400(string rawTarget) =>
        BffRequestGuard.Refusal(Request("/api/ui/x", rawTarget: rawTarget)).Should().Be(400);

    [Fact]
    public void EncodedSlashInTheQuery_IsNotAPathEscape() =>
        BffRequestGuard.Refusal(Request("/api/ui/browse", rawTarget: "/api/ui/browse?q=a%2Fb")).Should().BeNull();

    [Fact]
    public void EncodedSlash_IsRefusedBeforeTheCsrfCheck() =>
        BffRequestGuard.Refusal(Request("/api/ui/x", csrf: false, rawTarget: "/api/ui/..%2Fconfig")).Should().Be(400);

    [Fact]
    public void HubWithoutWebSocket_Is400() =>
        BffRequestGuard.Refusal(Request("/hubs/ui", origin: OwnOrigin)).Should().Be(400);

    [Fact]
    public void HubUpgrade_FromTheOwnOrigin_Proceeds_WithoutTheCsrfHeader() =>
        BffRequestGuard.Refusal(Request("/hubs/ui", csrf: false, origin: OwnOrigin, webSocket: true)).Should().BeNull(
            "a browser WebSocket cannot send custom headers; Origin is the check");

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData(null)]
    public void HubUpgrade_WithAForeignOrMissingOrigin_Is403(string? origin) =>
        BffRequestGuard.Refusal(Request("/hubs/ui", origin: origin, webSocket: true)).Should().Be(403);

    [Fact]
    public void OtherHubPaths_FollowTheCsrfRules() =>
        BffRequestGuard.Refusal(Request("/hubs/agent", csrf: false, webSocket: true)).Should().Be(400);

    private sealed class WebSocketFeature(bool isWebSocketRequest) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => isWebSocketRequest;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }
}
