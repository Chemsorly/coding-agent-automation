using KiroCliLib.Models;

namespace KiroCliLib.Core;

/// <summary>
/// Defines the contract for parsing Kiro CLI output to detect states and extract information.
/// </summary>
public interface IOutputParser
{
    /// <summary>The most recently detected test results, or null if none detected.</summary>
    TestResult? TestResults { get; }

    /// <summary>Occurs when the detected execution state changes.</summary>
    event EventHandler<KiroState>? StateChanged;

    /// <summary>
    /// Processes a single line of CLI output, detecting state changes and test results.
    /// </summary>
    /// <param name="line">The output line to process.</param>
    void ProcessLine(string line);
}
