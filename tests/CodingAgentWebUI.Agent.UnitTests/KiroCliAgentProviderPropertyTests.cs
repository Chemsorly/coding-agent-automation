using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;
using KiroCliLib.Core;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Agent.KiroCli;

namespace CodingAgentWebUI.Agent.UnitTests;

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
    [Property(Arbitrary = [typeof(AnsiOutputArbitrary)])]
    public void ExecuteAsync_OutputLines_Contain_No_Ansi_Sequences(List<AnsiLine> rawLines)
    {
        // Arrange
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var provider = new KiroCliAgentProvider(mockOrchestrator.Object, mockLogger.Object);

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Callback<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                (_, _, _, _, onOutput, _) =>
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
    [Property(Arbitrary = [typeof(WorkspacePathArbitrary)])]
    public void EnsureSession_CallCount_Equals_DistinctNormalizedPaths(List<WorkspacePath> paths)
    {
        // Arrange
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var provider = new KiroCliAgentProvider(mockOrchestrator.Object, mockLogger.Object);

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
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
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()),
            Times.Exactly(expectedDistinctCount));
    }

    /// <summary>
    /// Feature: provider-interface-gaps, Property 3: UseResume flag forwarding
    /// For any AgentRequest with a UseResume value (true or false), ExecuteAsync passes the
    /// identical boolean to IKiroCliOrchestrator.ExecutePromptAsync.
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property]
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
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Callback<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                (_, _, resume, _, _, _) => capturedUseResume = resume)
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

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 2: ANSI escape sequence stripping
    /// For any output string containing ANSI escape sequences (CSI sequences like \x1b[31m,
    /// \x1b[0m, etc.), when processed through KiroCliAgentProvider's ExecuteAsync output pipeline,
    /// the resulting output lines SHALL contain no ANSI escape sequences.
    /// **Validates: Requirements 5.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(AnsiContainingStringArbitrary)])]
    public void AnsiStripping_ForAnyStringWithCsiSequences_ResultContainsNoAnsiSequences(AnsiContainingString input)
    {
        // Arrange — the input is guaranteed to contain at least one ANSI CSI sequence
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var provider = new KiroCliAgentProvider(mockOrchestrator.Object, mockLogger.Object);

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Callback<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                (_, _, _, _, onOutput, _) =>
                {
                    onOutput?.Invoke(input.Value);
                })
            .ReturnsAsync(0);

        var request = new AgentRequest
        {
            Prompt = "test",
            WorkspacePath = Path.GetTempPath()
        };

        // Act
        var result = provider.ExecuteAsync(request, CancellationToken.None).GetAwaiter().GetResult();

        // Assert — no ANSI CSI sequences remain in the output
        Assert.Single(result.OutputLines);
        var outputLine = result.OutputLines[0];

        // Verify stripping is idempotent (applying Strip again produces the same result)
        // This proves no ANSI sequences remain — if any did, Strip would change the string
        Assert.Equal(outputLine, AnsiStripper.Strip(outputLine));

        // Verify the input actually contained ANSI sequences (precondition)
        Assert.NotEqual(input.Value, AnsiStripper.Strip(input.Value));
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
/// </summary>
public static class WorkspacePathArbitrary
{
    public static Arbitrary<WorkspacePath> WorkspacePaths()
    {
        var tempRoot = Path.GetTempPath();

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
/// </summary>
public static class AnsiOutputArbitrary
{
    private static readonly string[] AnsiSequences =
    [
        "\x1b[31m",
        "\x1b[0m",
        "\x1b[1;32m",
        "\x1b[38;5;196m",
        "\x1b[48;2;0;128;0m",
        "\x1b[K",
        "\x1b[2J",
        "\x1b]0;title\x07",
        "[32m",
        "[0m",
        "[K",
        "[1;33m",
    ];

    public static Arbitrary<AnsiLine> AnsiLines()
    {
        var lineGen =
            from segmentCount in Gen.Choose(1, 5)
            from segments in Gen.ArrayOf(SegmentGen(), segmentCount)
            select new AnsiLine(string.Concat(segments));

        return lineGen.ToArbitrary();
    }

    private static Gen<string> SegmentGen()
    {
        const string safeChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !@#$%^&*()_+-={}|:\"<>?,./~`';";

        var charGen = Gen.Elements(safeChars.ToCharArray());

        var plainGen =
            from len in Gen.Choose(1, 20)
            from chars in Gen.ArrayOf(charGen, len)
            select new string(chars);

        var ansiGen =
            from idx in Gen.Choose(0, AnsiSequences.Length - 1)
            select AnsiSequences[idx];

        var ansiPrefixed = ansiGen.Zip(plainGen).Select(t => t.Item1 + t.Item2);
        var ansiSuffixed = plainGen.Zip(ansiGen).Select(t => t.Item1 + t.Item2);
        var ansiWrapped = ansiGen.Zip(plainGen).Zip(ansiGen)
            .Select(t => t.Item1.Item1 + t.Item1.Item2 + t.Item2);

        return Gen.OneOf(plainGen, ansiPrefixed, ansiSuffixed, ansiWrapped, ansiGen);
    }
}

/// <summary>
/// Wrapper type for strings that always contain at least one ANSI CSI escape sequence.
/// Used for property testing that ANSI stripping removes all sequences.
/// </summary>
public sealed class AnsiContainingString
{
    public string Value { get; }
    public AnsiContainingString(string value) => Value = value;
    public override string ToString() => Value;
}

/// <summary>
/// FsCheck arbitrary that generates strings guaranteed to contain ANSI CSI escape sequences.
/// </summary>
public static class AnsiContainingStringArbitrary
{
    /// <summary>
    /// CSI sequences matched by AnsiStripper's regex: \x1B\[[0-9;]*[A-Za-z]
    /// These are ESC + [ + zero or more digits/semicolons + a single letter.
    /// </summary>
    private static readonly string[] CsiSequences =
    [
        "\x1b[0m",       // Reset
        "\x1b[1m",       // Bold
        "\x1b[31m",      // Red foreground
        "\x1b[32m",      // Green foreground
        "\x1b[1;32m",    // Bold green
        "\x1b[38;5;196m",// 256-color red
        "\x1b[48;2;0;128;0m", // 24-bit green background
        "\x1b[K",        // Erase to end of line
        "\x1b[2J",       // Clear screen
        "\x1b[H",        // Cursor home
        "\x1b[10A",      // Cursor up 10
        "\x1b[5B",       // Cursor down 5
        "\x1b[3C",       // Cursor forward 3
        "\x1b[2D",       // Cursor back 2
        "\x1b[4m",       // Underline
        "\x1b[7m",       // Reverse video
    ];

    public static Arbitrary<AnsiContainingString> AnsiContainingStrings()
    {
        const string safeChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !@#$%^&*()_+-={}|:\"<>?,./~`';";
        var charGen = Gen.Elements(safeChars.ToCharArray());

        var plainGen =
            from len in Gen.Choose(0, 15)
            from chars in Gen.ArrayOf(charGen, len)
            select new string(chars);

        var csiGen =
            from idx in Gen.Choose(0, CsiSequences.Length - 1)
            select CsiSequences[idx];

        // Generate strings with 1-4 ANSI sequences interspersed with plain text
        var gen =
            from segmentCount in Gen.Choose(1, 4)
            from segments in Gen.ArrayOf(
                from plain in plainGen
                from ansi in csiGen
                select plain + ansi, segmentCount)
            from suffix in plainGen
            select new AnsiContainingString(string.Concat(segments) + suffix);

        return gen.ToArbitrary();
    }
}
