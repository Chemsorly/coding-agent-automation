using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.IntegrationTests.Helpers;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Hosting;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>The smoke-test host with one environment variable replaced (null removes it).</summary>
public sealed class SettingOverrideWebApplicationFactory(string variable, string? value) : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable(variable, value);
    }
}

/// <summary>
/// Spec 051 1.3 and D10: the web host refuses to start without a usable user-token signing key or
/// Data Protection key, both from the <c>…-web-user-token</c> Secret.
/// </summary>
[Collection("SmokeTests")]
public class SecretKeyStartupTests
{
    /// <summary>20 characters, below the 32-character minimum (low entropy, so secret scanners ignore it).</summary>
    private const string TooShort = "aaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("UserToken__SigningKey", null, "UserToken:SigningKey")]
    [InlineData("UserToken__SigningKey", TooShort, "UserToken:SigningKey")]
    [InlineData("DataProtection__Key", null, "DataProtection:Key")]
    [InlineData("DataProtection__Key", TooShort, "DataProtection:Key")]
    public void Startup_MissingOrShortKey_FailsFastWithNamedSetting(string variable, string? value, string setting)
    {
        using var factory = new SettingOverrideWebApplicationFactory(variable, value);

        Exception? ex = null;
        var console = CaptureConsole(() => ex = Record.Exception(() => factory.CreateClient()));

        ex.Should().NotBeNull("startup must fail without a usable key");
        console.Should().Contain(setting);
    }

    /// <summary>Program assigns its own bootstrap logger, whose console sink writes to Console.Out as it is at that moment.</summary>
    private static string CaptureConsole(Action action)
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return captured.ToString();
    }
}
