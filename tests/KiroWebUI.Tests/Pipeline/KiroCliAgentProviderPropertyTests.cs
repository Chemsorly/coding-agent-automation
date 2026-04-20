using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;
using KiroCliLib.Core;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for KiroCliAgentProvider.
/// Feature: provider-interface-gaps
/// </summary>
public class KiroCliAgentProviderPropertyTests
{
    /// <summary>
    /// Feature: provider-interface-gaps, Property 2: ANSI-free output
    /// For any string (including ANSI-injected strings), after processing through the output
    /// pipeline, output lines contain no ANSI escape sequences; applying AnsiStripper.Strip
    /// to any output line returns the line unchanged.
    /// **Validates: REQ-1.3**
    /// </summary>
    // Feature: provider-interface-gaps, Property 2: ANSI-free output
    [Property(MaxTest = 20, Arbitrary = [typeof(AnsiOutputArbitrary)])]
    public void ExecuteAsync_OutputLines_Contain_No_Ansi_Sequences(List<AnsiLine> rawLines)
    {
        // Arrange
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var provider = new KiroCliAgentProvider(mockOrchestrator.Object, mockLogger.Object);

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<string, string, bool, CancellationToken, Action<string>?>(
                (_, _, _, _, onOutput) =>
                {
                    if (onOutput is not null)
                    {
                        foreach (var line in rawLines)
                            onOutput(line.Value);
                    }
                })
            .ReturnsAsync(0);

        var callbackLines = new List<string>();
        var request = new AgentRequest
        {
            Prompt = "test",
            WorkspacePath = Path.GetTempPath()
        };

        // Act
        var result = provider.ExecuteAsync(request, CancellationToken.None,
            onOutputLine: line => callbackLines.Add(line)).GetAwaiter().GetResult();

        // Assert — every output line is ANSI-free (stripping is idempotent on the output)
        foreach (var line in result.OutputLines)
        {
            Assert.Equal(line, AnsiStripper.Strip(line));
        }

        // Assert — callback lines are also ANSI-free
        Assert.Equal(rawLines.Count, callbackLines.Count);
        foreach (var line in callbackLines)
        {
            Assert.Equal(line, AnsiStripper.Strip(line));
        }
    }
    /// <summary>
    /// Feature: provider-interface-gaps, Property 1: Session warm-up idempotence
    /// For any sequence of workspace path strings, EnsureSessionAsync calls result in exactly
    /// as many underlying ExecutePromptAsync calls as there are distinct (normalized) paths.
    /// **Validates: Requirements 1.1**
    /// </summary>
    [Property(MaxTest = 50, Arbitrary = [typeof(WorkspacePathArbitrary)])]
    public void EnsureSession_CallCount_Equals_DistinctNormalizedPaths(List<WorkspacePath> paths)
    {
        // Arrange
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var provider = new KiroCliAgentProvider(mockOrchestrator.Object, mockLogger.Object);

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(0);

        // Act — call EnsureSessionAsync for each path in sequence
        foreach (var path in paths)
        {
            provider.EnsureSessionAsync(path.Value, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        // Assert — the number of ExecutePromptAsync calls equals the number of distinct normalized paths
        var expectedDistinctCount = paths
            .Select(p => Path.GetFullPath(p.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        mockOrchestrator.Verify(
            o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(expectedDistinctCount));
    }

    /// <summary>
    /// Feature: provider-interface-gaps, Property 3: UseResume flag forwarding
    /// For any AgentRequest with a UseResume value (true or false), ExecuteAsync passes the
    /// identical boolean to IKiroCliOrchestrator.ExecutePromptAsync.
    /// **Validates: Requirements 1.4**
    /// </summary>
    // Feature: provider-interface-gaps, Property 3: UseResume flag forwarding
    [Property(MaxTest = 50)]
    public void ExecuteAsync_Forwards_UseResume_To_Orchestrator(bool useResume)
    {
        // Arrange
        var capturedUseResume = (bool?)null;
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var provider = new KiroCliAgentProvider(mockOrchestrator.Object, mockLogger.Object);

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<string, string, bool, CancellationToken, Action<string>?>(
                (_, _, resume, _, _) => capturedUseResume = resume)
            .ReturnsAsync(0);

        var request = new AgentRequest
        {
            Prompt = "test prompt",
            WorkspacePath = Path.GetTempPath(),
            UseResume = useResume
        };

        // Act
        provider.ExecuteAsync(request, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — the useResume parameter forwarded to ExecutePromptAsync matches the request
        Assert.NotNull(capturedUseResume);
        Assert.Equal(useResume, capturedUseResume.Value);
    }
}

/// <summary>
/// Wrapper type for valid workspace path strings that won't throw on Path.GetFullPath.
/// </summary>
public sealed class WorkspacePath
{
    public string Value { get; }
    public WorkspacePath(string value) => Value = value;
    public override string ToString() => Value;
}

/// <summary>
/// FsCheck arbitrary that generates workspace path strings valid for Path.GetFullPath.
/// Produces paths from a small pool (workspace-0 through workspace-9) rooted in the temp
/// directory, with controlled repetition to exercise the idempotence property.
/// </summary>
public static class WorkspacePathArbitrary
{
    public static Arbitrary<WorkspacePath> WorkspacePaths()
    {
        var tempRoot = Path.GetTempPath();

        // Generate from a pool of 10 distinct path segments to get meaningful repetition
        var pathGen =
            from index in Gen.Choose(0, 9)
            select new WorkspacePath(Path.Combine(tempRoot, $"workspace-{index}"));

        return pathGen.ToArbitrary();
    }
}

/// <summary>
/// Wrapper type for output lines that may contain ANSI escape sequences.
/// </summary>
public sealed class AnsiLine
{
    public string Value { get; }
    public AnsiLine(string value) => Value = value;
    public override string ToString() => Value;
}

/// <summary>
/// FsCheck arbitrary that generates output lines mixing plain text with ANSI escape sequences.
/// Covers standard ESC[...m color codes, OSC title sequences, bare bracket sequences, and [K erase.
/// </summary>
public static class AnsiOutputArbitrary
{
    private static readonly string[] AnsiSequences =
    [
        "\x1b[31m",          // red foreground
        "\x1b[0m",           // reset
        "\x1b[1;32m",        // bold green
        "\x1b[38;5;196m",    // 256-color red
        "\x1b[48;2;0;128;0m",// 24-bit green background
        "\x1b[K",            // erase to end of line
        "\x1b[2J",           // clear screen
        "\x1b]0;title\x07",  // OSC window title
        "[32m",              // bare bracket color (no ESC prefix)
        "[0m",               // bare bracket reset
        "[K",                // bare bracket erase
        "[1;33m",            // bare bracket bold yellow
    ];

    public static Arbitrary<AnsiLine> AnsiLines()
    {
        // Generator for a single line: interleave plain text segments with ANSI sequences
        var lineGen =
            from segmentCount in Gen.Choose(1, 5)
            from segments in Gen.ArrayOf(SegmentGen(), segmentCount)
            select new AnsiLine(string.Concat(segments));

        return lineGen.ToArbitrary();
    }

    private static Gen<string> SegmentGen()
    {
        // Safe characters that cannot form ANSI-like patterns (no ESC, no '[', no control chars)
        const string safeChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !@#$%^&*()_+-={}|:\"<>?,./~`';";

        var charGen = Gen.Elements(safeChars.ToCharArray());

        var plainGen =
            from len in Gen.Choose(1, 20)
            from chars in Gen.ArrayOf(charGen, len)
            select new string(chars);

        var ansiGen =
            from idx in Gen.Choose(0, AnsiSequences.Length - 1)
            select AnsiSequences[idx];

        // Combine ANSI sequences with plain text in various positions
        var ansiPrefixed = ansiGen.Zip(plainGen).Select(t => t.Item1 + t.Item2);
        var ansiSuffixed = plainGen.Zip(ansiGen).Select(t => t.Item1 + t.Item2);
        var ansiWrapped = ansiGen.Zip(plainGen).Zip(ansiGen)
            .Select(t => t.Item1.Item1 + t.Item1.Item2 + t.Item2);

        return Gen.OneOf(plainGen, ansiPrefixed, ansiSuffixed, ansiWrapped, ansiGen);
    }
}
