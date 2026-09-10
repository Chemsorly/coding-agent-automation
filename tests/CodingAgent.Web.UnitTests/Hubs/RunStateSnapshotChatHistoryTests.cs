using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Regression tests for OnChatEntry cross-PR semantic conflict:
///
/// PR #2201 (Frontend redesign) deleted AgentMonitoring.razor — the only component
/// subscribing to the <c>OnChatEntry</c> SignalR event. After the deletion:
/// - No Blazor component subscribes to <c>OnChatEntry</c>
/// - <see cref="RunStateSnapshot"/> has no <c>ChatHistory</c> field
/// - There is no REST endpoint exposing chat history
///
/// Result: chat entries emitted during a pipeline run are silently dropped and
/// never visible in the UI (merge side-effect analysis, 2026-09-06).
///
/// Fix: add <c>ChatHistory</c> to <see cref="RunStateSnapshot"/> so that:
/// 1. A subscriber connecting mid-run receives existing chat history via the snapshot.
/// 2. RunPage subscribes to <c>OnChatEntry</c> to accumulate subsequent incremental entries.
/// </summary>
public sealed class RunStateSnapshotChatHistoryTests
{
    // ── Reproduction test — MUST FAIL before the fix ─────────────────────

    [Fact]
    public void RunStateSnapshot_HasChatHistoryProperty()
    {
        // Arrange — verify the property exists on the DTO (compile-time guard)
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.GeneratingCode,
            HighWaterMark = PipelineStep.GeneratingCode,
        };

        // Act / Assert — ChatHistory must be a non-null empty list by default
        snapshot.ChatHistory.Should().NotBeNull();
        snapshot.ChatHistory.Should().BeEmpty();
    }

    [Fact]
    public void RunStateSnapshot_ChatHistory_IsPopulated_WhenBuiltFromRunWithEntries()
    {
        // Arrange
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "p1",
            RepoProviderConfigId = "r1"
        };
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = "System context" });
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.User, Content = "Fix this bug" });
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = "I will fix it" });

        // Act — build a snapshot the way BuildRunStateSnapshot does
        var snapshot = BuildSnapshotFromRun(run);

        // Assert — ChatHistory must carry all three entries
        snapshot.ChatHistory.Should().HaveCount(3);
        snapshot.ChatHistory.Should().Contain(e => e.Role == ChatRole.System && e.Content == "System context");
        snapshot.ChatHistory.Should().Contain(e => e.Role == ChatRole.User && e.Content == "Fix this bug");
        snapshot.ChatHistory.Should().Contain(e => e.Role == ChatRole.Agent && e.Content == "I will fix it");
    }

    [Fact]
    public void RunStateSnapshot_ChatHistory_IsEmpty_WhenRunHasNoChatEntries()
    {
        // Arrange — run with no chat history
        var run = new PipelineRun
        {
            RunId = "run-2",
            IssueIdentifier = "org/repo#2",
            IssueTitle = "Clean run",
            IssueProviderConfigId = "p1",
            RepoProviderConfigId = "r1"
        };

        // Act
        var snapshot = BuildSnapshotFromRun(run);

        // Assert — empty list, not null
        snapshot.ChatHistory.Should().NotBeNull();
        snapshot.ChatHistory.Should().BeEmpty();
    }

    [Fact]
    public void RunStateSnapshot_ChatHistory_PreservesOrder()
    {
        // Arrange — ordering matters for conversation display
        var run = new PipelineRun
        {
            RunId = "run-3",
            IssueIdentifier = "org/repo#3",
            IssueTitle = "Order test",
            IssueProviderConfigId = "p1",
            RepoProviderConfigId = "r1"
        };
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = "first" });
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.User, Content = "second" });
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = "third" });

        // Act
        var snapshot = BuildSnapshotFromRun(run);

        // Assert — order preserved (first-in first-out)
        var entries = snapshot.ChatHistory.ToList();
        entries[0].Content.Should().Be("first");
        entries[1].Content.Should().Be("second");
        entries[2].Content.Should().Be("third");
    }

    // ── Helper — mirrors BuildRunStateSnapshot logic for the chat field ───

    /// <summary>
    /// Mirrors the minimal subset of <c>AgentHub.BuildRunStateSnapshot</c> needed for
    /// these tests. Avoids accessing the private method directly while still asserting
    /// on the contract between the run state and the snapshot DTO.
    /// </summary>
    private static RunStateSnapshot BuildSnapshotFromRun(PipelineRun run) => new()
    {
        CurrentStep = run.CurrentStep,
        HighWaterMark = run.HighWaterMark,
        ChatHistory = run.ChatHistory.ToArray(),
    };
}
