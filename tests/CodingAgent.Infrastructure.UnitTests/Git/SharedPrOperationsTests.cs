using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Direct unit tests for <see cref="SharedPrOperations"/>.
/// These tests verify the shared contract in isolation, ensuring the helpers behave
/// correctly regardless of which provider delegates to them.
/// </summary>
public class SharedPrOperationsTests
{
    // ── ValidatePaginationArgs ────────────────────────────────────────────────

    [Fact]
    public void ValidatePaginationArgs_ValidArgs_DoesNotThrow()
    {
        var act = () => SharedPrOperations.ValidatePaginationArgs(1, 50);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidatePaginationArgs_PageZero_ThrowsArgumentOutOfRangeException()
    {
        var act = () => SharedPrOperations.ValidatePaginationArgs(0, 10);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("page");
    }

    [Fact]
    public void ValidatePaginationArgs_PageSizeZero_ThrowsArgumentOutOfRangeException()
    {
        var act = () => SharedPrOperations.ValidatePaginationArgs(1, 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("pageSize");
    }

    [Fact]
    public void ValidatePaginationArgs_PageSizeOver100_ThrowsArgumentOutOfRangeException()
    {
        var act = () => SharedPrOperations.ValidatePaginationArgs(1, 101);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("pageSize");
    }

    // ── BuildPagedResult (overfetch overload) ─────────────────────────────────

    [Fact]
    public void BuildPagedResult_HasMore_WhenOverfetched()
    {
        // pageSize = 2, list has 3 items (overfetch by 1) → HasMore = true
        var items = new List<string> { "a", "b", "c" };
        var result = SharedPrOperations.BuildPagedResult(items, page: 1, pageSize: 2);

        result.HasMore.Should().BeTrue();
        result.Items.Should().HaveCount(2, "trimmed to pageSize");
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(2);
    }

    [Fact]
    public void BuildPagedResult_HasMore_False_WhenExactlyPageSize()
    {
        // pageSize = 3, list has exactly 3 items → no overfetch → HasMore = false
        var items = new List<string> { "a", "b", "c" };
        var result = SharedPrOperations.BuildPagedResult(items, page: 1, pageSize: 3);

        result.HasMore.Should().BeFalse();
        result.Items.Should().HaveCount(3);
    }

    [Fact]
    public void BuildPagedResult_EmptyList_HasMoreFalse()
    {
        var items = new List<int>();
        var result = SharedPrOperations.BuildPagedResult(items, page: 1, pageSize: 10);

        result.HasMore.Should().BeFalse();
        result.Items.Should().BeEmpty();
    }

    // ── BuildPagedResult (explicit hasMore overload) ──────────────────────────

    [Fact]
    public void BuildPagedResult_ExplicitHasMore_True_IsPreserved()
    {
        var items = new List<string> { "x" };
        var result = SharedPrOperations.BuildPagedResult(items, page: 2, pageSize: 10, hasMore: true);

        result.HasMore.Should().BeTrue();
        result.Items.Should().ContainSingle();
    }

    [Fact]
    public void BuildPagedResult_ExplicitHasMore_False_IsPreserved()
    {
        var items = new List<string> { "x", "y" };
        var result = SharedPrOperations.BuildPagedResult(items, page: 1, pageSize: 10, hasMore: false);

        result.HasMore.Should().BeFalse();
        result.Items.Should().HaveCount(2);
    }

    // ── IsBotAuthor ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("dependabot[bot]")]
    [InlineData("renovate[bot]")]
    [InlineData("github-actions[bot]")]
    [InlineData("SOMEBOT[BOT]")] // case-insensitive
    public void IsBotAuthor_True_ForBotSuffix(string username)
    {
        SharedPrOperations.IsBotAuthor(username).Should().BeTrue(
            $"'{username}' ends with [bot] suffix");
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("robotnik")]   // contains "bot" but not [bot] suffix
    [InlineData("bot_admin")]  // starts with bot but not [bot] suffix
    [InlineData("")]
    public void IsBotAuthor_False_ForNonBotAuthor(string username)
    {
        SharedPrOperations.IsBotAuthor(username).Should().BeFalse(
            $"'{username}' does not end with [bot] suffix");
    }

    // ── IsCommentAuthor ───────────────────────────────────────────────────────

    [Fact]
    public void IsCommentAuthor_True_WhenSameCase()
    {
        SharedPrOperations.IsCommentAuthor("alice", "alice").Should().BeTrue();
    }

    [Fact]
    public void IsCommentAuthor_True_CaseInsensitive()
    {
        SharedPrOperations.IsCommentAuthor("Alice", "ALICE").Should().BeTrue();
    }

    [Fact]
    public void IsCommentAuthor_False_WhenDifferentUser()
    {
        SharedPrOperations.IsCommentAuthor("alice", "bob").Should().BeFalse();
    }

    [Fact]
    public void IsCommentAuthor_False_WhenEmpty()
    {
        SharedPrOperations.IsCommentAuthor("", "alice").Should().BeFalse();
    }

    // ── FinalizeConversationComments ──────────────────────────────────────────

    [Fact]
    public void FinalizeConversationComments_OrdersByCreatedAt()
    {
        var items = new List<PrConversationComment>
        {
            MakeConversationComment("B", new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)),
            MakeConversationComment("A", new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)),
            MakeConversationComment("C", new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc)),
        };

