using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="WorkItemEndpoints.IsUniqueViolation"/>.
/// Exercises each branch of the method to ensure new code paths have coverage:
/// - DbUpdateException wrapping a "duplicate key" inner message (Postgres fallback path)
/// - DbUpdateException wrapping a "unique constraint" inner message (Postgres fallback path)
/// - Plain exception with "duplicate key" in the top-level message
/// - Plain exception with "unique constraint" in the top-level message
/// - EF InMemory exact phrase ("An item with the same key has already been added")
/// - Non-matching exceptions that should return false
/// </summary>
public sealed class IsUniqueViolationTests
{
    // ── DbUpdateException wrapping inner messages ──────────────────────────────

    [Fact]
    public void DbUpdateException_WithDuplicateKeyInnerMessage_ReturnsTrue()
    {
        var inner = new InvalidOperationException("ERROR: duplicate key value violates unique constraint");
        var ex = new DbUpdateException("save failed", inner);

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException whose inner message contains 'duplicate key' is a unique violation");
    }

    [Fact]
    public void DbUpdateException_WithUniqueConstraintInnerMessage_ReturnsTrue()
    {
        var inner = new InvalidOperationException("ERROR: unique constraint violation");
        var ex = new DbUpdateException("save failed", inner);

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException whose inner message contains 'unique constraint' is a unique violation");
    }

    [Fact]
    public void DbUpdateException_WithUnrelatedInnerMessage_ReturnsFalse()
    {
        var inner = new InvalidOperationException("connection refused");
        var ex = new DbUpdateException("save failed", inner);

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeFalse(
            "DbUpdateException with an unrelated inner message is not a unique violation");
    }

    // ── Top-level message matching (EF InMemory and Postgres fallback) ─────────

    [Fact]
    public void Exception_WithDuplicateKeyMessage_ReturnsTrue()
    {
        var ex = new InvalidOperationException("duplicate key value violates unique constraint");

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "exception whose message contains 'duplicate key' is a unique violation");
    }

    [Fact]
    public void Exception_WithUniqueConstraintMessage_ReturnsTrue()
    {
        var ex = new InvalidOperationException("unique constraint failed: work_items.ix_unique");

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "exception whose message contains 'unique constraint' is a unique violation");
    }

    [Fact]
    public void Exception_WithEfInMemoryPhrase_ReturnsTrue()
    {
        // EF Core InMemory throws ArgumentException with this exact phrase for PK duplicates.
        var ex = new ArgumentException("An item with the same key has already been added. Key: some-guid");

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "EF InMemory PK-duplicate ArgumentException must be recognised as a unique violation");
    }

    [Fact]
    public void Exception_WithUnrelatedMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException("timeout expired");

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeFalse(
            "exceptions unrelated to uniqueness must not be recognised as a unique violation");
    }

    [Fact]
    public void Exception_WithNullMessage_ReturnsFalse()
    {
        // Construct via a subclass so we can have a null-message exception-like scenario.
        // ArgumentException with an explicit empty message is the closest we can get without
        // a custom subclass, since Exception(null) normalises to an empty string in .NET.
        var ex = new InvalidOperationException(string.Empty);

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeFalse(
            "exception with empty message must not be recognised as a unique violation");
    }

    // ── Case-insensitivity ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("DUPLICATE KEY")]
    [InlineData("Duplicate Key")]
    [InlineData("UNIQUE CONSTRAINT")]
    [InlineData("Unique Constraint")]
    public void IsUniqueViolation_IsCaseInsensitive(string message)
    {
        var ex = new InvalidOperationException(message);

        WorkItemEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "the check must be case-insensitive");
    }
}
