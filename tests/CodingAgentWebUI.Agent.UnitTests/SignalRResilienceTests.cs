using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Resilience;
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
}
