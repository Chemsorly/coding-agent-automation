using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Validates <see cref="AgentProfile"/> instances before persistence.
/// Stateless — all methods are static and pure.
/// </summary>
public static class ProfileValidator
{
    /// <summary>
    /// Validates an <see cref="AgentProfile"/> against business rules.
    /// </summary>
    /// <param name="profile">The profile to validate.</param>
    /// <param name="existingProfiles">
    /// Other profiles already persisted, used to check uniqueness constraints.
    /// </param>
    /// <returns>A validation result indicating success or the first failure reason.</returns>
    public static ValidationResult Validate(AgentProfile profile, IReadOnlyList<AgentProfile> existingProfiles)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(existingProfiles);

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return ValidationResult.Failure("DisplayName must not be empty or whitespace.");
        }

        if (profile.MatchLabels.Count == 0)
        {
            var hasExistingDefault = existingProfiles.Any(p =>
                p.Id != profile.Id && p.MatchLabels.Count == 0);

            if (hasExistingDefault)
            {
                return ValidationResult.Failure(
                    "A default profile (empty MatchLabels) already exists. Only one default profile is allowed.");
            }
        }

        return ValidationResult.Success();
    }
}

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
