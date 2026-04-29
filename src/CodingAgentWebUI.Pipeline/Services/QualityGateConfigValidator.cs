using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Validates <see cref="QualityGateConfiguration"/> instances before persistence.
/// Stateless — all methods are static and pure.
/// </summary>
public static class QualityGateConfigValidator
{
    /// <summary>
    /// Validates a <see cref="QualityGateConfiguration"/> against business rules.
    /// A QGC must define at least one gate (compilation or test command).
    /// </summary>
    /// <param name="config">The quality gate configuration to validate.</param>
    /// <returns>A validation result indicating success or the first failure reason.</returns>
    public static ValidationResult Validate(QualityGateConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.DisplayName))
        {
            return ValidationResult.Failure("DisplayName must not be empty or whitespace.");
        }

        if (config.CompilationCommand is null && config.TestCommand is null)
        {
            return ValidationResult.Failure(
                "A quality gate configuration must define at least one gate (CompilationCommand or TestCommand).");
        }

        return ValidationResult.Success();
    }
}
