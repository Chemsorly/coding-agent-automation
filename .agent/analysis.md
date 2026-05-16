# Issue #358 Analysis: [REF-02] Extract hardcoded literals and magic numbers into centralized constants classes

## Overview

The issue requests centralization of hardcoded string literals, magic numbers, and repeated values into named constants classes. A significant portion of this work has **already been implemented** in the codebase — the constants classes themselves largely exist. The remaining work is **mechanical refactoring** to ensure every consumer (source *and* tests) references the constants instead of string literals.

---

## 1. Planned Approach

### Phase A — Finish creating missing constants classes
- **Create `AgentEnvironmentVariables`** (in `CodingAgentWebUI.Agent` or `CodingAgentWebUI.Pipeline` depending on project reference graph)
  - Extract all `Env*` prefixed constants out of the existing `AgentDefaults` class into this new class.
- **Extend `AgentDefaults`** with timeout constants
  - `OpenCodeRequestTimeout = TimeSpan.FromMinutes(30)`
  - `HeartbeatInterval = TimeSpan.FromSeconds(30)`

### Phase B — Update remaining source files with hardcoded literals
1. **`TokenVendingService.cs`** — Replace `"privateKeyBase64"`, `"clientId"`, `"installationId"`, `"apiUrl"`, `"repo"`, `"token"`, `"tokenExpiresAt"` with `ProviderSettingKeys` constants.
2. **`GitHubValidationService.cs`** — Replace `"apiUrl"`, `"clientId"`, `"installationId"`, `"privateKeyBase64"`, `"owner"`, `"repo"` with `ProviderSettingKeys` constants.
3. **`GitHubRepositoryProvider.cs`** — Replace 3 occurrences of `"x-access-token"` with `GitConstants.TokenUsername`; replace `"## 🤖"` and `"<!-- agent:"` with new `CommentMarkers` constants (see Phase D).
4. **`FeedbackCommentFormatter.cs`** — Replace `"## 🤖 Agent Feedback — Issue Quality"` with a new `CommentMarkers` constant.
5. **`PipelineFormatting.cs`** — Replace 3 occurrences of `"Automated implementation via pipeline"` with `PipelineConstants.AutomatedCommitSuffix`.
6. **Razor components** — `AgentProviderSection.razor` hardcodes `127.0.0.1:4096`, `kiro-cli`, and `OpenCode`; replace with `AgentDefaults`.
7. **Timeout hardcoding in Agent project** — `Program.cs`, `AgentWorkerService.cs`, `OpenCodeAgentProvider.cs`, `OpenCodeHealthMonitor.cs` contain magic `TimeSpan` literals that should reference `AgentDefaults`.

### Phase C — Update test files
- **All test files using `"/hubs/agent"`** (8 files, ~20 occurrences) should reference `HubRoutes.Agent`.
- **All test files initializing `ProviderConfig.Settings` dictionaries** should use `ProviderSettingKeys` instead of string literals. This is a large surface area affecting ~25+ test files across every test project.
- **Test files validating comment markers** should reference `CommentMarkers` constants.
- **Test files with hardcoded OpenCode URL / HttpClient name** should reference `AgentDefaults`.

### Phase D — Add missing `CommentMarkers` constants
The current `CommentMarkers` class is missing two generic prefixes used for detection:
- `PipelinePrefix = "## 🤖"`
- `AgentCommentPrefix = "<!-- agent:"`
- `IssueFeedbackHeader = "## 🤖 Agent Feedback — Issue Quality"` (optional but consistent)

If `IssueFeedbackHeader` is added, consider adding it to `PromptBuilder.ExcludedCommentMarkers`.

---

## 2. Affected Components

### New / Modified Constants Files
| File | Action |
|------|--------|
| `src/CodingAgentWebUI.Pipeline/HubRoutes.cs` | No changes needed (already exists and correct) |
| `src/CodingAgentWebUI.Pipeline/ProviderSettingKeys.cs` | No changes needed (already exists and correct) |
| `src/CodingAgentWebUI.Pipeline/CommentMarkers.cs` | Add `PipelinePrefix`, `AgentCommentPrefix`, optionally `IssueFeedbackHeader` |
| `src/CodingAgentWebUI.Pipeline/AgentDefaults.cs` | Add timeout constants; **or** split `Env*` constants into new `AgentEnvironmentVariables` |
| `src/CodingAgentWebUI.Agent/AgentEnvironmentVariables.cs` | **Create** — extract all environment variable names from `AgentDefaults` |
| `src/CodingAgentWebUI.Pipeline/Models/PipelineConstants.cs` | Already complete — no changes needed |
| `src/CodingAgentWebUI.Infrastructure/GitConstants.cs` | Already complete — no changes needed |
| `src/CodingAgentWebUI.Pipeline/GitHub/GitHubJwtGenerator.cs` | Already complete — no changes needed |

