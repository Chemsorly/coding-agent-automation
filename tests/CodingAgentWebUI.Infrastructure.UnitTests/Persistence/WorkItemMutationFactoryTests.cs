using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="WorkItemMutationFactory"/>.
/// Verifies that each factory method returns a mutation action with the correct
/// field assignments, default values, and null-coalescing semantics for FailureReason.
/// </summary>
public sealed class WorkItemMutationFactoryTests
{
    // ── Failed() ─────────────────────────────────────────────────────────

    [Fact]
    public void Failed_WithNullArgs_SetsCompletedAtAndDefaultMessageAndAgentErrorReason()
    {
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Failed()(item);

        item.CompletedAt.Should().NotBeNull();
        item.ErrorMessage.Should().Be("Job failed without specific error information");
        item.FailureReason.Should().Be(FailureReason.AgentError);
    }

    [Fact]
    public void Failed_WithExplicitArgs_SetsCompletedAtAndProvidedMessageAndReason()
    {
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Failed(
            errorMessage: "K8s Job timed out",
            failureReason: FailureReason.Timeout)(item);

        item.CompletedAt.Should().NotBeNull();
        item.ErrorMessage.Should().Be("K8s Job timed out");
        item.FailureReason.Should().Be(FailureReason.Timeout);
    }

    [Fact]
    public void Failed_DoesNotOverwriteExistingFailureReason()
    {
        // ??= semantics: a FailureReason already set by a prior transition must be preserved.
        // This matters for recovery paths in RunLifecycleManager where FailureReason may
        // have been set by an earlier infrastructure-failure transition.
        // TODO: This test only covers branch (1) of the ??= contract: pre-set reason is preserved.
        // Branch (2) — null reason is filled from the explicit failureReason argument — is only
        // tested indirectly via Failed_WithExplicitArgs_SetsCompletedAtAndProvidedMessageAndReason.
        // Add a dedicated test that starts with a null FailureReason and asserts the explicit argument is applied.
        var item = new WorkItemEntity
        {
            FailureReason = FailureReason.InfrastructureFailure
        };

        WorkItemMutationFactory.Failed(failureReason: FailureReason.AgentError)(item);

        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
    }

    [Fact]
    public void Failed_WithNullErrorMessage_UsesDefaultMessage()
    {
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Failed(errorMessage: null)(item);

        item.ErrorMessage.Should().Be("Job failed without specific error information");
    }

    [Fact]
    public void Failed_CompletedAt_IsUtcNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Failed()(item);

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        // TODO: The UTC offset assertion (Offset == TimeSpan.Zero) below is the primary correctness guard here.
        // The BeAfter/BeBefore window checks would still pass if the factory used DateTimeOffset.Now on a machine
        // where local time is close to UTC. On non-UTC machines the window checks would fail, but on UTC CI hosts
        // they would not catch the bug. Consider making the UTC offset assertion the first assertion to signal its
        // primacy, and/or replacing the window checks with a tighter assertion.
        item.CompletedAt.Should().BeAfter(before);
        item.CompletedAt.Should().BeBefore(after);
        item.CompletedAt!.Value.Offset.Should().Be(TimeSpan.Zero); // UTC
    }

    // ── Succeeded() ──────────────────────────────────────────────────────

    [Fact]
    public void Succeeded_SetsCompletedAt()
    {
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Succeeded()(item);

        // TODO: Weak assertion — NotBeNull() passes for any non-null value including DateTimeOffset.MinValue or a local-time
        // timestamp. Add a timestamp-range assertion and a UTC offset check (Offset == TimeSpan.Zero) to match the
        // coverage already present for Failed_CompletedAt_IsUtcNow.
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Succeeded_DoesNotSetErrorMessageOrFailureReason()
    {
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Succeeded()(item);

        item.ErrorMessage.Should().BeNull();
        item.FailureReason.Should().BeNull();
    }

    // ── Cancelled() ──────────────────────────────────────────────────────

    [Fact]
    public void Cancelled_SetsCompletedAt()
    {
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Cancelled()(item);

        // TODO: Weak assertion — NotBeNull() passes for any non-null value including DateTimeOffset.MinValue or a local-time
        // timestamp. Add a timestamp-range assertion and a UTC offset check (Offset == TimeSpan.Zero) to match the
        // coverage already present for Failed_CompletedAt_IsUtcNow.
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Cancelled_DoesNotSetErrorMessageOrFailureReason()
    {
        var item = new WorkItemEntity();

        WorkItemMutationFactory.Cancelled()(item);

        item.ErrorMessage.Should().BeNull();
        item.FailureReason.Should().BeNull();
    }
}
