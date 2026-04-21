# Kiro CLI Integration PoC

A proof-of-concept C# console application demonstrating programmatic integration with Kiro CLI through process management, real-time output parsing, and event-driven callbacks.

## Purpose

This PoC validates that Kiro CLI can be effectively controlled and monitored from external code before building a full automated development pipeline. It demonstrates:

- **Process Management**: Starting and controlling Kiro CLI via WSL (Windows) or natively (Linux/Mac)
- **Persistent Conversation Mode**: Maintaining conversation history across multiple prompts
- **Real-time Output Parsing**: Detecting state changes, file operations, and test results
- **Event-Driven Callbacks**: Triggering actions based on Kiro CLI state changes
- **File Change Detection**: Monitoring workspace modifications during execution
- **Cross-Platform Support**: Works on Windows (via WSL) and Linux

## Prerequisites

- **.NET 10 SDK**: Required for building and running
- **Kiro CLI**: Must be installed
  - **Windows**: Install in WSL at `/root/.local/bin/kiro-cli`
  - **Linux/Mac**: Install natively, accessible in PATH
- **WSL (Windows only)**: Required for running Kiro CLI on Windows
  - Install: `wsl --install`
  - Verify: `wsl /root/.local/bin/kiro-cli --version`

## Quick Start

### 1. Build the Application

```bash
dotnet build
```

### 2. Run in Interactive Mode

```bash
dotnet run
```

The application will start in interactive mode where you can type prompts directly:

```
[17:52:08 INF] Kiro CLI Integration PoC starting
[17:52:08 INF] Configuration loaded successfully
[17:52:08 INF] ================================================================================
[17:52:08 INF] Interactive Mode: Type your prompts and press Enter
[17:52:08 INF] Commands: 'exit' or 'quit' to stop, 'clear' to clear screen
[17:52:08 INF] ================================================================================

[Prompt 1] > Say hello and introduce yourself
[17:52:10 INF] Kiro: Hello! I'm Kiro, an AI assistant...

[Prompt 2] > What can you help me with?
[17:52:15 INF] Kiro: I can help you with...

[Prompt 3] > exit
[17:52:20 INF] Exiting...
```

### 3. Interactive Commands

- **Type any prompt**: Sends the prompt to Kiro CLI and displays the response
- **`exit` or `quit`**: Exits the application
- **`clear`**: Clears the screen and resets the prompt counter

## Features

### Interactive Mode

The PoC runs in interactive mode with **persistent conversation support**, allowing you to:
- Type prompts directly into the console
- See Kiro's responses in real-time
- Have multi-turn conversations with **conversation history maintained**
- Simulate API-driven interactions

**Key Feature: Persistent Conversation Mode**
- A single Kiro CLI process is maintained across all prompts
- Kiro remembers previous prompts and responses (conversation history)
- More efficient than starting a new process for each prompt
- Mimics real interactive usage and API-driven workflows

**Response Completion Detection**
- The application automatically detects when Kiro finishes responding
- Uses silence detection: waits for 2 seconds of no output
- Maximum wait time of 60 seconds per prompt
- Allows natural conversation flow without manual intervention

This approach mimics how an API would work: each prompt is submitted independently, responses are processed as they arrive, and conversation context is preserved.

### Real-Time Output Parsing

The application parses Kiro CLI output in real-time to detect:
- **State Changes**: Started, ResearchPhase, PlanPhase, ImplementPhase, TestPhase, Completed, Error, NeedsInput
- **File Operations**: Created, Modified, Deleted files
- **Test Results**: Passed/failed test counts, coverage percentages
- **Progress Updates**: Current phase and activity

### Event-Driven Callbacks

Callbacks are triggered automatically based on Kiro's state:
- 🚀 **OnStarted**: When Kiro CLI begins execution
- ⚙️ **OnProgress**: During research, planning, implementation, or testing phases
- ✅ **OnCompleted**: When execution finishes successfully
- ❌ **OnError**: When an error occurs
- ⏱️ **OnTimeout**: When execution exceeds the timeout
- ❓ **OnNeedsInput**: When Kiro requests clarification
- 📁 **OnFilesChanged**: When files are created, modified, or deleted

### File Change Detection

The application monitors the workspace directory:
- Scans before execution to capture initial state
- Scans after execution to detect changes
- Reports all created, modified, and deleted files
- Triggers callbacks with file change information

## Test Scenarios

The `TestScenarios` class provides predefined multi-turn conversations for testing:

### HelloWorld
```csharp
Prompts:
1. "Say 'Hello, World!' and introduce yourself briefly."
2. "What are your main capabilities?"
3. "Thank you! That's all for now."
```

### AnalyzeDirectory
```csharp
Prompts:
1. "Analyze the current directory structure and tell me what type of project this is."
2. "What files are most important in this project?"
3. "Are there any potential improvements you'd suggest?"
```

### CreateFile
```csharp
Prompts:
1. "Create a file named 'test.txt' with the content 'Hello from Kiro CLI PoC!'"
2. "Did you create the file successfully? Please confirm."
3. "Great! Now delete the test.txt file to clean up."
```

These scenarios demonstrate multi-turn conversations but are not used in interactive mode.

