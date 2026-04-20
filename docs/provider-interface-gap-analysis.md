# Provider Interface Gap Analysis

**Date:** April 19, 2026  
**Scope:** All 4 provider interfaces in `src/KiroWebUI/Pipeline/Interfaces/` and their consumers  
**Goal:** Identify functionality that lives on concrete implementations or in orchestrator workarounds but should be exposed on the provider interfaces.

---

## 1. IAgentProvider

### 1.1 Session Warm-Up Duplicated in Orchestrator [HIGH]

**Problem:** The orchestrator sends a throwaway read-only prompt (`"Briefly describe the project structure..."`) before every real `ExecuteWithResumeAsync` call. This works around a Kiro CLI quirk where the first `--no-interactive` call can't use write tools. The ~25-line block is copy-pasted in two code paths (existing-analysis branch and fresh-analysis branch).

**Location:** `PipelineOrchestrationService.ExecutePipelineStepsAsync` — two identical blocks.

**Proposed fix:** Add an `EnsureSessionAsync(string workspacePath, CancellationToken ct)` method to `IAgentProvider`. The `KiroCliAgentProvider` implementation would send the warm-up prompt internally and track whether a session has been established for a given workspace. The orchestrator would call it once before the first agent interaction, and the provider would no-op if a session already exists. This eliminates ~50 lines of duplicated workaround code from the orchestrator.

Alternative: have `ExecuteWithResumeAsync` detect internally that no session exists and auto-warm-up. This is simpler for callers but hides a potentially slow operation.

---

### 1.2 Stall Detection Reimplemented Outside the Provider [HIGH]

**Problem:** The orchestrator builds its own background `Task.Run` polling loop in `ApproveAnalysisAsync` to detect agent silence. It tracks a local `lastOutputTime` variable via the `onOutputLine` callback and warns if no output arrives for 5 minutes. Meanwhile, `GetHealthStatus().LastOutputTime` already exposes the exact same data but is never called during execution.

**Location:** `PipelineOrchestrationService.ApproveAnalysisAsync` — stall monitor task (~45 lines).

**Proposed fix:** Two options:

- **Option A (event-based):** Add an `event Action<TimeSpan>? OnStallDetected` to `IAgentProvider`. The provider monitors its own output timing and fires the event when silence exceeds a configurable threshold. The orchestrator subscribes and logs/notifies. This keeps the detection logic inside the provider where the output data lives.

- **Option B (config-based):** Add a `StallWarningInterval` property to `AgentRequest`. The provider internally monitors output timing during execution and invokes the `onOutputLine` callback with a synthetic stall-warning message. Simpler, no new events.

Recommendation: Option B — it's less invasive and keeps the callback as the single output channel.

---

### 1.3 ANSI Stripping at Every Call Site [MEDIUM]

**Problem:** All 6 `onOutputLine` callbacks in the orchestrator wrap output with `StripAnsi()`. The provider knows it wraps a CLI tool that produces ANSI escape sequences but passes raw output through.

**Location:** 6 call sites in `PipelineOrchestrationService`.

**Proposed fix:** Strip ANSI inside `KiroCliAgentProvider` before invoking the `onOutputLine` callback and before adding lines to `AgentResult.OutputLines`. The consumer should receive clean text. If a future provider doesn't produce ANSI output, it simply doesn't strip — no harm done. This is a one-line change in the provider and removes 6 `StripAnsi()` calls from the orchestrator.

---

### 1.4 Inconsistent Method Signatures [MEDIUM]

**Problem:** `ExecuteAsync` takes an `AgentRequest` object; `ExecuteWithResumeAsync` takes individual parameters (`string instruction, string workspacePath, TimeSpan timeout`). The orchestrator has to destructure `AgentRequest` when calling the resume variant.

**Proposed fix:** Add a `bool UseResume` property (default `false`) to `AgentRequest` and collapse both methods into a single `ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)`. The provider checks `request.UseResume` internally. This simplifies the interface from 2 execution methods to 1 and makes the API shape consistent.

Keep the old methods as extension methods or mark them `[Obsolete]` for a transition period if needed.

---

### 1.5 `GetHealthStatus()` Never Called in Production [LOW]

**Problem:** `GetHealthStatus()` is on the interface and implemented, but `PipelineOrchestrationService` never calls it. Only test mocks use it. The stall monitor (Finding 1.2) reimplements the same capability.

**Proposed fix:** If Finding 1.2 is implemented (stall detection moves into the provider), `GetHealthStatus()` becomes the mechanism the provider uses internally. Keep it on the interface for diagnostics/UI purposes but don't rely on callers polling it. If it remains unused after the stall detection refactor, remove it.

