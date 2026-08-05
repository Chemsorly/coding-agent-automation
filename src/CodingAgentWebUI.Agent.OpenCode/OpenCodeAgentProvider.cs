using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using KiroCliLib.Core;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.OpenCode;

/// <summary>
/// Agent provider that communicates with an OpenCode server via localhost HTTP API.
/// Implements IAgentProvider for pipeline integration and IOpenCodeDiffProvider for
/// diff retrieval. Does not spawn processes — uses IHttpClientFactory named client.
/// </summary>
public sealed class OpenCodeAgentProvider : IAgentProvider, IOpenCodeDiffProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string? _model;
    private long _lastOutputTimeTicks; // Interlocked access for DateTime
    private int _activeExecutionCount; // Tracks concurrent executions for correct IsExecuting
    private volatile string? _sessionStatus; // "idle", "busy", "retry"
    private volatile string? _sessionStatusMessage; // Error/retry message from session.status event
    private volatile string? _allSessionsSummary; // Cached summary from polling GET /session/status
    private CancellationTokenSource? _sessionStatusPollCts; // Controls the background polling loop
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
    public string McpConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "mcp.json");

    /// <inheritdoc />
    public bool SupportsParallelExecution => true;

    /// <inheritdoc />
    public bool SupportsVisionInput => !IsTextOnlyModel(_model);

    /// <inheritdoc />
    public IReadOnlyList<string> PipelineInjectedPaths { get; } = ["AGENTS.md"];

    public OpenCodeAgentProvider(
        IHttpClientFactory httpClientFactory,
        ILogger? logger = null,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? Serilog.Log.Logger;
        _model = model;
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

    public Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        // No-op: sessions are now created per-ExecuteAsync call based on the workspace path.
        // The opencode server manages session lifecycle internally.
        return Task.CompletedTask;
    }

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        Interlocked.Increment(ref _activeExecutionCount);
        ResetExecutionState();

        var sseEmitted = false;
        // NOTE: Per-call local variable for SSE dedup — avoids races with concurrent parallel calls.

        var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sessionStatusPollCts = pollCts;
        var pollTask = PollAllSessionStatusesAsync(pollCts.Token);

        try
        {
            // 1. Session selection — always create/resolve per workspace path (stateless)
            var workspacePath = Path.GetFullPath(request.WorkspacePath);
            var sessionId = await ResolveSessionIdAsync(request, ct);
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

            // 2. Timeout enforcement
            var sseCts = new CancellationTokenSource();

            // 3. Start SSE reader (always — needed for permission auto-approval)
            var sseTask = ConnectAndProcessSseAsync(sessionId, onOutputLine, sseCts.Token,
                workspacePath, _ => { sseEmitted = true; });

            // 4. Send message (synchronous — blocks until agent finishes)
            AgentResult result;
            try
            {
                result = await SendMessageWithTimeoutAsync(
                    request, sessionId, workspacePath, sseEmitted, onOutputLine, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await AbortBestEffortAsync(sessionId, workspacePath);
                _ = await CaptureSessionTokenDeltaAsync(sessionId, workspacePath);
                throw; // finally block handles SSE cleanup
            }
            catch (OperationCanceledException ex)
            {
                result = new AgentResult
                {
                    ExitCode = ExitCodes.GeneralFailure,
                    OutputLines = [$"Operation cancelled unexpectedly: {ex.GetType().Name}: {ex.Message}"]
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
                Cost = cost
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeExecutionCount);
            // Stop polling
            try { await pollCts.CancelAsync(); } catch (OperationCanceledException) { }
            try { await pollTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            pollCts.Dispose();
            _sessionStatusPollCts = null;
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
    /// Tears down the SSE reader by waiting briefly for late-arriving events,
    /// then cancelling and awaiting the reader task.
    /// </summary>
    private static async Task TearDownSseAsync(CancellationTokenSource sseCts, Task sseTask)
    {
        // Allow a brief window for late-arriving SSE events (e.g., final
        // message.part.updated) to be processed before tearing down the stream.
        try { await Task.Delay(500, CancellationToken.None); } catch { }
        await sseCts.CancelAsync();
        try { await sseTask.ConfigureAwait(false); } catch { /* expected cancellation */ }
        sseCts.Dispose();
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
        bool sseEmitted,
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
                    Model = null // Model is configured server-side via OPENCODE_CONFIG_CONTENT
                };

                _logger.Debug("POST /session/{SessionId}/message", sessionId);
                var response = await client.PostAsJsonAsync(
                    $"/session/{sessionId}/message", messageRequest, OpenCodeJson.JsonOptions, linkedCt);

                if (!response.IsSuccessStatusCode)
                    return await HandleHttpErrorResponseAsync(response, sessionId, workspacePath);

                return await ParseAndEmitResponseAsync(response, sseEmitted, onOutputLine);
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
        return new AgentResult
        {
            ExitCode = ExitCodes.GeneralFailure,
            OutputLines = [$"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}"]
        };
    }

    /// <summary>
    /// Reads and parses the message HTTP response, emitting output lines if SSE did not
    /// already stream the assistant content for this call.
    /// </summary>
    private async Task<AgentResult> ParseAndEmitResponseAsync(
        HttpResponseMessage response, bool sseEmitted, Action<string>? onOutputLine)
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

        // Extract text parts, concatenate, split into lines
        var textParts = messageResponse?.Parts
            .Where(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Text ?? string.Empty)
            ?? [];
        var combinedText = string.Join("\n", textParts);
        var outputLines = combinedText.Split('\n')
            .Select(line => StripAnsiEscapes(line))
            .ToList();

        // Dedup: Only emit HTTP response lines to the output callback if
        // SSE did not already stream assistant content for this call.
        // Use the local `sseEmitted` variable — not the shared _sseEmittedAssistantContent
        // which can be set by concurrent parallel calls.
        if (onOutputLine is not null && !sseEmitted)
        {
            foreach (var line in outputLines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    onOutputLine(line);
            }
        }

        return new AgentResult
        {
            ExitCode = ExitCodes.Success,
            OutputLines = outputLines
        };
    }

    public async Task KillAsync()
    {
        // Abort all active workspace sessions (parallel execution may have multiple)
        var sessionIds = _sessionByWorkspace.Values.Distinct().ToList();

        // Also include _lastKnownSessionId in case it's not in the workspace cache
        // (e.g., set via explicit ResumeSessionId)
        var lastKnown = _lastKnownSessionId;
        if (lastKnown is not null && !sessionIds.Contains(lastKnown))
            sessionIds.Add(lastKnown);

        if (sessionIds.Count == 0)
            return;

        foreach (var sessionId in sessionIds)
        {
            try
            {
                _logger.Debug("POST /session/{SessionId}/abort", sessionId);
                using var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
                await client.PostAsync($"/session/{sessionId}/abort", null);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to abort OpenCode session {SessionId}", sessionId);
            }
        }
    }

    public async Task ValidateAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        using var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
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
            _logger.Error("OpenCode server at {ServerUrl} did not respond within 10 seconds (timeout)", serverUrl);
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

    public Task<string?> GetLatestSessionIdAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        return Task.FromResult(_lastKnownSessionId);
    }

    public ValueTask DisposeAsync()
    {
        _lastKnownSessionId = null;
        _sessionByWorkspace.Clear();
        return ValueTask.CompletedTask;
    }

    // ── IOpenCodeDiffProvider ───────────────────────────────────────────

    public async Task<IReadOnlyList<FileChangeSummary>> GetSessionDiffAsync(CancellationToken ct)
    {
        var sessionId = _lastKnownSessionId;
        if (sessionId is null)
            return Array.Empty<FileChangeSummary>();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            using var client = CreateDirectoryClient();
            var response = await client.GetAsync($"/session/{sessionId}/diff", timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            var diffs = await response.Content.ReadFromJsonAsync<FileDiff[]>(OpenCodeJson.JsonOptions, timeoutCts.Token);
            if (diffs is null || diffs.Length == 0)
                return Array.Empty<FileChangeSummary>();

            var results = new List<FileChangeSummary>(diffs.Length);
            foreach (var fileDiff in diffs)
            {
                var status = MapDiffStatus(fileDiff.Status);
                results.Add(new FileChangeSummary(status, fileDiff.Path, fileDiff.LinesAdded, fileDiff.LinesDeleted));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to retrieve diff for session {SessionId}", sessionId);
            return Array.Empty<FileChangeSummary>();
        }
    }

    private static string MapDiffStatus(string? status)
    {
        if (string.Equals(status, "added", StringComparison.OrdinalIgnoreCase))
            return "Added";
        if (string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase))
            return "Deleted";
        return "Modified";
    }

    // ── Internal helpers ────────────────────────────────────────────────

    /// <summary>
    /// Creates an HttpClient without a workspace-specific directory header.
    /// Used only for global operations (health check, session status polling).
    /// For workspace-scoped operations, always use <see cref="CreateDirectoryClientForPath"/>.
    /// </summary>
    private HttpClient CreateDirectoryClient()
    {
        return _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
    }

    /// <summary>
    /// Creates an HttpClient with the x-opencode-directory header set to an explicit path.
    /// Used by isolated (parallel) calls that must not read shared <see cref="_currentSessionWorkspacePath"/>.
    /// </summary>
    private HttpClient CreateDirectoryClientForPath(string absoluteWorkspacePath)
    {
        var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
        client.DefaultRequestHeaders.Add("x-opencode-directory", absoluteWorkspacePath);
        return client;
    }

    private async Task<string?> ResolveSessionIdAsync(AgentRequest request, CancellationToken ct)
    {
        // ResumeSessionId takes precedence (explicit session targeting, e.g., adversarial review refinement)
        if (!string.IsNullOrEmpty(request.ResumeSessionId))
        {
            return request.ResumeSessionId;
        }

        var workspacePath = Path.GetFullPath(request.WorkspacePath);

        // UseResume=true within the same workspace → reuse the cached session for that workspace
        if (request.UseResume && _sessionByWorkspace.TryGetValue(workspacePath, out var cachedSessionId))
        {
            _logger.Debug("Reusing cached session {SessionId} for workspace {WorkspacePath}",
                cachedSessionId, workspacePath);
            return cachedSessionId;
        }

        // Create a fresh session for this workspace (UseResume=false, or no cached session yet)
        var sessionId = await CreateIsolatedSessionAsync(request.WorkspacePath, ct);
        if (sessionId is not null)
        {
            // Cache the session for this workspace so future UseResume=true calls reuse it
            _sessionByWorkspace[workspacePath] = sessionId;
            _lastKnownSessionId = sessionId;
        }
        return sessionId;
    }

    /// <summary>
    /// Creates a new session and returns the ID without writing to shared instance fields.
    /// Used for isolated (non-resume) calls to enable safe parallel execution.
    /// </summary>
    private async Task<string?> CreateIsolatedSessionAsync(string workspacePath, CancellationToken ct)
    {
        try
        {
            var absolutePath = Path.GetFullPath(workspacePath);
            var title = Path.GetFileName(absolutePath) ?? absolutePath;

            using var client = CreateDirectoryClientForPath(absolutePath);
            var request = new CreateSessionRequest { Title = title, Path = absolutePath };

            var response = await client.PostAsJsonAsync("/session", request, OpenCodeJson.JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(OpenCodeJson.JsonOptions, ct);
            if (result is not null)
            {
                _logger.Debug("Created isolated session {SessionId} for workspace {WorkspacePath}",
                    result.Id, absolutePath);
                return result.Id;
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Real cancellation — propagate
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to create isolated session for workspace {WorkspacePath}", workspacePath);
            return null;
        }
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
    /// Background loop that polls GET /session/status every 10s and caches a human-readable
    /// summary of all session statuses (including child/subagent sessions). This provides
    /// observability into subagent retries that don't surface on the parent session's SSE stream.
    /// </summary>
    private async Task PollAllSessionStatusesAsync(CancellationToken ct)
    {
        // Small initial delay to let the session start
        try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // GET /session/status returns all sessions globally — no directory header needed.
                using var client = CreateDirectoryClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                var response = await client.GetAsync("/session/status", timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    var statuses = JsonSerializer.Deserialize<Dictionary<string, SseSessionStatus>>(json, OpenCodeJson.JsonOptions);

                    if (statuses is not null && statuses.Count > 0)
                    {
                        var parts = new List<string>();
                        var retryCount = 0;
                        var busyCount = 0;
                        var idleCount = 0;

                        foreach (var (_, status) in statuses)
                        {
                            if (string.Equals(status.Type, "retry", StringComparison.OrdinalIgnoreCase))
                                retryCount++;
                            else if (string.Equals(status.Type, "busy", StringComparison.OrdinalIgnoreCase))
                                busyCount++;
                            else
                                idleCount++;
                        }

                        parts.Add($"{statuses.Count} total");
                        if (retryCount > 0) parts.Add($"{retryCount} retrying");
                        if (busyCount > 0) parts.Add($"{busyCount} busy");
                        if (idleCount > 0) parts.Add($"{idleCount} idle");

                        // Add retry details (first 3)
                        var retryDetails = statuses
                            .Where(kv => string.Equals(kv.Value.Type, "retry", StringComparison.OrdinalIgnoreCase))
                            .Take(3)
                            .Select(kv => $"attempt {kv.Value.Attempt}: {kv.Value.Message ?? "unknown"}")
                            .ToList();
                        if (retryDetails.Count > 0)
                            parts.Add($"detail: {string.Join("; ", retryDetails)}");

                        _allSessionsSummary = string.Join(", ", parts);
                    }
                    else
                    {
                        _allSessionsSummary = null;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable S108 // Intentional: diagnostic polling is best-effort; failures must not affect agent execution.
            catch
            {
                // Intentional: diagnostic polling is best-effort; failures must not affect agent execution.
            }
#pragma warning restore S108

            try { await Task.Delay(10_000, ct); } catch (OperationCanceledException) { break; }
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
    /// Connects to the SSE stream (GET /event) and processes events for the given session.
    /// Routes events to the onOutputLine callback and auto-approves permission requests.
    /// Logs a warning on unexpected disconnect; does not reconnect.
    /// </summary>
    internal async Task ConnectAndProcessSseAsync(string sessionId, Action<string>? onOutputLine, CancellationToken ct,
        string? workspacePath = null, Action<bool>? onSseEmitted = null)
    {
        using var client = workspacePath is not null
            ? CreateDirectoryClientForPath(workspacePath)
            : CreateDirectoryClient();

        try
        {
            // 5-second connection timeout — only applies to establishing the connection
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));

            _logger.Debug("GET /event (SSE stream for session {SessionId})", sessionId);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/event");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // After connection is established, use the original cancellation token (not the 5s timeout)
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break; // stream closed by server

                // SSE format: lines starting with "data:" contain JSON payload
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var json = line["data:".Length..].Trim();
                if (string.IsNullOrEmpty(json))
                    continue;

                SseEvent? sseEvent;
                try
                {
                    sseEvent = JsonSerializer.Deserialize<SseEvent>(json, OpenCodeJson.JsonOptions);
                }
                catch (JsonException)
                {
                    // Malformed SSE data line — skip
                    continue;
                }

                if (sseEvent is null)
                    continue;

                // Filter: only process events for the active session
                if (sseEvent.SessionId != sessionId)
                    continue;

                // Route events based on type — only update LastOutputTime on events
                // that represent meaningful progress (text output, tool calls, token streaming).
                // Metadata-only events (session.idle, session.status, session.updated,
                // session.diff, message.updated) are excluded so the stall monitor can
                // detect extended LLM thinking/reasoning phases where no visible output
                // is being produced.
                await ProcessSseEventAsync(sseEvent, sessionId, onOutputLine, onSseEmitted, ct, workspacePath);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on completion or caller cancellation — just return
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SSE stream disconnected unexpectedly");
        }
    }

    /// <summary>
    /// Routes a single SSE event to the appropriate handler based on its type.
    /// Updates LastOutputTime only for events that represent meaningful agent progress.
    /// </summary>
    private async Task ProcessSseEventAsync(
        SseEvent sseEvent,
        string sessionId,
        Action<string>? onOutputLine,
        Action<bool>? onSseEmitted,
        CancellationToken ct,
        string? workspacePath)
    {
        switch (sseEvent.Type)
        {
            case "message.part.updated":
                LastOutputTime = DateTime.UtcNow;
                onSseEmitted?.Invoke(true);
                onOutputLine?.Invoke(StripAnsiEscapes($"[assistant] {sseEvent.Part?.Text}"));
                break;

            case "tool.execute.before":
                LastOutputTime = DateTime.UtcNow;
                onOutputLine?.Invoke(StripAnsiEscapes($"[tool_call] {sseEvent.ToolName} {sseEvent.ToolArgs}"));
                break;

            case "tool.execute.after":
                LastOutputTime = DateTime.UtcNow;
                onOutputLine?.Invoke(StripAnsiEscapes($"[tool_result] {sseEvent.ToolResult}"));
                break;

            case "permission.updated":
                LastOutputTime = DateTime.UtcNow;
                await AutoApprovePermissionAsync(sessionId, sseEvent.PermissionId, ct, workspacePath);
                break;

            case "session.idle":
                // Signal completion — informational only, sync message response is primary
                _sessionStatus = "idle";
                _sessionStatusMessage = null;
                break;

            case "session.status":
                HandleSessionStatusEvent(sseEvent, sessionId, onOutputLine);
                break;

            default:
                // Discard metadata events (session.updated, session.diff, message.updated, etc.)
                break;
        }
    }

    /// <summary>
    /// Handles session.status SSE events by updating session status fields and
    /// logging/emitting retry details when the provider indicates a retry.
    /// </summary>
    private void HandleSessionStatusEvent(SseEvent sseEvent, string sessionId, Action<string>? onOutputLine)
    {
        if (sseEvent.Status is null)
            return;

        _sessionStatus = sseEvent.Status.Type;
        if (string.Equals(sseEvent.Status.Type, "retry", StringComparison.OrdinalIgnoreCase))
        {
            var retryMsg = sseEvent.Status.Message ?? "unknown error";
            var provider = sseEvent.Status.Action?.Provider;
            _sessionStatusMessage = provider is not null
                ? $"[{provider}] attempt {sseEvent.Status.Attempt}: {retryMsg}"
                : $"attempt {sseEvent.Status.Attempt}: {retryMsg}";
            _logger.Warning("Session {SessionId} retry status: {Message}", sessionId, _sessionStatusMessage);
            onOutputLine?.Invoke(StripAnsiEscapes($"[session.status] retry — {_sessionStatusMessage}"));
        }
        else
        {
            _sessionStatusMessage = null;
        }
    }

    /// <summary>
    /// Auto-approves a permission request by calling POST /session/:id/permissions/:permissionId.
    /// Best-effort — logs warning on failure without rethrowing.
    /// </summary>
    private async Task AutoApprovePermissionAsync(string sessionId, string? permissionId, CancellationToken ct, string? workspacePath = null)
    {
        if (string.IsNullOrEmpty(permissionId))
            return;

        try
        {
            _logger.Debug("POST /session/{SessionId}/permissions/{PermissionId} (auto-approve)", sessionId, permissionId);
            using var client = workspacePath is not null
                ? CreateDirectoryClientForPath(workspacePath)
                : CreateDirectoryClient();
            var body = new PermissionResponse { Response = "allow", Remember = true };
            await client.PostAsJsonAsync($"/session/{sessionId}/permissions/{permissionId}", body, OpenCodeJson.JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to auto-approve permission {PermissionId} for session {SessionId}", permissionId, sessionId);
        }
    }

    /// <summary>
    /// Environment variable keys that MUST NOT be passed to MCP server child processes.
    /// </summary>
    private static readonly HashSet<string> ExcludedEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        AgentDefaults.EnvOpenCodeServerPassword,
        AgentDefaults.EnvAnthropicApiKey,
        AgentDefaults.EnvOpenAiApiKey,
        AgentDefaults.EnvOpenRouterApiKey
    };

    internal async Task RegisterMcpServersAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken ct)
    {
        var enabledServers = servers.Where(s => !s.Disabled).ToList();

        foreach (var server in enabledServers)
        {
            try
            {
                object config;

                if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
                {
                    // TODO: Headers passed to the OpenCode API are not filtered through an equivalent of
                    // ExcludedEnvKeys. The stdio path explicitly strips sensitive keys (Anthropic, OpenAI,
                    // OpenRouter API keys, OpenCode server password) before forwarding env vars. The HTTP
                    // header path has no such guard — a user who sets e.g. Authorization=Bearer <ANTHROPIC_API_KEY>
                    // will have that value forwarded verbatim to the external HTTP MCP server. Consider applying
                    // a header-key filter analogous to ExcludedEnvKeys to prevent accidental credential leakage.
                    config = new McpHttpConfig
                    {
                        Url = server.Url ?? string.Empty,
                        Headers = server.Headers.Count > 0 ? server.Headers : null
                    };
                }
                else
                {
                    // stdio (default)
                    var filteredEnv = server.Env
                        .Where(kvp => !ExcludedEnvKeys.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    config = new McpStdioConfig
                    {
                        Command = server.Command ?? string.Empty,
                        Args = server.Args,
                        Env = filteredEnv
                    };
                }

                var request = new RegisterMcpRequest
                {
                    Name = server.Name,
                    Config = config
                };

                using var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
                var response = await client.PostAsJsonAsync("/mcp", request, OpenCodeJson.JsonOptions, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Failed to register MCP server {ServerName}", server.Name);
            }
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
    /// Determines if a model identifier refers to a text-only (non-vision) model.
    /// Returns false (not text-only) when model is null or empty (assume capable).
    /// </summary>
    internal static bool IsTextOnlyModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return false;

        return model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
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
