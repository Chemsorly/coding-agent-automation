using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="CiLogWriter"/>.
/// </summary>
public class CiLogWriterTests : IDisposable
{
    private readonly CiLogWriter _writer;
    private readonly string _tempDir;

    public CiLogWriterTests()
    {
        _writer = new CiLogWriter(new Mock<ILogger>().Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"cilog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static PipelineRunStatus CreateStatus(params PipelineJobResult[] jobs) => new()
    {
        State = PipelineRunState.Failed,
        Jobs = jobs
    };

    private static PipelineJobResult CreateFailedJob(long jobId, string name, string logContent) => new()
    {
        JobId = jobId,
        Name = name,
        State = PipelineRunState.Failed,
        LogContent = logContent
    };

    private static PipelineJobResult CreatePassedJob(long jobId, string name) => new()
    {
        JobId = jobId,
        Name = name,
        State = PipelineRunState.Passed
    };

    [Fact]
    public void WriteJobLogs_NoFailedJobs_ReturnsEmpty()
    {
        var status = CreateStatus(CreatePassedJob(1, "build"));
        var result = _writer.WriteJobLogs(status, _tempDir, "run-1");
        result.Should().BeEmpty();
    }

    [Fact]
    public void WriteJobLogs_FailedJobWithLog_WritesFile()
    {
        var status = CreateStatus(CreateFailedJob(42, "build", "Error: compilation failed"));
        var result = _writer.WriteJobLogs(status, _tempDir, "run-1");

        result.Should().HaveCount(1);
        result.Should().ContainKey(42);

        var filePath = Path.Combine(_tempDir, result[42]);
        File.Exists(filePath).Should().BeTrue();
        File.ReadAllText(filePath).Should().Be("Error: compilation failed");
    }

    [Fact]
    public void WriteJobLogs_MultipleFailedJobs_WritesAllFiles()
    {
        var status = CreateStatus(
            CreateFailedJob(1, "build", "build error"),
            CreateFailedJob(2, "test", "test error"),
            CreatePassedJob(3, "lint"));

        var result = _writer.WriteJobLogs(status, _tempDir, "run-1");

        result.Should().HaveCount(2);
        result.Should().ContainKey(1);
        result.Should().ContainKey(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void WriteJobLogs_FailedJobWithEmptyOrNullLog_IsSkipped(string? logContent)
    {
        var status = CreateStatus(new PipelineJobResult
        {
            JobId = 1,
            Name = "build",
            State = PipelineRunState.Failed,
            LogContent = logContent
        });

        var result = _writer.WriteJobLogs(status, _tempDir, "run-1");
        result.Should().BeEmpty();
    }

    [Fact]
    public void WriteJobLogs_FilePathIsRelative()
    {
        var status = CreateStatus(CreateFailedJob(1, "build", "error"));
        var result = _writer.WriteJobLogs(status, _tempDir, "run-1");

        result[1].Should().StartWith(".agent/quality-gates/");
        result[1].Should().Contain("build");
    }

    [Fact]
    public void WriteJobLogs_FilePathUsesForwardSlashes()
    {
        var status = CreateStatus(CreateFailedJob(1, "build", "error"));
        var result = _writer.WriteJobLogs(status, _tempDir, "run-1");

        result[1].Should().NotContain("\\");
    }

    [Fact]
    public void WriteJobLogs_SanitizesJobName()
    {
        var status = CreateStatus(CreateFailedJob(1, "build/test:special<chars>", "error"));
        var result = _writer.WriteJobLogs(status, _tempDir, "run-1");

        // Should not throw and should produce a valid file
        result.Should().HaveCount(1);
        var filePath = Path.Combine(_tempDir, result[1]);
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public void WriteJobLogs_NullStatus_Throws()
    {
        var act = () => _writer.WriteJobLogs(null!, _tempDir, "run-1");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WriteJobLogs_NullWorkspacePath_Throws()
    {
        var status = CreateStatus();
        var act = () => _writer.WriteJobLogs(status, null!, "run-1");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WriteJobLogs_NullRunId_Throws()
    {
        var status = CreateStatus();
        var act = () => _writer.WriteJobLogs(status, _tempDir, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new CiLogWriter(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
