# KiroCliLib

A standalone .NET library for wrapping and orchestrating the [Kiro CLI](https://kiro.dev/docs/cli/) tool. Provides a clean programmatic interface for executing prompts, monitoring workspace changes, and receiving lifecycle callbacks.

## Purpose

KiroCliLib enables automated workflows that invoke the Kiro CLI as an agent — sending prompts, capturing output, detecting file changes, and tracking execution state. It is designed for integration into larger orchestration systems (e.g., CI/CD pipelines, multi-agent platforms) where Kiro acts as a code generation agent.

## Dependencies

- **Serilog** — structured logging
- **Microsoft.Extensions.Configuration** — configuration loading

No other external dependencies. The library is self-contained and does not reference any other solution projects.

## Public API

### IKiroCliOrchestrator

The primary interface for consumers:

```csharp
public interface IKiroCliOrchestrator
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
        Action<string>? onOutputLine = null);

    void Kill();
}
```

**Key members:**

| Member | Description |
|--------|-------------|
| `ExecutePromptAsync` | Sends a prompt to Kiro CLI and returns the process exit code |
| `Kill` | Forcefully terminates the active agent process |
| `IsExecuting` | Whether a prompt execution is currently in progress |
| `ActiveProcessId` | OS process ID of the running agent (for external monitoring) |
| `LastOutputTime` | Timestamp of the last output line (for stall detection) |

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
using KiroCliLib.Models;
using Serilog;

var logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var config = new Configuration { KiroCliPath = "/usr/local/bin/kiro-cli" };
var callbacks = new CallbackHandler(logger);

callbacks.RegisterCallback(KiroState.Completed, ctx =>
    logger.Information("Done! {FileCount} file(s) changed", ctx.FileChanges?.Count ?? 0));

var orchestrator = new KiroCliOrchestrator(config, callbacks, logger);

// First prompt — starts a new conversation
var exitCode = await orchestrator.ExecutePromptAsync(
    prompt: "Analyze this repository and describe the architecture",
    workspaceDirectory: "/workspace/my-project",
    useResume: false,
    cancellationToken: CancellationToken.None);

// Second prompt — resumes the conversation (Kiro remembers context)
exitCode = await orchestrator.ExecutePromptAsync(
    prompt: "Now implement the feature described in issue #42",
    workspaceDirectory: "/workspace/my-project",
    useResume: true,
    cancellationToken: CancellationToken.None);
```

## The `--resume` Pattern

KiroCliLib uses the `--resume` flag to maintain conversation history across multiple prompts without keeping a persistent process:

1. **First prompt**: Invokes `kiro-cli chat --no-interactive --trust-all-tools "prompt"` — starts a new session.
2. **Subsequent prompts**: Invokes `kiro-cli chat --no-interactive --resume --trust-all-tools "prompt"` — continues the existing session.

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
│   └── KiroCliConstants.cs     — Centralized constants (timeouts, paths)
├── Core/
│   ├── IKiroCliOrchestrator.cs — Public API interface
│   ├── KiroCliOrchestrator.cs  — Orchestrates execution workflow
│   ├── IProcessWrapper.cs      — Process wrapper interface (for testing)
│   ├── ProcessWrapper.cs       — Manages CLI process lifecycle + WSL integration
│   ├── IOutputParser.cs        — Output parser interface
│   ├── OutputParser.cs         — Parses CLI output for state/test detection
│   ├── IFileSystemMonitor.cs   — File system monitor interface (for testing)
│   ├── FileSystemMonitor.cs    — Before/after workspace snapshot comparison
│   ├── CallbackHandler.cs      — Event registration and invocation with error isolation
│   ├── AnsiStripper.cs         — Strips ANSI escape codes from output
│   ├── GracefulShutdownHelper.cs — Async shutdown with timeout + logging
│   └── ExitCodes.cs            — Well-known exit code constants (shared with pipeline)
└── Models/
    ├── KiroState.cs            — Execution state enum (9 states)
    ├── CallbackContext.cs      — Context passed to callbacks
    ├── FileChange.cs           — File change record (path + type)
    └── TestResult.cs           — Parsed test results (passed/failed counts)
```

### Component Responsibilities

| Component | Role |
|-----------|------|
| **KiroCliOrchestrator** | Coordinates the full execution workflow: scan workspace → start process → parse output → detect changes → invoke callbacks |
| **ProcessWrapper** | Starts and manages the Kiro CLI OS process. Handles WSL integration on Windows (auto-detects platform, converts paths). Supports cancellation and forceful termination. |
| **OutputParser** | Processes stdout/stderr lines using regex patterns to detect execution phases (Research → Plan → Implement → Test → Completed) and test results. Emits `StateChanged` events and exposes detected test results via the `TestResults` property. |
| **FileSystemMonitor** | Takes recursive filesystem snapshots before and after execution, then compares them to produce a list of Created/Modified/Deleted file changes. |
| **CallbackHandler** | Allows consumers to register callbacks per `KiroState`. Invokes callbacks with error isolation (one failing callback does not prevent others from running). |

### Execution Flow

```
ExecutePromptAsync(prompt, workspace, useResume, ct)
  │
  ├─ FileSystemMonitor.ScanWorkspace(before)
  ├─ ProcessWrapper.StartAsync(prompt, workspace, useResume, ct)
  │     ├─ Write prompt to .agent/prompt-input.md
  │     ├─ Start process: kiro-cli chat [--resume] @.agent/prompt-input.md
  │     ├─ OutputReceived → OutputParser.ProcessLine → StateChanged → CallbackHandler.Invoke
  │     └─ WaitForExitAsync
  ├─ FileSystemMonitor.ScanWorkspace(after)
  ├─ FileSystemMonitor.CompareSnapshots(before, after)
  └─ CallbackHandler.Invoke(Completed, context)
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
