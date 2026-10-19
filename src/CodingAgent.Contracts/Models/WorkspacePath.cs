namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for workspace directory paths used in repository provider operations.
/// Prevents accidental transposition of string parameters (workspacePath vs branchName vs message).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="JobId"/>, <see cref="RunId"/>, and <see cref="ProviderConfigId"/>, this type
/// includes a reverse implicit conversion (<c>WorkspacePath → string</c>). This deviation is
/// intentional: <see cref="WorkspacePath"/> is passed to numerous downstream APIs that accept
/// <c>string</c> (e.g., <c>RepositoryGitOperations</c>, <c>Directory.CreateDirectory</c>), and
/// requiring <c>.Value</c> at every call site would add excessive churn for a process-local type
/// that is never serialized over the wire.
/// </para>
/// <para>
/// <c>default(WorkspacePath)</c> has <c>Value = null</c> because struct defaults bypass constructors
/// and operators. Implementation methods guard against this with
/// <c>ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value)</c>.
/// The <c>WorkspacePath → string</c> implicit operator also calls this guard so that a
/// <c>default(WorkspacePath)</c> converted to <c>string</c> fails at the conversion point rather
/// than propagating null to a downstream API.
/// </para>
/// </remarks>
public readonly record struct WorkspacePath(string Value)
{
    /// <summary>Validates that <see cref="Value"/> is non-null and non-empty on construction.</summary>
    public string Value { get; init; } = ValidateAndReturn(Value);

    public static implicit operator WorkspacePath(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public static implicit operator string(WorkspacePath path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path.Value);
        return path.Value;
    }

    public override string ToString() => Value;

    private static string ValidateAndReturn(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return value;
    }
}
