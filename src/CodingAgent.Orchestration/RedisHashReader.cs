using System.Text.Json;
using StackExchange.Redis;

namespace CodingAgent.Orchestration;

/// <summary>
/// Lightweight typed reader for a Redis hash returned as <see cref="HashEntry[]"/>.
/// Wraps the HGETALL result in a dictionary and exposes per-type accessor methods that
/// replace inlined <c>TryGetValue</c> / <c>TryParse</c> decode blocks.
///
/// <para>
/// All accessors follow "silent-default on failure" semantics matching the two hand-rolled
/// decoders they replace: parse failures never throw; missing or empty values produce <c>null</c>,
/// <c>default(T)</c>, or zero depending on the return type.
/// </para>
/// </summary>
internal readonly struct RedisHashReader
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    private readonly Dictionary<string, string?> _d;

    /// <summary>
    /// Constructs a reader from a raw HGETALL result.
    /// The <see cref="HashEntry[]"/> is converted to a dictionary once at construction time.
    /// </summary>
    public RedisHashReader(HashEntry[] hash)
    {
        _d = hash.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);
    }

    // ── String accessors ──────────────────────────────────────────────

    /// <summary>
    /// Returns the string value for <paramref name="key"/>, or <c>null</c> if the key is absent
    /// or the value is null/empty. Callers use a null-return to indicate a corrupt/partial hash
    /// and short-circuit with <c>return null</c>.
    /// </summary>
    public string? RequiredString(string key)
    {
        if (!_d.TryGetValue(key, out var val) || string.IsNullOrEmpty(val)) return null;
        return val;
    }

    /// <summary>
    /// Returns the string value for <paramref name="key"/>, or <c>null</c> if the key is absent
    /// or the value is null/empty. Intended for optional fields where absence and empty string
    /// are both treated as "no value".
    /// </summary>
    public string? OptionalString(string key)
    {
        _d.TryGetValue(key, out var val);
        return string.IsNullOrEmpty(val) ? null : val;
    }

    // ── Enum accessors ────────────────────────────────────────────────

    /// <summary>
    /// Parses an enum value for <paramref name="key"/>. Works on both name strings and
    /// integer strings (e.g. <c>"3"</c> for numeric-serialised enums like <c>PipelineStep</c>).
    /// Returns <paramref name="defaultValue"/> when the key is absent, the value is empty, or
    /// parsing fails.
    /// </summary>
    public T Enum<T>(string key, T defaultValue = default!) where T : struct, System.Enum
    {
        _d.TryGetValue(key, out var val);
        if (string.IsNullOrEmpty(val)) return defaultValue;
        return System.Enum.TryParse<T>(val, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Parses an enum value for <paramref name="key"/>. Returns <c>null</c> when the key is
    /// absent, the value is empty, or parsing fails. Used for nullable enum fields serialised
    /// as raw enum-name strings (not JSON).
    /// </summary>
    public T? EnumOrNull<T>(string key) where T : struct, System.Enum
    {
        _d.TryGetValue(key, out var val);
        if (string.IsNullOrEmpty(val)) return null;
        return System.Enum.TryParse<T>(val, out var result) ? result : null;
    }

    // ── DateTimeOffset accessors ──────────────────────────────────────

    /// <summary>
    /// Parses a <see cref="DateTimeOffset"/> for <paramref name="key"/>.
    /// Returns <c>default</c> when the key is absent or the value cannot be parsed.
    /// </summary>
    public DateTimeOffset DateTimeOffset(string key)
    {
        _d.TryGetValue(key, out var val);
        return System.DateTimeOffset.TryParse(val, out var result) ? result : default;
    }

    /// <summary>
    /// Parses a nullable <see cref="DateTimeOffset"/> for <paramref name="key"/>.
    /// Returns <c>null</c> when the key is absent or the value cannot be parsed.
    /// </summary>
    public DateTimeOffset? DateTimeOffsetOrNull(string key)
    {
        _d.TryGetValue(key, out var val);
        return System.DateTimeOffset.TryParse(val, out var result) ? result : null;
    }

    // ── Numeric accessors ─────────────────────────────────────────────

    /// <summary>Parses an <see cref="int"/> for <paramref name="key"/>. Returns 0 on failure.</summary>
    public int Int(string key)
    {
        _d.TryGetValue(key, out var val);
        return int.TryParse(val, out var result) ? result : 0;
    }

    /// <summary>Parses a <see cref="long"/> for <paramref name="key"/>. Returns 0 on failure.</summary>
    public long Long(string key)
    {
        _d.TryGetValue(key, out var val);
        return long.TryParse(val, out var result) ? result : 0L;
    }

    /// <summary>Parses a nullable <see cref="decimal"/> for <paramref name="key"/>. Returns <c>null</c> on failure.</summary>
    public decimal? Decimal(string key)
    {
        _d.TryGetValue(key, out var val);
        return decimal.TryParse(val, out var result) ? result : null;
    }

    // ── Boolean accessors ─────────────────────────────────────────────

    /// <summary>
    /// Parses a <see cref="bool"/> for <paramref name="key"/>.
    /// Returns <c>false</c> when the key is absent, the value is empty, or parsing fails.
    /// This is equivalent to <c>bool.TryParse(dict.GetValueOrDefault(key) ?? "false", out var v)</c>.
    /// </summary>
    public bool Bool(string key)
    {
        _d.TryGetValue(key, out var val);
        return bool.TryParse(val, out var result) && result;
    }

    /// <summary>
    /// Parses a nullable <see cref="bool"/> for <paramref name="key"/>.
    /// Returns <c>null</c> when the key is absent, the value is empty, or parsing fails.
    /// </summary>
    public bool? BoolNullable(string key)
    {
        _d.TryGetValue(key, out var val);
        return bool.TryParse(val, out var result) ? result : null;
    }

    // ── JSON accessors ────────────────────────────────────────────────

    /// <summary>
    /// Deserialises a JSON-encoded value for <paramref name="key"/> into <typeparamref name="T"/>.
    /// Returns <c>null</c> when the key is absent, the value is empty, or deserialisation fails.
    /// </summary>
    public T? Json<T>(string key) where T : class
    {
        if (!_d.TryGetValue(key, out var json) || string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch { return null; }
    }

    /// <summary>
    /// Reads a raw string value for <paramref name="key"/> and parses it as an enum.
    /// Returns <c>null</c> when the key is absent, the value is empty, or parsing fails.
    /// Used for nullable enum fields that are serialised as a plain enum-name string (not wrapped in JSON).
    /// </summary>
    public T? JsonEnumOrNull<T>(string key) where T : struct, System.Enum
    {
        if (!_d.TryGetValue(key, out var val) || string.IsNullOrEmpty(val)) return null;
        return System.Enum.TryParse<T>(val, out var result) ? result : null;
    }
}
