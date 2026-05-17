# Correctness Review — Issue #358 [REF-02]

1. **[WARNING]** `ProviderSettingKeys.Token` and `ProviderSettingKeys.TokenValue` are both defined as `"token"` — the `TokenVendingService` writes via `TokenValue` while consumers (e.g., `AgentProviderFactory`, `LocalConsolidationExecutor`) read via `Token`. This works at runtime because both resolve to the same string, but having two constants for the same key creates confusion about which to use. The TODO in the file acknowledges this, but it should be resolved before merging to avoid future drift if someone changes one but not the other.

2. **[WARNING]** `AgentProviderSection.razor` still contains hardcoded `"timeout"` and `"agentName"` dictionary keys (lines 192, 193, 218, 225) that are not represented in `ProviderSettingKeys`. The acceptance criterion states "all provider config dictionary keys" should be in the class. These are actively used settings keys that appear in multiple places (the razor component reads and writes them). A `TODO` comment was added acknowledging this, but the keys remain hardcoded.

3. **[WARNING]** The `.agent/full-diff.txt` file shows `Timeout = AgentDefaults.OpenCodeRequestTimeout` for the chat request in `AgentWorkerService.cs` (line 365), which would be a behavioral change from 30 minutes to 60 minutes. However, the actual committed code uses `AgentDefaults.ChatRequestTimeout` (30 minutes), preserving the original behavior. The diff file is stale and does not reflect the final state. This is not a code issue but means the review artifact is unreliable — the actual code is correct.

4. **[WARNING]** `PipelineConstants` defines alias constants (`BranchNamePrefix`, `AutomatedFooter`, `DefaultConfigBaseDirectory`) with `// TODO: ... alias is unused — remove if no consumer adopts it` comments. These aliases exist solely to match the acceptance criteria naming but are never referenced by any consumer. The actual consumers use the original names (`BranchPrefix`, `AutomatedCommitSuffix`, `ConfigBaseDirectory`). These dead constants add noise without providing value.

5. **[SUGGESTION]** The acceptance criterion states "Git restriction instruction extracted to `PromptBuilder` constant (2 variants: full + short)" but the constants live on `PipelineConstants` instead. They are correctly referenced by `PromptBuilder` and `QualityGateOrchestrator.RetryLoop`, so behavior is correct. This is a minor deviation from the stated requirement location.

6. **[SUGGESTION]** The error message in `AgentProviderFactory.cs` line 130 hardcodes the string `"OPENCODE_SERVER_PASSWORD not set."` rather than interpolating `AgentEnvironmentVariables.OpenCodeServerPassword`. If the env var name ever changes, the error message would become misleading. Consider: `$"{AgentEnvironmentVariables.OpenCodeServerPassword} not set."`.

7. **[SUGGESTION]** `ProviderSettingKeys` is missing constants for `"timeout"` and `"agentName"` which are used in `AgentProviderSection.razor`. While these may be considered lower priority, they are provider config dictionary keys used in the settings UI and should eventually be added for completeness.

--- Agent: SecurityReviewer ---
# Security Review Findings

**Reviewer:** SecurityReviewer
**Scope:** Issue #358 — Extract hardcoded literals and magic numbers into centralized constants classes

---

## Findings

No security issues were identified in this change set.

---

## Rationale

This is a pure mechanical refactoring that replaces hardcoded string literals with references to centralized constants classes. The review verified:

1. **No hardcoded credentials, API keys, or tokens** — The constants classes (`AgentEnvironmentVariables`, `ProviderSettingKeys`, `AgentDefaults`) contain only environment variable *names* and dictionary *key names*, not actual secret values.

2. **No new logging of sensitive data** — No new logging statements were introduced. The pre-existing `ResolveApiKey` method that logs a generated API key (`AgentApiKeyAuthHandler.cs:124`) was not introduced or modified by this PR (only the env var lookup was changed from a literal to a constant reference).

