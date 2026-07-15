using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for SsrfGuard — validates IP blocking for SSRF prevention and
/// CreateHandler configuration.
/// </summary>
[Trait("Feature", "037-issue-image-extraction")]
public class SsrfGuardTests
{
    // ─── IsBlocked: blocked IPv4 ────────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.1")]       // 10.0.0.0/8
    [InlineData("10.255.255.255")] // 10.0.0.0/8 upper bound
    [InlineData("172.16.0.1")]     // 172.16.0.0/12
    [InlineData("172.31.255.255")] // 172.16.0.0/12 upper bound
    [InlineData("192.168.1.1")]    // 192.168.0.0/16
    [InlineData("192.168.0.0")]    // 192.168.0.0/16 lower bound
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("127.255.255.255")]// loopback upper bound
    [InlineData("169.254.169.254")]// cloud metadata
    [InlineData("169.254.0.1")]    // link-local
    public void IsBlocked_BlockedIPv4_ReturnsTrue(string ip)
    {
        var address = IPAddress.Parse(ip);

        SsrfGuard.IsBlocked(address).Should().BeTrue();
    }

    // ─── IsBlocked: blocked IPv6 ────────────────────────────────────────────────

    [Fact]
    public void IsBlocked_IPv6Loopback_ReturnsTrue()
    {
        SsrfGuard.IsBlocked(IPAddress.IPv6Loopback).Should().BeTrue();
    }

    [Fact]
    public void IsBlocked_IPv6LinkLocal_ReturnsTrue()
    {
        var fe80 = IPAddress.Parse("fe80::1");
        SsrfGuard.IsBlocked(fe80).Should().BeTrue();
    }

    [Fact]
    public void IsBlocked_IPv4MappedIPv6Loopback_ReturnsTrue()
    {
        // ::ffff:127.0.0.1
        var mapped = IPAddress.Parse("127.0.0.1").MapToIPv6();
        SsrfGuard.IsBlocked(mapped).Should().BeTrue();
    }

    [Fact]
    public void IsBlocked_IPv4MappedIPv6PrivateRange_ReturnsTrue()
    {
        // ::ffff:10.0.0.1
        var mapped = IPAddress.Parse("10.0.0.1").MapToIPv6();
        SsrfGuard.IsBlocked(mapped).Should().BeTrue();
    }

    // ─── IsBlocked: allowed IPs ─────────────────────────────────────────────────

    [Theory]
    [InlineData("8.8.8.8")]       // Google DNS
    [InlineData("1.1.1.1")]       // Cloudflare
    [InlineData("93.184.216.34")] // example.com
    [InlineData("172.32.0.1")]    // just above 172.16-31 range
    [InlineData("172.15.255.255")]// just below 172.16-31 range
    [InlineData("11.0.0.1")]      // just above 10.x range
    public void IsBlocked_PublicIPv4_ReturnsFalse(string ip)
    {
        var address = IPAddress.Parse(ip);

        SsrfGuard.IsBlocked(address).Should().BeFalse();
    }

    [Fact]
    public void IsBlocked_PublicIPv6_ReturnsFalse()
    {
        // 2001:4860:4860::8888 (Google public DNS IPv6)
        var address = IPAddress.Parse("2001:4860:4860::8888");
        SsrfGuard.IsBlocked(address).Should().BeFalse();
    }

    // ─── CreateHandler ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateHandler_ReturnsHandlerWithAutoRedirectDisabled()
    {
        using var handler = SsrfGuard.CreateHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public void CreateHandler_ReturnsHandlerWithDecompressionDisabled()
    {
        using var handler = SsrfGuard.CreateHandler();

        handler.AutomaticDecompression.Should().Be(DecompressionMethods.None);
    }

    [Fact]
    public void CreateHandler_ReturnsHandlerWithConnectCallbackSet()
    {
        using var handler = SsrfGuard.CreateHandler();

        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public void CreateHandler_WithCustomResolver_ReturnsHandler()
    {
        using var handler = SsrfGuard.CreateHandler(
            dnsResolver: (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

        handler.ConnectCallback.Should().NotBeNull();
    }

    // ─── ConnectCallbackAsync integration (via HttpClient) ──────────────────────

    [Fact]
    public async Task ConnectCallbackAsync_AllBlockedIPs_ThrowsHttpRequestException()
    {
        // Arrange: DNS resolves to only blocked IPs
        using var handler = SsrfGuard.CreateHandler(
            dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("192.168.1.1") }));
        using var client = new HttpClient(handler);

        // Act & Assert: request should fail with SSRF policy exception
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://evil.example.com/image.png"));

        ex.Message.Should().Contain("blocked by SSRF policy");
    }

    [Fact]
    public async Task ConnectCallbackAsync_EmptyDnsResult_ThrowsHttpRequestException()
    {
        // Arrange: DNS resolves to empty
        using var handler = SsrfGuard.CreateHandler(
            dnsResolver: (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        using var client = new HttpClient(handler);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://empty.example.com/image.png"));

        ex.Message.Should().Contain("no addresses");
    }

    [Fact]
    public async Task ConnectCallbackAsync_DnsResolverInjection_UsesProvidedResolver()
    {
        var resolverCalled = false;
        string? resolvedHost = null;

        using var handler = SsrfGuard.CreateHandler(
            dnsResolver: (host, _) =>
            {
                resolverCalled = true;
                resolvedHost = host;
                // Return blocked IP to prevent actual connection
                return Task.FromResult(new[] { IPAddress.Parse("192.168.1.1") });
            });
        using var client = new HttpClient(handler);

        // Will throw because all IPs blocked, but resolver should be called
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://test.example.com/image.png"));

        resolverCalled.Should().BeTrue();
        resolvedHost.Should().Be("test.example.com");
    }

    [Fact]
    public async Task ConnectCallbackAsync_MixOfBlockedAndPublic_SkipsBlockedAttemptsPublic()
    {
        // Arrange: first IP is blocked (10.x), second is public but unreachable port
        using var handler = SsrfGuard.CreateHandler(
            dnsResolver: (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("10.0.0.1"),      // blocked - should skip
                IPAddress.Parse("93.184.216.34"), // public - will attempt connect
            }));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        // Act: should NOT throw SSRF policy exception (it tries the public IP)
        // It will throw a different exception (connection refused/timeout) because we're
        // connecting to a real IP on a random port
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("https://mixed.example.com:12345/image.png"));

        // Key assertion: NOT an SSRF policy block
        ex.Message.Should().NotContain("blocked by SSRF policy");
    }
}