---

## 2. IRepositoryProvider

> **Note:** The unmerged GIT-04 branch (`feature/auto-78-git-04-blacklisted-path-enforcement`) changed
> `CommitAllAsync` from `Task CommitAllAsync(string workspacePath, string message, CancellationToken ct)`
> to `Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message, IReadOnlyList<string> blacklistedPaths, CancellationToken ct)`.
> This returns the list of excluded files and adds blacklist enforcement at the provider level.
> The orchestrator was updated to pass `_activeConfig.BlacklistedPaths` and handle `Fail` mode.
> This is a good example of moving logic into the provider interface — but it also changed the interface
> signature in a breaking way. The findings below are based on the current `main` branch.

### 2.1 `GetHeadCommitShaAsync` Missing [HIGH]

**Problem:** The orchestrator shells out to `git rev-parse HEAD` via `Process.Start` to get the commit SHA after pushing. The provider already has LibGit2Sharp loaded and authenticated. This introduces a dependency on `git` being on PATH, which the provider's LibGit2Sharp approach avoids.

**Location:** `PipelineOrchestrationService.RunGitCommandAsync` (private static helper).

**Proposed fix:** Add `Task<string> GetHeadCommitShaAsync(string workspacePath, CancellationToken ct)` to `IRepositoryProvider`. The `GitHubRepositoryProvider` implementation is trivial:

```csharp
public Task<string> GetHeadCommitShaAsync(string workspacePath, CancellationToken ct)
{
    return Task.Run(() =>
    {
        using var repo = new Repository(workspacePath);
        return repo.Head.Tip.Sha;
    }, ct);
}
```

Alternative: have `PushBranchAsync` return the pushed commit SHA as its return value (change from `Task` to `Task<string>`). This is more ergonomic since the caller always needs the SHA right after pushing.

Recommendation: Change `PushBranchAsync` return type to `Task<string>` returning the commit SHA. It's a natural extension of the push operation and eliminates the need for a separate call.

---

### 2.2 `HasCommitsAheadAsync` Missing [HIGH]

**Problem:** The orchestrator directly opens a `LibGit2Sharp.Repository` to check if the feature branch has commits ahead of the base branch before creating a PR. This breaks the abstraction — a non-Git provider (API-based) couldn't substitute its own implementation.

**Location:** `PipelineOrchestrationService.BranchHasCommitsAhead` (private static method).

**Proposed fix:** Add `Task<bool> HasCommitsAheadAsync(string workspacePath, CancellationToken ct)` to `IRepositoryProvider`. The provider already knows its base branch, so no `baseBranch` parameter is needed. The `GitHubRepositoryProvider` implementation moves the existing `BranchHasCommitsAhead` logic into the provider.

---

### 2.3 `GetFileChangesAsync` Missing [HIGH]

**Problem:** `PipelineFormatting.GetFileChanges()` is a static method that directly uses `LibGit2Sharp.Repository` to diff branches. It can't be mocked, couples the formatting layer to Git, and would break for non-Git providers.

**Location:** `PipelineFormatting.GetFileChanges` (static method), called from `PipelineOrchestrationService` in 2 places.

**Proposed fix:** Add `Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(string workspacePath, CancellationToken ct)` to `IRepositoryProvider`. Move the LibGit2Sharp diff logic from `PipelineFormatting` into `GitHubRepositoryProvider`. The provider already knows its base branch. `PipelineFormatting.GetFileChanges` becomes a thin wrapper that calls the provider, or is removed entirely with callers using the provider directly.

---

### 2.4 `BaseBranch` Not Exposed [MEDIUM]

**Problem:** The orchestrator reads `baseBranch` from the raw config settings dictionary (`repoProviderConfig.Settings.GetValueOrDefault("baseBranch", "main")`), even though the provider already stores it internally as `_baseBranch`.

**Location:** `PipelineOrchestrationService` line ~115.

**Proposed fix:** Add `string BaseBranch { get; }` to `IRepositoryProvider`. The orchestrator reads it from the provider instead of the config bag. Simple property addition, no behavior change.

---

### 2.5 `RepositoryFullName` Not Exposed [MEDIUM]

**Problem:** The orchestrator constructs `owner/repo` from config settings to set `run.RepositoryName`. The provider already knows its identity.

**Location:** `PipelineOrchestrationService` line ~126.

**Proposed fix:** Add `string RepositoryFullName { get; }` to `IRepositoryProvider`. Implementation returns `$"{_owner}/{_repo}"`.

---

### 2.6 Vestigial Static Helpers on Concrete Class [LOW]

