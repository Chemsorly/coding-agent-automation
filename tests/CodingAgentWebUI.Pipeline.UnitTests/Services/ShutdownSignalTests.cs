using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ShutdownSignal — volatile bool state machine.
/// </summary>
public sealed class ShutdownSignalTests
{
    [Fact]
    public void IsShuttingDown_InitiallyFalse()
    {
        var signal = new ShutdownSignal();
        signal.IsShuttingDown.Should().BeFalse();
    }

    [Fact]
    public void SignalShutdown_SetsIsShuttingDownTrue()
    {
        var signal = new ShutdownSignal();
        signal.SignalShutdown();
        signal.IsShuttingDown.Should().BeTrue();
    }

    [Fact]
    public void SignalShutdown_IsIdempotent()
    {
        var signal = new ShutdownSignal();
        signal.SignalShutdown();
        signal.SignalShutdown();
        signal.IsShuttingDown.Should().BeTrue();
    }

    [Fact]
    public void MultipleInstances_AreIndependent()
    {
        var a = new ShutdownSignal();
        var b = new ShutdownSignal();
        a.SignalShutdown();
        b.IsShuttingDown.Should().BeFalse();
    }
}
