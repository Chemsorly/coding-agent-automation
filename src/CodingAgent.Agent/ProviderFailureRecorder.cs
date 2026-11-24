using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Agent;

/// <summary>
/// Spec 056 DD13: the first provider failure (quota used up or login rejected) of the job this agent pod runs. Written
/// by <see cref="ProviderFailureRecordingAgentProvider"/>; read when the job's completion is built, which reports it
/// only when the run ended Failed. One per pod (a pod runs one job).
/// </summary>
public sealed class ProviderFailureRecorder
{
    // Environment variables that hold the agent's own credentials; their values never leave the pod in a detail.
    private static readonly string[] CredentialVariables =
    [
        AgentDefaults.EnvClaudeApiKey,
        AgentDefaults.EnvClaudeOAuthToken,
        AgentDefaults.EnvAnthropicApiKey,
        "CLAUDE_CODE_OAUTH_TOKEN",
    ];

    private AgentProviderFailure? _failure;

    /// <summary>The first recorded failure; null when none was recorded.</summary>
    public AgentProviderFailure? Failure => Volatile.Read(ref _failure);

    /// <summary>
    /// Records <paramref name="failure"/> unless one is recorded already. Its detail is masked with
    /// <paramref name="secrets"/> (the run's secrets) and the agent's own credentials, redacted by pattern and cut to
    /// <see cref="AgentProviderFailure.MaxDetailLength"/> characters (req 6.1, 9.4). Returns true when it was recorded.
    /// </summary>
    public bool Record(AgentProviderFailure failure, IReadOnlyDictionary<string, string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var masked = AgentProviderFailure.Create(failure.Category, failure.ResetsAt, MaskDetail(failure.Detail, secrets));
        return Interlocked.CompareExchange(ref _failure, masked, null) is null;
    }

    /// <summary>Forgets the recorded failure (a new job starts).</summary>
    public void Reset() => Volatile.Write(ref _failure, null);

    private Action<IReadOnlyList<AgentRateLimitObservation>>? _rateLimitSink;

    // The readings last relayed per window: an unchanged reading is not sent again (Claude Code reports one per request).
    private readonly Dictionary<string, AgentRateLimitObservation> _lastRelayed = new(StringComparer.Ordinal);

    /// <summary>
    /// Sends the subscription usage readings of every agent call of the job to <paramref name="sink"/> (the job's
    /// reporter) until the returned handle is disposed, so the API can pause the credential at its usage threshold while
    /// the run goes on.
    /// </summary>
    public IDisposable RelayRateLimits(Action<IReadOnlyList<AgentRateLimitObservation>> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lastRelayed)
            _lastRelayed.Clear();
        Volatile.Write(ref _rateLimitSink, sink);
        return new RelayHandle(this, sink);
    }

    /// <summary>
    /// Passes usage readings to the relay, if one is set and any window changed since the last relayed reading — during
    /// a call (<see cref="IReportsRateLimits"/>) and with its result. Never throws.
    /// </summary>
    public void ReportRateLimits(IReadOnlyList<AgentRateLimitObservation> readings)
    {
        if (readings.Count == 0 || Volatile.Read(ref _rateLimitSink) is not { } sink)
            return;
        lock (_lastRelayed)
        {
            var changed = false;
            foreach (var reading in readings)
            {
                if (_lastRelayed.TryGetValue(reading.Window, out var last) && last == reading)
                    continue;
                _lastRelayed[reading.Window] = reading;
                changed = true;
            }
            if (!changed)
                return;
        }
        try
        {
            sink(readings);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Relaying {Count} usage readings failed", readings.Count);
        }
    }

    private sealed class RelayHandle(ProviderFailureRecorder recorder, Action<IReadOnlyList<AgentRateLimitObservation>> sink) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref recorder._rateLimitSink, null, sink);
    }

    /// <summary>
    /// <paramref name="payload"/> with the recorded failure when it ended Failed; unchanged otherwise (a run that
    /// recovered and succeeded, or was cancelled, reports none).
    /// </summary>
    public JobCompletionPayload Attach(JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.FinalStep == PipelineStep.Failed && Failure is { } failure && payload.ProviderFailure is null
            ? payload with { ProviderFailure = failure }
            : payload;
    }

    private static string MaskDetail(string? detail, IReadOnlyDictionary<string, string>? secrets)
    {
        var text = detail ?? string.Empty;
        var known = new List<KeyValuePair<string, string>>(secrets ?? new Dictionary<string, string>());
        foreach (var name in CredentialVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                known.Add(new KeyValuePair<string, string>(name, value));
        }
        return SecretPatternRedactor.Redact(SecretMasker.Mask(text, known));
    }
}