## Architecture

### Core Components

- **ProcessWrapper**: Manages Kiro CLI process lifecycle with WSL support
  - `StartInteractiveAsync()`: Starts Kiro CLI in persistent conversation mode
  - `SendPromptAsync()`: Sends prompts to the running process via stdin
  - `WaitForResponseAsync()`: Detects response completion via silence detection
- **OutputParser**: Parses Kiro CLI output to detect states and extract information
- **CallbackHandler**: Manages callback registration and invocation with error isolation
- **FileSystemMonitor**: Tracks file changes in the workspace directory
- **KiroCliOrchestrator**: Coordinates all components and manages execution flow

### Execution Flow

```
1. Load configuration
2. Initialize components (ProcessWrapper, OutputParser, CallbackHandler, FileSystemMonitor)
3. Register callbacks for state changes
4. Scan workspace (before snapshot)
5. Start Kiro CLI in persistent interactive mode
   ├─▶ Trigger OnStarted callback
   └─▶ Begin output capture
6. For each prompt:
   ├─▶ Send prompt to stdin
   ├─▶ Process output in real-time
   │   ├─▶ Parse each line for patterns
   │   ├─▶ Detect state changes
   │   └─▶ Trigger appropriate callbacks
   └─▶ Wait for response completion (silence detection)
7. Scan workspace (after snapshot)
8. Compare snapshots and trigger OnFilesChanged
9. Trigger OnCompleted callback
10. Return exit code
```

## Configuration

Configuration is loaded from `config/appsettings.json`:

```json
{
  "KiroCliPath": "/root/.local/bin/kiro-cli",
  "UseWsl": true,
  "WorkspaceDirectory": "./workspace",
  "AgentName": "feature-developer",
  "Timeout": "00:30:00",
  "LogLevel": "Information",
  "LogFilePath": null
}
```

### Configuration Options

- **KiroCliPath**: Path to Kiro CLI executable
- **UseWsl**: Auto-detects Windows, set to `false` to disable WSL
- **WorkspaceDirectory**: Directory where Kiro CLI executes
- **AgentName**: Kiro CLI agent to use
- **Timeout**: Maximum execution time (format: `HH:MM:SS`)
- **LogLevel**: Logging verbosity (Verbose, Debug, Information, Warning, Error, Fatal)
- **LogFilePath**: Optional path for log file output

## Exit Codes

- **0**: Success
- **1**: Generic error
- **124**: Timeout
- **130**: Cancelled (Ctrl+C)

## Logging

The application uses Serilog for structured logging:

- **Console**: Formatted output with timestamps and log levels
- **File** (optional): Rolling daily logs with detailed information

Log levels:
- **Information**: Normal execution flow
- **Warning**: Non-critical issues (timeouts, missing files)
- **Error**: Execution failures
- **Debug**: Detailed output parsing information

## KiroWebUI (Blazor Server)

The KiroWebUI application provides a web-based interface for the automated development pipeline. It runs as a Blazor Server app inside a Docker container.

### Docker Build & Run

```powershell
docker build -f webUI.Dockerfile -t kiro-webui:latest .

docker run -it --rm -p 5000:5000 -v ${PWD}/config/kiro-cli-data:/home/ubuntu/.local/share/kiro-cli -v "$env:USERPROFILE\.aws:/home/ubuntu/.aws" -v "$env:USERPROFILE\.kiro\settings:/home/ubuntu/.kiro/settings" -v ${PWD}/config/pipeline:/app/config/pipeline kiro-webui:latest 2>&1 | Tee-Object -FilePath .kiro/debug.log


```

### Required Volume Mounts

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Kiro CLI auth | `/home/ubuntu/.local/share/kiro-cli` | Kiro CLI login tokens (persists auth across container restarts) |
| AWS SSO | `/home/ubuntu/.aws` | AWS SSO cache and config for Kiro CLI auth |
| Kiro settings | `/home/ubuntu/.kiro/settings` | MCP and CLI settings |
| Pipeline config | `/app/config/pipeline` | Provider configs and pipeline settings (persists across restarts) |

The pipeline config volume is important — without it, any providers you configure in the Settings page will be lost when the container restarts.

Workspaces are created inside the container at `/app/workspaces/` (configurable via `WorkspaceBaseDirectory` in pipeline config). The pipeline clones a fresh copy of the repository for each run, so no workspace volume mount is needed. Successful workspaces are cleaned up automatically; failed ones are retained based on the `FailedWorkspaceRetentionDays` setting.

### First-Time Setup

1. Start the container with the command above
2. Open `http://localhost:5000` in your browser
3. Go to **Settings** and configure your providers (Issue, Repository, Agent)
4. Go to **Agent Coding** to select an issue and start a pipeline run

## Testing

### Run Unit Tests

```bash
dotnet test
```

### Run Tests on Linux (via Docker)

```bash
docker run --rm -v "${PWD}:/app" -w /app mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

### Code Quality

All code follows:
- Microsoft C# coding conventions
- SOLID principles
- Immutability patterns (init-only properties)
- Comprehensive XML documentation
- Input validation with ArgumentNullException.ThrowIfNull

## License

This is a proof-of-concept project for internal use.
