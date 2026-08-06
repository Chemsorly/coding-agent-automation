using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

/// <summary>
/// Parses error and warning counts from MSBuild output.
/// </summary>
public static class BuildOutputParser
{
    /// <summary>
    /// Parses error and warning counts from MSBuild output.
    /// Returns zeros on any error (including regex timeout on adversarial input).
    /// </summary>
    public static (int Errors, int Warnings) ParseBuildErrorCounts(string output)
    {
        var errors = 0;
        var warnings = 0;

        if (string.IsNullOrWhiteSpace(output))
            return (errors, warnings);

        try
        {
            var errorMatch = Regex.Match(output, @"(\d+)\s+Error\(s\)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            if (errorMatch.Success)
                int.TryParse(errorMatch.Groups[1].Value, out errors);

            var warningMatch = Regex.Match(output, @"(\d+)\s+Warning\(s\)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            if (warningMatch.Success)
                int.TryParse(warningMatch.Groups[1].Value, out warnings);
        }
        catch (RegexMatchTimeoutException) { /* return partial/zero counts */ }

        return (errors, warnings);
    }
}
