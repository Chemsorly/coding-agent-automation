# Issue: [REF-02] Extract hardcoded literals and magic numbers into centralized constants classes

## Description
## Summary

Multiple hardcoded string literals, magic numbers, and repeated values are scattered across the codebase without centralized constants. This creates drift risk (values change in one place but not another), makes typos hard to catch at compile time, and reduces discoverability of the system's conventions.

The codebase already has good constant extraction in some areas (`PipelineConstants`, `ExitCodes`, `AgentLabels`, `FeedbackConstraints`). This issue addresses the remaining gaps identified by a full-codebase audit.

**Impact:** Medium | Reduces runtime failure risk from typos in dictionary keys, hub paths, and HttpClient names | Improves maintainability

## Affected Components

### High Priority (cross-project, runtime failure risk)

| Literal | Occurrences | Files |
|---------|-------------|-------|
| `"/hubs/agent"` (SignalR hub path) | 14+ (4 source + 10 tests) | `Program.cs`, `HubConnectionManager.cs`, 10+ test files |
| Provider settings keys (`"apiUrl"`, `"clientId"`, `"owner"`, `"repo"`, etc.) | 50+ total | `ProviderFactory.cs`, `TokenVendingService.cs`, `AgentProviderFactory.cs`, `LocalConsolidationExecutor.cs`, `GitHubValidationService.cs` |
| Comment markers (`"## 🤖 Agent Analysis"`, `"<!-- agent:gate-rejection -->"`, etc.) | 12 source | `PromptBuilder.cs`, `AgentExecutionOrchestrator.Analysis.cs`, `AgentJobDispatcher.cs`, `IssueAnalysisComment.cs`, `FeedbackCommentFormatter.cs`, `GitHubRepositoryProvider.cs` |
| `"/home/ubuntu/.local/bin/kiro-cli"` (default CLI path) | 4 source + 2 razor | `Program.cs`, `AgentProviderFactory.cs`, `AgentWorkerService.cs`, `KiroCliAgentProvider.cs` |
| `"http://127.0.0.1:4096"` (OpenCode base URL) | 4 source + 5 razor | `Program.cs`, `AgentProviderFactory.cs`, `OpenCodeAgentProvider.cs`, `AgentProviderSection.razor` |
| `"OpenCode"` (named HttpClient) | 13 source | `Program.cs`, `OpenCodeAgentProvider.cs` (11×), `OpenCodeHealthMonitor.cs` |

### Medium Priority (2+ files or important operational parameters)

| Literal | Occurrences | Files |
|---------|-------------|-------|
| Git signature `"CodingAgentWebUI Pipeline"` / `"pipeline@kiro.dev"` | 5 pairs | `GitHubRepositoryProvider.cs` (3×), `BrainUpdateService.cs` (2×) |
| `.TakeLast(10)` output tail count | 7 source | `AgentExecutionOrchestrator*.cs` (5×), `PipelineFormatting.cs`, `PromptBuilder.cs` |
| `"(no output)"` fallback string | 5 source | `AgentExecutionOrchestrator*.cs` (5×) |
| Git restriction instruction | 7 occurrences (3 variants) | `PromptBuilder.cs` (6×), `QualityGateOrchestrator.RetryLoop.cs` (1×) |
| `"feature/auto-"` branch prefix | 2 files | `PipelineFormatting.cs`, `GitHubRepositoryProvider.cs` |
| `"Automated implementation via pipeline"` | 3 in same file | `PipelineFormatting.cs` |
| `"config/pipeline"` base directory | 2 files | `JsonConfigurationStore.cs`, `Program.cs` |
| JWT generation logic (full duplication) | 2 files | `GitHubAppAuthService.cs`, `TokenVendingService.cs` |
| Environment variable names (`AGENT_TYPE`, `AGENT_ID`, etc.) | 2-3 files each | `Program.cs`, `AgentWorkerService.cs`, `AgentProviderFactory.cs` |

### Low Priority (single-file readability improvements)

| Literal | Notes |
|---------|-------|
| JS interop method names (`"scrollToBottom"`, `"scrollActiveStepIntoView"`) | 3 uses across Blazor components |
| `"chat-session"` sentinel value | 3 uses in `AgentChat.razor` |
| Process kill timeouts (2000ms, 5000ms) in `ProcessWrapper.cs` | Private consts in same class |
| SignalR reconnect delays `[1s, 2s, 5s, 10s, 30s]` | Named constant in `HubConnectionManager.cs` |

