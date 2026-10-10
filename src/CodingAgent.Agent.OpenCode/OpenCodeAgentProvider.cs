using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using KiroCliLib.Core;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent.OpenCode;

/// <summary>
/// Agent provider that communicates with an OpenCode server via localhost HTTP API.
/// Does not spawn processes — uses IHttpClientFactory named client.
/// </summary>
/// <remarks>
/// Sessions: the first session started in a workspace is its main conversation, which
/// <c>UseResume</c> continues; later fresh sessions are isolated calls (reviewers, review agents).
/// </remarks>
public sealed partial class OpenCodeAgentProvider : IAgentProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string? _model;
    private readonly Uri? _baseUrl;

    /// <summary>Sessions with a call in progress, mapped to their workspace, so KillAsync can abort them all.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _activeSessions = new(StringComparer.Ordinal);
    private long _lastOutputTimeTicks; // Interlocked access for DateTime
    private int _activeExecutionCount; // Tracks concurrent executions for correct IsExecuting
    private volatile string? _sessionStatus; // "idle", "busy", "retry"
    private volatile string? _sessionStatusMessage; // Error/retry message from session.status event
    private volatile string? _allSessionsSummary; // Cached summary from polling GET /session/status
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Input, long Output, long Reasoning, long CacheRead, long CacheWrite, double Cost)> _lastSessionTokens = new();

    /// <summary>
    /// Last session ID returned by the opencode server (used only for health/diagnostics — NOT for session routing).
    /// Session routing is always based on workspace path: each ExecuteAsync call resolves its own session.
    /// </summary>
    private volatile string? _lastKnownSessionId;

    /// <summary>
    /// Per-workspace session cache. Maps absolute workspace path → session ID.
    /// Sessions are scoped to their workspace: different workspaces always get fresh sessions.
    /// UseResume=true within the same workspace reuses the cached session.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessionByWorkspace = new(StringComparer.Ordinal);

    /// <summary>Test-only: sets _lastKnownSessionId for verifying diff/kill behavior.</summary>
    internal void SetLastKnownSessionIdForTest(string? sessionId) => _lastKnownSessionId = sessionId;

    public AgentProviderType ProviderType => AgentProviderType.OpenCode;

    /// <inheritdoc />
    public string? Model => _model;

    /// <inheritdoc />
    /// <remarks>
    /// OpenCode reads <c>~/.opencode/opencode.json</c> when it first serves a workspace (only the global
    /// <c>~/.config/opencode</c> config is cached at startup), so servers written here before the
    /// first call reach it. The agent's MCP config writer fills its <c>mcp</c> section.
    /// </remarks>
    public string McpConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "opencode.json");

    /// <inheritdoc />
    public bool SupportsParallelExecution => true;

    /// <inheritdoc />
    public bool SupportsVisionInput => !AgentModelCapabilities.IsTextOnlyModel(_model);

    /// <inheritdoc />
    public IReadOnlyList<string> PipelineInjectedPaths { get; } = ["AGENTS.md"];

    /// <param name="httpClientFactory">Provides the named OpenCode client (Basic auth, base address).</param>
    /// <param name="logger">Logger; defaults to the static Serilog logger.</param>
    /// <param name="model">Model in <c>provider/model</c> form, sent with every prompt; null or <c>auto</c> leaves it to the server.</param>
    /// <param name="baseUrl">Server address overriding the named client's; null keeps the client's.</param>
    public OpenCodeAgentProvider(
        IHttpClientFactory httpClientFactory,
        ILogger? logger = null,
        string? model = null,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? Serilog.Log.Logger;
        _model = model;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : new Uri(baseUrl);
    }

    // ── Thread-safe state access ────────────────────────────────────────
    private DateTime? LastOutputTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastOutputTimeTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Interlocked.Exchange(ref _lastOutputTimeTicks, value?.Ticks ?? 0);
    }

    // ── IAgentProvider ──────────────────────────────────────────────────

    public AgentHealthStatus GetHealthStatus()
    {
        return new AgentHealthStatus
        {
            IsExecuting = Interlocked.CompareExchange(ref _activeExecutionCount, 0, 0) > 0,
            ProcessId = null,
            IsProcessAlive = null,
            LastOutputTime = LastOutputTime,
            SessionStatus = _sessionStatus,
            SessionStatusMessage = _sessionStatusMessage,
            AllSessionsSummary = _allSessionsSummary
        };
    }

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        Interlocked.Increment(ref _activeExecutionCount);
        ResetExecutionState();

        // Per-call SSE state (assistant messages seen, text already emitted): parallel calls must not share it.
        var sseState = new SseCallState();
        var workspacePath = Path.GetFullPath(request.WorkspacePath);

        var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = PollAllSessionStatusesAsync(workspacePath, pollCts.Token);
        string? sessionId = null;

        try
        {
            // 1. Session selection — always create/resolve per workspace path (stateless)
            sessionId = await ResolveSessionIdAsync(request, ct);
            if (sessionId is null)
            {
                return new AgentResult
                {
                    ExitCode = ExitCodes.GeneralFailure,
                    OutputLines = ["Failed to establish OpenCode session"]
                };
            }

            // Track for diagnostics/health only
            _lastKnownSessionId = sessionId;
            _activeSessions[sessionId] = workspacePath;

            // 2. Timeout enforcement
            var sseCts = new CancellationTokenSource();

            // 3. Start SSE reader (always — needed for permission auto-approval)
            var sseTask = ConnectAndProcessSseAsync(sessionId, onOutputLine, sseCts.Token, workspacePath, sseState);

            // 4. Send message (synchronous — blocks until agent finishes)
            AgentResult result;
            try
            {
                result = await SendMessageWithTimeoutAsync(
                    request, sessionId, workspacePath, sseState, onOutputLine, ct);
            }
            catch (OperationCanceledException oce)
            {
                // Abort on any cancellation: the server keeps running the turn after the client
                // gives up, and would go on editing the workspace while the pipeline moves on.
                // Use ct.IsCancellationRequested in the catch BODY rather than a when-filter to avoid
                // the timing race where IsCancellationRequested is momentarily false when the exception
                // is first caught but becomes true before the body executes.
                await AbortBestEffortAsync(sessionId, workspacePath);
                if (ct.IsCancellationRequested)
                {
                    _ = await CaptureSessionTokenDeltaAsync(sessionId, workspacePath);
                    throw; // finally block handles SSE cleanup
                }
                result = new AgentResult
                {
                    ExitCode = ExitCodes.GeneralFailure,
                    OutputLines = [$"Operation cancelled unexpectedly: {oce.GetType().Name}: {oce.Message}"]
                };
            }
            catch (HttpRequestException ex)
            {
                result = new AgentResult
                {
                    ExitCode = ExitCodes.GeneralFailure,
                    OutputLines = [$"HTTP error: {ex.Message}"]
                };
            }
            catch (Exception ex)
            {
                result = new AgentResult
                {
                    ExitCode = ExitCodes.GeneralFailure,
                    OutputLines = [$"Unexpected error: {ex.GetType().Name}: {ex.Message}"]
                };
            }
            finally
            {
                await TearDownSseAsync(sseCts, sseTask);
            }

            // Capture token usage delta on all paths (success, timeout, error)
            var (usage, cost) = await CaptureSessionTokenDeltaAsync(sessionId, workspacePath);
            return new AgentResult
            {
                ExitCode = result.ExitCode,
                OutputLines = result.OutputLines,
                Usage = usage,
                Cost = cost,
                // Forward the error category set by HandleHttpErrorResponseAsync.
                // Other result construction paths (timeout, exception) produce None by default,
                // which is correct — they are not provider HTTP classification failures.
                ErrorCategory = result.ErrorCategory
            };
        }
        finally
        {
            if (sessionId is not null)
                _activeSessions.TryRemove(sessionId, out _);
            Interlocked.Decrement(ref _activeExecutionCount);
            // Stop polling
            try { await pollCts.CancelAsync(); } catch (OperationCanceledException) { /* expected during shutdown */ }
            try { await pollTask.ConfigureAwait(false); } catch (OperationCanceledException) { /* expected during shutdown */ }
            pollCts.Dispose();
        }
    }

    /// <summary>Resets per-call state before execution begins.</summary>
    private void ResetExecutionState()
    {
        _sessionStatus = null;
        _sessionStatusMessage = null;
        _allSessionsSummary = null;
        LastOutputTime = DateTime.UtcNow; // Reset so stall monitor measures from this call's start
    }

    /// <summary>
    /// Sends the agent message with timeout enforcement and returns the result.
    /// Builds message parts (text + optional images), posts to the session, and
    /// processes the response. On timeout, aborts the session best-effort.
    /// </summary>
    private async Task<AgentResult> SendMessageWithTimeoutAsync(
        AgentRequest request,
        string sessionId,
        string workspacePath,
        SseCallState sseState,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        return await TimeoutHelper.ExecuteWithTimeoutAsync(
            request.Timeout, ct,
            async linkedCt =>
            {
                using var client = CreateDirectoryClientForPath(workspacePath);

                var parts = BuildTextPart(request.Prompt);
                await AppendImagePartsAsync(parts, request.ImagePaths, linkedCt);

                var messageRequest = new SendMessageRequest
                {
                    Parts = parts,
                    // The configured provider/model; null leaves it to the server's config.
                    Model = OpenCodeModelRef.Parse(_model)
                };

                _logger.Debug("POST /session/{SessionId}/message", sessionId);
                var response = await client.PostAsJsonAsync(
                    $"/session/{sessionId}/message", messageRequest, OpenCodeJson.JsonOptions, linkedCt);

                if (!response.IsSuccessStatusCode)
                    return await HandleHttpErrorResponseAsync(response, sessionId, workspacePath);

                return await ParseAndEmitResponseAsync(response, sseState, onOutputLine);
            },
            async () =>
            {
                await AbortBestEffortAsync(sessionId, workspacePath);
                return new AgentResult
                {
                    ExitCode = ExitCodes.Timeout,
                    OutputLines = ["Execution timed out"]
                };
            });
    }

    /// <summary>Builds the initial message parts list containing only the text prompt.</summary>
    private static List<MessagePart> BuildTextPart(string prompt)
        => [new() { Type = "text", Text = prompt }];

    /// <summary>
    /// Appends image file parts to an existing parts list.
    /// Failures per image are logged and skipped; processing continues with remaining images.
    /// </summary>
    private async Task AppendImagePartsAsync(List<MessagePart> parts, IReadOnlyList<string>? imagePaths, CancellationToken ct)
    {
        if (imagePaths is not { Count: > 0 })
            return;

        foreach (var imagePath in imagePaths)
        {
            try
            {
                byte[] bytes;
                try
                {
                    bytes = ImageResizer.DownscaleIfNeeded(imagePath);
                }
                catch
                {
                    // Fallback to raw bytes if resizer fails (e.g., NetVips unavailable)
                    bytes = await File.ReadAllBytesAsync(imagePath, ct);
                }
                var mime = GetMimeFromExtension(Path.GetExtension(imagePath));
                var base64 = Convert.ToBase64String(bytes);
                parts.Add(new MessagePart
                {
                    Type = "file",
                    Mime = mime,
                    Url = $"data:{mime};base64,{base64}",
                    Filename = Path.GetFileName(imagePath)
                });
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to encode image {Path} as file part", imagePath);
            }
        }
    }

    /// <summary>
    /// Handles a non-success HTTP response from the message endpoint.
    /// Evicts stale cached sessions on 404/410 and returns a failure result.
    /// </summary>
    private async Task<AgentResult> HandleHttpErrorResponseAsync(
        HttpResponseMessage response, string sessionId, string workspacePath)
    {
        // 404/410: session no longer exists (e.g., opencode server restarted).
        // Evict the stale cached session and let the caller retry via the pipeline retry logic.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _logger.Warning("Session {SessionId} not found on server (HTTP {Status}) — evicting from cache",
                sessionId, (int)response.StatusCode);
            _sessionByWorkspace.TryRemove(workspacePath, out _);
            if (_lastKnownSessionId == sessionId)
                _lastKnownSessionId = null;
        }

        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        // Classify provider-side transient and permanent failures so callers (e.g. the QG retry
        // loop) can distinguish them from code-level failures without re-parsing OutputLines text.
        // 404/410 are handled above and fall through with ErrorCategory.None (the default).
        var category = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => AgentErrorCategory.ProviderRateLimit,   // 429
            HttpStatusCode.ServiceUnavailable => AgentErrorCategory.ProviderOverload,    // 503
            HttpStatusCode.Unauthorized => AgentErrorCategory.PermanentAuthFailure, // 401
            HttpStatusCode.Forbidden => AgentErrorCategory.PermanentAuthFailure, // 403
            _ => AgentErrorCategory.None
        };

        return new AgentResult
        {
            ExitCode = ExitCodes.GeneralFailure,
            OutputLines = [$"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}"],
            ErrorCategory = category
        };
    }

    /// <summary>
    /// Reads and parses the message HTTP response, emitting the text parts SSE did not already
    /// stream. A turn that failed (bad key, quota, exhausted retries) still answers HTTP 200, with
    /// the error on the assistant message.
    /// </summary>
    private async Task<AgentResult> ParseAndEmitResponseAsync(
        HttpResponseMessage response, SseCallState sseState, Action<string>? onOutputLine)
    {
        var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
        SendMessageResponse? messageResponse;
        try
        {
            messageResponse = JsonSerializer.Deserialize<SendMessageResponse>(json, OpenCodeJson.JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.Debug(ex, "Malformed JSON response: {RawResponse}", json[..Math.Min(json.Length, 500)]);
            return new AgentResult
            {
                ExitCode = ExitCodes.GeneralFailure,
                OutputLines = [$"JSON parse error ({ex.GetType().Name}): {json[..Math.Min(json.Length, 500)]}"]
            };
        }

        var textParts = messageResponse?.Parts
            .Where(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var outputLines = string.Join("\n", textParts.Select(p => p.Text ?? string.Empty))
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => StripAnsiEscapes(line))
            .ToList();

        // Dedup by part: emit only the text parts the SSE stream did not already show.
        if (onOutputLine is not null)
        {
            foreach (var part in textParts.Where(p => p.Id is null || sseState.EmittedTextPartIds.TryAdd(p.Id, 0)))
            {
                foreach (var line in (part.Text ?? "").ReplaceLineEndings("\n").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                    onOutputLine(StripAnsiEscapes(line));
            }
        }

        if (messageResponse?.Info?.Error is { } error)
        {
            var message = $"✖ OpenCode {error.Name ?? "error"}: {error.Data?.Message ?? "no message"}";
            _logger.Warning("OpenCode turn failed: {Error}", message);
            onOutputLine?.Invoke(StripAnsiEscapes(message));
            outputLines.Add(message);
            return new AgentResult
            {
                ExitCode = ExitCodes.GeneralFailure,
                OutputLines = outputLines,
                ErrorCategory = ClassifyError(error)
            };
        }

        return new AgentResult
        {
            ExitCode = ExitCodes.Success,
            OutputLines = outputLines
        };
    }

    /// <summary>Maps an OpenCode error on the assistant message to a provider-side failure category.</summary>
    internal static AgentErrorCategory ClassifyError(OpenCodeError error)
    {
        if (error.Name == "ProviderAuthError")
            return AgentErrorCategory.PermanentAuthFailure;
        return error.Data?.StatusCode switch
        {
            401 or 403 => AgentErrorCategory.PermanentAuthFailure,
            429 => AgentErrorCategory.ProviderRateLimit,
            500 or 502 or 503 or 529 => AgentErrorCategory.ProviderOverload,
            _ => AgentErrorCategory.None
        };
    }

    public async Task KillAsync()
    {
        // Abort every session with a call in progress (isolated calls included), plus the workspace
        // sessions and the last one used, in case a call is between requests.
        var sessions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (workspacePath, sessionId) in _sessionByWorkspace)
            sessions[sessionId] = workspacePath;
        if (_lastKnownSessionId is { } lastKnown)
            sessions.TryAdd(lastKnown, null);
        foreach (var (sessionId, workspacePath) in _activeSessions)
            sessions[sessionId] = workspacePath;

        foreach (var (sessionId, workspacePath) in sessions)
        {
            _logger.Debug("POST /session/{SessionId}/abort", sessionId);
            await AbortBestEffortAsync(sessionId, workspacePath);
        }
    }

    public async Task ValidateAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        using var client = CreateDirectoryClient();
        var serverUrl = client.BaseAddress?.ToString() ?? AgentDefaults.OpenCodeBaseUrl;

        try
        {
            _logger.Debug("GET /global/health");
            var response = await client.GetAsync("/global/health", timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                throw new InvalidOperationException(
                    $"OpenCode server at {serverUrl} returned unhealthy response: HTTP {(int)response.StatusCode} — {body[..Math.Min(body.Length, 500)]}");
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var health = System.Text.Json.JsonSerializer.Deserialize<HealthResponse>(json, OpenCodeJson.JsonOptions);

            if (health is not { Healthy: true })
            {
                throw new InvalidOperationException(
                    $"OpenCode server at {serverUrl} is not healthy: response indicates unhealthy state.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate caller cancellation
        }
        catch (OperationCanceledException)
        {
            // TODO (WARNING — Correctness): Sonar S6667 was reported here against _logger.Error(...). Verify in SonarCloud
            // that the suppression on this line actually matches the flagged finding — Sonar sometimes reports Error-level
            // calls differently to Warning-level ones and the NOSONAR may need to move if the rule is flagged on a different line.
            _logger.Error("OpenCode server at {ServerUrl} did not respond within 10 seconds (timeout)", serverUrl); // NOSONAR S6667 — expected timeout; the message says so
            throw new InvalidOperationException(
                $"OpenCode server at {serverUrl} did not respond within 10 seconds (timeout).");
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(ex, "OpenCode server at {ServerUrl} is unreachable: {Message}", serverUrl, ex.Message);
            throw new InvalidOperationException(
                $"OpenCode server at {serverUrl} is unreachable: {ex.Message}", ex);
        }
        catch (InvalidOperationException)
        {
            throw; // re-throw our own exceptions
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "OpenCode server at {ServerUrl} health check failed: {Message}", serverUrl, ex.Message);
            throw new InvalidOperationException(
                $"OpenCode server at {serverUrl} health check failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>The workspace's main conversation; the last session used when the workspace has none.</remarks>
    public Task<string?> GetLatestSessionIdAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        return Task.FromResult(_sessionByWorkspace.TryGetValue(Path.GetFullPath(workspacePath), out var sessionId)
            ? sessionId
            : _lastKnownSessionId);
    }

    public ValueTask DisposeAsync()
    {
        _lastKnownSessionId = null;
        _sessionByWorkspace.Clear();
        return ValueTask.CompletedTask;
    }

    // ── Internal helpers ────────────────────────────────────────────────

    /// <summary>
    /// Creates an HttpClient without a workspace-specific directory header.
    /// Used only for global operations (health check, session status polling).
    /// For workspace-scoped operations, always use <see cref="CreateDirectoryClientForPath"/>.
    /// </summary>
    private HttpClient CreateDirectoryClient()
    {
        var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
        if (_baseUrl is not null)
            client.BaseAddress = _baseUrl;
        return client;
    }

    /// <summary>
    /// Creates an HttpClient with the x-opencode-directory header set to an explicit path. OpenCode
    /// keeps one instance per directory: sessions, events, status and config are all scoped by it.
    /// </summary>
    private HttpClient CreateDirectoryClientForPath(string absoluteWorkspacePath)
    {
        var client = CreateDirectoryClient();
        client.DefaultRequestHeaders.Add("x-opencode-directory", absoluteWorkspacePath);
        return client;
    }

    private async Task AbortBestEffortAsync(string sessionId, string? workspacePath = null)
    {
        try
        {
            using var client = workspacePath is not null
                ? CreateDirectoryClientForPath(workspacePath)
                : CreateDirectoryClient();
            await client.PostAsync($"/session/{sessionId}/abort", null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Best-effort abort failed for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Queries the session for token usage, computes the delta from last known values,
    /// logs it, and returns the delta as (TokenUsage?, decimal? Cost).
    /// Best-effort — failures return (null, null).
    /// </summary>
    private async Task<(TokenUsage? Usage, decimal? Cost)> CaptureSessionTokenDeltaAsync(string sessionId, string? workspacePath = null)
    {
        try
        {
            using var client = workspacePath is not null
                ? CreateDirectoryClientForPath(workspacePath)
                : CreateDirectoryClient();
            var response = await client.GetAsync($"/session/{sessionId}", CancellationToken.None);
            if (!response.IsSuccessStatusCode) return (null, null);

            var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var session = System.Text.Json.JsonSerializer.Deserialize<SessionDetailResponse>(json, OpenCodeJson.JsonOptions);
            if (session?.Tokens is null) return (null, null);

            var t = session.Tokens;
            var currentInput = t.Input;
            var currentOutput = t.Output;
            var currentReasoning = t.Reasoning;
            var currentCacheRead = t.Cache?.Read ?? 0;
            var currentCacheWrite = t.Cache?.Write ?? 0;
            var currentCost = session.Cost;

            // Compute delta from last known values
            long deltaInput = currentInput, deltaOutput = currentOutput, deltaReasoning = currentReasoning;
            long deltaCacheRead = currentCacheRead, deltaCacheWrite = currentCacheWrite;
            double deltaCost = currentCost;

            if (_lastSessionTokens.TryGetValue(sessionId, out var last))
            {
                deltaInput = currentInput - last.Input;
                deltaOutput = currentOutput - last.Output;
                deltaReasoning = currentReasoning - last.Reasoning;
                deltaCacheRead = currentCacheRead - last.CacheRead;
                deltaCacheWrite = currentCacheWrite - last.CacheWrite;
                deltaCost = currentCost - last.Cost;
            }

            // Store current cumulative values for next delta calculation
            _lastSessionTokens[sessionId] = (currentInput, currentOutput, currentReasoning, currentCacheRead, currentCacheWrite, currentCost);

            var usage = new TokenUsage
            {
                InputTokens = deltaInput,
                OutputTokens = deltaOutput,
                ReasoningTokens = deltaReasoning,
                CacheReadTokens = deltaCacheRead,
                CacheWriteTokens = deltaCacheWrite
            };

            // Cost is null when OpenCode reports 0 (unknown pricing)
            decimal? cost = deltaCost > 0 ? (decimal)deltaCost : null;

            _logger.Information(
                "Session {SessionId} token delta: input={Input}, output={Output}, reasoning={Reasoning}, cache_read={CacheRead}, cache_write={CacheWrite}, total={Total}, cost=${Cost:F4}",
                sessionId, deltaInput, deltaOutput, deltaReasoning, deltaCacheRead, deltaCacheWrite,
                usage.TotalTokens, deltaCost);

            return (usage, cost);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to capture token delta for session {SessionId}", sessionId);
            return (null, null);
        }
    }

    /// <summary>
    /// Strips ANSI escape sequences (CSI codes, OSC sequences, color codes) from output strings.
    /// Delegates to <see cref="KiroCliLib.Core.AnsiStripper.Strip"/> with null/empty guard.
    /// </summary>
    internal static string StripAnsiEscapes(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        return KiroCliLib.Core.AnsiStripper.Strip(input);
    }

    /// <summary>
    /// Maps a file extension to its MIME type for image file parts.
    /// </summary>
    internal static string GetMimeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "application/octet-stream"
    };
}