### Source Files Requiring Updates
| File | Project | Changes |
|------|---------|---------|
| `src/CodingAgentWebUI.Orchestration/TokenVendingService.cs` | Orchestration | Replace ~10 hardcoded dict keys with `ProviderSettingKeys` |
| `src/CodingAgentWebUI.Infrastructure/GitHub/GitHubValidationService.cs` | Infrastructure | Replace 6 hardcoded dict keys with `ProviderSettingKeys` |
| `src/CodingAgentWebUI.Infrastructure/GitHub/GitHubRepositoryProvider.cs` | Infrastructure | Replace `"x-access-token"` (3×) and comment marker prefixes |
| `src/CodingAgentWebUI.Pipeline/Services/FeedbackCommentFormatter.cs` | Pipeline | Replace header literal with `CommentMarkers.IssueFeedbackHeader` |
| `src/CodingAgentWebUI.Pipeline/Services/PipelineFormatting.cs` | Pipeline | Replace `"Automated implementation via pipeline"` (3×) with `PipelineConstants.AutomatedCommitSuffix` |
| `src/CodingAgentWebUI.Agent/Program.cs` | Agent | Replace `TimeSpan.FromMinutes(30)` with `AgentDefaults.OpenCodeRequestTimeout` |
| `src/CodingAgentWebUI.Agent/AgentWorkerService.cs` | Agent | Replace magic `TimeSpan` values with `AgentDefaults` constants |
| `src/CodingAgentWebUI.Agent.OpenCode/OpenCodeAgentProvider.cs` | Agent.OpenCode | Replace magic `TimeSpan` values with `AgentDefaults` constants |
| `src/CodingAgentWebUI.Agent.OpenCode/OpenCodeHealthMonitor.cs` | Agent.OpenCode | Replace magic `TimeSpan` values with `AgentDefaults` constants |
| `src/CodingAgentWebUI/Components/Pages/AgentProviderSection.razor` | WebUI | Replace hardcoded CLI path, URL, and client name with `AgentDefaults` |

### Test Files Requiring Updates
| Test Project | Files | Nature of Changes |
|--------------|-------|-------------------|
| `CodingAgentWebUI.Agent.UnitTests` | `OrchestratorProxyTests.cs`, `LocalPipelineExecutorTests.cs`, `LocalConsolidationExecutorTests.cs`, `AgentProviderFactoryTests.cs`, `BrainTokenScopingRegressionTests.cs`, `HubConnectionManagerTests.cs` | Replace `"/hubs/agent"` with `HubRoutes.Agent`; replace dict keys with `ProviderSettingKeys`; replace OpenCode URL/client with `AgentDefaults` |
| `CodingAgentWebUI.E2ETests` | `Infrastructure/FakeAgentClient.cs` | Replace `"/hubs/agent"` with `HubRoutes.Agent` |
| `CodingAgentWebUI.UnitTests` | `TokenVendingServiceTests.cs`, `BrainTokenRefreshRegressionTests.cs`, multiple component tests | Replace dict keys with `ProviderSettingKeys` |
| `CodingAgentWebUI.Infrastructure.UnitTests` | `ProviderFactoryTests.cs`, `GitHubRepositoryProviderTests.cs`, etc. | Replace dict keys and git/token strings with constants |
| `CodingAgentWebUI.Pipeline.UnitTests` | `AnalysisConfidenceGateTests.cs`, `PromptBuilderTests.cs`, etc. | Replace comment marker literals with `CommentMarkers` |

---

## 3. Test Coverage

### Existing Tests That Cover Affected Code
- **`HubConnectionManagerTests.cs`** — Tests hub URL formatting. Must be updated to use `HubRoutes.Agent` in assertions.
- **`TokenVendingServiceTests.cs`** — Tests token vending with private-key configs. Must use `ProviderSettingKeys`.
- **`GitHubRepositoryProviderTests.cs`** / `GitHubRepositoryProviderWireMockTests.cs` — Tests commit creation, branch naming, comment detection. Must use `GitConstants` and `CommentMarkers`.
- **`PromptBuilderTests.cs`** / `AnalysisConfidenceGateTests.cs` — Tests `ExcludedCommentMarkers`. Already references `CommentMarkers` in latest code; may need updates if new constants are added.
- **`LocalPipelineExecutorTests.cs`** / `LocalConsolidationExecutorTests.cs` — Tests executor orchestration. Must use `HubRoutes.Agent` and `ProviderSettingKeys`.
- **Component tests** (`SettingsPageComponentTests.cs`, `AgentProviderSectionComponentTests.cs`, etc.) — Initialize `ProviderConfig` objects. Must use `ProviderSettingKeys`.

