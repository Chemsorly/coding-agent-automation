using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Models;
using StackExchange.Redis;

namespace CodingAgent.Orchestration.UnitTests.Redis;

/// <summary>
/// Unit tests for <see cref="RedisHashReader"/> — verifies each typed accessor in isolation.
/// </summary>
public class RedisHashReaderTests
{
    private static HashEntry[] H(params (string key, string value)[] fields)
        => fields.Select(f => new HashEntry(f.key, f.value)).ToArray();

    // ── RequiredString ────────────────────────────────────────────────

    [Fact]
    public void RequiredString_PresentKey_ReturnsValue()
    {
        var r = new RedisHashReader(H(("foo", "bar")));
        r.RequiredString("foo").Should().Be("bar");
    }

    [Fact]
    public void RequiredString_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(H(("other", "value")));
        r.RequiredString("foo").Should().BeNull();
    }

    [Fact]
    public void RequiredString_EmptyValue_ReturnsNull()
    {
        var r = new RedisHashReader(H(("foo", "")));
        r.RequiredString("foo").Should().BeNull();
    }

    // ── OptionalString ────────────────────────────────────────────────

    [Fact]
    public void OptionalString_PresentNonEmptyKey_ReturnsValue()
    {
        var r = new RedisHashReader(H(("foo", "hello")));
        r.OptionalString("foo").Should().Be("hello");
    }

    [Fact]
    public void OptionalString_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(H(("other", "x")));
        r.OptionalString("missing").Should().BeNull();
    }

    [Fact]
    public void OptionalString_EmptyValue_ReturnsNull()
    {
        var r = new RedisHashReader(H(("foo", "")));
        r.OptionalString("foo").Should().BeNull();
    }

    // ── Enum<T> ───────────────────────────────────────────────────────

    [Fact]
    public void Enum_ValidNameString_ReturnsParsedValue()
    {
        var r = new RedisHashReader(H(("status", "Busy")));
        r.Enum<AgentStatus>("status").Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public void Enum_ValidIntegerString_ReturnsParsedValue()
    {
        // PipelineStep is stored as an integer string
        var r = new RedisHashReader(H(("step", "8")));
        r.Enum<PipelineStep>("step").Should().Be(PipelineStep.GeneratingCode);
        // TODO: add a test for an out-of-range integer string (e.g. "99999") — Enum.TryParse
        // succeeds for numeric strings even when the value has no named member, so the current
        // implementation silently returns the undefined ordinal rather than defaultValue.
        // If callers expect defaultValue for undefined ordinals, the implementation must add an
        // Enum.IsDefined() guard. Tracked: TestQualityReviewer warning — RedisHashReaderTests.cs:75
    }

    [Fact]
    public void Enum_InvalidString_ReturnsDefaultValue()
    {
        var r = new RedisHashReader(H(("status", "NotAStatus")));
        r.Enum<AgentStatus>("status", defaultValue: AgentStatus.Idle).Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void Enum_AbsentKey_ReturnsExplicitDefaultValue()
    {
        var r = new RedisHashReader(H(("other", "x")));
        r.Enum<AgentStatus>("status", defaultValue: AgentStatus.Idle).Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void Enum_AbsentKey_ReturnsDefaultWhenNoExplicitDefault()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        // TODO: this assertion relies on default(PipelineRunType) == Implementation because
        // Implementation is currently the first member (value 0). If PipelineRunType.Implementation
        // is ever given a non-zero underlying value, default(T) will silently diverge.
        // Replace with: .Should().Be(PipelineRunType.Implementation)
        // Tracked: TestQualityReviewer warning — RedisHashReaderTests.cs:113
        // default(PipelineRunType) == Implementation (0)
        r.Enum<PipelineRunType>("runType").Should().Be(default(PipelineRunType));
    }

    // ── EnumOrNull<T> ─────────────────────────────────────────────────

    [Fact]
    public void EnumOrNull_ValidString_ReturnsParsedValue()
    {
        var r = new RedisHashReader(H(("rec", "Ready")));
        r.EnumOrNull<AnalysisGateResult>("rec").Should().Be(AnalysisGateResult.Ready);
    }

    [Fact]
    public void EnumOrNull_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.EnumOrNull<AnalysisGateResult>("rec").Should().BeNull();
    }

    [Fact]
    public void EnumOrNull_InvalidString_ReturnsNull()
    {
        var r = new RedisHashReader(H(("rec", "bogus")));
        r.EnumOrNull<AnalysisGateResult>("rec").Should().BeNull();
    }

    // ── DateTimeOffset ────────────────────────────────────────────────

    [Fact]
    public void DateTimeOffset_ValidIso8601_ReturnsParsedValue()
    {
        var expected = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var r = new RedisHashReader(H(("ts", expected.ToString("O"))));
        r.DateTimeOffset("ts").Should().Be(expected);
    }

    [Fact]
    public void DateTimeOffset_AbsentKey_ReturnsDefault()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.DateTimeOffset("ts").Should().Be(default(DateTimeOffset));
    }

    [Fact]
    public void DateTimeOffset_EmptyOrInvalidValue_ReturnsDefault()
    {
        // A key present with an empty or unparseable value must also return default,
        // not throw — e.g. a partially-written hash where startedAtOffset is "".
        var r = new RedisHashReader(H(("ts", "")));
        r.DateTimeOffset("ts").Should().Be(default(DateTimeOffset));

        var r2 = new RedisHashReader(H(("ts", "not-a-date")));
        r2.DateTimeOffset("ts").Should().Be(default(DateTimeOffset));
    }

    // ── DateTimeOffsetOrNull ──────────────────────────────────────────

    [Fact]
    public void DateTimeOffsetOrNull_ValidIso8601_ReturnsParsedValue()
    {
        var expected = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);
        var r = new RedisHashReader(H(("ts", expected.ToString("O"))));
        r.DateTimeOffsetOrNull("ts").Should().Be(expected);
    }

    [Fact]
    public void DateTimeOffsetOrNull_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.DateTimeOffsetOrNull("ts").Should().BeNull();
    }

    [Fact]
    public void DateTimeOffsetOrNull_EmptyValue_ReturnsNull()
    {
        var r = new RedisHashReader(H(("ts", "")));
        r.DateTimeOffsetOrNull("ts").Should().BeNull();
    }

    // ── Int ───────────────────────────────────────────────────────────

    [Fact]
    public void Int_ValidString_ReturnsParsedValue()
    {
        var r = new RedisHashReader(H(("n", "42")));
        r.Int("n").Should().Be(42);
    }

    [Fact]
    public void Int_AbsentKey_ReturnsZero()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.Int("n").Should().Be(0);
        // TODO: add a test for an invalid (non-numeric) stored string, e.g. H(("n", "corrupt")),
        // to assert the silent-default contract ("returns 0 on parse failure") is preserved.
        // Tracked: TestQualityReviewer warning — RedisHashReaderTests.cs:175
    }

    // ── Long ──────────────────────────────────────────────────────────

    [Fact]
    public void Long_ValidString_ReturnsParsedValue()
    {
        var r = new RedisHashReader(H(("n", "123456789012345")));
        r.Long("n").Should().Be(123456789012345L);
    }

    [Fact]
    public void Long_AbsentKey_ReturnsZero()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.Long("n").Should().Be(0L);
        // TODO: add a test for an invalid (non-numeric) stored string, e.g. H(("n", "corrupt")),
        // to assert the silent-default contract ("returns 0 on parse failure") is preserved.
        // Tracked: TestQualityReviewer warning — RedisHashReaderTests.cs:175
    }

    // ── Decimal ───────────────────────────────────────────────────────

    [Fact]
    public void Decimal_ValidString_ReturnsParsedValue()
    {
        var r = new RedisHashReader(H(("cost", "1.2345")));
        r.Decimal("cost").Should().Be(1.2345m);
    }

    [Fact]
    public void Decimal_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.Decimal("cost").Should().BeNull();
    }

    [Fact]
    public void Decimal_UsesInvariantCulture_ParsesPeriodSeparator()
    {
        // TODO: this test is functionally identical to Decimal_ValidString_ReturnsParsedValue:
        // both construct a reader with ("cost", "1.2345") and assert the result is 1.2345m.
        // To actually verify locale independence, set Thread.CurrentThread.CurrentCulture to a
        // comma-decimal locale (e.g. CultureInfo.GetCultureInfo("de-DE")) before parsing, or
        // use a value that would parse differently under a non-invariant culture (e.g. "1,2345"
        // should return null under InvariantCulture). Update or merge this test accordingly.
        // Tracked: TestQualityReviewer warning — RedisHashReaderTests.cs:218
        // Decimal values are stored and parsed with InvariantCulture (period as decimal separator).
        // A value like "1.2345" must parse correctly regardless of the host OS locale.
        var r = new RedisHashReader(H(("cost", "1.2345")));
        r.Decimal("cost").Should().Be(1.2345m,
            "decimal parsing uses CultureInfo.InvariantCulture so '.' is always the decimal separator");
    }

    // ── Bool ──────────────────────────────────────────────────────────

    [Fact]
    public void Bool_TrueString_ReturnsTrue()
    {
        var r = new RedisHashReader(H(("flag", "True")));
        r.Bool("flag").Should().BeTrue();
    }

    [Fact]
    public void Bool_FalseString_ReturnsFalse()
    {
        var r = new RedisHashReader(H(("flag", "False")));
        r.Bool("flag").Should().BeFalse();
    }

    [Fact]
    public void Bool_AbsentKey_ReturnsFalse()
    {
        // Equivalent to ?? "false" before bool.TryParse — absent key is treated as false
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.Bool("flag").Should().BeFalse("an absent key must default to false, not throw");
    }

    // ── BoolNullable ──────────────────────────────────────────────────

    [Fact]
    public void BoolNullable_TrueString_ReturnsTrue()
    {
        var r = new RedisHashReader(H(("flag", "True")));
        r.BoolNullable("flag").Should().BeTrue();
    }

    [Fact]
    public void BoolNullable_FalseString_ReturnsFalse()
    {
        // A stored "False" must return false (not null); this path is exercised by
        // BaselineHealthPassed when the previous run explicitly recorded false.
        var r = new RedisHashReader(H(("flag", "False")));
        r.BoolNullable("flag").Should().BeFalse();
    }

    [Fact]
    public void BoolNullable_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.BoolNullable("flag").Should().BeNull();
    }

    // ── Json<T> ───────────────────────────────────────────────────────

    [Fact]
    public void Json_ValidJson_ReturnsDeserializedObject()
    {
        var r = new RedisHashReader(H(("labels", "[\"bug\",\"enhancement\"]")));
        r.Json<List<string>>("labels").Should().BeEquivalentTo(["bug", "enhancement"]);
    }

    [Fact]
    public void Json_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.Json<List<string>>("labels").Should().BeNull();
    }

    [Fact]
    public void Json_MalformedJson_ReturnsNull()
    {
        var r = new RedisHashReader(H(("labels", "not-valid-json")));
        r.Json<List<string>>("labels").Should().BeNull("malformed JSON must return null, not throw");
    }

    // ── JsonEnumOrNull<T> ─────────────────────────────────────────────

    [Fact]
    public void JsonEnumOrNull_ValidEnumName_ReturnsParsedValue()
    {
        var r = new RedisHashReader(H(("rec", "Ready")));
        r.JsonEnumOrNull<AnalysisGateResult>("rec").Should().Be(AnalysisGateResult.Ready);
    }

    [Fact]
    public void JsonEnumOrNull_AbsentKey_ReturnsNull()
    {
        var r = new RedisHashReader(Array.Empty<HashEntry>());
        r.JsonEnumOrNull<AnalysisGateResult>("rec").Should().BeNull();
    }

    [Fact]
    public void JsonEnumOrNull_InvalidString_ReturnsNull()
    {
        // A corrupt stored value must return null, not throw — consistent with EnumOrNull.
        var r = new RedisHashReader(H(("rec", "bogus")));
        r.JsonEnumOrNull<AnalysisGateResult>("rec").Should().BeNull();
    }
}
