using AwesomeAssertions;
using CodingAgent.Web.Auth;

namespace CodingAgent.Web.UnitTests.Auth;

public class AuthOptionsValidatorTests
{
    private static readonly AuthOptionsValidator Validator = new();

    private static AuthOptions Valid() => new()
    {
        Admin = new LocalAdminOptions { Enabled = true, Password = "secret" },
        Oidc = new OidcProviderOptions
        {
            Enabled = true, Name = "Keycloak", Issuer = "https://kc.example.com/realms/acme",
            ClientId = "coding-agent", ClientSecret = "s3cret",
        },
        Rbac = new RbacOptions
        {
            DefaultRole = "readonly",
            Bindings =
            [
                new() { Group = "platform-team", Role = "admin" },
                new() { Group = "team-a", Role = "operator", Project = "payments" },
                new() { User = "alice@acme.com", Role = "readonly" },
            ],
        },
    };

    private static IEnumerable<string> Errors(AuthOptions options) =>
        Validator.Validate(null, options).Failures ?? [];

    [Fact]
    public void ValidFullConfiguration_Succeeds()
    {
        Validator.Validate(null, Valid()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void AdminOnlyConfiguration_Succeeds()
    {
        var options = new AuthOptions { Admin = { Password = "x" } };
        Validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void NeitherLoginMethod_Fails()
    {
        var options = Valid();
        options.Admin.Enabled = false;
        options.Oidc.Enabled = false;
        Errors(options).Should().ContainSingle(e => e.Contains("cannot be disabled"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AdminEnabledWithoutPassword_Fails(string? password)
    {
        var options = Valid();
        options.Admin.Password = password;
        Errors(options).Should().ContainSingle(e => e.Contains("Auth:Admin:Password"));
    }

    [Fact]
    public void AdminDisabledWithoutPassword_Succeeds()
    {
        var options = Valid();
        options.Admin.Enabled = false;
        options.Admin.Password = null;
        Validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/realms/relative")]
    public void OidcWithoutAbsoluteIssuer_Fails(string? issuer)
    {
        var options = Valid();
        options.Oidc.Issuer = issuer;
        Errors(options).Should().ContainSingle(e => e.Contains("Auth:Oidc:Issuer"));
    }

    [Fact]
    public void OidcWithoutClientId_Fails()
    {
        var options = Valid();
        options.Oidc.ClientId = " ";
        Errors(options).Should().ContainSingle(e => e.Contains("Auth:Oidc:ClientId"));
    }

    [Fact]
    public void OidcWithoutClientSecret_Fails()
    {
        var options = Valid();
        options.Oidc.ClientSecret = null;
        Errors(options).Should().ContainSingle(e => e.Contains("Auth:Oidc:ClientSecret"));
    }

    [Fact]
    public void OidcDisabled_DoesNotRequireOidcFields()
    {
        var options = new AuthOptions { Admin = { Password = "x" }, Oidc = { Enabled = false, Issuer = null } };
        Validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("0h")]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("")]
    public void InvalidSessionDuration_Fails(string duration)
    {
        var options = Valid();
        options.SessionDuration = duration;
        Errors(options).Should().ContainSingle(e => e.Contains("Auth:SessionDuration"));
    }

    [Fact]
    public void NonPositiveRateLimit_Fails()
    {
        var options = Valid();
        options.LoginRateLimitPerMinute = 0;
        Errors(options).Should().ContainSingle(e => e.Contains("LoginRateLimitPerMinute"));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("superuser")]
    public void InvalidDefaultRole_Fails(string role)
    {
        var options = Valid();
        options.Rbac.DefaultRole = role;
        Errors(options).Should().ContainSingle(e => e.Contains("Auth:Rbac:DefaultRole"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("readonly")]
    [InlineData("Operator")]
    public void AllowedDefaultRole_Succeeds(string role)
    {
        var options = Valid();
        options.Rbac.DefaultRole = role;
        Validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void BindingWithUnknownRole_Fails()
    {
        var options = Valid();
        options.Rbac.Bindings.Add(new RoleBindingOptions { Group = "g", Role = "superuser" });
        Errors(options).Should().ContainSingle(e => e.Contains("Bindings:3") && e.Contains("unknown role"));
    }

    [Fact]
    public void BindingWithBothGroupAndUser_Fails()
    {
        var options = Valid();
        options.Rbac.Bindings.Add(new RoleBindingOptions { Group = "g", User = "u", Role = "readonly" });
        Errors(options).Should().ContainSingle(e => e.Contains("Bindings:3") && e.Contains("exactly one"));
    }

    [Fact]
    public void BindingWithNeitherGroupNorUser_Fails()
    {
        var options = Valid();
        options.Rbac.Bindings.Add(new RoleBindingOptions { Role = "readonly" });
        Errors(options).Should().ContainSingle(e => e.Contains("Bindings:3") && e.Contains("exactly one"));
    }

    [Fact]
    public void AdminBoundToProject_Fails()
    {
        var options = Valid();
        options.Rbac.Bindings.Add(new RoleBindingOptions { Group = "g", Role = "admin", Project = "payments" });
        Errors(options).Should().ContainSingle(e => e.Contains("Bindings:3") && e.Contains("only be bound globally"));
    }

    [Fact]
    public void MultipleProblems_AreAllReported()
    {
        var options = Valid();
        options.Admin.Password = "";
        options.Oidc.ClientId = "";
        options.Rbac.Bindings.Add(new RoleBindingOptions { Role = "nope" });
        Errors(options).Should().HaveCount(4);
    }
}
