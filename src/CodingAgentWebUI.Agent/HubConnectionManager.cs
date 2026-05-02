using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
namespace CodingAgentWebUI.Agent;

/// <summary>
/// Manages the SignalR hub connection to the orchestrator, including authentication,
/// MessagePack protocol, automatic reconnection with exponential backoff, and
/// client-side handler registration.
/// </summary>
public sealed class HubConnectionManager : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly Serilog.ILogger _logger;

    /// <summary>
    /// Fired when the orchestrator assigns a job to this agent.
    /// </summary>
    public event Func<JobAssignmentMessage, Task>? OnAssignJob;

    /// <summary>
    /// Fired when the orchestrator requests cancellation of the current job.
    /// </summary>
    public event Func<string, Task>? OnCancelJob;

    /// <summary>
    /// Fired when the orchestrator assigns an interactive chat prompt to this agent.
    /// </summary>
    public event Func<ChatPromptMessage, Task>? OnAssignChatPrompt;

    /// <summary>
    /// Fired when the orchestrator requests cancellation of the active chat session.
    /// </summary>
    public event Func<string, Task>? OnCancelChat;

    /// <summary>
    /// Fired when the orchestrator requests a model list fetch.
    /// </summary>
    public event Func<FetchModelsRequest, Task>? OnFetchModels;

    /// <summary>
    /// The underlying SignalR hub connection for invoking server methods.
    /// </summary>
    public HubConnection Connection => _connection;

    public HubConnectionManager(string orchestratorUrl, string agentId, string apiKey, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orchestratorUrl);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        var hubUrl = $"{orchestratorUrl.TrimEnd('/')}/hubs/agent?agentId={Uri.EscapeDataString(agentId)}";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
            })
            .AddMessagePackProtocol()
            .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
            .Build();

        // Wire up connection lifecycle events
        _connection.Reconnecting += error =>
        {
            _logger.Warning(error, "SignalR connection lost, reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            _logger.Information("SignalR reconnected with connection ID {ConnectionId}", connectionId);
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            if (error is not null)
                _logger.Error(error, "SignalR connection closed with error");
            else
                _logger.Information("SignalR connection closed");
            return Task.CompletedTask;
        };

        // Register client-side handlers (Orchestrator → Agent)
        _connection.On<JobAssignmentMessage>("AssignJob", async message =>
        {
            _logger.Information("Received job assignment {JobId} for issue {IssueIdentifier}",
                message.JobId, message.IssueIdentifier);
            if (OnAssignJob is not null)
                await OnAssignJob(message);
        });

        _connection.On<string>("CancelJob", async jobId =>
        {
            _logger.Information("Received cancellation request for job {JobId}", jobId);
            if (OnCancelJob is not null)
                await OnCancelJob(jobId);
        });

        _connection.On<ChatPromptMessage>("AssignChatPrompt", async message =>
        {
            _logger.Information("Received chat prompt for session {SessionId}", message.SessionId);
            if (OnAssignChatPrompt is not null)
                await OnAssignChatPrompt(message);
        });

        _connection.On<string>("CancelChat", async sessionId =>
        {
            _logger.Information("Received chat cancellation for session {SessionId}", sessionId);
            if (OnCancelChat is not null)
                await OnCancelChat(sessionId);
        });

        _connection.On<FetchModelsRequest>("RequestFetchModels", async request =>
        {
            _logger.Information("Received FetchModels request {RequestId}", request.RequestId);
            if (OnFetchModels is not null)
                await OnFetchModels(request);
        });
    }

    /// <summary>
    /// Starts the SignalR connection to the orchestrator.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _logger.Information("Connecting to orchestrator hub...");
        await _connection.StartAsync(ct);
        _logger.Information("Connected to orchestrator hub (connection ID: {ConnectionId})", _connection.ConnectionId);
    }

    /// <summary>
    /// Stops the SignalR connection gracefully.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _logger.Information("Disconnecting from orchestrator hub...");
        await _connection.StopAsync(ct);
        _logger.Information("Disconnected from orchestrator hub");
    }

    /// <summary>
    /// Whether the connection is currently active.
    /// </summary>
    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
