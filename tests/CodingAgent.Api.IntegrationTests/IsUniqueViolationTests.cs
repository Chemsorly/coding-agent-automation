using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Smoke tests for <see cref="WorkItemDispatchEndpoints.IsUniqueViolation"/>, which delegates
/// to <see cref="CodingAgent.Infrastructure.Persistence.Services.PostgresErrorClassifier.IsUniqueViolation"/>.
/// Primary coverage lives in <c>PostgresErrorClassifierTests</c> in CodingAgent.Infrastructure.UnitTests.
/// These tests verify the delegation path and keep the existing contract from drifting.
/// </summary>
// TODO: Add a smoke test that exercises the delegation path through the reflection-based
// PostgresException.SqlState == "23505" branch (i.e., construct a DbUpdateException wrapping
// a real PostgresException with SQLSTATE 23505 and assert IsUniqueViolation returns true).
// All current tests only hit the message-based fallback arm; if the reflection call in
// PostgresErrorClassifier silently broke (e.g., SqlState property name changed), none of
// these smoke tests would catch it. This requires adding 'using Npgsql;' and a PackageReference
// for Npgsql (which flows transitively via CodingAgent.Infrastructure.Persistence, but may
// need an explicit reference for direct constructor use). (TestQualityReviewer review warning)
public sealed class IsUniqueViolationTests
{
    // ── DbUpdateException wrapping inner messages ──────────────────────────────

    [Fact]
    public void DbUpdateException_WithDuplicateKeyInnerMessage_ReturnsTrue()
    {
        var inner = new InvalidOperationException("ERROR: duplicate key value violates unique constraint");
        var ex = new DbUpdateException("save failed", inner);

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException whose inner message contains 'duplicate key' is a unique violation");
    }

    [Fact]
    public void DbUpdateException_WithUniqueConstraintInnerMessage_ReturnsTrue()
    {
        var inner = new InvalidOperationException("ERROR: unique constraint violation");
        var ex = new DbUpdateException("save failed", inner);

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException whose inner message contains 'unique constraint' is a unique violation");
    }

    [Fact]
    public void DbUpdateException_WithUnrelatedInnerMessage_ReturnsFalse()
    {
        var inner = new InvalidOperationException("connection refused");
        var ex = new DbUpdateException("save failed", inner);

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeFalse(
            "DbUpdateException with an unrelated inner message is not a unique violation");
    }

    // ── Top-level message matching (EF InMemory and Postgres fallback) ─────────

    [Fact]
    public void Exception_WithDuplicateKeyMessage_ReturnsTrue()
    {
        var ex = new InvalidOperationException("duplicate key value violates unique constraint");

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "exception whose message contains 'duplicate key' is a unique violation");
    }

    [Fact]
    public void Exception_WithUniqueConstraintMessage_ReturnsTrue()
    {
        var ex = new InvalidOperationException("unique constraint failed: work_items.ix_unique");

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "exception whose message contains 'unique constraint' is a unique violation");
    }

    [Fact]
    public void Exception_WithEfInMemoryPhrase_ReturnsTrue()
    {
        // EF Core InMemory throws ArgumentException with this exact phrase for PK duplicates.
        var ex = new ArgumentException("An item with the same key has already been added. Key: some-guid");

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "EF InMemory PK-duplicate ArgumentException must be recognised as a unique violation");
    }

    [Fact]
    public void Exception_WithUnrelatedMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException("timeout expired");

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeFalse(
            "exceptions unrelated to uniqueness must not be recognised as a unique violation");
    }

    [Fact]
    public void Exception_WithNullMessage_ReturnsFalse()
    {
        // Construct via a subclass so we can have a null-message exception-like scenario.
        // ArgumentException with an explicit empty message is the closest we can get without
        // a custom subclass, since Exception(null) normalises to an empty string in .NET.
        var ex = new InvalidOperationException(string.Empty);

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeFalse(
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

        WorkItemDispatchEndpoints.IsUniqueViolation(ex).Should().BeTrue(
            "the check must be case-insensitive");
    }
}
