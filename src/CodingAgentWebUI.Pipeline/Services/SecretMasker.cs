namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Masks known secret values in output text. Values shorter than 4 characters are not masked
/// to avoid excessive false-positive redaction.
/// </summary>
public static class SecretMasker
{
    /// <summary>
    /// Replaces all secret values (≥ 4 characters) in <paramref name="output"/> with <c>***</c>.
    /// </summary>
    public static string Mask(string output, IEnumerable<KeyValuePair<string, string>> secrets)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(secrets);

        foreach (var (_, value) in secrets)
        {
            if (value.Length >= 4)
                output = output.Replace(value, "***");
        }
        return output;
    }
}
