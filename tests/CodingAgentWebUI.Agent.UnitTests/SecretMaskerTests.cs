using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="SecretMasker"/>.
/// </summary>
public class SecretMaskerTests
{
    [Fact]
    public void Mask_NullOutput_ThrowsArgumentNullException()
    {
        var act = () => SecretMasker.Mask(null!, new Dictionary<string, string>());
        act.Should().Throw<ArgumentNullException>().WithParameterName("output");
    }

    [Fact]
    public void Mask_NullSecrets_ThrowsArgumentNullException()
    {
        var act = () => SecretMasker.Mask("output", null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("secrets");
    }

    [Fact]
    public void Mask_EmptySecrets_ReturnsOutputUnchanged()
    {
        var result = SecretMasker.Mask("some output", new Dictionary<string, string>());
        result.Should().Be("some output");
    }

    [Fact]
    public void Mask_ShortValue_NotMasked()
    {
        var secrets = new Dictionary<string, string> { ["KEY"] = "abc" };
        var result = SecretMasker.Mask("value is abc here", secrets);
        result.Should().Be("value is abc here");
    }

    [Fact]
    public void Mask_ExactlyFourChars_IsMasked()
    {
        var secrets = new Dictionary<string, string> { ["KEY"] = "abcd" };
        var result = SecretMasker.Mask("value is abcd here", secrets);
        result.Should().Be("value is *** here");
    }

    [Fact]
    public void Mask_LongValue_IsMasked()
    {
        var secrets = new Dictionary<string, string> { ["TOKEN"] = "my-secret-token" };
        var result = SecretMasker.Mask("auth: my-secret-token", secrets);
        result.Should().Be("auth: ***");
    }

    [Fact]
    public void Mask_MultipleSecrets_AllLongOnesMasked()
    {
        var secrets = new Dictionary<string, string>
        {
            ["A"] = "secret-alpha",
            ["B"] = "secret-beta",
            ["C"] = "ab" // short — not masked
        };
        var result = SecretMasker.Mask("secret-alpha and secret-beta and ab", secrets);
        result.Should().Be("*** and *** and ab");
    }

    [Fact]
    public void Mask_MultipleOccurrences_AllReplaced()
    {
        var secrets = new Dictionary<string, string> { ["KEY"] = "token123" };
        var result = SecretMasker.Mask("token123 appears token123 twice", secrets);
        result.Should().Be("*** appears *** twice");
    }
}
