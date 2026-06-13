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
    private volatile string? _currentSessionId;
    private volatile string? _currentSessionWorkspacePath;
    private volatile bool _isExecuting;
    private volatile bool _sseEmittedAssistantContent;
    private long _lastOutputTimeTicks; // Interlocked access for DateTime
    private volatile string? _sessionStatus; // "idle", "busy", "retry"
    private volatile string? _sessionStatusMessage; // Error/retry message from session.status event
    private volatile string? _allSessionsSummary; // Cached summary from polling GET /session/status
    private CancellationTokenSource? _sessionStatusPollCts; // Controls the background polling loop
    private readonly Dictionary<string, (long Input, long Output, long Reasoning, long CacheRead, long CacheWrite, double Cost)> _lastSessionTokens = new();

    public AgentProviderType ProviderType => AgentProviderType.OpenCode;

    /// <inheritdoc />
    public string McpConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "mcp.json");

    /// <inheritdoc />
    public bool SupportsParallelExecution => true;

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
            IsExecuting = _isExecuting,
            ProcessId = null,
            IsProcessAlive = null,
            LastOutputTime = LastOutputTime,
            SessionStatus = _sessionStatus,
            SessionStatusMessage = _sessionStatusMessage,
            AllSessionsSummary = _allSessionsSummary
        };
    }

    public async Task EnsureSessionAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        try
        {
            var absolutePath = Path.GetFullPath(workspacePath);

            if (_currentSessionId is not null)
            {
                // If workspace path changed, we need a new session regardless
                if (_currentSessionWorkspacePath != null && _currentSessionWorkspacePath != absolutePath)
                {
                    _logger.Debug("Workspace path changed from {Old} to {New}, creating new session",
                        _currentSessionWorkspacePath, absolutePath);
                    _currentSessionId = null;
                    _currentSessionWorkspacePath = null;
                }
                else
                {
                    // Validate existing session
                    var validated = await ValidateExistingSessionAsync(_currentSessionId, ct);
                    if (validated)
                        return;
                }

                // Session no longer valid — create a new one
            }

            // Create a new session
            await CreateNewSessionAsync(workspacePath, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to ensure OpenCode session for workspace {WorkspacePath}", workspacePath);
        }
    }

    private async Task<bool> ValidateExistingSessionAsync(string sessionId, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);

        try
        {
            var response = await client.GetAsync($"/session/{sessionId}", ct);

            if (response.IsSuccessStatusCode)
                return true;

            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;

            // Other error status — treat as invalid
            return false;
        }
        catch (Exception ex)
        {
            // Network error during validation — keep existing session (don't discard it)
            _logger.Warning(ex, "Failed to validate existing OpenCode session {SessionId}", sessionId);
            return true;
        }
    }

    private async Task CreateNewSessionAsync(string workspacePath, CancellationToken ct)
    {
        using var client = CreateDirectoryClient();

        // OpenCode requires an absolute path for the session directory.
        // Consolidation jobs pass relative paths (e.g., "./workspaces/consolidation/{guid}/refactoring")
        // which would otherwise resolve to the wrong directory on the server.
        var absolutePath = Path.GetFullPath(workspacePath);
        var title = Path.GetFileName(absolutePath) ?? absolutePath;

        // The Path field is kept for backward compatibility, but the x-opencode-directory
        // header (set by CreateDirectoryClient) is the primary mechanism for scoping the
        // session to the correct workspace directory.
        var request = new CreateSessionRequest { Title = title, Path = absolutePath };

        var response = await client.PostAsJsonAsync("/session", request, OpenCodeJson.JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(OpenCodeJson.JsonOptions, ct);
        if (result is not null)
        {
            _currentSessionId = result.Id;
            _currentSessionWorkspacePath = absolutePath;
        }
    }

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        _isExecuting = true;
        _sseEmittedAssistantContent = false;
        _sessionStatus = null;
        _sessionStatusMessage = null;
        _allSessionsSummary = null;
        LastOutputTime = DateTime.UtcNow; // Reset so stall monitor measures from this call's start

        // Start background session-status polling (provides observability into subagent retries)
        _sessionStatusPollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pollTask = PollAllSessionStatusesAsync(_sessionStatusPollCts.Token);

        try
        {
            // 1. Session selection
            var sessionId = await ResolveSessionIdAsync(request, ct);
            if (sessionId is null)
            {
                return new AgentResult
                {
                    ExitCode = ExitCodes.GeneralFailure,
                    OutputLines = ["Failed to establish OpenCode session"]
                };
            }

            // 2. Timeout enforcement
            var sseCts = new CancellationTokenSource();

            // 3. Start SSE reader (always — needed for permission auto-approval)
            var sseTask = ConnectAndProcessSseAsync(sessionId, onOutputLine, sseCts.Token);

            // 4. Send message (synchronous — blocks until agent finishes)
            AgentResult result;
            try
            {
                result = await TimeoutHelper.ExecuteWithTimeoutAsync(
                    request.Timeout, ct,
                    async linkedCt =>
                    {
                        using var client = CreateDirectoryClient();
                        var messageRequest = new SendMessageRequest
                        {
                            Parts = [new MessagePart { Type = "text", Text = request.Prompt }],
                            Model = null // Model is configured server-side via OPENCODE_CONFIG_CONTENT
                        };

                        _logger.Debug("POST /session/{SessionId}/message", sessionId);
                        var response = await client.PostAsJsonAsync(
                            $"/session/{sessionId}/message", messageRequest, OpenCodeJson.JsonOptions, linkedCt);

                        if (!response.IsSuccessStatusCode)
                        {
                            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                            return new AgentResult
                            {
                                ExitCode = ExitCodes.GeneralFailure,
                                OutputLines = [$"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 1000)]}"]
                            };
                        }

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
                        // SSE did not already stream assistant content. When SSE is active,
                        // message.part.updated events provide real-time [assistant] prefixed
                        // lines — emitting the HTTP response too would duplicate the content.
                        // The HTTP response fallback ensures output still appears when SSE
                        // fails to connect or drops before streaming any assistant text.
                        if (onOutputLine is not null && !_sseEmittedAssistantContent)
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
                    },
                    async () =>
                    {
                        await AbortBestEffortAsync(sessionId);
                        return new AgentResult
                        {
                            ExitCode = ExitCodes.Timeout,
                            OutputLines = ["Execution timed out"]
                        };
                    });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await AbortBestEffortAsync(sessionId);
                _ = await CaptureSessionTokenDeltaAsync(sessionId);
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
                // Allow a brief window for late-arriving SSE events (e.g., final
                // message.part.updated) to be processed before tearing down the stream.
                try { await Task.Delay(500, CancellationToken.None); } catch { }
                sseCts.Cancel();
                try { await sseTask.ConfigureAwait(false); } catch { /* expected cancellation */ }
                sseCts.Dispose();
            }

            // Capture token usage delta on all paths (success, timeout, error)
            var (usage, cost) = await CaptureSessionTokenDeltaAsync(sessionId);
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
            _isExecuting = false;
            // Stop session-status polling
            try { _sessionStatusPollCts?.Cancel(); } catch { }
            try { await pollTask.ConfigureAwait(false); } catch { }
            _sessionStatusPollCts?.Dispose();
            _sessionStatusPollCts = null;
        }
    }

    public async Task KillAsync()
    {
        var sessionId = _currentSessionId;
        if (sessionId is null)
            return;

        try
        {
            _logger.Debug("POST /session/{SessionId}/abort", sessionId);
            using var client = CreateDirectoryClient();
            await client.PostAsync($"/session/{sessionId}/abort", null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to abort OpenCode session {SessionId}", sessionId);
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
            throw new InvalidOperationException(
                $"OpenCode server at {serverUrl} did not respond within 10 seconds (timeout).");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"OpenCode server at {serverUrl} is unreachable: {ex.Message}", ex);
        }
        catch (InvalidOperationException)
        {
            throw; // re-throw our own exceptions
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"OpenCode server at {serverUrl} health check failed: {ex.Message}", ex);
        }
    }

    public Task<string?> GetLatestSessionIdAsync(string workspacePath, CancellationToken ct)
    {
        return Task.FromResult(_currentSessionId);
    }

    public ValueTask DisposeAsync()
    {
        _currentSessionId = null;
        _currentSessionWorkspacePath = null;
        return ValueTask.CompletedTask;
    }

    // ── IOpenCodeDiffProvider ───────────────────────────────────────────

    public async Task<IReadOnlyList<FileChangeSummary>> GetSessionDiffAsync(CancellationToken ct)
    {
        var sessionId = _currentSessionId;
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
    /// Creates an HttpClient with the x-opencode-directory header set to the current workspace path.
    /// OpenCode uses this header to scope all operations (file reads, shell commands, sessions)
    /// to the specified directory instead of the server's CWD.
    /// </summary>
    private HttpClient CreateDirectoryClient()
    {
        var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
        if (_currentSessionWorkspacePath is not null)
        {
            client.DefaultRequestHeaders.Add("x-opencode-directory", _currentSessionWorkspacePath);
        }
        return client;
    }

    private async Task<string?> ResolveSessionIdAsync(AgentRequest request, CancellationToken ct)
    {
        // ResumeSessionId takes precedence
        if (!string.IsNullOrEmpty(request.ResumeSessionId))
        {
            _currentSessionId = request.ResumeSessionId;
            return request.ResumeSessionId;
        }

        // UseResume = true → reuse existing if available
        if (request.UseResume && _currentSessionId is not null)
            return _currentSessionId;

        // UseResume=false → discard old session to force creation of a new one
        if (!request.UseResume)
            _currentSessionId = null;

        // Create new session (or no existing session when UseResume=true)
        await EnsureSessionAsync(request.WorkspacePath, ct);
        return _currentSessionId;
    }

    private async Task AbortBestEffortAsync(string sessionId)
    {
        try
        {
            using var client = CreateDirectoryClient();
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
                // Must use CreateDirectoryClient() to include x-opencode-directory header,
                // otherwise the server returns statuses scoped to the wrong workspace instance.
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
            catch
            {
                // Best-effort — don't fail the execution over diagnostic polling
            }

            try { await Task.Delay(10_000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Queries the session for token usage, computes the delta from last known values,
    /// logs it, and returns the delta as (TokenUsage?, decimal? Cost).
    /// Best-effort — failures return (null, null).
    /// </summary>
    private async Task<(TokenUsage? Usage, decimal? Cost)> CaptureSessionTokenDeltaAsync(string sessionId)
    {
        try
        {
            using var client = CreateDirectoryClient();
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
    internal async Task ConnectAndProcessSseAsync(string sessionId, Action<string>? onOutputLine, CancellationToken ct)
    {
        using var client = CreateDirectoryClient();

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
                switch (sseEvent.Type)
                {
                    case "message.part.updated":
                        LastOutputTime = DateTime.UtcNow;
                        _sseEmittedAssistantContent = true;
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
                        await AutoApprovePermissionAsync(sessionId, sseEvent.PermissionId, ct);
                        break;

                    case "session.idle":
                        // Signal completion — informational only, sync message response is primary
                        _sessionStatus = "idle";
                        _sessionStatusMessage = null;
                        break;

                    case "session.status":
                        // Track session status for health reporting.
                        // The "retry" status indicates an LLM provider error with details.
                        if (sseEvent.Status is not null)
                        {
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
                        break;

                    default:
                        // Discard metadata events (session.status, session.updated,
                        // session.diff, message.updated, etc.)
                        break;
                }
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
    /// Auto-approves a permission request by calling POST /session/:id/permissions/:permissionId.
    /// Best-effort — logs warning on failure without rethrowing.
    /// </summary>
    private async Task AutoApprovePermissionAsync(string sessionId, string? permissionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(permissionId))
            return;

        try
        {
            _logger.Debug("POST /session/{SessionId}/permissions/{PermissionId} (auto-approve)", sessionId, permissionId);
            using var client = CreateDirectoryClient();
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
                    config = new McpHttpConfig { Url = server.Url ?? string.Empty };
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
}
