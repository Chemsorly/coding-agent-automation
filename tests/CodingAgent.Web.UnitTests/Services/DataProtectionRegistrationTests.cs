using System.Security.Cryptography;
using System.Xml.Linq;
using AwesomeAssertions;
using CodingAgent.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Spec 051 D10: the web host's Data Protection ring is one fixed key from the
/// <c>data-protection-key</c> Secret value. Each test builds separate hosts' worth of DI, as two
/// replicas (or a restart) would.
/// </summary>
public class DataProtectionRegistrationTests
{
    private const string Key = "data-protection-test-key-0123456789abcdef";
    private const string OtherKey = "another-data-protection-key-0123456789ab";

    private static ServiceProvider Host(string key)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtectionServices(key);
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(ServiceProvider host) =>
        host.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-purpose");

    [Fact]
    public void TwoHostsWithTheSameKey_ReadEachOthersPayloads()
    {
        using var replicaA = Host(Key);
        using var replicaB = Host(Key);

        var payload = Protector(replicaA).Protect("session");

        Protector(replicaB).Unprotect(payload).Should().Be("session");
    }

    [Fact]
    public void AHostWithAnotherKey_RejectsThePayload()
    {
        using var issuer = Host(Key);
        using var rotated = Host(OtherKey);

        var payload = Protector(issuer).Protect("session");

        var act = () => Protector(rotated).Unprotect(payload);
        act.Should().Throw<CryptographicException>("a rotated key must sign everyone out");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short-0123456789")]
    public void MissingOrShortKey_IsRefused_NamingTheSetting(string? key)
    {
        var act = () => new ServiceCollection().AddDataProtectionServices(key);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{DataProtectionRegistration.KeyConfigKey}*");
    }

    [Fact]
    public void TheRing_IsReadOnly_AndNeverGeneratesKeys()
    {
        using var host = Host(Key);
        var options = host.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        options.AutoGenerateKeys.Should().BeFalse();
        options.XmlRepository.Should().BeOfType<DataProtectionRegistration.ReadOnlyKeyRing>();
        options.XmlRepository!.GetAllElements().Should().ContainSingle();
        var store = () => options.XmlRepository.StoreElement(new XElement("key"), "attacker-key");
        store.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void TheKey_IsUsedForProtection()
    {
        using var host = Host(Key);
        var keys = host.GetRequiredService<IKeyManager>().GetAllKeys();

        keys.Should().ContainSingle().Which.KeyId.Should().Be(DataProtectionRegistration.KeyId);
        Protector(host).Unprotect(Protector(host).Protect("x")).Should().Be("x");
    }

    [Fact]
    public void ApplicationName_IsPinned()
    {
        // Changing it invalidates every session and in-flight antiforgery token.
        using var host = Host(Key);

        host.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator
            .Should().Be("coding-agent-web");
    }
}
