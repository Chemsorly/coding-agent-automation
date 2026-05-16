1. **[WARNING]** `AgentProviderSection.razor` still contains hardcoded provider setting keys on lines 22, 190, 191, 216, and 223 that were not replaced with `ProviderSettingKeys` constants:
   - Line 22: `"executablePath"`, `"timeout"`, `"model"`
   - Lines 190, 216: `"timeout"`
   - Lines 191, 223: `"agentName"`
   Additionally, `ProviderSettingKeys` is missing `Timeout` and `AgentName` constants. This violates the acceptance criteria that "No hardcoded string literals remain for the high-priority items" and that the class should contain "all provider config dictionary keys."

2. **[WARNING]** `AgentProviderFactory.cs` line 130 hardcodes the environment variable name `"OPENCODE_SERVER_PASSWORD not set."` in the exception message, while the preceding `Environment.GetEnvironmentVariable` call correctly uses `AgentEnvironmentVariables.OpenCodeServerPassword`. The message should reference the constant.

3. **[SUGGESTION]** Git restriction instructions (`GitRestrictionFull` and `GitRestrictionShort`) were extracted to `PipelineConstants` rather than `PromptBuilder` as specified in the acceptance criteria. While they are referenced correctly and function identically, the stated requirement expects them to live on `PromptBuilder`.

--- Agent: DotNetSpecialist ---
No .NET-specific issues were identified in the reviewed changes.

--- Agent: SecurityReviewer ---
No security issues were identified in the reviewed changes.

--- Agent: AcceptanceCriteria ---
# Acceptance Criteria Review — Issue #358

## Findings

### 1. [CRITICAL] No hardcoded string literals remain for the high-priority items (grep-verifiable)

**Acceptance Criterion:**
> No hardcoded string literals remain for the high-priority items (grep-verifiable)

**What is missing:**
Multiple source and test files still contain hardcoded provider settings dictionary keys and environment variable names that are listed as high-priority items in the issue.

**Source files with remaining hardcoded provider keys:**
- `src/CodingAgentWebUI/Components/Pages/PipelineProviderSection.razor` lines 98–103: `"apiUrl"`, `"clientId"`, `"installationId"`, `"privateKeyBase64"`, `"owner"`, `"repo"`
- `src/CodingAgentWebUI/Components/Pages/IssueProviderSection.razor` lines 150–155: `"apiUrl"`, `"clientId"`, `"installationId"`, `"privateKeyBase64"`, `"owner"`, `"repo"`
- `src/CodingAgentWebUI/Components/Pages/RepoProviderSection.razor` lines 151–157: `"apiUrl"`, `"clientId"`, `"installationId"`, `"privateKeyBase64"`, `"owner"`, `"repo"`, `"baseBranch"`
- `src/CodingAgentWebUI/Components/Pages/SettingsModals.razor` line 94: `SharedGitHubSettingKeys = ["apiUrl", "clientId", "installationId", "privateKeyBase64", "owner", "repo"]`
- `src/CodingAgentWebUI/Components/Pages/ProviderSelectionPanel.razor` lines 17, 29, 42: `"owner"`, `"repo"`, `"baseBranch"`
- `src/CodingAgentWebUI/Components/Pages/IssueListPanel.razor` line 15: `"owner"`, `"repo"`
- `src/CodingAgentWebUI.Agent.OpenCode/OpenCodeAgentProvider.cs` line 637: `"OPENCODE_SERVER_PASSWORD"` in `ExcludedEnvKeys`

**Test files with remaining hardcoded provider keys / env vars:**
- `tests/CodingAgentWebUI.UnitTests/Components/SettingsPageComponentTests.cs` lines 278, 284, 330: `["owner"]`, `["repo"]`, `["baseBranch"]`
- `tests/CodingAgentWebUI.UnitTests/Components/IssueListPanelComponentTests.cs` lines 36, 123, 125: `["owner"]`, `["repo"]`
- `tests/CodingAgentWebUI.UnitTests/Components/AgentProviderSectionComponentTests.cs` lines 55, 83, 160, 302: `["executablePath"]`, `["model"]`
- `tests/CodingAgentWebUI.Pipeline.UnitTests/Services/PipelineOrchestrationServiceTests.cs` lines 221, 311: `["model"]`
- `tests/CodingAgentWebUI.IntegrationTests/Helpers/IntegrationTestBase.cs` line 139: `["model"]`
- Multiple test files hardcode environment variable names (`AGENT_TYPE`, `AGENT_API_KEY`, `OPENCODE_SERVER_PASSWORD`) instead of referencing `AgentEnvironmentVariables` constants.

These literals are precisely the high-priority items the issue targets for elimination.

---

### 2. [WARNING] `ProviderSettingKeys` class exists with all provider config dictionary keys

**Acceptance Criterion:**
> `ProviderSettingKeys` class exists with all provider config dictionary keys