3. **No new endpoints or auth changes** — No `[Authorize]`, `[AllowAnonymous]`, or route mapping changes.

4. **No path traversal risks** — No file path operations using user input were introduced.

5. **No SQL injection or insecure deserialization** — No database or deserialization code was added.

6. **No SSRF vectors** — No new user-controlled URLs passed to HTTP clients. The `OpenCodeBaseUrl` constant (`http://127.0.0.1:4096`) is a default value that can be overridden via environment variable, but this pattern is pre-existing and unchanged.

7. **No secrets in committed files** — All values in the new constants files are either environment variable names (e.g., `"AGENT_API_KEY"`), dictionary key names (e.g., `"privateKeyBase64"`), or non-sensitive defaults (e.g., file paths, URLs to localhost).

**Verdict:** Clean — no security findings.

--- Agent: AcceptanceCriteria ---
# Acceptance Criteria Review — Issue #358

## Findings

### 1. `HubRoutes.Agent` constant exists and is referenced by all hub path consumers (source + tests)

**FULLY MET.** `HubRoutes.Agent` exists in `src/CodingAgentWebUI.Pipeline/HubRoutes.cs` with value `"/hubs/agent"`. Grep confirms zero remaining hardcoded `"/hubs/agent"` literals in both `src/` and `tests/`. All consumers (source: `Program.cs`, `HubConnectionManager.cs`; tests: 10+ files) reference the constant.

---

### 2. `ProviderSettingKeys` class exists with all provider config dictionary keys

**[WARNING]** — Acceptance criterion is PARTIALLY met.

> `ProviderSettingKeys` class exists with all provider config dictionary keys

The class exists and covers the keys listed in the issue's suggested approach. However, `AgentProviderSection.razor` still uses hardcoded `"timeout"` (lines 192, 218) and `"agentName"` (lines 193, 225) as dictionary keys. `ProviderSettingKeys` does not define constants for these keys. Since the acceptance criterion states "all provider config dictionary keys," these two keys represent a gap.

---

### 3. `CommentMarkers` class exists with named constants; `PromptBuilder.ExcludedCommentMarkers` references them; all detection/emission sites use the constants

**FULLY MET.** `CommentMarkers` class contains `AnalysisHeader`, `GateRejection`, `GateWontDo`, `IssueFeedback`, `IssueFeedbackHeader`, `PipelinePrefix`, and `AgentCommentPrefix`. `PromptBuilder.ExcludedCommentMarkers` references `CommentMarkers.AnalysisHeader`, `GateRejection`, `GateWontDo`, and `IssueFeedback`. Detection site (`GitHubRepositoryProvider.IsPipelineGeneratedComment`) uses `CommentMarkers.PipelinePrefix` and `CommentMarkers.AgentCommentPrefix`. Emission site (`FeedbackCommentFormatter`) uses `CommentMarkers.IssueFeedbackHeader`.

---

### 4. `AgentDefaults` class exists with CLI path, OpenCode URL, HttpClient name, and timeouts

**FULLY MET.** `AgentDefaults` contains `KiroCliPath`, `OpenCodeBaseUrl`, `OpenCodeHttpClientName`, `OpenCodeRequestTimeout`, `ChatRequestTimeout`, and `HeartbeatInterval`. All source and test consumers reference these constants.

---

### 5. `AgentEnvironmentVariables` class exists for env var names used in 2+ files

**FULLY MET.** `AgentEnvironmentVariables` class exists in `src/CodingAgentWebUI.Pipeline/AgentEnvironmentVariables.cs` with all 10 environment variable constants. Source files (`Program.cs`, `AgentWorkerService.cs`, `AgentProviderFactory.cs`, `AgentApiKeyAuthHandler.cs`) and test files all reference the constants. The `OpenCodeAgentProvider.cs` `ExcludedEnvKeys` also uses `AgentEnvironmentVariables.OpenCodeServerPassword`.

---

### 6. `GitConstants` class exists with commit signature and token username

