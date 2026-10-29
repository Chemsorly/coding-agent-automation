namespace CodingAgent.Agent.UnitTests.Executors;

using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using Moq;
using Serilog;
using Xunit;

/// <summary>
/// Tests for <see cref="BrainReviewInputWriter.WriteAsync"/> (spec 054, Task 7A.3).
/// Uses temp directories; no Docker (T12).
/// </summary>
public sealed class BrainReviewInputWriterTests : IDisposable
{
    private readonly string _workspace;
    private readonly Mock<IBrainUpdateService> _brainSvc;
    private readonly BrainReviewInputWriter _sut;
    private static readonly ILogger NullLogger = new LoggerConfiguration().CreateLogger();

    public BrainReviewInputWriterTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"riw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _brainSvc = new Mock<IBrainUpdateService>();
        // Default setups for all synchronous/async methods called in BuildModelAsync
        _brainSvc.Setup(s => s.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IReadOnlyList<string>)[]);
        _brainSvc.Setup(s => s.ReadFileAtCommit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                 .Returns((string?)null);
        _brainSvc.Setup(s => s.ReadFileAtHead(It.IsAny<string>(), It.IsAny<string>()))
                 .Returns((string?)null);
        _brainSvc.Setup(s => s.GetSkillCommitsSinceLastReview(It.IsAny<string>()))
                 .Returns((IReadOnlyList<BrainCommitInfo>)[]);
        _sut = new BrainReviewInputWriter(_brainSvc.Object, NullLogger);
    }

    private static ConsolidationJobMessage MakeJob(string jobId = "job-1") => new ConsolidationJobMessage
    {
        JobId = jobId,
        Type = ConsolidationRunType.BrainConsolidation,
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration()
    };

    [Fact]
    public async Task WriteAsync_EmptyBrain_WritesOutputFile()
    {
        await _sut.WriteAsync(_workspace, null, MakeJob(), CancellationToken.None);

        var outputPath = Path.Combine(_workspace, AgentWorkspacePaths.BrainReviewInputFilePath);
        File.Exists(outputPath).Should().BeTrue("WriteAsync must create the review input file");
        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("# Brain review input");
    }

    [Fact]
    public async Task WriteAsync_WithInboxFile_IncludesInboxInOutput()
    {
        // Write a valid inbox file to the workspace (brain path = workspace itself in this test)
        var inboxRel = BrainLayout.InboxFileName(DateOnly.FromDateTime(DateTime.UtcNow), "run-1");
        var inboxFull = Path.Combine(_workspace, inboxRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(inboxFull)!);
        var inboxContent = $"{BrainLayout.InboxMarker}\nrun: run-1\ntype: implementation\noutcome: succeeded\nrepository: org/repo\nissue: #1\n\n## Candidates\n- a candidate\n";
        await File.WriteAllTextAsync(inboxFull, inboxContent);

        _brainSvc.Setup(s => s.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IReadOnlyList<string>)[inboxRel]);

        await _sut.WriteAsync(_workspace, null, MakeJob(), CancellationToken.None);

        var outputPath = Path.Combine(_workspace, AgentWorkspacePaths.BrainReviewInputFilePath);
        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain(inboxRel, "the inbox file path must appear in the review input");
    }

    [Fact]
    public async Task WriteAsync_ListTrackedFilesThrows_IsNonFatal()
    {
        // T6: WriteAsync must not throw on service exceptions
        _brainSvc.Setup(s => s.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("no git"));

        var act = () => _sut.WriteAsync(_workspace, null, MakeJob(), CancellationToken.None);
        await act.Should().NotThrowAsync("WriteAsync must be non-fatal (T6)");
    }

    [Fact]
    public async Task WriteAsync_WithGroup_IncludesGroupInOutput()
    {
        _brainSvc.Setup(s => s.ListTrackedFilesAsync(_workspace, It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IReadOnlyList<string>)[]);

        var group = new List<BrainGroupMember>
        {
            new BrainGroupMember { RepositoryFullName = "org/repo", RepositoryShortName = "repo", ProjectName = "P", StackLabels = ["dotnet"] }
        };

        await _sut.WriteAsync(_workspace, group, MakeJob(), CancellationToken.None);

        var outputPath = Path.Combine(_workspace, AgentWorkspacePaths.BrainReviewInputFilePath);
        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("org/repo");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            foreach (var f in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_workspace, recursive: true);
        }
    }
}