        var result = SharedPrOperations.FinalizeConversationComments(items);

        result.Select(c => c.Body).Should().ContainInOrder("A", "B", "C");
    }

    [Fact]
    public void FinalizeConversationComments_NoTakeCap_Returns51Items()
    {
        // Explicitly verifies no hidden Take(50) cap exists in FinalizeConversationComments
        var items = Enumerable.Range(1, 51)
            .Select(i => MakeConversationComment($"Comment {i}", DateTime.UtcNow.AddMinutes(i)))
            .ToList();

        var result = SharedPrOperations.FinalizeConversationComments(items);

        result.Should().HaveCount(51, "FinalizeConversationComments must NOT apply a Take(50) cap");
    }

    [Fact]
    public void FinalizeConversationComments_EmptyList_ReturnsEmpty()
    {
        var result = SharedPrOperations.FinalizeConversationComments(new List<PrConversationComment>());
        result.Should().BeEmpty();
    }

    // ── FinalizeReviewComments ────────────────────────────────────────────────

    [Fact]
    public void FinalizeReviewComments_FiltersPipelineGeneratedComments()
    {
        var comments = new[]
        {
            MakeReviewComment("## 🤖 Pipeline generated", DateTime.UtcNow),
            MakeReviewComment("<!-- agent:pr-review --> marker", DateTime.UtcNow.AddMinutes(1)),
            MakeReviewComment("Real human comment", DateTime.UtcNow.AddMinutes(2)),
        };

        var result = SharedPrOperations.FinalizeReviewComments(comments);

        result.Should().ContainSingle("only the non-pipeline-generated comment should remain");
        result[0].Body.Should().Be("Real human comment");
    }

    [Fact]
    public void FinalizeReviewComments_CapsAt50()
    {
        // 51 non-pipeline-generated comments → only 50 returned
        var comments = Enumerable.Range(1, 51)
            .Select(i => MakeReviewComment($"Comment {i}", DateTime.UtcNow.AddMinutes(i)))
            .ToArray();

        var result = SharedPrOperations.FinalizeReviewComments(comments);

        result.Should().HaveCount(50, "FinalizeReviewComments must cap at 50");
    }

    [Fact]
    public void FinalizeReviewComments_OrdersByCreatedAt()
    {
        var comments = new[]
        {
            MakeReviewComment("Second", new DateTime(2026, 1, 15, 11, 0, 0, DateTimeKind.Utc)),
            MakeReviewComment("First", new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc)),
            MakeReviewComment("Third", new DateTime(2026, 1, 15, 13, 0, 0, DateTimeKind.Utc)),
        };

        var result = SharedPrOperations.FinalizeReviewComments(comments);

        result.Select(c => c.Body).Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public void FinalizeReviewComments_EmptyInput_ReturnsEmpty()
    {
        var result = SharedPrOperations.FinalizeReviewComments(Array.Empty<PullRequestReviewComment>());
        result.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // TODO [WARNING]: RunDismissLoopAsync has no direct unit tests in this class. Two critical
    // behaviors are untested:
    //   (a) When dismissItem throws a non-OperationCanceledException on item N, the loop must
    //       continue and still process item N+1 (continue-on-error). Test by passing a lambda
    //       that throws InvalidOperationException on the first call and verifies the second item
    //       is still processed.
    //   (b) When dismissItem throws OperationCanceledException, it must propagate out of
    //       RunDismissLoopAsync rather than being swallowed by the catch block. Test by passing
    //       a lambda that throws OperationCanceledException and asserting the exception escapes.
    // Without these tests, a regression (e.g. removing the 'when (ex is not OperationCanceledException)'
    // filter) would go undetected. The corresponding production-code TODO in SharedPrOperations.cs
    // documents the same gap.

    private static PrConversationComment MakeConversationComment(string body, DateTime createdAt)
        => new()
        {
            Author = "testuser",
            Body = body,
            CreatedAt = createdAt,
            IsBot = false
        };

    private static PullRequestReviewComment MakeReviewComment(string body, DateTime createdAt)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Author = "testuser",
            Body = body,
            CreatedAt = createdAt
        };
}
