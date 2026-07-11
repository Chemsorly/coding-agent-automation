using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using CodingAgentWebUI.Agent.KiroCli;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using System.Diagnostics;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for IAgentProvider.SupportsVisionInput across all provider implementations.
/// Vision detection logic: null/empty model -> true, model contains "deepseek" (case-insensitive) -> false, otherwise -> true.
/// </summary>
public class SupportsVisionInputTests
{
    // ── KiroCliAgentProvider ────────────────────────────────────────────

    [Fact]
    public void KiroCli_NullModel_ReturnsTrue()
    {
        var provider = CreateKiroProvider(model: null);
        provider.SupportsVisionInput.Should().BeTrue();
    }

    [Fact]
    public void KiroCli_EmptyModel_ReturnsTrue()
    {
        var provider = CreateKiroProvider(model: "");
        provider.SupportsVisionInput.Should().BeTrue();
    }

    [Theory]
    [InlineData("deepseek-v4")]
    [InlineData("deepseek-v4-pro")]
    [InlineData("DeepSeek-V4")]
    [InlineData("DEEPSEEK")]
    [InlineData("some-deepseek-model")]
    public void KiroCli_DeepSeekModel_ReturnsFalse(string model)
    {
        var provider = CreateKiroProvider(model: model);
        provider.SupportsVisionInput.Should().BeFalse();
    }

    [Theory]
    [InlineData("claude-sonnet-4")]
    [InlineData("kimi-k2.5")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-5")]
    public void KiroCli_VisionCapableModel_ReturnsTrue(string model)
    {
        var provider = CreateKiroProvider(model: model);
        provider.SupportsVisionInput.Should().BeTrue();
    }

    // ── OpenCodeAgentProvider ───────────────────────────────────────────

    [Fact]
    public void OpenCode_NullModel_ReturnsTrue()
    {
        var provider = CreateOpenCodeProvider(model: null);
        provider.SupportsVisionInput.Should().BeTrue();
    }

    [Fact]
    public void OpenCode_EmptyModel_ReturnsTrue()
    {
        var provider = CreateOpenCodeProvider(model: "");
        provider.SupportsVisionInput.Should().BeTrue();
    }

    [Theory]
    [InlineData("deepseek-v4")]
    [InlineData("deepseek-v4-pro")]
    [InlineData("DeepSeek-V4")]
    [InlineData("DEEPSEEK")]
    public void OpenCode_DeepSeekModel_ReturnsFalse(string model)
    {
        var provider = CreateOpenCodeProvider(model: model);
        provider.SupportsVisionInput.Should().BeFalse();
    }

    [Theory]
    [InlineData("claude-sonnet-4")]
    [InlineData("kimi-k2.5")]
    [InlineData("gpt-4o")]
    public void OpenCode_VisionCapableModel_ReturnsTrue(string model)
    {
        var provider = CreateOpenCodeProvider(model: model);
        provider.SupportsVisionInput.Should().BeTrue();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static KiroCliAgentProvider CreateKiroProvider(string? model)
    {
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        var mockProcessStarter = new Mock<IProcessStarter>();
        mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>())).Returns((Process?)null);
        return new KiroCliAgentProvider(
            mockOrchestrator.Object, null, model,
            "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, mockProcessStarter.Object);
    }

    private static OpenCodeAgentProvider CreateOpenCodeProvider(string? model)
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        return new OpenCodeAgentProvider(mockFactory.Object, null, model);
    }
}
