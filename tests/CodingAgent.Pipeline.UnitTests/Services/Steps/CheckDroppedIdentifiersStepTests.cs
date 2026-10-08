using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Tests for <see cref="CheckDroppedIdentifiersStep"/> — post-codegen check that identifiers the
/// branch had added in force-resolved conflict files were re-applied (issue #3435).
/// </summary>
public class CheckDroppedIdentifiersStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly List<string> _outputLines = [];
    private readonly List<PipelineStep> _transitions = [];
    private readonly string _tempDir;

    public CheckDroppedIdentifiersStepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"check-dropped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _outputLines.Add(line));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => _transitions.Add(step));
        _callbacks.Setup(c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── Short-circuit guards ────────────────────────────────────────────────

    [Fact]
    public async Task WhenWorkspacePathIsNull_ReturnsImmediatelyWithoutTransition()
    {
        var (context, _) = BuildContext(workspacePath: null, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                ["src/Foo.cs"] = ["FooClass"]
            });

        var result = await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _transitions.Should().NotContain(PipelineStep.CheckingDroppedIdentifiers);
    }

    [Fact]
    public async Task WhenNotForceResolved_ReturnsImmediatelyWithoutTransition()
    {
        var (context, _) = BuildContext(workspacePath: _tempDir, mergeForceResolved: false,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                ["src/Foo.cs"] = ["FooClass"]
            });

        var result = await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _transitions.Should().NotContain(PipelineStep.CheckingDroppedIdentifiers);
    }

    [Fact]
    public async Task WhenNoDroppedIdentifiers_ReturnsImmediatelyWithoutTransition()
    {
        var (context, _) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>());

        var result = await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _transitions.Should().NotContain(PipelineStep.CheckingDroppedIdentifiers);
    }

    // ── Re-applied (AC #2) ──────────────────────────────────────────────────

    /// <summary>
    /// AC #2: A dropped change that the agent re-applies is not reported.
    /// </summary>
    [Fact]
    public async Task WhenIdentifierPresentInWorkingTree_NotReportedMissing()
    {
        const string fileName = "src/MyService.cs";
        WriteFile(fileName, "public class MyService\n{\n    public void DoWork() { }\n}\n");

        var (context, run) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [fileName] = ["MyService"]
            });

        await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        run.NotReappliedIdentifiersByFile.Should().NotContainKey(fileName,
            "identifier is present in the working tree — must not be reported as missing");
        _outputLines.Should().NotContain(line => line.Contains("MyService") && line.Contains("not re-applied"),
            "should not emit a not-re-applied warning for a present identifier");
    }

    // ── Not re-applied (AC #1) ──────────────────────────────────────────────

    /// <summary>
    /// AC #1: A rework run whose force-resolved rebase drops a test class that the agent does
    /// not re-apply ends with that class named in the run's NotReappliedIdentifiersByFile.
    /// </summary>
    [Fact]
    public async Task WhenIdentifierAbsentFromWorkingTree_ReportedMissing()
    {
        const string fileName = "tests/SomeTests.cs";
        // File exists but does NOT contain the dropped class
        WriteFile(fileName, "// This file was reset to main's version.\npublic class SomeOtherTests { }\n");

        var (context, run) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [fileName] = ["DroppedTestClass"]
            });

        await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        run.NotReappliedIdentifiersByFile.Should().ContainKey(fileName);
        run.NotReappliedIdentifiersByFile[fileName].Should().Contain("DroppedTestClass");

        _outputLines.Should().Contain(line => line.Contains("DroppedTestClass") && line.Contains("not re-applied"),
            "must emit a warning line for the missing identifier");
    }

    [Fact]
    public async Task WhenIdentifierPartiallyReapplied_OnlyMissingOnesReported()
    {
        const string fileName = "src/Partial.cs";
        // File re-applies ClassA but not ClassB
        WriteFile(fileName, "public class ClassA { }\n");

        var (context, run) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [fileName] = ["ClassA", "ClassB"]
            });

        await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        run.NotReappliedIdentifiersByFile.Should().ContainKey(fileName);
        run.NotReappliedIdentifiersByFile[fileName].Should().NotContain("ClassA");
        run.NotReappliedIdentifiersByFile[fileName].Should().Contain("ClassB");
    }

    // ── Deleted file edge case ──────────────────────────────────────────────

    [Fact]
    public async Task WhenFileDoesNotExist_SkipsWithWarning()
    {
        // Do NOT create the file in _tempDir
        const string missingFile = "src/Deleted.cs";

        var (context, run) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [missingFile] = ["DeletedClass"]
            });

        // Should not throw
        var result = await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue, "file-not-found must not fail the pipeline");
        _outputLines.Should().Contain(line => line.Contains(missingFile) && line.Contains("not found"),
            "must emit a warning about the missing file");
        // File is skipped — not added to NotReappliedIdentifiersByFile
        run.NotReappliedIdentifiersByFile.Should().NotContainKey(missingFile);
    }

    // ── Always returns Continue ─────────────────────────────────────────────

    [Fact]
    public async Task AlwaysReturnsContinue_WhenIdentifiersMissing()
    {
        const string fileName = "src/Missing.cs";
        WriteFile(fileName, "// no relevant content");

        var (context, _) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [fileName] = ["SomeClass"]
            });

        var result = await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue, "step must never fail the pipeline");
    }

    [Fact]
    public async Task AlwaysReturnsContinue_WhenAllIdentifiersPresent()
    {
        const string fileName = "src/Present.cs";
        WriteFile(fileName, "public class PresentClass { }");

        var (context, _) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [fileName] = ["PresentClass"]
            });

        var result = await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task TransitionsToCheckingDroppedIdentifiers_WhenRunning()
    {
        const string fileName = "src/Check.cs";
        WriteFile(fileName, "public class CheckClass { }");

        var (context, _) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [fileName] = ["CheckClass"]
            });

        await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        _transitions.Should().Contain(PipelineStep.CheckingDroppedIdentifiers);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private (PipelineStepContext context, PipelineRun run) BuildContext(
        string? workspacePath,
        bool mergeForceResolved,
        IReadOnlyDictionary<string, IReadOnlyList<string>> droppedIdentifiers)
    {
        var run = new PipelineRun
        {
            RunId = $"check-dropped-{Guid.NewGuid():N}",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = workspacePath,
            MergeForceResolved = mergeForceResolved,
            DroppedIdentifiersByFile = droppedIdentifiers
        };

        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration(),
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            QualityGateValidator = Mock.Of<IQualityGateValidator>()
        };

        return (context, run);
    }

    [Fact]
    public async Task WhenReadingFileThrowsUnexpectedException_StepContinues()
    {
        // Create a directory at the expected file path — File.ReadAllTextAsync will throw
        // UnauthorizedAccessException / IOException (not FileNotFoundException), exercising
        // the generic catch (Exception) handler.
        const string pathKey = "src/BadPath.cs";
        var absPath = Path.Combine(_tempDir, pathKey);
        Directory.CreateDirectory(absPath); // directory, not file

        var (context, run) = BuildContext(workspacePath: _tempDir, mergeForceResolved: true,
            droppedIdentifiers: new Dictionary<string, IReadOnlyList<string>>
            {
                [pathKey] = ["SomeClass"]
            });

        var result = await new CheckDroppedIdentifiersStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue, "unexpected read failures must not fail the pipeline");
        run.NotReappliedIdentifiersByFile.Should().NotContainKey(pathKey,
            "a skipped file due to read error must not appear in not-reapplied");
    }
}
