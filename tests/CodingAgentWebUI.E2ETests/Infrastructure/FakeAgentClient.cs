using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

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
    public string AgentType { get; init; } = "kiro-dotnet10";

    // Observability
    public TaskCompletionSource<JobAssignmentMessage> JobAssigned { get; private set; } = new();
    public TaskCompletionSource<ConsolidationJobMessage> ConsolidationJobAssigned { get; private set; } = new();
    public TaskCompletionSource<ChatPromptMessage> ChatPromptAssigned { get; private set; } = new();
    public List<string> ReceivedJobIds { get; } = new();
    public List<string> ReceivedConsolidationJobIds { get; } = new();
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public FakeAgentClient(string agentId, params string[] labels)
    {
        AgentId = agentId;
        Labels = labels;
    }

    /// <summary>
    /// Connects to the SignalR hub and registers as an agent.
    /// </summary>
    public async Task ConnectAsync(string serverAddress, string apiKey)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{serverAddress}{HubRoutes.Agent}?agentId={AgentId}&access_token={apiKey}")
            .Build();

        // Register all IAgentHubClient handlers
        _connection.On<JobAssignmentMessage>("AssignJob", OnAssignJob);
        _connection.On<string>("CancelJob", _ => { });
        _connection.On<ChatPromptMessage>("AssignChatPrompt", msg => ChatPromptAssigned.TrySetResult(msg));
        _connection.On<string>("CancelChat", _ => { });
        _connection.On<FetchModelsRequest>("RequestFetchModels", _ => { });
        _connection.On<string, ConsolidationJobMessage>("AssignConsolidationJob", OnAssignConsolidationJob);
        _connection.On("ForceDisconnect", async () =>
        {
            if (_connection is not null)
                await _connection.StopAsync();
        });

        await _connection.StartAsync();

        // Register with the hub
        await _connection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = AgentId,
            Hostname = "fake-agent-host",
            AgentType = AgentType,
            Labels = Labels
        });
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
    /// </summary>
    public async Task ReportConsolidationCompleteAsync(ConsolidationJobResult result)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        await _connection.InvokeAsync("ReportConsolidationComplete", result);
    }

    /// <summary>
    /// Creates a sub-issue via the hub's RequestCreateIssue method (requires an active job).
    /// </summary>
    public async Task<CreatedIssueResult> RequestCreateIssueAsync(string jobId, string title, string body, IReadOnlyList<string> labels)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        return await _connection.InvokeAsync<CreatedIssueResult>("RequestCreateIssue", jobId, title, body, labels);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
