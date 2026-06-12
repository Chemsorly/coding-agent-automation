using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AgentLabelOperations"/>.
/// </summary>
public class AgentLabelOperationsTests
{
    [Fact]
    public async Task SwapAsync_AddsTarget_ThenRemovesAllOtherLabels()
    {
        var removed = new List<string>();
        var added = new List<string>();
        var callOrder = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); callOrder.Add($"remove:{label}"); return Task.CompletedTask; },
            (label, ct) => { added.Add(label); callOrder.Add($"add:{label}"); return Task.CompletedTask; },
            AgentLabels.InProgress,
            CancellationToken.None);

        removed.Should().NotContain(AgentLabels.InProgress);
        removed.Should().HaveCount(AgentLabels.All.Count - 1);
        added.Should().ContainSingle().Which.Should().Be(AgentLabels.InProgress);

        // Add happens before any removes (crash-safe ordering)
        callOrder.First().Should().Be($"add:{AgentLabels.InProgress}");
    }

    [Fact]
    public async Task SwapAsync_WhenNewLabelIsEmpty_RemovesAllWithoutAdding()
    {
        var removed = new List<string>();
        var added = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            (label, ct) => { added.Add(label); return Task.CompletedTask; },
            string.Empty,
            CancellationToken.None);

        removed.Should().HaveCount(AgentLabels.All.Count);
        added.Should().BeEmpty();
    }

    [Fact]
    public async Task SwapAsync_WhenNewLabelIsNull_RemovesAllWithoutAdding()
    {
        var removed = new List<string>();
        var added = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            (label, ct) => { added.Add(label); return Task.CompletedTask; },
            null!,
            CancellationToken.None);

        removed.Should().HaveCount(AgentLabels.All.Count);
        added.Should().BeEmpty();
    }

    [Fact]
    public async Task SwapAsync_SkipsTargetLabelInRemoveLoop()
    {
        var removed = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            (label, ct) => Task.CompletedTask,
            AgentLabels.Error,
            CancellationToken.None);

        removed.Should().NotContain(AgentLabels.Error);
    }

    [Fact]
    public async Task RemoveAllAsync_RemovesEveryLabelInAgentLabelsAll()
    {
        var removed = new List<string>();

        await AgentLabelOperations.RemoveAllAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            CancellationToken.None);

        removed.Should().BeEquivalentTo(AgentLabels.All);
    }

    [Fact]
    public async Task SwapAsync_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var receivedTokens = new List<CancellationToken>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { receivedTokens.Add(ct); return Task.CompletedTask; },
            (label, ct) => { receivedTokens.Add(ct); return Task.CompletedTask; },
            AgentLabels.Done,
            cts.Token);

        receivedTokens.Should().AllSatisfy(t => t.Should().Be(cts.Token));
    }
}