## Current Behavior

Values are hardcoded as string literals. A typo in a dictionary key (e.g., `Settings["apiurl"]` instead of `Settings["apiUrl"]`) causes a runtime `KeyNotFoundException`. A typo in the hub path causes silent connection failure. Comment marker drift between detection and emission sites causes pipeline logic bugs.

## Expected Behavior

Centralized constants classes that are referenced by all consumers. Compile-time safety for shared string values.

## Requirements

- Constants must live in the lowest-level project that all consumers can reference (typically `CodingAgentWebUI.Pipeline` for cross-project values)
- Agent-specific constants (CLI paths, env vars, HttpClient names) should live in `CodingAgentWebUI.Agent`
- Infrastructure-specific constants (git signature, token username) should live in `CodingAgentWebUI.Infrastructure`
- Existing `PipelineConstants.cs` should be extended (not replaced) for pipeline-scoped values
- Comment markers must be extracted as individual named constants (not just the existing array)
- The JWT duplication between `GitHubAppAuthService` and `TokenVendingService` should be deduplicated into a shared helper
- No behavioral changes — this is a pure refactoring

## Suggested Approach

### New constants classes:

```csharp
// src/CodingAgentWebUI.Pipeline/Models/HubRoutes.cs
public static class HubRoutes
{
    public const string Agent = "/hubs/agent";
}

// src/CodingAgentWebUI.Pipeline/Models/CommentMarkers.cs
public static class CommentMarkers
{
    public const string AnalysisHeader = "## 🤖 Agent Analysis";
    public const string GateRejection = "<!-- agent:gate-rejection -->";
    public const string GateWontDo = "<!-- agent:gate-wont-do -->";
    public const string IssueFeedback = "<!-- agent:issue-feedback -->";
    public const string PipelinePrefix = "## 🤖";
    public const string AgentCommentPrefix = "<!-- agent:";
}

// src/CodingAgentWebUI.Pipeline/Models/ProviderSettingKeys.cs
public static class ProviderSettingKeys
{
    public const string ApiUrl = "apiUrl";
    public const string ClientId = "clientId";
    public const string InstallationId = "installationId";
    public const string PrivateKeyBase64 = "privateKeyBase64";
    public const string Owner = "owner";
    public const string Repo = "repo";
    public const string BaseBranch = "baseBranch";
    public const string Token = "token";
    public const string TokenExpiresAt = "tokenExpiresAt";
    public const string Model = "model";
    public const string ExecPath = "executablePath";
    public const string BaseUrl = "baseUrl";
    public const string McpConfigPath = "mcpConfigPath";
}

// src/CodingAgentWebUI.Agent/AgentDefaults.cs
public static class AgentDefaults
{
    public const string DefaultKiroCliPath = "/home/ubuntu/.local/bin/kiro-cli";
    public const string DefaultOpenCodeBaseUrl = "http://127.0.0.1:4096";
    public const string OpenCodeHttpClientName = "OpenCode";
    public const string DefaultWorkspaceRoot = "/app/workspaces";
    public static readonly TimeSpan OpenCodeRequestTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
}

// src/CodingAgentWebUI.Agent/AgentEnvironmentVariables.cs
public static class AgentEnvironmentVariables
{
    public const string AgentType = "AGENT_TYPE";
    public const string AgentId = "AGENT_ID";
    public const string AgentProviderType = "AGENT_PROVIDER_TYPE";
    public const string AgentLabels = "AGENT_LABELS";
    public const string AgentApiKey = "AGENT_API_KEY";
    public const string OrchestratorUrl = "ORCHESTRATOR_URL";
    public const string OpenCodeServerPassword = "OPENCODE_SERVER_PASSWORD";
    public const string OpenCodeBaseUrl = "OPENCODE_BASE_URL";
    public const string KiroCliPath = "KIRO_CLI_PATH";
    public const string LogLevel = "LOG_LEVEL";
}

// src/CodingAgentWebUI.Infrastructure/Git/GitConstants.cs
public static class GitConstants
{
    public const string CommitAuthorName = "CodingAgentWebUI Pipeline";
    public const string CommitAuthorEmail = "pipeline@kiro.dev";
    public const string TokenUsername = "x-access-token";
}
```

