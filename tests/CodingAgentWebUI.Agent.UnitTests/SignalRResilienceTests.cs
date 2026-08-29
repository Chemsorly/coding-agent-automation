using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Resilience;
using Microsoft.AspNetCore.SignalR;
using Polly;
using Serilog;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for SignalR resilience pipeline behavior.
/// </summary>
public class SignalRResilienceTests
{
    private readonly ResiliencePipeline _pipeline;

    public SignalRResilienceTests()
    {
        _pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger);
    }

    [Fact]
    public async Task SignalRPipeline_TransientIOException_Retries()
    {
        var callCount = 0;
        await _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new IOException("Connection reset");
        }, CancellationToken.None);

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task SignalRPipeline_NotConnectedState_Retries()
    {
        var callCount = 0;
        await _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new InvalidOperationException("The connection is not in the 'Connected' state.");
        }, CancellationToken.None);

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task SignalRPipeline_HttpRequestException_Retries()
    {
        var callCount = 0;
        await _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("Connection refused");
        }, CancellationToken.None);

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task SignalRPipeline_OperationCanceled_DoesNotRetry()
    {
        var callCount = 0;
        var act = () => _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            throw new OperationCanceledException("Cancelled");
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task SignalRPipeline_MaxRetriesExhausted_ThrowsOriginal()
    {
        var callCount = 0;
        var act = () => _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            throw new IOException("persistent failure");
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<IOException>();
        callCount.Should().Be(4); // 1 initial + 3 retries
    }

    [Fact]
    public async Task SignalRPipeline_GenericReturnValue_RetriesAndReturns()
    {
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger);
        var callCount = 0;

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new IOException("transient");
            return "success";
        }, CancellationToken.None);

        result.Should().Be("success");
        callCount.Should().Be(2);
    }

    // ── Fix 2: HubException retry predicate ──────────────────────────────────

    /// <summary>
    /// Verifies that a <see cref="HubException"/> with "Agent not registered" is retried.
    /// This covers the reconnect window where RegisterAgent has not yet written the new
    /// connectionId to Redis on any replica.
    /// </summary>
    [Fact]
    public async Task SignalRPipeline_AgentNotRegisteredHubException_Retries()
    {
        var callCount = 0;
        await _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HubException("Agent not registered (connection abc123)");
        }, CancellationToken.None);

        callCount.Should().Be(2, "Agent not registered HubException should trigger one retry");
    }

    /// <summary>
    /// Verifies that a <see cref="HubException"/> containing "is not assigned to agent" is retried.
    /// The realistic message format from GuardActiveJob is
    /// "Job {jobId} is not assigned to agent {agentId}" — the predicate matches the substring.
    /// </summary>
    [Fact]
    public async Task SignalRPipeline_NotAssignedToAgentHubException_Retries()
    {
        var callCount = 0;
        await _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HubException("Job c6c178ab is not assigned to agent caa-agent-c6c178ab2d2");
        }, CancellationToken.None);

        callCount.Should().Be(2, "Job-not-assigned HubException should trigger one retry");
    }

    /// <summary>
    /// Verifies that a <see cref="HubException"/> with an unrelated message is NOT retried.
    /// Only the two reconnect-window patterns should be covered by the predicate.
    /// </summary>
    [Fact]
    public async Task SignalRPipeline_UnrelatedHubException_DoesNotRetry()
    {
        var callCount = 0;
        var act = () => _pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            throw new HubException("Method Foo is not available to operator connections");
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<HubException>();
        callCount.Should().Be(1, "Unrelated HubException should not be retried");
    }
}