**Problem:** 4 `internal static` methods on `GitHubRepositoryProvider` (`GenerateBranchName`, `GeneratePrTitle`, `GeneratePrBody`, `GenerateCommitMessage`) are pure pass-throughs to `PipelineFormatting`. Tests call them through the concrete type, creating false coupling.

**Proposed fix:** Delete the 4 wrapper methods. Update tests to call `PipelineFormatting` directly. This is a pure cleanup — no behavior change.

---

## 3. IIssueProvider

> **Note:** The UX-10 label filtering PR (`fdf72c7`) merged but did NOT add label filtering to the
> `IIssueProvider` interface. It implemented client-side filtering in the Blazor UI only. Finding 3.4
> still applies.

### 3.1 No `UpdateCommentAsync` — Creates Duplicate Analysis Comments [HIGH]

**Problem:** The interface only has `PostCommentAsync` (create new). When the pipeline runs analysis, it always posts a new comment. Re-runs on the same issue create duplicate "🤖 Agent Analysis" comments. The orchestrator has no way to update the existing one.

**Location:** `PipelineOrchestrationService.ExecutePipelineStepsAsync`.

**Proposed fix:** Add `Task UpdateCommentAsync(string issueIdentifier, string commentId, string body, CancellationToken ct)` to `IIssueProvider`. The `GitHubIssueProvider` implementation calls `client.Issue.Comment.Update(_owner, _repo, commentId, body)`.

Then the orchestrator can check for an existing analysis comment and update it instead of creating a new one. Combined with Finding 3.2, this becomes an upsert pattern.

---

### 3.2 Comment Search by Marker in Orchestrator [HIGH]

**Problem:** The orchestrator fetches all comments and does `FirstOrDefault(c => c.Body.Contains(marker))` in-memory. This is a second redundant API call — comments were already fetched earlier in the same method. The "search comments by content" operation is a provider-level concern.

**Location:** `PipelineOrchestrationService.ExecutePipelineStepsAsync` — 2 `ListCommentsAsync` calls for the same issue.

**Proposed fix:** Add `Task<IssueComment?> FindCommentByMarkerAsync(string issueIdentifier, string marker, CancellationToken ct)` to `IIssueProvider`. The implementation fetches comments and searches server-side if the platform supports it, or in-memory otherwise. This eliminates the duplicate fetch and moves the search logic into the provider.

Combined with Finding 3.1, consider a higher-level `UpsertCommentAsync(string issueIdentifier, string marker, string body, CancellationToken ct)` that finds-by-marker and updates, or creates if not found. This would replace 3 separate calls (list, find, post/update) with 1.

---

### 3.3 `AcceptanceCriteria` Always Empty From Provider [MEDIUM]

**Problem:** `IssueDetail.AcceptanceCriteria` is always `Array.Empty<string>()` from the provider. A separate `IssueDescriptionParser` service extracts them from the description. The field is dead weight on the model.

**Location:** `GitHubIssueProvider.MapToIssueDetail` + `IssueDescriptionParser` + orchestrator.

**Proposed fix:** Two options:

- **Option A:** Inject `IssueDescriptionParser` into the provider and populate `AcceptanceCriteria` during mapping. The provider returns a fully-populated `IssueDetail`. This eliminates the separate `_activeParsedIssue` field in the orchestrator and the `ParsedIssue` model.

- **Option B:** Remove `AcceptanceCriteria` from `IssueDetail` entirely. Keep the parser as a separate service. The orchestrator continues to use `ParsedIssue` for structured data. This is honest about the separation of concerns — the provider fetches raw data, the parser structures it.

Recommendation: Option B. Parsing markdown structure is not a provider concern — it's domain logic that varies by project convention, not by platform. Remove the dead field from `IssueDetail`.

---

### 3.4 No Label-Filtered Listing [MEDIUM]

**Problem:** The Blazor UI fetches all open issues then filters client-side by label. GitHub's API supports label filtering natively via `RepositoryIssueRequest.Labels`.

**Location:** `AgentCoding.razor`.

**Proposed fix:** Add an overload `Task<IReadOnlyList<IssueSummary>> ListOpenIssuesAsync(IReadOnlyList<string>? labels, CancellationToken ct)` to `IIssueProvider`. The GitHub implementation passes labels to `RepositoryIssueRequest.Labels`. The parameterless overload becomes a convenience wrapper that calls the new one with `null`.

---

### 3.5 No Issue State Management [LOW]

**Problem:** Can't add/remove labels or close issues programmatically. Relies on PR body "Closes #N" convention.