**What is missing:**
The class exists and covers the keys explicitly listed in the issue’s suggested approach. However, the agent provider settings UI and persistence also use `"timeout"` and `"agentName"` as dictionary keys (see `AgentProviderSection.razor` lines 22, 190, 191, 216, 223). `ProviderSettingKeys` does not define constants for these keys. Because the acceptance criterion states "all provider config dictionary keys," the omission leaves a gap where the compile-time safety the issue intends to provide does not apply to every key used in `ProviderConfig.Settings`.

---

### 3. [WARNING] `AgentEnvironmentVariables` class exists for env var names used in 2+ files

**Acceptance Criterion:**
> `AgentEnvironmentVariables` class exists for env var names used in 2+ files

**What is missing:**
The class exists and is referenced by all agent-side source files (`Program.cs`, `AgentWorkerService.cs`, `AgentProviderFactory.cs`). However, one source file does **not** use the constant:
- `src/CodingAgentWebUI.Agent.OpenCode/OpenCodeAgentProvider.cs` line 637 hardcodes `"OPENCODE_SERVER_PASSWORD"` in the `ExcludedEnvKeys` hash set instead of using `AgentEnvironmentVariables.OpenCodeServerPassword`.

Additionally, many test files that exercise environment-variable logic still hardcode the string literals (e.g., `AgentApiKeyAuthHandlerTests.cs`, `AgentWorkerServiceTests.cs`, `OpenCodeFactoryAndLifecycleTests.cs`). The acceptance criteria do not limit the verification to source files only.

---

### 4. [WARNING] Git restriction instruction extracted to `PromptBuilder` constant (2 variants: full + short)

**Acceptance Criterion:**
> Git restriction instruction extracted to `PromptBuilder` constant (2 variants: full + short)

**What is missing:**
The two variants exist (`GitRestrictionFull` and `GitRestrictionShort`) and are correctly referenced by `PromptBuilder` and `QualityGateOrchestrator.RetryLoop`. However, they were placed on `PipelineConstants` rather than on `PromptBuilder` as the acceptance criterion explicitly requires. The behavior is identical, but the stated requirement expects the constants to live on `PromptBuilder`.

---

### 5. [SUGGESTION] `AgentDefaults` class exists with CLI path, OpenCode URL, HttpClient name, and timeouts

**Acceptance Criterion:**
> `AgentDefaults` class exists with CLI path, OpenCode URL, HttpClient name, and timeouts

**Observation:**
Fully satisfied — the class contains `KiroCliPath`, `OpenCodeBaseUrl`, `OpenCodeHttpClientName`, `OpenCodeRequestTimeout`, and `HeartbeatInterval`. All source and test consumers reference them.

**Suggestion:**
The physical file is located in `src/CodingAgentWebUI.Pipeline/AgentDefaults.cs` and declares `namespace CodingAgentWebUI.Agent;`. The issue Requirements section states: "Agent-specific constants (CLI paths, env vars, HttpClient names) should live in `CodingAgentWebUI.Agent`." Keeping the file in `CodingAgentWebUI.Pipeline` is pragmatic (avoids circular references), but consider moving it to the Agent project if the reference graph allows.

---

### 6. [SUGGESTION] JWT generation deduplicated into a shared helper

**Acceptance Criterion:**
> JWT generation deduplicated into a shared helper

**Observation:**
Fully satisfied — `GitHubJwtGenerator.GenerateFromPem` / `GenerateFromBase64` live in `CodingAgentWebUI.Pipeline/GitHub/GitHubJwtGenerator.cs` and are called by both `GitHubAppAuthService` and `TokenVendingService`.

**Suggestion:**
The issue Requirements section specifies the helper should live in the Infrastructure project. It currently resides in `CodingAgentWebUI.Pipeline`. This works because Infrastructure already references Pipeline, but aligning the file with the stated location would be cleaner.

---

## Fully Met Criteria

The following acceptance criteria are fully satisfied with no findings:

- **`HubRoutes.Agent` constant exists and is referenced by all hub path consumers (source + tests)** — Verified via grep: no remaining `"/hubs/agent"` literals outside `HubRoutes.cs`; all consumers in source and tests reference `HubRoutes.Agent`.
- **`CommentMarkers` class exists with named constants; `PromptBuilder.ExcludedCommentMarkers` references them; all detection/emission sites use the constants** — `ExcludedCommentMarkers` array references `CommentMarkers.AnalysisHeader`, `GateRejection`, `GateWontDo`, and `IssueFeedback`. Emission sites (`FeedbackCommentFormatter`, `IssueAnalysisComment`) and detection sites (`GitHubRepositoryProvider`) all use the constants.
- **`GitConstants` class exists with commit signature and token username** — Present and referenced by `GitHubRepositoryProvider` and `BrainUpdateService`.
- **`PipelineConstants` extended with `OutputTailLineCount`, `NoOutputFallback`, `BranchNamePrefix`, `AutomatedFooter`, `DefaultConfigBaseDirectory`** — All constants present and referenced by their consumers.
- **All existing tests pass / Build succeeds with no warnings** — These criteria could not be verified in the review environment because no compatible .NET SDK is installed. The code changes appear syntactically correct, but a full `dotnet build` and `dotnet test` execution is required to confirm.

