using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Wraps SignalR hub invocations so <see cref="LocalPipelineExecutor"/> can call
/// issue operations without knowing about SignalR.
/// Implements <see cref="IAgentIssueOperations"/> so existing orchestrators
/// (<see cref="CodingAgentWebUI.Pipeline.Services.AgentExecutionOrchestrator"/>,
/// <see cref="CodingAgentWebUI.Pipeline.Services.QualityGateOrchestrator"/>)
/// can be reused on the agent.
/// </summary>
public sealed class OrchestratorProxy : IAgentIssueOperations
{
    private readonly HubConnection _connection;
    private readonly string _jobId;

    public OrchestratorProxy(HubConnection connection, string jobId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobId);

        _connection = connection;
        _jobId = jobId;
    }

    /// <summary>
    /// Posts an analysis comment on the issue via the orchestrator.
    /// </summary>
    public Task PostCommentAsync(string issueIdentifier, string body, CancellationToken ct)
        => _connection.InvokeAsync(
            "RequestPostComment",
            _jobId,
            CommentType.Analysis,
            new CommentPayload { AnalysisMarkdown = body },
            ct);

    /// <summary>
    /// Swaps the agent label on the issue via the orchestrator.
    /// </summary>
    public Task SwapLabelAsync(string issueIdentifier, string newLabel, CancellationToken ct)
        => _connection.InvokeAsync("RequestLabelChange", _jobId, newLabel, ct);

    /// <summary>
    /// Posts a gate rejection comment (not_ready assessment) via the orchestrator.
    /// </summary>
    public Task PostGateRejectionAsync(string assessmentJson, CancellationToken ct)
        => _connection.InvokeAsync(
            "RequestPostComment",
            _jobId,
            CommentType.GateRejection,
            new CommentPayload { AssessmentJson = assessmentJson },
            ct);

    /// <summary>
    /// Posts a gate wont-do comment via the orchestrator.
    /// </summary>
    public Task PostGateWontDoAsync(string assessmentJson, CancellationToken ct)
        => _connection.InvokeAsync(
            "RequestPostComment",
            _jobId,
            CommentType.GateWontDo,
            new CommentPayload { AssessmentJson = assessmentJson },
            ct);

    /// <summary>
    /// Requests a fresh short-lived token from the orchestrator when the current one expires.
    /// </summary>
    public async Task<string> RequestTokenRefreshAsync(ProviderKind kind, CancellationToken ct)
    {
        var response = await _connection.InvokeAsync<TokenRefreshResponse>(
            "RequestTokenRefresh", _jobId, kind, ct);
        return response.Token;
    }
}
