using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Extended unit tests for OrchestratorRunService — covers additional paths
/// for output buffers, concurrent access, and edge cases.
/// </summary>
public class OrchestratorRunServiceExtendedTests
{
    private readonly OrchestratorRunService _service;

    public OrchestratorRunServiceExtendedTests()
    {
        _service = new OrchestratorRunService(new Mock<ILogger>().Object);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OrchestratorRunService(null!));
    }

    [Fact]
    public void Constructor_ZeroCapacity_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrchestratorRunService(new Mock<ILogger>().Object, 0));
    }

    [Fact]
    public void Constructor_NegativeCapacity_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrchestratorRunService(new Mock<ILogger>().Object, -1));
    }

    [Fact]
    public void HasActiveRuns_WhenEmpty_ReturnsFalse()
    {
        _service.HasActiveRuns.Should().BeFalse();
    }

    [Fact]
    public void HasActiveRuns_WhenRunAdded_ReturnsTrue()
    {
        _service.AddRun(CreateRun("run-1"));

        _service.HasActiveRuns.Should().BeTrue();
    }

    [Fact]
    public void ActiveRunCount_ReflectsAddedRuns()
    {
        _service.AddRun(CreateRun("run-1"));
        _service.AddRun(CreateRun("run-2"));

        _service.ActiveRunCount.Should().Be(2);
    }

    [Fact]
    public void IsIssueBeingProcessed_WhenIssueActive_ReturnsTrue()
    {
        _service.AddRun(CreateRun("run-1", "org/repo#1"));

        _service.IsIssueBeingProcessed("org/repo#1").Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessed_WhenIssueNotActive_ReturnsFalse()
    {
        _service.AddRun(CreateRun("run-1", "org/repo#1"));

        _service.IsIssueBeingProcessed("org/repo#2").Should().BeFalse();
    }

    [Fact]
    public void IsIssueBeingProcessed_NullIdentifier_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsIssueBeingProcessed(null!));
    }

    [Fact]
    public void GetRun_ExistingRun_ReturnsRun()
    {
        var run = CreateRun("run-1");
        _service.AddRun(run);

        var result = _service.GetRun("run-1");

        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-1");
    }

    [Fact]
    public void GetRun_NonExistentRun_ReturnsNull()
    {
        _service.GetRun("non-existent").Should().BeNull();
    }

    [Fact]
    public void GetRun_NullRunId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _service.GetRun(null!));
    }

    [Fact]
    public void AddRun_NullRun_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _service.AddRun(null!));
    }

    [Fact]
    public void AddRun_DuplicateRunId_DoesNotOverwrite()
    {
        var run1 = CreateRun("run-1", "org/repo#1");
        var run2 = CreateRun("run-1", "org/repo#2");

        _service.AddRun(run1);
        _service.AddRun(run2); // Should log warning, not overwrite

        var result = _service.GetRun("run-1");
        result!.IssueIdentifier.Should().Be("org/repo#1"); // Original preserved
    }

    [Fact]
    public void RemoveRun_ExistingRun_ReturnsRemovedRun()
    {
        var run = CreateRun("run-1");
        _service.AddRun(run);

        var removed = _service.RemoveRun("run-1");

        removed.Should().NotBeNull();
        removed!.RunId.Should().Be("run-1");
        _service.GetRun("run-1").Should().BeNull();
    }

    [Fact]
    public void RemoveRun_NonExistentRun_ReturnsNull()
    {
        var removed = _service.RemoveRun("non-existent");

        removed.Should().BeNull();
    }

    [Fact]
    public void RemoveRun_NullRunId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _service.RemoveRun(null!));
    }

    [Fact]
    public void GetOutputBuffer_CreatesBufferOnDemand()
    {
        var buffer = _service.GetOutputBuffer("run-1");

        buffer.Should().NotBeNull();
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public void GetOutputBuffer_NullRunId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _service.GetOutputBuffer(null!));
    }

    [Fact]
    public void GetOutputBuffer_SameRunId_ReturnsSameInstance()
    {
        var buffer1 = _service.GetOutputBuffer("run-1");
        var buffer2 = _service.GetOutputBuffer("run-1");

        ReferenceEquals(buffer1, buffer2).Should().BeTrue();
    }

    [Fact]
    public void GetActiveRuns_ReturnsSnapshot()
    {
        _service.AddRun(CreateRun("run-1"));
        _service.AddRun(CreateRun("run-2"));

        var runs = _service.GetActiveRuns();

        runs.Should().HaveCount(2);
    }

    [Fact]
    public void GetActiveRuns_WhenEmpty_ReturnsEmptyList()
    {
        var runs = _service.GetActiveRuns();

        runs.Should().BeEmpty();
    }

    [Fact]
    public void ConcurrentAddAndRemove_IsThreadSafe()
    {
        // Add 100 runs concurrently
        Parallel.For(0, 100, i => _service.AddRun(CreateRun($"run-{i}")));

        _service.ActiveRunCount.Should().Be(100);

        // Remove 50 concurrently
        Parallel.For(0, 50, i => _service.RemoveRun($"run-{i}"));

        _service.ActiveRunCount.Should().Be(50);
    }

    private static PipelineRun CreateRun(string runId, string issueIdentifier = "org/repo#1")
    {
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };
    }
}