**FULLY MET.** `GitConstants` class exists in `src/CodingAgentWebUI.Infrastructure/GitConstants.cs` with `CommitAuthorName`, `CommitAuthorEmail`, and `TokenUsername`. `GitHubRepositoryProvider` uses `GitConstants.TokenUsername` for all 3 occurrences. No remaining hardcoded `"x-access-token"` in source.

---

### 7. `PipelineConstants` extended with `OutputTailLineCount`, `NoOutputFallback`, `BranchNamePrefix`, `AutomatedFooter`, `DefaultConfigBaseDirectory`

**FULLY MET.** All five constants are present in `PipelineConstants.cs`:
- `OutputTailLineCount = 10`
- `NoOutputFallback = "(no output)"`
- `BranchNamePrefix = BranchPrefix` (alias for `"feature/auto-"`)
- `AutomatedFooter = AutomatedCommitSuffix` (alias for `"Automated implementation via pipeline"`)
- `DefaultConfigBaseDirectory = ConfigBaseDirectory` (alias for `"config/pipeline"`)

Consumers (`PipelineFormatting.cs`) reference `PipelineConstants.AutomatedCommitSuffix` directly.

---

### 8. JWT generation deduplicated into a shared helper

**FULLY MET.** `GitHubJwtGenerator` class exists in `src/CodingAgentWebUI.Pipeline/GitHub/GitHubJwtGenerator.cs` with `GenerateFromPem` and `GenerateFromBase64` methods. Both `GitHubAppAuthService` and `TokenVendingService` call this shared helper.

---

### 9. Git restriction instruction extracted to `PromptBuilder` constant (2 variants: full + short)

**[WARNING]** — Acceptance criterion is PARTIALLY met.

> Git restriction instruction extracted to `PromptBuilder` constant (2 variants: full + short)

The two variants exist (`GitRestrictionFull` and `GitRestrictionShort`) and are correctly referenced by `PromptBuilder` (5 usages) and `QualityGateOrchestrator.RetryLoop` (1 usage). However, they are placed on `PipelineConstants` rather than on `PromptBuilder` as the acceptance criterion explicitly states. The behavior is identical, but the stated requirement says "extracted to `PromptBuilder` constant."

---

### 10. No hardcoded string literals remain for the high-priority items (grep-verifiable)

**FULLY MET** for the high-priority items listed in the issue:
- `"/hubs/agent"` — zero remaining (verified via grep)
- `"apiUrl"`, `"clientId"`, `"installationId"`, `"privateKeyBase64"` as dictionary keys — zero remaining in source or tests
- `"x-access-token"` — zero remaining (only in `GitConstants.cs` definition)
- `"Automated implementation via pipeline"` — zero remaining (only in `PipelineConstants.cs` definition)
- `"/home/ubuntu/.local/bin/kiro-cli"` — only in `AgentDefaults.cs` definition
- `"http://127.0.0.1:4096"` — only in `AgentDefaults.cs` definition
- `"OpenCode"` HttpClient name — only in `AgentDefaults.cs` definition

Note: `"timeout"` and `"agentName"` remain hardcoded in `AgentProviderSection.razor` but these are not listed as high-priority items in the issue.

---

### 11. All existing tests pass

**FULLY MET.** Build succeeds and all unit/integration tests pass:
- Pipeline.UnitTests: 828 passed
- Infrastructure.UnitTests: 380 passed
- UnitTests: 802 passed
- Agent.UnitTests: 303 passed
- KiroCliLib.UnitTests: 57 passed
- IntegrationTests: 89 passed

E2E tests (57 failures) fail due to infrastructure requirements (need running application server), not due to this refactoring.

---

### 12. Build succeeds with no warnings

**[CRITICAL]** — Acceptance criterion is NOT met at the time the quality gate ran.

> Build succeeds with no warnings

The quality gate build log shows **9 compilation errors** including:
1. Merge conflict markers in `src/CodingAgentWebUI.Agent/Program.cs` (3 errors: CS8300)
2. `ProviderSettingKeys` not in scope in `AgentProviderSection.razor` (6 errors: CS0103)

