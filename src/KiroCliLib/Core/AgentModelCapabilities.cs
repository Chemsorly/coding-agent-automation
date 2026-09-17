namespace KiroCliLib.Core;

/// <summary>
/// Shared helper for querying agent model capabilities.
/// </summary>
public static class AgentModelCapabilities
{
    /// <summary>
    /// Determines if a model identifier refers to a text-only (non-vision) model.
    /// Returns false (not text-only = vision capable) when model is null or empty (assume capable).
    /// </summary>
    public static bool IsTextOnlyModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return false;

        return model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    }
}
