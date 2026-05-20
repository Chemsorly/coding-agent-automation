using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

internal static class BuildOutputParser
{
    /// <summary>
    /// Parses error and warning counts from MSBuild output.
    /// Looks for the summary line pattern: "X Error(s)" and "Y Warning(s)".
    /// </summary>
    internal static (int Errors, int Warnings) ParseBuildErrorCounts(string output)
    {
        var errors = 0;
        var warnings = 0;

        if (string.IsNullOrWhiteSpace(output))
            return (errors, warnings);

        var errorMatch = Regex.Match(output, @"(\d+)\s+Error\(s\)", RegexOptions.IgnoreCase);
        if (errorMatch.Success)
            int.TryParse(errorMatch.Groups[1].Value, out errors);

        var warningMatch = Regex.Match(output, @"(\d+)\s+Warning\(s\)", RegexOptions.IgnoreCase);
        if (warningMatch.Success)
            int.TryParse(warningMatch.Groups[1].Value, out warnings);

        return (errors, warnings);
    }
}
