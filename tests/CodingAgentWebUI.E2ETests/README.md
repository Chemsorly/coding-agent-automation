# CodingAgentWebUI.E2ETests

End-to-end tests for the Coding Agent pipeline using Playwright and Blazor TestServer.

## Architecture

The harness was rebuilt in Spec 045 against the four-service Kubernetes architecture. Four legacy factory
base classes (`E2EWebApplicationFactory`, `DbModeE2EWebApplicationFactory`, `K8sModeE2EWebApplicationFactory`,
`K8sChatE2EWebApplicationFactory`) were collapsed into a single `E2EWebApplicationFactory` with an
in-memory Kubernetes stub. `CrossModeParityTests.cs` was deleted.

## Running tests

```bash
# Build the E2E test image (first time or after code changes)
docker build -f dockerfiles/e2e-tests.Dockerfile -t e2e-tests .

# Run E2E tests
docker run --rm --ipc=host e2e-tests
```

The `--ipc=host` flag is required for Chromium shared-memory stability.

## CI

The workflow (`.github/workflows/e2e-tests.yml`) runs on `push` to `main` and on all pull requests.

## Infrastructure

| File | Purpose |
|------|---------|
| `E2EWebApplicationFactory.cs` | Single factory targeting the four-service architecture |
| `E2ETestBase.cs` | Base class for all E2E tests |
| `E2EFixture.cs` | xUnit collection fixture (shared browser instance) |
| `SchedulerE2EWebApplicationFactory.cs` | Extended factory for Scheduler-level tests |
| `FakeAgentClient.cs` | In-process fake agent for tests that don't need a real agent pod |
| `FakeJobController.cs` | Stubs K8s Job dispatch for in-process tests |
