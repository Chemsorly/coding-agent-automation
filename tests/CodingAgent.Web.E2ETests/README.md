# CodingAgent.Web.E2ETests

End-to-end tests for the Coding Agent pipeline using Playwright and Blazor TestServer.

## Architecture

The harness was rebuilt in Spec 045 against the four-service Kubernetes architecture. Four legacy factory
base classes (`E2EWebApplicationFactory`, `DbModeE2EWebApplicationFactory`, `K8sModeE2EWebApplicationFactory`,
`K8sChatE2EWebApplicationFactory`) were collapsed into a single `E2EWebApplicationFactory` with an
in-memory Kubernetes stub. `CrossModeParityTests.cs` was deleted.

## Running tests

The full suite takes about 15 minutes. While you work on tests, run only the class you change and
let CI run the full suite.

### Without Docker (agent pods, Linux and Windows dev machines)

`dotnet test` on this project is a silent no-op: the csproj keeps `IsTestProject=false` so the
solution-wide unit test run skips it. Build with the property set, then run the DLL with
`dotnet vstest`:

```bash
dotnet build tests/CodingAgent.Web.E2ETests/ -c Debug -p:IsTestProject=true
dotnet vstest tests/CodingAgent.Web.E2ETests/bin/Debug/net10.0/CodingAgent.Web.E2ETests.dll --TestCaseFilter:"FullyQualifiedName~RunsListTests" --Blame:"CollectHangDump;HangDumpType=None;TestTimeout=5m"
```

- Classes that derive from `HeadlessE2ETestBase` need no browser.
- Classes that derive from `E2ETestBase` drive Chromium through Playwright. They need the browser
  that matches the repository's `Microsoft.Playwright` and Chromium's system libraries. The agent
  images include neither. In an agent pod (non-root, no sudo), install both after the build, then
  run the test with the `user-apt` environment loaded:

  ```bash
  PW=tests/CodingAgent.Web.E2ETests/bin/Debug/net10.0/.playwright
  $PW/node/linux-*/node $PW/package/cli.js install --only-shell chromium
  user-apt install libasound2t64 libatk1.0-0t64 libatk-bridge2.0-0t64 libatspi2.0-0t64 libdbus-1-3 libgbm1 libxcomposite1 libxdamage1 libxfixes3 libxkbcommon0 libxrandr2
  . ~/.user-apt/env && dotnet vstest tests/CodingAgent.Web.E2ETests/bin/Debug/net10.0/CodingAgent.Web.E2ETests.dll --TestCaseFilter:"FullyQualifiedName~SettingsCrudTests" --Blame:"CollectHangDump;HangDumpType=None;TestTimeout=5m"
  ```

  This downloads about 150 MB, so do it only when you work on a Playwright class. `user-apt`
  skips packages that are already installed (see `docs/configuration.md`). A test that fails
  with `error while loading shared libraries: <name>.so` ran without the libraries: load
  `~/.user-apt/env` in the same shell command as `dotnet vstest`.
- On a Linux dev machine, install the system libraries (needs root) and the browser that matches
  the repository's `Microsoft.Playwright` (as your own user) once:

  ```bash
  PW=tests/CodingAgent.Web.E2ETests/bin/Debug/net10.0/.playwright
  sudo $PW/node/linux-*/node $PW/package/cli.js install-deps chromium
  $PW/node/linux-*/node $PW/package/cli.js install --only-shell chromium
  ```

  On Windows, run `pwsh tests/CodingAgent.Web.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium`.
- In an agent pod, do not run the whole suite. It is long and memory-heavy. Run the classes you
  changed, and let the pipeline's CI round run the rest.
- The `--Blame` option aborts a test that makes no progress for 5 minutes and prints its name
  ("The test running when the crash occurred: ..."). CI uses the same setting.

### With Docker

```bash
# Build the E2E test image (first time or after code changes)
docker build -f dockerfiles/e2e-tests.Dockerfile -t e2e-tests .

# Run E2E tests
docker run --rm --ipc=host e2e-tests
```

The `--ipc=host` flag is required for Chromium shared-memory stability.

## CI

The workflow (`.github/workflows/e2e-tests.yml`) runs on `push` to `main` and on all pull requests.
It splits the suite across three matrix jobs, `e2e (shard N/3)`: the sorted list of E2E test
methods is dealt round-robin, so each shard runs about a third of the browser and the headless
tests. A gate job named `e2e` passes only when every shard passes. To find a failed test, open the
failed shard's log; the `e2e` job only reports that a shard failed.

Each shard job has `timeout-minutes: 30`. GitHub reports a job that hits this limit as cancelled,
not failed, and its log ends without a test summary. The 5-minute hang timeout fails the run first
and names the hung test.

## Infrastructure

| File | Purpose |
|------|---------|
| `E2EWebApplicationFactory.cs` | Single factory targeting the four-service architecture |
| `E2ETestBase.cs` | Base class for all E2E tests |
| `E2EFixture.cs` | xUnit collection fixture (shared browser instance) |
| `SchedulerE2EWebApplicationFactory.cs` | Extended factory for Scheduler-level tests |
| `FakeAgentClient.cs` | In-process fake agent for tests that don't need a real agent pod |
| `FakeJobController.cs` | Stubs K8s Job dispatch for in-process tests |
