using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodingAgentWebUI.Pipeline.GitHub;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for <see cref="GitHubJwtGenerator"/> — the shared RS256 JWT generator for GitHub App
/// authentication. Extracted to Infrastructure.Common in Spec 048 Phase 1 and previously untested.
/// The JWT is decoded manually (split + base64url) so the test needs no JWT-parsing package.
/// </summary>
public class GitHubJwtGeneratorTests
{
    private static string NewPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static JsonElement DecodeSegment(string token, int index)
    {
        var seg = token.Split('.')[index].Replace('-', '+').Replace('_', '/');
        seg += (seg.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(seg));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void GenerateFromPem_ProducesRs256Jwt_WithExpectedIssuerAndValidity()
    {
        var pem = NewPrivateKeyPem();
        var beforeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var token = GitHubJwtGenerator.GenerateFromPem("client-123", pem);

        Assert.Equal(3, token.Split('.').Length); // header.payload.signature

        var header = DecodeSegment(token, 0);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());

        var payload = DecodeSegment(token, 1);
        Assert.Equal("client-123", payload.GetProperty("iss").GetString());

        var iat = payload.GetProperty("iat").GetInt64();
        var exp = payload.GetProperty("exp").GetInt64();
        // iat is backdated ~60s (clock-skew guard), exp is ~5 minutes after 'now'.
        Assert.True(iat <= beforeUnix, "iat should be backdated to at most 'now'");
        Assert.InRange(exp - iat, 300, 420); // 5 min window + up to 60s backdate
    }

    [Fact]
    public void GenerateFromBase64_DecodesPemAndSigns()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(NewPrivateKeyPem()));

        var token = GitHubJwtGenerator.GenerateFromBase64("app-42", b64);

        Assert.Equal("app-42", DecodeSegment(token, 1).GetProperty("iss").GetString());
    }

    [Fact]
    public void GenerateFromBase64_NonPemContent_ThrowsInvalidOperation()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not a pem key"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => GitHubJwtGenerator.GenerateFromBase64("c", b64));
        Assert.Contains("PEM", ex.Message);
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("c", null)]
    public void GenerateFromPem_NullArgs_ThrowsArgumentNull(string? clientId, string? pem)
        => Assert.Throws<ArgumentNullException>(() => GitHubJwtGenerator.GenerateFromPem(clientId!, pem!));

    [Theory]
    [InlineData(null, "x")]
    [InlineData("c", null)]
    public void GenerateFromBase64_NullArgs_ThrowsArgumentNull(string? clientId, string? b64)
        => Assert.Throws<ArgumentNullException>(() => GitHubJwtGenerator.GenerateFromBase64(clientId!, b64!));
}