/// <summary>
/// Spec 056 DD13: wraps every agent provider the pod creates (<see cref="AgentProviderFactory.CreateAgentProvider"/>)
/// and records the first <see cref="AgentErrorCategory.ProviderQuotaExhausted"/> or
/// <see cref="AgentErrorCategory.PermanentAuthFailure"/> result, and a missing credential found by
/// <see cref="ValidateAsync"/> (DD16). Results and exceptions pass through unchanged.
/// </summary>
public sealed class ProviderFailureRecordingAgentProvider(IAgentProvider inner, ProviderFailureRecorder recorder) : IAgentProvider
{
    private readonly IAgentProvider _inner = WithRateLimitRelay(inner, recorder);
    private readonly ProviderFailureRecorder _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));

    // A provider that reports usage while a call runs relays each new reading at once.
    private static IAgentProvider WithRateLimitRelay(IAgentProvider inner, ProviderFailureRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(recorder);
        if (inner is IReportsRateLimits reporting)
            reporting.RateLimitsObserved = recorder.ReportRateLimits;
        return inner;
    }

    /// <summary>The wrapped provider.</summary>
    public IAgentProvider Inner => _inner;

    public AgentProviderType ProviderType => _inner.ProviderType;
    public string? Model => _inner.Model;
    public string? LightModel => _inner.LightModel;
    public string McpConfigPath => _inner.McpConfigPath;
    public bool SupportsParallelExecution => _inner.SupportsParallelExecution;
    public IReadOnlyList<string> PipelineInjectedPaths => _inner.PipelineInjectedPaths;
    public bool SupportsVisionInput => _inner.SupportsVisionInput;

    public AgentHealthStatus GetHealthStatus() => _inner.GetHealthStatus();

    public Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct) => _inner.EnsureSessionAsync(workspacePath, ct);

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        var result = await _inner.ExecuteAsync(request, ct, onOutputLine);
        if (result.UsageDetails is { RateLimits.Count: > 0 } usage)
            _recorder.ReportRateLimits(usage.RateLimits);
        if (result.ErrorCategory is AgentErrorCategory.ProviderQuotaExhausted or AgentErrorCategory.PermanentAuthFailure)
        {
            var failure = result.ProviderFailure ?? new AgentProviderFailure(
                result.ErrorCategory == AgentErrorCategory.PermanentAuthFailure
                    ? AgentProviderFailure.AuthFailed
                    : AgentProviderFailure.QuotaExhausted,
                null,
                $"{_inner.ProviderType}: {result.ErrorCategory}");
            _recorder.Record(failure, request?.EnvironmentVariables);
        }
        return result;
    }

    public Task KillAsync() => _inner.KillAsync();

    public async Task ValidateAsync(CancellationToken ct)
    {
        try
        {
            await _inner.ValidateAsync(ct);
        }
        catch (ProviderCredentialMissingException ex)
        {
            _recorder.Record(new AgentProviderFailure(AgentProviderFailure.AuthFailed, null, ex.Message));
            throw;
        }
    }

    public Task<string?> GetLatestSessionIdAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        _inner.GetLatestSessionIdAsync(workspacePath, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
