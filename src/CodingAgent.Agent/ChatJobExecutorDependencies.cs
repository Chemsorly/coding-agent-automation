using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="ChatJobExecutor"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record ChatJobExecutorDependencies(
    AgentConnectionLifecycle ConnectionLifecycle,
    ChatSlotManager SlotManager,
    IKiroCliOrchestrator Orchestrator,
    System.Net.Http.IHttpClientFactory HttpClientFactory,
    IHostApplicationLifetime HostApplicationLifetime,
    Func<Task> SignalAgentReady,
    bool IsOpenCodeProvider,
    bool IsChatMode,
    Serilog.ILogger Logger)
{
    /// <summary>
    /// Grace period to wait for the in-flight chat task to finish after CancelChat before giving up.
    /// Defaults to the production 10s; tests set a small value so a deliberately-hanging chat task
    /// does not force a real 10s wait.
    /// </summary>
    public TimeSpan ChatTaskCompletionGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delegate used to start a child process in <see cref="ChatJobExecutor.HandleFetchModelsAsync"/>.
    /// Defaults to <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
    /// Override in tests to capture the <see cref="System.Diagnostics.ProcessStartInfo"/> passed to the
    /// process starter and assert that OTEL environment variables have been stripped.
    /// </summary>
    public Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?> ProcessStarter { get; init; }
        = System.Diagnostics.Process.Start;

    /// <summary>
    /// The provider the chat pod runs. Null derives it from <see cref="IsOpenCodeProvider"/>
    /// (OpenCode or Kiro CLI), which is all the type could express before Claude Code.
    /// </summary>
    public AgentProviderType? ProviderType { get; init; }

    /// <summary>Model for Claude Code chat (AGENT_CHAT_MODEL); null or "auto" uses the CLI default.</summary>
    public string? ChatModel { get; init; }

    /// <summary>Effort for Claude Code chat (AGENT_CHAT_EFFORT); null or "auto" uses the CLI default.</summary>
    public string? ChatEffort { get; init; }

    /// <summary>Claude Code CLI path override (CLAUDE_CLI_PATH); null uses the image default.</summary>
    public string? ClaudeCliPath { get; init; }

    /// <summary>
    /// Creates the provider a Claude Code chat prompt runs on. Tests substitute a fake;
    /// null builds a <c>ClaudeCodeAgentProvider</c> from the chat settings and the prompt message.
    /// </summary>
    public Func<ChatPromptMessage, IAgentProvider>? ClaudeCodeProviderFactory { get; init; }

    /// <summary>
    /// Creates the provider an OpenCode chat conversation runs on. Tests substitute a fake;
    /// null builds an <c>OpenCodeAgentProvider</c> with the chat model.
    /// </summary>
    public Func<ChatPromptMessage, IAgentProvider>? OpenCodeProviderFactory { get; init; }

    /// <summary>Directory for Claude Code steering rules; null means <c>~/.claude/rules</c>.</summary>
    public string? ClaudeRulesDirectory { get; init; }
}
