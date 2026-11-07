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
    private readonly IAgentProvider _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ProviderFailureRecorder _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));

    /// <summary>The wrapped provider.</summary>
    public IAgentProvider Inner => _inner;

    public AgentProviderType ProviderType => _inner.ProviderType;
    public string? Model => _inner.Model;
    public string McpConfigPath => _inner.McpConfigPath;
    public bool SupportsParallelExecution => _inner.SupportsParallelExecution;
    public IReadOnlyList<string> PipelineInjectedPaths => _inner.PipelineInjectedPaths;
    public bool SupportsVisionInput => _inner.SupportsVisionInput;

    public AgentHealthStatus GetHealthStatus() => _inner.GetHealthStatus();

    public Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct) => _inner.EnsureSessionAsync(workspacePath, ct);

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        var result = await _inner.ExecuteAsync(request, ct, onOutputLine);
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
