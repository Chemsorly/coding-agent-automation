using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Characterization tests for <see cref="PostgresErrorClassifier.IsUniqueViolation"/>.
/// Covers:
/// <list type="bullet">
///   <item>Reflection-based Npgsql <c>PostgresException.SqlState</c> path (SQLSTATE 23505)</item>
///   <item>Message-based fallbacks (inner and top-level)</item>
///   <item>EF InMemory <c>ArgumentException</c> phrase</item>
///   <item>Case-insensitivity</item>
///   <item>Behavioral expansion: <c>DbUpdateException</c> top-level message now also matched
///         (previously only <c>InnerException.Message</c> was checked in the two Persistence callers)</item>
/// </list>
/// </summary>
public sealed class PostgresErrorClassifierTests
{
    // ── Reflection-based PostgresException SqlState path ──────────────────────
    // PostgresException has a public constructor: (string messageText, string severity,
    // string invariantSeverity, string sqlState). Use it directly to exercise the real
    // production code path rather than a surrogate stub type.

    [Fact]
    public void DbUpdateException_WrappingPostgresException_WithSqlState23505_ReturnsTrue()
    {
        var pg = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505");
        var ex = new DbUpdateException("save failed", pg);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException wrapping PostgresException with SQLSTATE 23505 is a unique violation");
    }

    [Fact]
    public void DbUpdateException_WrappingPostgresException_WithOtherSqlState_ReturnsFalse()
    {
        // SQLSTATE 40001 = serialization_failure — not a unique violation
        var pg = new PostgresException("could not serialize access", "ERROR", "ERROR", "40001");
        var ex = new DbUpdateException("save failed", pg);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeFalse(
            "DbUpdateException wrapping PostgresException with SQLSTATE 40001 is not a unique violation");
    }

    [Fact]
    public void DbUpdateException_WrappingPostgresException_SqlStateTakesPrecedenceOverMessage()
    {
        // Inner message does NOT contain "duplicate key", but SqlState 23505 still returns true.
        var pg = new PostgresException("an unexpected error occurred", "ERROR", "ERROR", "23505");
        var ex = new DbUpdateException("save failed", pg);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "SqlState 23505 takes precedence: the method returns true even when the message doesn't match");
    }

    [Fact]
    public void DbUpdateException_WrappingPostgresException_NonViolatingSqlStateAndUnrelatedMessage_ReturnsFalse()
    {
        var pg = new PostgresException("connection timeout", "ERROR", "ERROR", "08006");
        var ex = new DbUpdateException("save failed", pg);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeFalse(
            "non-23505 SqlState with an unrelated message is not a unique violation");
    }

    // ── DbUpdateException message-based fallback path ─────────────────────────

    [Fact]
    public void DbUpdateException_WithDuplicateKeyInnerMessage_ReturnsTrue()
    {
        var inner = new InvalidOperationException("ERROR: duplicate key value violates unique constraint");
        var ex = new DbUpdateException("save failed", inner);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException whose inner message contains 'duplicate key' is a unique violation");
    }

    [Fact]
    public void DbUpdateException_WithUniqueConstraintInnerMessage_ReturnsTrue()
    {
        var inner = new InvalidOperationException("ERROR: unique constraint violation");
        var ex = new DbUpdateException("save failed", inner);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException whose inner message contains 'unique constraint' is a unique violation");
    }

    [Fact]
    public void DbUpdateException_WithUnrelatedInnerMessage_ReturnsFalse()
    {
        var inner = new InvalidOperationException("connection refused");
        var ex = new DbUpdateException("save failed", inner);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeFalse(
            "DbUpdateException with an unrelated inner message is not a unique violation");
    }

    [Fact]
    public void DbUpdateException_WithDuplicateKeyInTopLevelMessageOnly_ReturnsTrue()
    {
        // Pins the behavioral expansion relative to the two original Persistence callers,
        // which previously only checked InnerException.Message.
        // The consolidated classifier also checks the top-level message on DbUpdateException.
        var inner = new InvalidOperationException("connection reset by peer");
        var ex = new DbUpdateException("duplicate key value violates unique constraint", inner);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "DbUpdateException whose top-level message contains 'duplicate key' (not inner) must also be recognised");
    }

    // ── EF InMemory ArgumentException path ────────────────────────────────────

    [Fact]
    public void ArgumentException_WithEfInMemoryPhrase_ReturnsTrue()
    {
        // EF Core InMemory throws ArgumentException with this exact phrase for PK duplicates.
        // It is NOT wrapped in DbUpdateException.
        var ex = new ArgumentException("An item with the same key has already been added. Key: some-guid");

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "EF InMemory PK-duplicate ArgumentException must be recognised as a unique violation");
    }

    // ── Top-level message fallback (plain Exception) ───────────────────────────

    [Fact]
    public void Exception_WithDuplicateKeyMessage_ReturnsTrue()
    {
        var ex = new InvalidOperationException("duplicate key value violates unique constraint");

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "exception whose message contains 'duplicate key' is a unique violation");
    }

    [Fact]
    public void Exception_WithUniqueConstraintMessage_ReturnsTrue()
    {
        var ex = new InvalidOperationException("unique constraint failed: work_items.ix_unique");

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "exception whose message contains 'unique constraint' is a unique violation");
    }

    [Fact]
    public void Exception_WithEmptyMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException(string.Empty);

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeFalse(
            "exception with empty message must not be recognised as a unique violation");
    }

    [Fact]
    public void Exception_WithUnrelatedMessage_ReturnsFalse()
    {
        var ex = new InvalidOperationException("timeout expired");

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeFalse(
            "exceptions unrelated to uniqueness must not be recognised as a unique violation");
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

        PostgresErrorClassifier.IsUniqueViolation(ex).Should().BeTrue(
            "the check must be case-insensitive");
    }
}
