# KiroCliLib

A standalone .NET library for wrapping and orchestrating the [Kiro CLI](https://kiro.dev/docs/cli/) tool. Provides a clean programmatic interface for executing prompts, streaming output, and tracking execution state.

## Purpose

KiroCliLib enables automated workflows that invoke the Kiro CLI as an agent — sending prompts, capturing output, and tracking execution state. It is designed for integration into larger orchestration systems (e.g., CI/CD pipelines, multi-agent platforms) where Kiro acts as a code generation agent.

## Dependencies

- **Serilog** — structured logging
- **Microsoft.Extensions.Configuration** and **Microsoft.Extensions.Configuration.Json** — referenced by the project; the library's own code does not use them today.

No other external dependencies. The library is self-contained and does not reference any other solution projects.

## Public API

### IKiroCliOrchestrator

The primary interface for consumers:

```csharp
public interface IKiroCliOrchestrator : IDisposable
{
    bool IsExecuting { get; }
    int? ActiveProcessId { get; }
    bool? IsActiveProcessAlive { get; }
    DateTime? LastOutputTime { get; }

    Task<int> ExecutePromptAsync(
        string prompt,
        string workspaceDirectory,
        bool useResume,
        CancellationToken cancellationToken,
        Func<string, Task>? onOutputLine = null,
        string? resumeSessionId = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null);

    void Kill();
}
```

**Key members:**

| Member | Description |
|--------|-------------|
| `ExecutePromptAsync` | Sends a prompt to Kiro CLI and returns the process exit code. Accepts an optional `resumeSessionId` to target a specific session. Accepts optional `environmentVariables` that are added to the child process environment only |
| `Kill` | Forcefully terminates the active agent process |
| `IsExecuting` | Whether a prompt execution is currently in progress |
| `ActiveProcessId` | OS process ID of the running agent (for external monitoring) |
| `LastOutputTime` | Timestamp of the last output line (for stall detection) |
| `IDisposable` | Implements `IDisposable` for cleanup of managed resources |

### Configuration

```csharp
public class Configuration
{
    public string KiroCliPath { get; init; } = "/root/.local/bin/kiro-cli";
    public bool UseWsl { get; init; } = OperatingSystem.IsWindows();
    public string WorkspaceDirectory { get; init; } = "./workspace";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public LogEventLevel LogLevel { get; init; } = LogEventLevel.Information;
}
```

## Usage Example

```csharp
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using Serilog;

var logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var config = new Configuration { KiroCliPath = "/usr/local/bin/kiro-cli" };
var orchestrator = new KiroCliOrchestrator(config, logger);

// First prompt — starts a new conversation with output monitoring
var exitCode = await orchestrator.ExecutePromptAsync(
    prompt: "Analyze this repository and describe the architecture",
    workspaceDirectory: "/workspace/my-project",
    useResume: false,
    cancellationToken: CancellationToken.None,
    onOutputLine: line =>
    {
        // Detect execution phases from agent output
        if (line.Contains("research", StringComparison.OrdinalIgnoreCase))
            logger.Information("Phase: Research");
        else if (line.Contains("plan", StringComparison.OrdinalIgnoreCase))
            logger.Information("Phase: Planning");
        else
            Console.WriteLine($"[agent] {line}");
        return Task.CompletedTask;
    });

// Second prompt — resumes the conversation (Kiro remembers context)
exitCode = await orchestrator.ExecutePromptAsync(
    prompt: "Now implement the feature described in issue #42",
    workspaceDirectory: "/workspace/my-project",
    useResume: true,
    cancellationToken: CancellationToken.None);

if (exitCode == ExitCodes.Success)
    logger.Information("Agent completed successfully");
else
    logger.Warning("Agent exited with code {ExitCode}", exitCode);
```

## The `--resume` Pattern

KiroCliLib uses the `--resume` / `--resume-id` flags to maintain conversation history across multiple prompts without keeping a persistent process:

1. **First prompt**: Invokes `kiro-cli chat --agent-engine v2 --no-interactive --trust-all-tools "@.agent/prompt-input-<id>.md"` — starts a new session.
2. **Subsequent prompts (session-targeted)**: Invokes `kiro-cli chat --agent-engine v2 --no-interactive --resume-id <sessionId> --trust-all-tools "@.agent/prompt-input-<id>.md"` — continues a specific named session (primary path when `resumeSessionId` is provided to `ExecutePromptAsync`).
3. **Subsequent prompts (stateless resume)**: Invokes `kiro-cli chat --agent-engine v2 --no-interactive --resume --trust-all-tools "@.agent/prompt-input-<id>.md"` — resumes the most recent session in the workspace (used when `useResume: true` but no `resumeSessionId` is provided).

The prompt is written to that file first; Kiro's `@path` syntax expands it inline.

Kiro CLI stores session data internally, scoped by workspace directory. Each workspace gets its own isolated conversation history. This approach provides:

- Clean output capture (each invocation is a separate process)
- Full conversation context (Kiro remembers all prior prompts and responses)
- No TTY or stdin management complexity
- Suitable for non-interactive/automated environments

Set `useResume: false` to start a fresh conversation, or `useResume: true` to continue an existing one.

## Component Architecture

```
KiroCliLib/
├── Configuration/
│   ├── Configuration.cs        — Settings (CLI path, WSL mode, timeout)
│   └── KiroCliConstants.cs     — Default agent timeout (30 minutes)
├── Core/
│   ├── AgentModelCapabilities.cs — IsTextOnlyModel helper (text-only vs vision models)
│   ├── ChildProcessEnvironment.cs — Strips OpenTelemetry, trace-context and pipeline LLM credential variables from child processes
│   ├── IKiroCliOrchestrator.cs — Public API interface
│   ├── KiroCliOrchestrator.cs  — Orchestrates execution workflow
│   ├── IProcessWrapper.cs      — Process wrapper interface (for testing)
│   ├── ProcessWrapper.cs       — Manages CLI process lifecycle + WSL integration
│   ├── IOutputParser.cs        — Output parser interface
│   ├── OutputParser.cs         — Parses CLI output for state/test detection
│   ├── AnsiStripper.cs         — Strips ANSI escape codes from output
│   ├── GracefulShutdownHelper.cs — Async shutdown with timeout + logging
│   └── ExitCodes.cs            — Well-known exit code constants (shared with pipeline)
└── Models/
    ├── KiroState.cs            — Execution state enum (9 states)
    └── TestResult.cs           — Parsed test results (passed/failed counts)
```

### Component Responsibilities

| Component | Role |
|-----------|------|
| **KiroCliOrchestrator** | Coordinates the execution workflow: start process → parse output → stream output lines |
| **ProcessWrapper** | Starts and manages the Kiro CLI OS process. On Windows it runs the CLI through wsl.exe when `UseWsl` is set (the default on Windows); paths are passed unchanged. Supports cancellation and forceful termination. |
| **OutputParser** | Processes stdout/stderr lines using regex patterns to detect execution phases (Research → Plan → Implement → Test → Completed) and test results. Emits `StateChanged` events and exposes detected test results via the `TestResults` property. |

### Execution Flow

```
ExecutePromptAsync(prompt, workspace, useResume, ct)
  │
  └─ ProcessWrapper.StartAsync(prompt, workspace, useResume, ct)
        ├─ Write prompt to .agent/prompt-input-<8 hex>.md (unique per call, deleted afterwards)
        ├─ Strip OTEL_*/trace-context/pipeline credential variables, add environmentVariables
        ├─ Start process: kiro-cli chat --agent-engine v2 --no-interactive [--resume | --resume-id <id>] --trust-all-tools "@.agent/prompt-input-<8 hex>.md"
        ├─ OutputReceived → OutputParser.ProcessLine → StateChanged
        └─ WaitForExitAsync
```

## Exit Codes

| Code | Constant | Meaning |
|------|----------|---------|
| 0 | `ExitCodes.Success` | Prompt completed successfully |
| 1 | `ExitCodes.GeneralFailure` | Unspecified error |
| 124 | `ExitCodes.Timeout` | Execution exceeded the configured timeout |
| 130 | `ExitCodes.Cancelled` | Execution was cancelled (SIGINT) |

## Execution States

The `KiroState` enum tracks the agent's progress:

| State | Description |
|-------|-------------|
| `Started` | Initial state when execution begins |
| `ResearchPhase` | Agent is researching/analyzing |
| `PlanPhase` | Agent is creating a plan |
| `ImplementPhase` | Agent is writing code |
| `TestPhase` | Agent is running tests |
| `Completed` | Execution finished successfully |
| `Error` | An error occurred |
| `NeedsInput` | Agent is waiting for user input |
| `Timeout` | Execution exceeded the configured timeout |
