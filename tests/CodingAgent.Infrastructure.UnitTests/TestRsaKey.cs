using System.Security.Cryptography;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// One RSA key for the whole test assembly. Generating a 2048-bit key takes up to hundreds of
/// milliseconds, which adds up in property tests that need a valid key on every iteration but
/// don't depend on which key it is.
/// </summary>
internal static class TestRsaKey
{
    internal static readonly string Pem = CreatePem();

    private static string CreatePem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }
}
