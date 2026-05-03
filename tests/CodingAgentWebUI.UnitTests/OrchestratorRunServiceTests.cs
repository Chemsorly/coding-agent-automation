using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="OrchestratorRunService"/>.
/// </summary>
public class OrchestratorRunServiceTests
{
    private static OrchestratorRunService CreateService(int bufferCapacity = 100) =>
        new(new Mock<ILogger>().Object, bufferCapacity);

    private static PipelineRun CreateRun(string runId = "run-1", string issueId = "issue-1") => new()
    {
        RunId = runId,
        IssueIdentifier = issueId,
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow
    };

    [Fact]
    public void HasActiveRuns_NoRuns_ReturnsFalse()
    {
        var service = CreateService();
        service.HasActiveRuns.Should().BeFalse();
    }

    [Fact]
    public void HasActiveRuns_WithRun_ReturnsTrue()
    {
        var service = CreateService();
        service.AddRun(CreateRun());
        service.HasActiveRuns.Should().BeTrue();
    }

    [Fact]
    public void ActiveRunCount_ReflectsAddedRuns()
    {
        var service = CreateService();
        service.ActiveRunCount.Should().Be(0);

        service.AddRun(CreateRun("run-1"));
        service.ActiveRunCount.Should().Be(1);

        service.AddRun(CreateRun("run-2"));
        service.ActiveRunCount.Should().Be(2);
    }

    [Fact]
    public void AddRun_DuplicateRunId_DoesNotOverwrite()
    {
        var service = CreateService();
        var run1 = CreateRun("run-1", "issue-A");
        var run2 = CreateRun("run-1", "issue-B");

        service.AddRun(run1);
        service.AddRun(run2); // duplicate — should be ignored

        service.ActiveRunCount.Should().Be(1);
        service.GetRun("run-1")!.IssueIdentifier.Should().Be("issue-A");
    }

    [Fact]
    public void GetRun_ExistingRun_ReturnsRun()
    {
        var service = CreateService();
        var run = CreateRun("run-42");
        service.AddRun(run);

        service.GetRun("run-42").Should().BeSameAs(run);
    }

    [Fact]
    public void GetRun_NonExistentRun_ReturnsNull()
    {
        var service = CreateService();
        service.GetRun("nonexistent").Should().BeNull();
    }

    [Fact]
    public void RemoveRun_ExistingRun_RemovesAndReturns()
    {
        var service = CreateService();
        var run = CreateRun("run-1");
        service.AddRun(run);

        var removed = service.RemoveRun("run-1");

        removed.Should().BeSameAs(run);
        service.HasActiveRuns.Should().BeFalse();
        service.ActiveRunCount.Should().Be(0);
    }

    [Fact]
    public void RemoveRun_NonExistentRun_ReturnsNull()
    {
        var service = CreateService();
        service.RemoveRun("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetActiveRuns_ReturnsAllRuns()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1"));
        service.AddRun(CreateRun("run-2"));
        service.AddRun(CreateRun("run-3"));

        var runs = service.GetActiveRuns();
        runs.Should().HaveCount(3);
    }

    [Fact]
    public void IsIssueBeingProcessed_ActiveIssue_ReturnsTrue()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1", "issue-42"));

        service.IsIssueBeingProcessed("issue-42").Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessed_InactiveIssue_ReturnsFalse()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1", "issue-42"));

        service.IsIssueBeingProcessed("issue-99").Should().BeFalse();
    }

    [Fact]
    public void IsIssueBeingProcessed_AfterRemoval_ReturnsFalse()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1", "issue-42"));
        service.RemoveRun("run-1");

        service.IsIssueBeingProcessed("issue-42").Should().BeFalse();
    }

    [Fact]
    public void GetOutputBuffer_CreatesBufferOnAdd()
    {
        var service = CreateService(bufferCapacity: 500);
        service.AddRun(CreateRun("run-1"));

        var buffer = service.GetOutputBuffer("run-1");
        buffer.Should().NotBeNull();
        buffer.Capacity.Should().Be(500);
    }

    [Fact]
    public void GetOutputBuffer_NonExistentRun_CreatesNewBuffer()
    {
        var service = CreateService(bufferCapacity: 200);
        var buffer = service.GetOutputBuffer("new-run");
        buffer.Should().NotBeNull();
        buffer.Capacity.Should().Be(200);
    }

    [Fact]
    public void GetOutputBuffer_SameRunId_ReturnsSameInstance()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1"));

        var buffer1 = service.GetOutputBuffer("run-1");
        var buffer2 = service.GetOutputBuffer("run-1");

        buffer1.Should().BeSameAs(buffer2);
    }

    [Fact]
    public void RemoveRun_CleansUpOutputBuffer()
    {
        var service = CreateService();
        service.AddRun(CreateRun("run-1"));
        var originalBuffer = service.GetOutputBuffer("run-1");
        originalBuffer.Add("some output");

        service.RemoveRun("run-1");

        // After removal, GetOutputBuffer creates a new empty buffer
        var newBuffer = service.GetOutputBuffer("run-1");
        newBuffer.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new OrchestratorRunService(null!, 100);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_InvalidBufferCapacity_Throws()
    {
        var act = () => new OrchestratorRunService(new Mock<ILogger>().Object, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddRun_NullRun_Throws()
    {
        var service = CreateService();
        var act = () => service.AddRun(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetRun_NullRunId_Throws()
    {
        var service = CreateService();
        var act = () => service.GetRun(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RemoveRun_NullRunId_Throws()
    {
        var service = CreateService();
        var act = () => service.RemoveRun(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IsIssueBeingProcessed_NullIdentifier_Throws()
    {
        var service = CreateService();
        var act = () => service.IsIssueBeingProcessed(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetOutputBuffer_NullRunId_Throws()
    {
        var service = CreateService();
        var act = () => service.GetOutputBuffer(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