**Proposed fix:** Add `Task AddLabelsAsync(string identifier, IReadOnlyList<string> labels, CancellationToken ct)` and `Task CloseIssueAsync(string identifier, CancellationToken ct)` to `IIssueProvider`. These are optional for the current pipeline but would enable:
- Adding "in-progress" label when work starts
- Removing it if the pipeline fails
- Closing the issue directly after PR merge (instead of relying on GitHub's auto-close)

Low priority — implement when the pipeline needs explicit state management.

---

## 4. IPipelineProvider

> **Note:** Post-STK-03 follow-up commits (`42935eb`..`ea89928`) added Serilog logging to the provider,
> `JobId` tracking on `PipelineJobResult`, and automatic log enrichment for failed jobs via a private
> `EnrichFailedJobsWithLogsAsync` method. The interface XML doc was also updated to correctly reference
> `PipelineJobResult.LogContent`. The `WriteCiLogsToWorkspace` method in the orchestrator was improved
> to clean up previous log directories on retries and null out `LogContent` after writing to free memory.
> These changes addressed some earlier gaps but introduced new ones.

### 4.1 Log Fetching Not Independently Callable [HIGH]

**Problem:** `EnrichFailedJobsWithLogsAsync` is a private method that only runs as a side effect inside `WaitForCompletionAsync` when CI fails. If a caller uses `GetRunStatusAsync` (which does NOT fetch logs), they get `PipelineJobResult` objects with `LogContent = null` and `JobId` set but no way to fetch logs through the interface. The log-fetching capability exists on the implementation but is not exposed.

**Location:** `GitHubActionsPipelineProvider.EnrichFailedJobsWithLogsAsync` (private).

**Proposed fix:** Add `Task<string?> GetJobLogsAsync(long jobId, CancellationToken ct)` to `IPipelineProvider`. This makes log fetching a first-class operation. The existing `EnrichFailedJobsWithLogsAsync` can call this new public method internally. Callers who use `GetRunStatusAsync` can fetch logs on demand without having to go through `WaitForCompletionAsync`.

---

### 4.2 CI Log File Writing in Orchestrator — Mutable Model Properties [MEDIUM]

**Problem:** The orchestrator's `WriteCiLogsToWorkspace` writes `LogContent` to `.kiro/ci-logs/` files, sets `PipelineJobResult.LogFilePath`, then nulls out `LogContent` to free memory. This is why `LogContent` and `LogFilePath` are `{ get; set; }` (mutable) — breaking the project's immutability pattern where all other properties use `{ get; init; }`.

The post-STK-03 fix improved this method (cleanup of old log dirs, memory freeing via `job.LogContent = null`), but the fundamental issue remains: the orchestrator mutates provider-returned model objects after the fact.

**Location:** `PipelineOrchestrationService.WriteCiLogsToWorkspace`.

**Proposed fix:** Create a small `CiLogWriter` service (not on the provider interface — this is file I/O, not CI platform interaction). The service takes a `PipelineRunStatus` and a workspace path, writes logs to disk, and returns a mapping of `jobId → filePath`. The orchestrator uses this mapping to build quality gate error messages. `PipelineJobResult.LogContent` becomes `{ get; init; }` (set during construction in the enrichment pass), and `LogFilePath` moves off the model entirely — it's a local concern of the log writer, tracked in a separate dictionary.

---

### 4.3 No `TriggerRunAsync` — Passive Only [MEDIUM]

**Problem:** The provider is read-only. The pipeline pushes a branch and hopes CI auto-triggers. If the workflow doesn't auto-trigger (path filters, `workflow_dispatch` only), `WaitForCompletionAsync` times out silently.

**Proposed fix:** Add `Task TriggerRunAsync(string branchName, string? commitSha, CancellationToken ct)` to `IPipelineProvider`. The GitHub implementation calls `client.Actions.Workflows.CreateDispatch(...)`. This is optional — the orchestrator can try to trigger explicitly and fall back to push-triggered if the method throws `NotSupportedException`.

---

### 4.4 Missing `ProviderType` Property [MEDIUM]

**Problem:** The other 3 provider interfaces all have a `ProviderType` property. `IPipelineProvider` doesn't. This asymmetry means the orchestrator can't treat all providers uniformly for diagnostics.

**Proposed fix:** Add `PipelineProviderType ProviderType { get; }` to `IPipelineProvider` with an enum `PipelineProviderType { GitHubActions }`. Matches the pattern of the other 3 interfaces.

---

### 4.5 Poll Interval Baked Into Constructor [LOW]

**Problem:** `WaitForCompletionAsync` accepts `timeout` but not `pollInterval`. The interval is set at construction time and can't be adjusted per-call. Two sources of truth exist (`ProviderConfig.Settings["pollIntervalSeconds"]` and `PipelineConfiguration.ExternalCiPollInterval`).

**Proposed fix:** Add an optional `TimeSpan? pollInterval = null` parameter to `WaitForCompletionAsync`. If null, use the constructor default. This gives callers control without breaking existing code. Consolidate the two config sources into one.

---

## 5. Cross-Cutting Concerns

### 5.1 Duplicated `GitHubAppAuthService` Construction [MEDIUM]

**Problem:** The factory creates 3 independent `GitHubAppAuthService` instances for the same GitHub App installation (one each for Issue, Repository, Pipeline providers). Each has its own token cache and semaphore, so the same installation makes 3 separate token exchange calls.

**Proposed fix:** Cache `GitHubAppAuthService` instances by installation ID in the `ProviderFactory`. When multiple providers share the same `clientId + installationId`, they share a single auth service and token cache. This is a ~10-line change in the factory constructor.

---

### 5.2 No `ValidateAsync` on Any Provider [MEDIUM]

**Problem:** Credential/config errors only surface when the first API call fails deep in the pipeline. There's no fail-fast mechanism at pipeline start.

**Proposed fix:** Add `Task ValidateAsync(CancellationToken ct)` to all 4 provider interfaces. Implementations make a lightweight API call to verify credentials (e.g., `client.User.Current()` for GitHub, health check for the agent). The orchestrator calls `ValidateAsync` on all providers at the start of `StartPipelineAsync`, before creating the workspace or cloning.

---

### 5.3 No `IAsyncDisposable` on Any Provider [LOW]

**Problem:** Providers hold HTTP clients (`IGitHubClient`) but are never disposed. The orchestrator stores them in fields and replaces them on the next run without cleanup.

**Proposed fix:** Have all 4 provider interfaces extend `IAsyncDisposable`. The orchestrator disposes the previous providers when creating new ones in `StartPipelineAsync`, and in its own `Dispose` method. For providers that don't hold resources, `DisposeAsync` is a no-op.

---

### 5.4 Duplicated `GetClientAsync` Pattern [LOW]

**Problem:** All 3 GitHub providers copy-paste the same dual-auth client construction logic (~15 lines each). The `ProductHeaderValue("KiroWebUI-Pipeline")` string literal appears in 5 locations.

**Proposed fix:** Extract a `GitHubClientProvider` helper class that encapsulates the dual-auth pattern:

```csharp
internal class GitHubClientProvider
{
    public GitHubClientProvider(IGitHubClient? staticClient, string? apiUrl, Func<CancellationToken, Task<string>>? tokenProvider) { ... }
    public Task<IGitHubClient> GetClientAsync(CancellationToken ct) { ... }
}
```

All 3 GitHub providers compose this helper instead of duplicating the logic. The `ProductHeaderValue` is defined once.

---

## Priority Summary

### Do First (High — leaky abstractions, duplicated workarounds)
- 1.1 Agent session warm-up → `EnsureSessionAsync` or internal handling
- 1.2 Stall detection → move into provider
- 2.1 HEAD commit SHA → change `PushBranchAsync` return type or add method
- 2.2 Branch-ahead check → `HasCommitsAheadAsync`
- 2.3 File changes diff → `GetFileChangesAsync`
- 3.1 Update comment → `UpdateCommentAsync`
- 3.2 Comment search → `FindCommentByMarkerAsync` or `UpsertCommentAsync`
- 4.1 Log fetching → `GetJobLogsAsync` (enrichment exists but is private/hidden)

### Do Next (Medium — DRY, encapsulation, consistency)
- 1.3 ANSI stripping → strip inside provider
- 1.4 Method signature unification → single `ExecuteAsync` with `UseResume`
- 2.4 BaseBranch property
- 2.5 RepositoryFullName property
- 3.3 Remove dead `AcceptanceCriteria` field from `IssueDetail`
- 3.4 Label-filtered listing
- 4.2 CI log writer service
- 4.3 `TriggerRunAsync`
- 4.4 `ProviderType` on `IPipelineProvider`
- 5.1 Shared `GitHubAppAuthService` cache
- 5.2 `ValidateAsync` on all providers

### Do Later (Low — cleanup, nice-to-have)
- 1.5 Remove or use `GetHealthStatus`
- 2.6 Delete vestigial static helpers
- 3.5 Issue state management (labels, close)
- 4.5 Per-call poll interval
- 5.3 `IAsyncDisposable`
- 5.4 Extract `GitHubClientProvider` helper
