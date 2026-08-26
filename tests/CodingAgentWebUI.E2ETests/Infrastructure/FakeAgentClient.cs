using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Fake SignalR agent client that connects to the real AgentHub for multi-agent dispatch tests.
/// Simulates agent registration, job acceptance, step reporting, and completion.
/// Also handles consolidation job assignments for consolidation loop e2e tests.
/// </summary>
public sealed class FakeAgentClient : IAsyncDisposable
{
    private HubConnection? _connection;

    public string AgentId { get; }
    public string[] Labels { get; }

    // Observability
    public TaskCompletionSource<JobAssignmentMessage> JobAssigned { get; private set; } = new();
    public TaskCompletionSource<ConsolidationJobMessage> ConsolidationJobAssigned { get; private set; } = new();
    public TaskCompletionSource<ChatPromptMessage> ChatPromptAssigned { get; private set; } = new();
    public ConcurrentBag<string> ReceivedJobIds { get; } = new();
    public ConcurrentBag<string> ReceivedConsolidationJobIds { get; } = new();
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Resolved when a <c>CancelChat</c> hub message is received.
    /// Reset with <see cref="ResetCancelChatReceived"/> for multi-test reuse.
    /// </summary>
    public TaskCompletionSource CancelChatReceived { get; private set; } = new();

    public FakeAgentClient(string agentId, params string[] labels)
    {
        AgentId = agentId;
        Labels = labels;
    }

    /// <summary>
    /// Connects to the SignalR hub and registers as an agent.
    /// Registration is synchronous — by the time this method returns, the agent is
    /// in the registry with Idle status. No additional delay is needed before dispatch.
    /// </summary>
    /// <summary>
    /// Agents that are currently connected, keyed by agent id.
    ///
    /// <see cref="FakeJobController"/> needs to reach the object behind a registry entry so it can
    /// bootstrap it the way a pod bootstraps. Tests construct these directly, so there is no DI
    /// container to look them up in.
    /// </summary>
    private static readonly ConcurrentDictionary<string, FakeAgentClient> Connected = new(StringComparer.Ordinal);

    internal static bool TryGetConnected(string agentId, out FakeAgentClient agent)
        => Connected.TryGetValue(agentId, out agent!);

    /// <summary>The API base address this agent connected to, for its HTTP assignment fetch.</summary>
    private string? _serverAddress;
    private string? _apiKey;

