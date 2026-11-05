using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>Spec 057 req 1.3, 1.4, 6.3, 8.3: validation of the UI agent limits.</summary>
public sealed class AgentCapacityLimitsValidatorTests
{
    private static AgentCapacityLimits Limits(int global = 0, params (string Key, int Value)[] entries) => new()
    {
        MaxConcurrentAgents = global,
        MaxConcurrentByJobTemplate = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)
    };

    // req 1.3, 1.4: 0, 100, the empty key and an untidy key are valid.
    [Fact]
    public void WhenValuesAndKeysAreValid_ShouldReturnNoErrors()
    {
        var errors = AgentCapacityLimitsValidator.Validate(
            Limits(100, ("", 0), (" kiro , dotnet ", 100), ("dotnet,dotnet10,kiro", 3)));

        errors.Should().BeEmpty();
    }

    // req 8.3
    [Fact]
    public void WhenMoreThan100Entries_ShouldReportTheCount()
    {
        var entries = Enumerable.Range(0, 101).Select(i => ($"t{i}", 1)).ToArray();

        var errors = AgentCapacityLimitsValidator.Validate(Limits(0, entries));

        errors.Should().Contain("At most 100 job template limits can be set (was 101).");
    }

    // req 1.3, 6.3
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void WhenTheGlobalValueIsOutOfRange_ShouldReportIt(int value)
    {
        var errors = AgentCapacityLimitsValidator.Validate(Limits(value));

        errors.Should().ContainSingle().Which.Should()
            .Be($"The limit for all agents must be between 0 and 100 (was {value}).");
    }

    // req 1.4, 6.3
    [Fact]
    public void WhenATemplateValueIsOutOfRange_ShouldNameTheNormalizedKey()
    {
        var errors = AgentCapacityLimitsValidator.Validate(Limits(0, ("kiro, dotnet", 101)));

        errors.Should().ContainSingle().Which.Should().Be("The limit for dotnet,kiro must be between 0 and 100 (was 101).");
    }

    // req 8.3
    [Theory]
    [InlineData("dotnet;kiro")]
    [InlineData("dot net")]
    [InlineData("kiro/x")]
    public void WhenAKeyHasAnInvalidCharacter_ShouldReportTheKey(string key)
    {
        var errors = AgentCapacityLimitsValidator.Validate(Limits(0, (key, 1)));

        errors.Should().ContainSingle().Which.Should().Be($"{key} is not a valid job template key.");
    }

    // req 8.3: longer than a Kubernetes label value; the message cuts the key to 63 characters.
    [Fact]
    public void WhenAKeyIsLongerThan63Characters_ShouldReportTheCutKey()
    {
        var key = new string('a', 70);

        var errors = AgentCapacityLimitsValidator.Validate(Limits(0, (key, 1)));

        errors.Should().ContainSingle().Which.Should().Be($"{new string('a', 63)} is not a valid job template key.");
    }

    // req 8.3: two spellings of one template.
    [Fact]
    public void WhenTwoKeysNormalizeToTheSameKey_ShouldReportItOnce()
    {
        var errors = AgentCapacityLimitsValidator.Validate(Limits(0, ("kiro,dotnet", 1), ("dotnet, kiro", 2), (" dotnet,kiro", 3)));

        errors.Should().ContainSingle().Which.Should().Be("dotnet,kiro is set twice.");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData(" kiro , dotnet ", "dotnet,kiro")]
    [InlineData("a,,b", "a,b")]
    [InlineData("Z,a", "Z,a")]
    public void NormalizeKey_ShouldTrimSortAndDropEmptyEntries(string? key, string expected)
    {
        AgentCapacityLimitsValidator.NormalizeKey(key).Should().Be(expected);
    }
}
