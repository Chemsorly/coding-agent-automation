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
dotnet exec tests/CodingAgentWebUI.E2ETests/bin/Debug/net10.0/Microsoft.Playwright.dll install --with-deps chromium

# Run tests
dotnet test tests/CodingAgentWebUI.E2ETests/ --filter "Category=E2E"
```

## How It Works

- `E2EWebApplicationFactory` starts the real Blazor Server app on a random localhost port
- Playwright launches a headless Chromium browser and navigates to the app
- All external providers (GitHub, Kiro CLI, Git) are replaced with in-memory fakes
- `FakeAgentClient` connects to the real SignalR hub for multi-agent dispatch tests
- Tests use Page Object Model with `data-testid` selectors for stability

## Adding a New Test

1. Create a test class in `Tests/` with `[Trait("Category", "E2E")]`
2. Inherit from `E2ETestBase` (provides `Page`, `Fixture`, fake access)
3. Use page objects from `PageObjects/` for interactions
4. Add `data-testid` attributes to Blazor components as needed
5. Assert on final states (use `TaskCompletionSource` gates for intermediate states)

## CI

- **Main CI** (`ci.yml`): Compiles E2E project but skips execution via `--filter "Category!=E2E"`
- **E2E workflow** (`e2e-tests.yml`): Uses the same Docker image to run tests on PR branches
- Screenshots are uploaded as artifacts on test failure

## Architecture

```
Single container (e2e-tests):
├── dotnet test (test runner process)
│   ├── WebApplicationFactory<Program> → Kestrel on localhost:{random}
│   │   ├── Blazor Server (real rendering)
│   │   ├── SignalR Hub /hubs/agent (real)
│   │   └── DI: fake providers injected
│   ├── Playwright → local Chromium (headless, pre-installed in image)
│   └── FakeAgentClient → SignalR client (for dispatch tests)
└── /ms-playwright/chromium-xxx/ (browser binary)
```