### New Tests Needed
**No new unit tests are required.** This is a pure refactoring with no behavioral changes. However, we should verify the refactoring with:
- A **grep-based verification step** (CI or manual) to ensure no high-priority literals remain.
- A full **build + test run** to confirm no compilation errors or test regressions.

**Optional but recommended:** Add a simple unit test for the new `CommentMarkers` constants (e.g., verify `PipelinePrefix` is a prefix of `AnalysisHeader`) to prevent accidental changes.

---

## 4. Risks & Considerations

### Namespace / Project Location of `AgentDefaults`
The existing `AgentDefaults.cs` lives in `src/CodingAgentWebUI.Pipeline/` but declares `namespace CodingAgentWebUI.Agent;`. The issue says agent-specific constants should live in `CodingAgentWebUI.Agent`. Moving the file to the `CodingAgentWebUI.Agent` project could break cross-project references if `CodingAgentWebUI.Pipeline` does not reference `CodingAgentWebUI.Agent` (and it likely shouldn't, to avoid circular dependencies). **Recommendation:** Keep the constants in `CodingAgentWebUI.Pipeline` (where they already are) since that's the lowest common denominator project that all consumers can reference. Creating `AgentEnvironmentVariables` in the same location is the safest path.

### Razor File Constants
`.razor` files can reference C# constants via `@using` directives, but they can make the markup slightly more verbose. Ensure `@using CodingAgentWebUI.Pipeline;` is present in `_Imports.razor` or at the top of the affected component.

### Test File Changes at Scale
Updating ~25+ test files is mechanical but creates a large diff. There is a small risk of typo-induced compilation errors. A structured approach (one test project at a time) and a full `dotnet test` after each batch mitigates this.

### Comment Marker Detection Logic
`GitHubRepositoryProvider.IsPipelineGeneratedComment` uses `"## 🤖"` and `"<!-- agent:"` as *prefix* checks. Adding `CommentMarkers.PipelinePrefix` and `CommentMarkers.AgentCommentPrefix` must preserve the exact string values to avoid breaking comment filtering logic.

### Backward Compatibility
- **No breaking changes** to public APIs.
- **No behavioral changes** — all constant values match the literals they replace.
- Provider settings dictionary keys remain the same strings; only the *source code representation* changes.

### Greppable Verification
After implementation, the following grep commands should return **zero matches** in `src/` (excluding XML doc comments):
- `grep -r '"/hubs/agent"' src/`
- `grep -r '"apiUrl"\|"clientId"\|"installationId"\|"privateKeyBase64"' src/ --include="*.cs"` (excluding `ProviderSettingKeys.cs`)
- `grep -r '"x-access-token"' src/`
- `grep -r '"Automated implementation via pipeline"' src/`

(Tests are also expected to be clean per acceptance criteria.)

---

## Summary of What Is Already Done vs. Remaining

| Acceptance Criterion | Status | Remaining Work |
|----------------------|--------|----------------|
| `HubRoutes.Agent` exists and referenced by all hub path consumers | 🟡 Partial | 8 test files still hardcode `"/hubs/agent"` |
| `ProviderSettingKeys` class exists with all keys | 🟢 Done | `TokenVendingService.cs`, `GitHubValidationService.cs`, `.razor` files, and many tests still use literals |
| `CommentMarkers` class exists with named constants | 🟡 Partial | `GitHubRepositoryProvider.cs` and `FeedbackCommentFormatter.cs` still use literals; missing generic prefix constants |
| `AgentDefaults` class exists with CLI path, OpenCode URL, HttpClient name, and timeouts | 🟡 Partial | Missing timeout constants; `AgentEnvironmentVariables` does not exist |
| `AgentEnvironmentVariables` class exists for env var names | 🔴 Not Done | Must be created by splitting from `AgentDefaults` |
| `GitConstants` class exists with commit signature and token username | 🟡 Partial | `GitHubRepositoryProvider.cs` still hardcodes `"x-access-token"` (3×) |
| `PipelineConstants` extended with requested values | 🟢 Done | `PipelineFormatting.cs` still hardcodes `AutomatedCommitSuffix` value (3×) |
| JWT generation deduplicated into shared helper | 🟢 Done | No remaining work |
| Git restriction instruction extracted to `PromptBuilder` constant | 🟢 Done | No remaining work |

**Conclusion:** The issue is a well-scoped refactoring where the infrastructure (constants classes) is mostly in place. The bulk of the remaining effort is updating consumers — particularly test files — to reference the existing constants. No architectural or design decisions are required.
