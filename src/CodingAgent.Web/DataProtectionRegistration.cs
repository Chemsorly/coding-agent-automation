using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace CodingAgent.Web;

/// <summary>
/// The web host's Data Protection key ring (spec 051 D10). Data Protection encrypts the login
/// cookie and the antiforgery tokens.
///
/// <para>
/// The ring holds one fixed key built from the <c>data-protection-key</c> of the
/// <c>…-web-user-token</c> Secret, which is mounted into the web pods only. Every replica builds the
/// same key, so a cookie issued by one replica is accepted by all of them and survives restarts,
/// with no shared storage. Without a shared ring, a page load on replica A and its circuit on
/// replica B fail with "The circuit failed to initialize".
/// </para>
///
/// <para>
/// The ring is read-only and automatic key generation is off, so nothing can add a key to it.
/// The Redis ring this replaces could be written by anything that reached Redis. Rotation means
/// replacing the Secret value and restarting the web pods; everyone signs in again.
/// </para>
/// </summary>
public static class DataProtectionRegistration
{
    internal const string ApplicationName = "coding-agent-web";

    /// <summary>IConfiguration key of the key material (Helm: <c>DataProtection__Key</c>).</summary>
    public const string KeyConfigKey = "DataProtection:Key";

    /// <summary>Minimum length in characters, as for the user-token signing key.</summary>
    public const int MinimumKeyLength = 32;

    /// <summary>
    /// The one key's ID. It is fixed: after a rotation, payloads under this ID fail authentication
    /// with the new material, which signs everyone out as intended.
    /// </summary>
    internal static readonly Guid KeyId = new("0a51d10e-0051-4d10-a051-000000000d10");

    private static readonly DateTimeOffset KeyActivation = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset KeyExpiration = new(2999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Registers Data Protection with the read-only ring. Throws <see cref="InvalidOperationException"/>
    /// when the key is missing or shorter than <see cref="MinimumKeyLength"/>.
    /// </summary>
    internal static IServiceCollection AddDataProtectionServices(this IServiceCollection services, string? configuredKey)
    {
        var key = CreateKeyElement(configuredKey);

        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .DisableAutomaticKeyGeneration();
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = new ReadOnlyKeyRing(key));

        return services;
    }

    /// <summary>The key in the XML form the key manager reads, as it would have written it.</summary>
    internal static XElement CreateKeyElement(string? configuredKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new InvalidOperationException($"{KeyConfigKey} is not configured.");
        if (configuredKey.Length < MinimumKeyLength)
            throw new InvalidOperationException($"{KeyConfigKey} must be at least {MinimumKeyLength} characters long.");

        // The framework's default algorithms (AES-256-CBC, HMACSHA256); subkeys are derived from the master key.
        var descriptor = new AuthenticatedEncryptorDescriptor(
            new AuthenticatedEncryptorConfiguration(), new Secret(Encoding.UTF8.GetBytes(configuredKey)));
        var xml = descriptor.ExportToXml();

        return new XElement("key",
            new XAttribute("id", KeyId),
            new XAttribute("version", 1),
            new XElement("creationDate", KeyActivation),
            new XElement("activationDate", KeyActivation),
            new XElement("expirationDate", KeyExpiration),
            new XElement("descriptor",
                new XAttribute("deserializerType", xml.DeserializerType.AssemblyQualifiedName!),
                xml.SerializedDescriptorElement));
    }

    /// <summary>A key ring with exactly one key that never changes.</summary>
    internal sealed class ReadOnlyKeyRing(XElement key) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => [new XElement(key)];

        public void StoreElement(XElement element, string friendlyName) =>
            throw new NotSupportedException(
                "The Data Protection key ring is read-only (spec 051 D10): its key comes from the web-user-token Secret.");
    }
}
