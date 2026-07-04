# E2E Tests — Playwright + WebApplicationFactory

End-to-end browser tests for the orchestrator pipeline using Playwright, WebApplicationFactory, and a fake SignalR agent client.

## Running Tests (Docker — recommended)

No local Playwright installation needed. Everything runs inside a container:

```bash
# Build the E2E test image
docker build -f dockerfiles/e2e-tests.Dockerfile -t e2e-tests .

# Run E2E tests
docker run --rm --ipc=host e2e-tests
```

The `--ipc=host` flag is required for Chromium stability (prevents shared memory issues).

To extract test results:
```bash
docker run --rm --ipc=host -v ./TestResults:/src/TestResults e2e-tests
```

## Running Tests (Local — requires Playwright install)

If you prefer running without Docker:

```bash
# One-time: install Playwright Chromium
dotnet build tests/CodingAgentWebUI.E2ETests/
pwsh tests/CodingAgentWebUI.E2ETests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium

# Run tests
dotnet vstest tests/CodingAgentWebUI.E2ETests/bin/Debug/net10.0/CodingAgentWebUI.E2ETests.dll --TestCaseFilter:Category=E2E
```

> **Note:** The project uses `IsTestProject=false` to avoid inclusion in solution-wide `dotnet test` runs. Use `dotnet vstest` targeting the DLL directly, or run via Docker.

## How It Works

- `E2EWebApplicationFactory` starts the real Blazor Server app on a random localhost port using .NET 10's `UseKestrel()` API
- Playwright launches a headless Chromium browser and navigates to the app
- All external providers (GitHub, Kiro CLI, Git) are replaced with in-memory fakes
- `FakeAgentClient` connects to the real SignalR hub for multi-agent dispatch tests
- Tests use Page Object Model with `data-testid` selectors for stability

## Test Infrastructure Modes

The test suite supports three infrastructure configurations, each with its own `WebApplicationFactory` and fixture:

| Mode | Factory Class | Fixture | Purpose |
|------|--------------|---------|---------|
| **Standard** | `E2EWebApplicationFactory` | `E2EFixture` | JSON file store, SignalR dispatch, in-memory state |
| **DB Mode** | `DbModeE2EWebApplicationFactory` | `DbModeE2EFixture` | PostgreSQL persistence (in-memory SQLite), DB-backed config/work items |
| **K8s Mode** | `K8sModeE2EWebApplicationFactory` | `K8sModeE2EFixture` | Kubernetes work distribution, ReconciliationService (no HeartbeatMonitor) |

### Standard Mode
Default test mode. Uses in-memory fakes for all providers. Configuration stored in JSON files. Dispatch via SignalR.

### DB Mode
Tests database-backed persistence: config import/export, work item lifecycle, DB-based template/project storage. Uses an in-memory SQLite database via `IDbContextFactory<PipelineDbContext>`.

### K8s Mode
Tests Kubernetes work distribution: `KubernetesWorkDistributor`, dispatch service cycles, reconciliation service. No `HeartbeatMonitorService` (K8s mode uses `ReconciliationService` instead).

## Adding a New Test

1. Create a test class in `Tests/` with `[Trait("Category", "E2E")]`
2. Inherit from `E2ETestBase` (provides `Page`, screenshot-on-failure)
3. Choose the appropriate fixture for the infrastructure mode you need
4. Use page objects from `PageObjects/` for interactions
5. Add `data-testid` attributes to Blazor components as needed
6. Assert on final states (use `TaskCompletionSource` gates for intermediate states)

## CI

- **Main CI** (`ci.yml`): Compiles E2E project but skips execution (project has `IsTestProject=false`)
- **E2E workflow** (`e2e-tests.yml`): Uses the Docker image to run tests on PR branches
- Screenshots are uploaded as artifacts on test failure

## Architecture

```
Single container (e2e-tests):
├── dotnet vstest (test runner process)
│   ├── WebApplicationFactory<Program> → Kestrel on localhost:{random}
│   │   ├── Blazor Server (real rendering)
│   │   ├── SignalR Hub /hubs/agent (real)
│   │   └── DI: fake providers injected
│   ├── Playwright → local Chromium (headless, pre-installed in image)
│   └── FakeAgentClient → SignalR client (for dispatch tests)
└── /ms-playwright/chromium-xxx/ (browser binary)
```
