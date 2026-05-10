using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Fake SignalR agent client that connects to the real AgentHub for multi-agent dispatch tests.
/// Simulates agent registration, job acceptance, step reporting, and completion.
/// </summary>
public sealed class FakeAgentClient : IAsyncDisposable
{
    private HubConnection? _connection;

    public string AgentId { get; }
    public string[] Labels { get; }
    public string AgentType { get; init; } = "kiro-dotnet10";

    // Observability
    public TaskCompletionSource<JobAssignmentMessage> JobAssigned { get; private set; } = new();
    public List<string> ReceivedJobIds { get; } = new();
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
            .WithUrl($"{serverAddress}/hubs/agent?agentId={AgentId}&access_token={apiKey}")
            .Build();

        // Register all IAgentHubClient handlers
        _connection.On<JobAssignmentMessage>("AssignJob", OnAssignJob);
        _connection.On<string>("CancelJob", _ => { });
        _connection.On<ChatPromptMessage>("AssignChatPrompt", _ => { });
        _connection.On<string>("CancelChat", _ => { });
        _connection.On<FetchModelsRequest>("RequestFetchModels", _ => { });
        _connection.On<string, ConsolidationJobMessage>("AssignConsolidationJob", (_, _) => { });
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
    public async Task ReportStepAsync(string jobId, PipelineStep step)
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");
        await _connection.InvokeAsync("ReportStepTransition", jobId, step, DateTimeOffset.UtcNow);
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
    /// </summary>
    public async Task AcceptAndCompleteJobAsync(
        string jobId,
        PipelineStep finalStep = PipelineStep.Completed,
        string? pullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1")
    {
        if (_connection is null) throw new InvalidOperationException("Not connected");

        // Accept the job
        await _connection.InvokeAsync("JobAccepted", jobId);

        // Report a few step transitions
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.CloningRepository, DateTimeOffset.UtcNow);
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);
        await _connection.InvokeAsync("ReportStepTransition", jobId, finalStep, DateTimeOffset.UtcNow);

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
            AnalysisRecommendation = "ready",
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

        // Report step transitions leading up to the final step
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.CloningRepository, DateTimeOffset.UtcNow);
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);
        await _connection.InvokeAsync("ReportStepTransition", jobId, PipelineStep.RunningQualityGates, DateTimeOffset.UtcNow);
        await _connection.InvokeAsync("ReportStepTransition", jobId, payload.FinalStep, DateTimeOffset.UtcNow);

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

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
