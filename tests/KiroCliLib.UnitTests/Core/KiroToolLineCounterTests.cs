using AwesomeAssertions;
using KiroCliLib.Core;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests for <see cref="KiroToolLineCounter"/>. Each test feeds the exact sequences from the issue's
/// acceptance criteria; each line is one <see cref="KiroToolLineCounter.ProcessLine"/> call.
/// </summary>
public class KiroToolLineCounterTests
{
    // ── One MCP call that fails ──────────────────────────────────────────────

    [Fact]
    public void OneMcpCallThatFails_AttributesFailed()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @sonarqube/get_issues");
        counter.ProcessLine("[tool] status: Failed");

        var snap = counter.Snapshot();
        snap.Should().ContainKey("sonarqube/get_issues");
        snap["sonarqube/get_issues"].Calls.Should().Be(1);
        snap["sonarqube/get_issues"].Failed.Should().Be(1);
    }

    // ── Parallel calls: failure is not attributed ────────────────────────────

    [Fact]
    public void ParallelCallsNotAttributed_FailureIsCountedButNotAssigned()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @sonarqube/a");
        counter.ProcessLine("[tool] Running: dotnet test /workspace/tests");  // shell line — no key
        counter.ProcessLine("[tool] status: Failed");
        counter.ProcessLine("[tool] status: Completed");

        var snap = counter.Snapshot();
        snap.Should().ContainKey("sonarqube/a");
        snap["sonarqube/a"].Calls.Should().Be(1);
        snap["sonarqube/a"].Failed.Should().Be(0);  // unattributed — two were outstanding
        snap.Should().HaveCount(1);                 // dotnet shell line never added a key
    }

    // ── Back to sequential after the parallel pair resolves ─────────────────

    [Fact]
    public void BackToSequential_AfterParallelIsResolved()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @sonarqube/a");
        counter.ProcessLine("[tool] Running: dotnet test /workspace/tests");
        counter.ProcessLine("[tool] status: Failed");
        counter.ProcessLine("[tool] status: Completed");
        counter.ProcessLine("[tool] Running: @grafana/query");
        counter.ProcessLine("[tool] status: Failed");

        var snap = counter.Snapshot();
        snap["sonarqube/a"].Calls.Should().Be(1);
        snap["sonarqube/a"].Failed.Should().Be(0);
        snap["grafana/query"].Calls.Should().Be(1);
        snap["grafana/query"].Failed.Should().Be(1);
    }

    // ── Shell lines are never counted ────────────────────────────────────────

    [Fact]
    public void ShellCommandLines_AreNeverCounted()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: cd /workspace && dotnet build");
        counter.ProcessLine(@"[tool] Running: curl -H ""Authorization: Bearer abc123"" https://api.example.com");

        var snap = counter.Snapshot();
        snap.Should().BeEmpty();
        snap.Keys.Should().NotContain(k => k.Contains("abc123"),
            "secret-like text in a shell command must never appear in any key");
    }

    // ── Search lines: web search counted, code search not ───────────────────

    [Fact]
    public void SearchLines_WebSearchCounted_CodeSearchNot()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Searching for 'class X|Y' in CodingAgent.Pipeline");
        counter.ProcessLine("[tool] Searching the web");

        var snap = counter.Snapshot();
        snap.Should().ContainKey("web/search");
        snap["web/search"].Calls.Should().Be(1);
        snap["web/search"].Failed.Should().Be(0);
        snap.Should().HaveCount(1);
    }

    // ── Extra status lines with nothing outstanding are safe ─────────────────

    [Fact]
    public void ExtraStatusLines_AreIgnoredSafely()
    {
        var counter = new KiroToolLineCounter();
        // No prior Running line — outstanding is 0.
        var act = () => counter.ProcessLine("[tool] status: Completed");
        act.Should().NotThrow();

        var snap = counter.Snapshot();
        snap.Should().BeEmpty();
    }

    // ── Bad MCP line (no slash) ──────────────────────────────────────────────

    [Fact]
    public void BadMcpLine_NoSlash_AddsNoKey()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @nosuchformat");

        var snap = counter.Snapshot();
        snap.Should().BeEmpty();
    }

    // ── Non-tool lines are ignored ───────────────────────────────────────────

    [Theory]
    [InlineData("[tool]")]                    // prefix only, no space after
    [InlineData("[tools] Running: @x/y")]    // wrong prefix
    [InlineData("plain text no bracket")]    // no bracket at all
    [InlineData("")]                          // empty
    public void NonToolLines_AreIgnored(string line)
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine(line);

        counter.Snapshot().Should().BeEmpty();
    }

    // ── Case-insensitive "Failed" ────────────────────────────────────────────

    [Theory]
    [InlineData("[tool] status: FAILED")]
    [InlineData("[tool] status: failed")]
    [InlineData("[tool] status: Failed")]
    [InlineData("[tool] status:  Failed  ")]   // leading/trailing whitespace after colon
    public void StatusFailed_IsCaseInsensitiveAndTrimmed(string statusLine)
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @sonarqube/get_issues");
        counter.ProcessLine(statusLine);

        counter.Snapshot()["sonarqube/get_issues"].Failed.Should().Be(1);
    }

    // ── Multiple calls to the same key accumulate ────────────────────────────

    [Fact]
    public void MultipleCallsSameKey_AccumulatesCount()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @sonarqube/a");
        counter.ProcessLine("[tool] status: Completed");
        counter.ProcessLine("[tool] Running: @sonarqube/a");
        counter.ProcessLine("[tool] status: Completed");

        var snap = counter.Snapshot();
        snap["sonarqube/a"].Calls.Should().Be(2);
        snap["sonarqube/a"].Failed.Should().Be(0);
    }

    // ── Snapshot returns a copy, not a live view ─────────────────────────────

    [Fact]
    public void SnapshotReturnsCopy_NotLiveState()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @sonarqube/a");
        counter.ProcessLine("[tool] status: Completed");

        var snap1 = counter.Snapshot();
        // Process more lines after taking the snapshot.
        counter.ProcessLine("[tool] Running: @grafana/b");
        counter.ProcessLine("[tool] status: Completed");

        var snap2 = counter.Snapshot();

        // snap1 must not have been mutated.
        snap1.Should().ContainKey("sonarqube/a");
        snap1.Should().NotContainKey("grafana/b");
        // snap2 has both.
        snap2.Should().ContainKey("sonarqube/a");
        snap2.Should().ContainKey("grafana/b");
    }

    // ── Snapshot keys are in ordinal order ──────────────────────────────────

    [Fact]
    public void Snapshot_KeysAreInOrdinalOrder()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("[tool] Running: @sonarqube/z");
        counter.ProcessLine("[tool] status: Completed");
        counter.ProcessLine("[tool] Running: @grafana/a");
        counter.ProcessLine("[tool] status: Completed");
        counter.ProcessLine("[tool] Running: @aws/b");
        counter.ProcessLine("[tool] status: Completed");

        var keys = counter.Snapshot().Keys.ToList();
        keys.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    // ── Lines with leading/trailing whitespace are trimmed ───────────────────

    [Fact]
    public void ProcessLine_TrimsLeadingAndTrailingWhitespace()
    {
        var counter = new KiroToolLineCounter();
        counter.ProcessLine("  [tool] Running: @sonarqube/get_issues  ");
        counter.ProcessLine("  [tool] status: Failed  ");

        var snap = counter.Snapshot();
        snap["sonarqube/get_issues"].Calls.Should().Be(1);
        snap["sonarqube/get_issues"].Failed.Should().Be(1);
    }
}