    public async Task ConnectAsync(string serverAddress, string apiKey)
    {
        _serverAddress = serverAddress;
        _apiKey = apiKey;
        await BuildAndStartConnectionAsync(serverAddress, apiKey);

        // Register with the hub — InvokeAsync is request-response, so when this returns
        // the agent IS in the registry with Idle status. No Task.Delay needed.
        await _connection!.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = AgentId,
            Hostname = "fake-agent-host",
            Labels = Labels
        });

        Connected[AgentId] = this;
    }

    /// <summary>
    /// Bootstraps this agent onto a work item the way a Kubernetes work-item pod does:
    /// fetch the assignment over HTTP, then re-register on the hub declaring the active job.
    ///
    /// There is no <c>AssignJob</c> push in Kubernetes mode — Spec 041 removed the dispatch mode
    /// that sent one. Completing <see cref="JobAssigned"/> here keeps the existing tests' shape
    /// (dispatch, then the agent has its job) while routing through the real pull-based path.
    ///
    /// The re-registration is what sets <c>ActiveJobId</c> in the registry. Without it the
    /// <c>AgentAuthorizationFilter</c> rejects every <c>[RequiresActiveJob]</c> call that follows.
    /// </summary>
    internal async Task StartAssignedWorkItemAsync(Guid workItemId, CancellationToken ct = default)
    {
        if (_connection is null || _serverAddress is null) return;

        using var http = new HttpClient { BaseAddress = new Uri(_serverAddress) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DeriveKey(_apiKey!, AgentId));

        var response = await http.GetAsync(
            $"/api/work-items/{workItemId}/assignment?agentId={Uri.EscapeDataString(AgentId)}", ct);
        if (!response.IsSuccessStatusCode) return;

        var assignment = await response.Content.ReadFromJsonAsync<JobAssignmentMessage>(
            CodingAgentWebUI.Pipeline.PipelineJsonOptions.Default, ct);
        if (assignment is null) return;

        await _connection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = AgentId,
            Hostname = "fake-k8s-pod",
            Labels = Labels,
            ActiveJob = new ActiveJobState
            {
                RunId = assignment.JobId,
                IssueIdentifier = assignment.IssueIdentifier,
                IssueTitle = assignment.IssueDetail.Title,
                // Not carried on the assignment message; the pod knows it from its own env.
                IssueProviderConfigId = "issue-e2e",
                RepoProviderConfigId = assignment.RepoProviderConfigId,
                AgentProviderConfigId = assignment.AgentProviderConfigId,
                BrainProviderConfigId = assignment.BrainProviderConfigId,
                PipelineProviderConfigId = assignment.PipelineProviderConfigId,
                ResolvedProfileId = assignment.ResolvedProfileId,
                InitiatedBy = assignment.InitiatedBy,
                CurrentStep = PipelineStep.Created,
                StartedAt = DateTimeOffset.UtcNow,
                RunType = assignment.RunType,
                // Mirrors ActiveJobStateFactory.ResolveModelName. The model is not a field on the
                // assignment — the agent derives it by finding its own provider config among the
                // ones delivered with the job. Registration is the only channel that carries it
                // back: JobCompletionPayload has no ModelName, so a run whose agent never reported
                // it here has a null ModelName in history for good.
                ModelName = assignment.ProviderConfigs
                    .FirstOrDefault(c => c.Id == assignment.AgentProviderConfigId)?
                    .Settings.GetValueOrDefault(ProviderSettingKeys.Model)
            }
        }, ct);

        ReceivedJobIds.Add(assignment.JobId);
        JobAssigned.TrySetResult(assignment);
    }

    /// <summary>
    /// HMAC-SHA256(master, agentId) — the per-agent key the Job Controller vends and
    /// <c>AgentApiKeyAuthHandler</c> re-derives. Matches <c>DispatchLoop.DeriveAgentKey</c>.
    /// </summary>
    private static string DeriveKey(string masterKey, string agentId)
        => Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(masterKey), Encoding.UTF8.GetBytes(agentId)))
            .ToLowerInvariant();

    /// <summary>
    /// Connects to the SignalR hub and registers as a chat-mode agent.
    /// Adds <c>"chat=true"</c> and <c>"chat-session-id=&lt;chatSessionId&gt;"</c> to labels,
    /// mirroring what <c>AgentConnectionLifecycle</c> does when <c>AGENT_CHAT_MODE=true</c>.
    /// </summary>
    public async Task ConnectAsChatAgentAsync(string serverAddress, string apiKey, string chatSessionId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await BuildAndStartConnectionAsync(serverAddress, apiKey, cts.Token);

        var chatLabels = Labels
            .Concat(new[] { "chat=true", $"chat-session-id={chatSessionId}" })
            .ToArray();

        await _connection!.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = AgentId,
            Hostname = "fake-chat-pod",
            Labels = chatLabels
        }, cts.Token);
    }

    private void OnAssignJob(JobAssignmentMessage msg)
    {
        ReceivedJobIds.Add(msg.JobId);
        JobAssigned.TrySetResult(msg);
    }

    private void OnAssignConsolidationJob(string agentId, ConsolidationJobMessage msg)
    {
        ReceivedConsolidationJobIds.Add(msg.JobId);
        ConsolidationJobAssigned.TrySetResult(msg);
    }

    /// <summary>
    /// Accepts a job without completing it. Use with <see cref="ReportStepAsync"/> for fine-grained control.
    /// </summary>
    public async Task AcceptJobAsync(string jobId)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        await _connection.InvokeAsync("JobAccepted", jobId);
    }

    /// <summary>
    /// Reports a single step transition for a job. Use after <see cref="AcceptJobAsync"/> for fine-grained control.
    /// </summary>
    public async Task ReportStepAsync(string jobId, PipelineStep step, Dictionary<string, string>? metadata = null)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        await _connection.InvokeAsync("ReportStepTransition", jobId, step, DateTimeOffset.UtcNow, metadata);
    }

    /// <summary>
    /// Reports job completion with a full payload. Use after <see cref="AcceptJobAsync"/> and <see cref="ReportStepAsync"/>.
    /// </summary>
    public async Task ReportCompletionAsync(string jobId, JobCompletionPayload payload)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        await _connection.InvokeAsync("ReportJobCompleted", jobId, payload);
    }

    /// <summary>
    /// Accepts a job and reports completion with the given final step.
    /// Includes step metadata to simulate real agent behavior.
    /// </summary>
    public async Task AcceptAndCompleteJobAsync(
        string jobId,
        PipelineStep finalStep = PipelineStep.Completed,
        string? pullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1")
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");

        // Accept the job
        await _connection.InvokeAsync("JobAccepted", jobId);

        // Report step transitions with metadata (simulating real agent behavior)
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.CloningRepository, DateTimeOffset.UtcNow,
            (Dictionary<string, string>?)null);
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["BranchName"] = "feature/auto-42-add-input-validation",
                ["BaselineHealthPassed"] = "True"
            });
        await _connection.InvokeAsync("ReportStepTransition", jobId, finalStep, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["FilesChangedCount"] = "3",
                ["LinesAdded"] = "50",
                ["LinesRemoved"] = "10"
            });

        // Report completion
        await _connection.InvokeAsync("ReportJobCompleted", jobId, new JobCompletionPayload
        {
            FinalStep = finalStep,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = finalStep == PipelineStep.Completed ? pullRequestUrl : null,
            RetryCount = 0,
            FilesChangedCount = 3,
            LinesAdded = 50,
            LinesRemoved = 10,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0
        });
    }

    /// <summary>
    /// Accepts a job and reports completion with a fully custom payload.
    /// Use this for tests that need to control RetryCount, FailureReason, IsDraftPr, etc.
    /// </summary>
    public async Task AcceptAndCompleteJobWithPayloadAsync(string jobId, JobCompletionPayload payload)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");

        // Accept the job
        await _connection.InvokeAsync("JobAccepted", jobId);

        // Report step transitions leading up to the final step (with metadata)
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.CloningRepository, DateTimeOffset.UtcNow,
            (Dictionary<string, string>?)null);
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["BranchName"] = "feature/auto-42-test",
                ["BaselineHealthPassed"] = "True"
            });
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.RunningQualityGates, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["FilesChangedCount"] = payload.FilesChangedCount.ToString(),
                ["LinesAdded"] = payload.LinesAdded.ToString(),
                ["LinesRemoved"] = payload.LinesRemoved.ToString()
            });
        await _connection.InvokeAsync("ReportStepTransition", jobId, payload.FinalStep, DateTimeOffset.UtcNow,
            (Dictionary<string, string>?)null);

        // Report completion with the provided payload
        await _connection.InvokeAsync("ReportJobCompleted", jobId, payload);
    }

    /// <summary>
    /// Sends a heartbeat to keep the agent alive.
    /// </summary>
    public async Task SendHeartbeatAsync()
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        await _connection.InvokeAsync("Heartbeat", new HeartbeatMessage
        {
            AgentId = AgentId,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Resets the JobAssigned TaskCompletionSource for reuse across multiple dispatches.
    /// </summary>
    public void ResetJobAssigned()
    {
        JobAssigned = new TaskCompletionSource<JobAssignmentMessage>();
    }

    /// <summary>
    /// Resets the ConsolidationJobAssigned TaskCompletionSource for reuse across multiple dispatches.
    /// </summary>
    public void ResetConsolidationJobAssigned()
    {
        ConsolidationJobAssigned = new TaskCompletionSource<ConsolidationJobMessage>();
    }

    /// <summary>
    /// Resets the ChatPromptAssigned TaskCompletionSource for reuse across multiple prompts.
    /// </summary>
    public void ResetChatPromptAssigned()
    {
        ChatPromptAssigned = new TaskCompletionSource<ChatPromptMessage>();
    }

    /// <summary>
    /// Resets the <see cref="CancelChatReceived"/> TCS for multi-test reuse.
    /// </summary>
    public void ResetCancelChatReceived()
    {
        CancelChatReceived = new TaskCompletionSource();
    }

    /// <summary>
    /// Sends <c>ReportChatResponse</c> + <c>ReportChatCompleted</c> for the given session.
    /// Simpler than <see cref="RespondToChatAsync"/> — does not wait on <see cref="ChatPromptAssigned"/>;
    /// caller must supply the sessionId directly.
    /// </summary>
    public async Task SendChatResponseAsync(string sessionId, string text)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");

        await _connection.InvokeAsync("ReportChatResponse", new ChatResponseMessage
        {
            SessionId = sessionId,
            Lines = text.Split('\n')
        });

        await _connection.InvokeAsync("ReportChatCompleted", new ChatCompletedMessage
        {
            SessionId = sessionId,
            ExitCode = 0
        });
    }

    /// <summary>
    /// Responds to a previously received chat prompt by sending response lines and completion.
    /// Reads the SessionId from the captured ChatPromptMessage.
    /// </summary>
    public async Task RespondToChatAsync(string response)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");

        var prompt = await ChatPromptAssigned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sessionId = prompt.SessionId;

        await _connection.InvokeAsync("ReportChatResponse", new ChatResponseMessage
        {
            SessionId = sessionId,
            Lines = response.Split('\n')
        });

        await _connection.InvokeAsync("ReportChatCompleted", new ChatCompletedMessage
        {
            SessionId = sessionId,
            ExitCode = 0
        });
    }

    /// <summary>
    /// Reports consolidation job completion back to the hub.
    /// Returns diagnostic info from the hub method for test observability.
    /// </summary>
    public async Task<string?> ReportConsolidationCompleteAsync(ConsolidationJobResult result)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        return await _connection.InvokeAsync<string>("ReportConsolidationComplete", result);
    }

    /// <summary>
    /// Creates a sub-issue via the hub's RequestCreateIssue method (requires an active job).
    /// </summary>
    public async Task<CreatedIssueResult> RequestCreateIssueAsync(string jobId, string title, string body, IReadOnlyList<string> labels)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        return await _connection.InvokeAsync<CreatedIssueResult>("RequestCreateIssue", jobId, title, body, labels);
    }

    /// <summary>
    /// Connects and registers with an ActiveJob — simulates K8s-mode agent that already has a work item.
    /// This is the pattern used by WorkItemAgentService after our fix (RegisterAgent with ActiveJobState).
    /// </summary>
    public async Task ConnectWithActiveJobAsync(
        string serverAddress,
        string apiKey,
        string workItemId,
        string issueIdentifier,
        string repoProviderConfigId,
        string? brainProviderConfigId = null)
    {
        await BuildAndStartConnectionAsync(serverAddress, apiKey);

        // Register with ActiveJob (K8s mode pattern)
        await _connection!.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = AgentId,
            Hostname = "fake-k8s-pod",
            Labels = Labels,
            ActiveJob = new ActiveJobState
            {
                RunId = workItemId,
                IssueIdentifier = issueIdentifier,
                IssueTitle = $"Test issue {issueIdentifier}",
                IssueProviderConfigId = "issue-e2e",
                RepoProviderConfigId = repoProviderConfigId,
                AgentProviderConfigId = "agent-e2e",
                BrainProviderConfigId = brainProviderConfigId,
                InitiatedBy = "k8s-e2e-test",
                CurrentStep = PipelineStep.Created,
                StartedAt = DateTimeOffset.UtcNow,
                RunType = PipelineRunType.Implementation
            }
        });
    }

    /// <summary>
    /// Invokes RequestTokenRefresh on the hub (requires prior registration with ActiveJob).
    /// Returns the token response. Throws HubException if the request is rejected.
    /// </summary>
    public async Task<TokenRefreshResponse> RequestTokenRefreshAsync(string jobId, ProviderKind providerKind)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        return await _connection.InvokeAsync<TokenRefreshResponse>("RequestTokenRefresh", jobId, providerKind);
    }

    /// <summary>
    /// Builds the SignalR connection, wires up client-side handlers, and starts it.
    /// Shared between ConnectAsync and ConnectWithActiveJobAsync.
    /// </summary>
    private async Task BuildAndStartConnectionAsync(string serverAddress, string apiKey, CancellationToken ct = default)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(AgentId));
        var derivedToken = Convert.ToHexString(hash).ToLowerInvariant();

        _connection = new HubConnectionBuilder()
            .WithUrl($"{serverAddress}{HubRoutes.Agent}?agentId={AgentId}&access_token={derivedToken}")
            .AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithResolver(CompositeResolver.Create(
                        new IMessagePackFormatter[] { new JobIdFormatter(), new AgentIdFormatter() },
                        new IFormatterResolver[] { ContractlessStandardResolverAllowPrivate.Instance }));
            })
            .Build();

        _connection.On<JobAssignmentMessage>("AssignJob", OnAssignJob);
        _connection.On<JobId>("CancelJob", _ => { });
        _connection.On<ChatPromptMessage>("AssignChatPrompt", msg => ChatPromptAssigned.TrySetResult(msg));
        _connection.On<string>("CancelChat", sessionId => CancelChatReceived.TrySetResult());
        _connection.On<FetchModelsRequest>("RequestFetchModels", _ => { });
        _connection.On<string, ConsolidationJobMessage>("AssignConsolidationJob", OnAssignConsolidationJob);
        _connection.On("ForceDisconnect", async () =>
        {
            if (_connection is not null)
                await _connection.StopAsync();
        });

        // Use a 25s timeout if no token provided — prevents indefinite hangs under CI load
        using var fallbackCts = ct == default ? new CancellationTokenSource(TimeSpan.FromSeconds(25)) : null;
        var effectiveCt = fallbackCts?.Token ?? ct;
        await _connection.StartAsync(effectiveCt);
    }

    public async ValueTask DisposeAsync()
    {
        Connected.TryRemove(AgentId, out _);
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
