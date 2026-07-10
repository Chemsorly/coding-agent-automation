namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Result of a validation operation.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>
    /// Whether the validation passed.
    /// </summary>
    public bool IsValid { get; private init; }

    /// <summary>
    /// The error message when validation fails; <c>null</c> when valid.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>Creates a successful validation result.</summary>
    public static ValidationResult Success() => new() { IsValid = true };

    /// <summary>Creates a failed validation result with the given message.</summary>
    public static ValidationResult Failure(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}