However, the **current working tree** builds successfully with 0 warnings and 0 errors. The `git status` shows `Program.cs` has `UU` (unmerged) status, indicating the merge conflict existed when the quality gate ran but has since been resolved. The quality gate log is stale relative to the current file state.

**Impact:** If the pipeline evaluates the quality gate log as-is, it will reject the PR. The actual code in the working tree compiles cleanly.

---

## Summary

| # | Criterion | Status |
|---|-----------|--------|
| 1 | HubRoutes.Agent | ✅ Fully met |
| 2 | ProviderSettingKeys | ⚠️ Missing `Timeout` and `AgentName` keys |
| 3 | CommentMarkers | ✅ Fully met |
| 4 | AgentDefaults | ✅ Fully met |
| 5 | AgentEnvironmentVariables | ✅ Fully met |
| 6 | GitConstants | ✅ Fully met |
| 7 | PipelineConstants extended | ✅ Fully met |
| 8 | JWT deduplicated | ✅ Fully met |
| 9 | Git restriction in PromptBuilder | ⚠️ Lives on PipelineConstants, not PromptBuilder |
| 10 | No hardcoded literals (high-priority) | ✅ Fully met |
| 11 | All existing tests pass | ✅ Fully met |
| 12 | Build succeeds with no warnings | ❌ Quality gate log shows failures (stale — current tree builds clean) |

--- Agent: DotNet Specialist ---
# .NET Specialist Review Findings

## Summary

The changes are a mechanical refactoring extracting hardcoded literals into constants classes. The .NET-specific patterns (disposal, async, DI, cancellation) are not materially affected by this refactoring. No critical issues found.

## Findings

1. [SUGGESTION] `src/CodingAgentWebUI.Pipeline/ProviderSettingKeys.cs` — `Token` and `TokenValue` are two distinct constants with the same value `"token"`. The `TokenVendingService` writes via `ProviderSettingKeys.TokenValue` and consumers read via `ProviderSettingKeys.Token`. While functionally correct at runtime, this creates a maintenance hazard: if someone changes one constant's value without the other, the read/write contract silently breaks. Consider making `TokenValue` an alias (`public const string TokenValue = Token;`) to make the relationship explicit and compiler-enforced.

2. [SUGGESTION] `src/CodingAgentWebUI.Pipeline/Models/PipelineConstants.cs` — Three alias constants (`BranchNamePrefix`, `AutomatedFooter`, `DefaultConfigBaseDirectory`) are added purely to satisfy acceptance criteria naming but have zero consumers in source or test code. These are dead code that will trigger IDE warnings (CS0414-adjacent) and add confusion. The existing TODOs acknowledge this, but the constants should either be used or removed.

3. [SUGGESTION] `src/CodingAgentWebUI.Pipeline/AgentDefaults.cs` — The `OpenCodeRequestTimeout` and `HeartbeatInterval` fields are `static readonly` (correct for `TimeSpan`), but `ChatRequestTimeout` is also `static readonly` with no XML doc indicating it differs from `OpenCodeRequestTimeout`. The comment says "60 minutes" for `OpenCodeRequestTimeout` and "30 minutes" for `ChatRequestTimeout`, which is correct, but the relationship between these two timeouts (HttpClient timeout must be >= individual request timeout) is not documented. A brief comment noting this invariant would prevent future maintainers from accidentally setting `ChatRequestTimeout > OpenCodeRequestTimeout`.

No issues found for:
- IDisposable resources (no new disposable patterns introduced)
- Async/await deadlock patterns (no sync-over-async introduced)
- DI lifetime mismatches (all new types are static classes)
- CancellationToken propagation (unchanged by this refactoring)
- ArgumentNullException.ThrowIfNull (no new public methods with reference parameters)
- Mutable collection exposure (no new collection-returning APIs)

