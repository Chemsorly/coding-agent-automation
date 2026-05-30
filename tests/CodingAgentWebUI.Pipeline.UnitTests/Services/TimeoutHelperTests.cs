using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class TimeoutHelperTests
{
    [Fact]
    public async Task ExecuteWithTimeoutAsync_WorkCompletes_ReturnsResult()
    {
        var result = await TimeoutHelper.ExecuteWithTimeoutAsync(
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            async _ => { await Task.Delay(1); return 42; },
            () => Task.FromResult(-1));

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteWithTimeoutAsync_Timeout_ReturnsOnTimeoutValue()
    {
        var result = await TimeoutHelper.ExecuteWithTimeoutAsync(
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None,
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return 42; },
            () => Task.FromResult(-1));

        result.Should().Be(-1);
    }

    [Fact]
    public async Task ExecuteWithTimeoutAsync_CallerCancellation_PropagatesException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => TimeoutHelper.ExecuteWithTimeoutAsync(
            TimeSpan.FromSeconds(5),
            cts.Token,
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return 42; },
            () => Task.FromResult(-1));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteWithTimeoutAsync_WorkThrowsNonCancellation_PropagatesException()
    {
        var act = () => TimeoutHelper.ExecuteWithTimeoutAsync<int>(
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            _ => throw new InvalidOperationException("test error"),
            () => Task.FromResult(-1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("test error");
    }

    [Fact]
    public async Task ExecuteWithTimeoutAsync_ZeroTimeout_ImmediateTimeout()
    {
        var result = await TimeoutHelper.ExecuteWithTimeoutAsync(
            TimeSpan.Zero,
            CancellationToken.None,
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return 42; },
            () => Task.FromResult(-1));

        result.Should().Be(-1);
    }

    [Fact]
    public async Task ExecuteWithTimeoutAsync_AsyncOnTimeout_IsAwaited()
    {
        var abortCalled = false;

        var result = await TimeoutHelper.ExecuteWithTimeoutAsync(
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None,
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return 42; },
            async () => { await Task.Delay(1); abortCalled = true; return -1; });

        result.Should().Be(-1);
        abortCalled.Should().BeTrue();
    }
}