### Extensions to existing `PipelineConstants.cs`:

```csharp
public const int OutputTailLineCount = 10;
public const string NoOutputFallback = "(no output)";
public const string AutomatedFooter = "Automated implementation via pipeline";
public const string BranchNamePrefix = "feature/auto-";
public const string DefaultConfigBaseDirectory = "config/pipeline";
public const int MaxCommentsToInclude = 50;
```

### JWT deduplication:

Extract a shared static helper (e.g., `GitHubJwtHelper.GenerateJwt(string privateKeyPem, string clientId)`) in the Infrastructure project. Both `GitHubAppAuthService` and `TokenVendingService` call it.

## Out of Scope

- CLI flags (`--no-interactive`, `--resume`, etc.) — single-use, self-documenting
- Regex patterns in `OutputParser` — co-located with logic, each used once
- CSS class names in Razor components — standard Blazor practice
- Error message strings — localized, clear in context
- Configuration defaults already in their config classes
- Single-use timeouts with clear comments

## Acceptance Criteria

- [ ] `HubRoutes.Agent` constant exists and is referenced by all hub path consumers (source + tests)
- [ ] `ProviderSettingKeys` class exists with all provider config dictionary keys
- [ ] `CommentMarkers` class exists with named constants; `PromptBuilder.ExcludedCommentMarkers` references them; all detection/emission sites use the constants
- [ ] `AgentDefaults` class exists with CLI path, OpenCode URL, HttpClient name, and timeouts
- [ ] `AgentEnvironmentVariables` class exists for env var names used in 2+ files
- [ ] `GitConstants` class exists with commit signature and token username
- [ ] `PipelineConstants` extended with `OutputTailLineCount`, `NoOutputFallback`, `BranchNamePrefix`, `AutomatedFooter`, `DefaultConfigBaseDirectory`
- [ ] JWT generation deduplicated into a shared helper
- [ ] Git restriction instruction extracted to `PromptBuilder` constant (2 variants: full + short)
- [ ] No hardcoded string literals remain for the high-priority items (grep-verifiable)
- [ ] All existing tests pass
- [ ] Build succeeds with no warnings

## Related Issues

- #349 — [REF-01] Rename `.kiro` to `.agent` (introduces `AgentWorkspacePaths` constants class — complementary)


## Requirements
- Constants must live in the lowest-level project that all consumers can reference (typically `CodingAgentWebUI.Pipeline` for cross-project values)
- Agent-specific constants (CLI paths, env vars, HttpClient names) should live in `CodingAgentWebUI.Agent`
- Infrastructure-specific constants (git signature, token username) should live in `CodingAgentWebUI.Infrastructure`
- Existing `PipelineConstants.cs` should be extended (not replaced) for pipeline-scoped values
- Comment markers must be extracted as individual named constants (not just the existing array)
- The JWT duplication between `GitHubAppAuthService` and `TokenVendingService` should be deduplicated into a shared helper
- No behavioral changes — this is a pure refactoring

## Acceptance Criteria
- `HubRoutes.Agent` constant exists and is referenced by all hub path consumers (source + tests)
- `ProviderSettingKeys` class exists with all provider config dictionary keys
- `CommentMarkers` class exists with named constants; `PromptBuilder.ExcludedCommentMarkers` references them; all detection/emission sites use the constants
- `AgentDefaults` class exists with CLI path, OpenCode URL, HttpClient name, and timeouts
- `AgentEnvironmentVariables` class exists for env var names used in 2+ files
- `GitConstants` class exists with commit signature and token username
- `PipelineConstants` extended with `OutputTailLineCount`, `NoOutputFallback`, `BranchNamePrefix`, `AutomatedFooter`, `DefaultConfigBaseDirectory`
- JWT generation deduplicated into a shared helper
- Git restriction instruction extracted to `PromptBuilder` constant (2 variants: full + short)
- No hardcoded string literals remain for the high-priority items (grep-verifiable)
- All existing tests pass
- Build succeeds with no warnings