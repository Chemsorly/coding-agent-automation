namespace CodingAgentWebUI.Pipeline.Models;

// TODO: Add unit tests for JobId value type covering: constructor rejects null/empty,
// ToString() returns value, implicit string conversion, equality semantics, and
// default(JobId) state (Value is null, bypasses constructor validation).

/// <summary>
/// Strongly-typed value type for job identifiers used in the <see cref="Interfaces.IAgentHub"/>
/// SignalR interface. Prevents accidental parameter confusion between jobId, runId, and templateId.
/// </summary>
/// <remarks>
/// <para>The primary constructor accepts any non-empty string. Production job IDs are GUIDs,
/// but test code uses simple strings like "job-1" for convenience.</para>
/// <para>Serialized as a plain string on the wire via <see cref="Serialization.JobIdFormatter"/>
/// to maintain wire-format compatibility with existing agents.</para>
/// </remarks>
// TODO: default(JobId) bypasses constructor validation, leaving Value = null. The implicit
// operator string would then return null to callers expecting non-null. Consider adding a
// parameterless constructor that throws, or a guard in the implicit operator.
public readonly record struct JobId
{
    /// <summary>The raw string value of the job identifier.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new <see cref="JobId"/> from the given string value.
    /// Accepts any non-empty string — no GUID format validation is enforced.
    /// </summary>
    /// <param name="value">The job identifier string. Must not be null or empty.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is null or empty.</exception>
    public JobId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>
    /// Generates a new <see cref="JobId"/> with a fresh GUID value.
    /// </summary>
    public static JobId NewJobId() => new(Guid.NewGuid().ToString());

    /// <summary>
    /// Implicit conversion to <see cref="string"/> for ergonomic use where a string is expected.
    /// </summary>
    public static implicit operator string(JobId jobId) => jobId.Value;

    /// <inheritdoc />
    public override string ToString() => Value;
}
