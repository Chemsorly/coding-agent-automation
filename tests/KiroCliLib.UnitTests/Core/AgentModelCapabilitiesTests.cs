using KiroCliLib.Core;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Unit tests for AgentModelCapabilities.
/// Validates IsTextOnlyModel detection logic: null/empty → false (vision capable),
/// deepseek variants (case-insensitive, substring) → true, other models → false.
/// </summary>
public class AgentModelCapabilitiesTests
{
    [Fact]
    public void IsTextOnlyModel_NullModel_ReturnsFalse()
    {
        Assert.False(AgentModelCapabilities.IsTextOnlyModel(null));
    }

    [Fact]
    public void IsTextOnlyModel_EmptyModel_ReturnsFalse()
    {
        Assert.False(AgentModelCapabilities.IsTextOnlyModel(""));
    }

    // TODO: Add a test case for whitespace-only input (e.g. "   ") to explicitly document the contract.
    // string.IsNullOrEmpty does not treat whitespace-only strings as empty, so "   " falls through to the
    // Contains("deepseek") check and returns false. This is likely intended behavior but is currently undocumented.
    // A whitespace-only value is a realistic misconfiguration scenario (failed config trimming).
    [Theory]
    [InlineData("deepseek-v4")]
    [InlineData("deepseek-v4-pro")]
    [InlineData("DeepSeek-V4")]
    [InlineData("DEEPSEEK")]
    [InlineData("some-deepseek-model")]
    public void IsTextOnlyModel_DeepSeekVariants_ReturnsTrue(string model)
    {
        Assert.True(AgentModelCapabilities.IsTextOnlyModel(model));
    }

    [Theory]
    [InlineData("claude-sonnet-4")]
    [InlineData("gpt-4o")]
    [InlineData("kimi-k2.5")]
    [InlineData("gpt-5")]
    public void IsTextOnlyModel_VisionCapableModels_ReturnsFalse(string model)
    {
        Assert.False(AgentModelCapabilities.IsTextOnlyModel(model));
    }
}
