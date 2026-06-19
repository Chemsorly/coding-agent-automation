using AwesomeAssertions;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="ConsolidationExecutorBase"/> helper methods.
/// </summary>
public class ConsolidationExecutorBaseTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    private TestableExecutor CreateExecutor() => new(_mockLogger.Object);

    private static ConsolidationJobMessage CreateJob(string? jobId = null, string? workspacePath = null) => new()
    {
        JobId = jobId ?? Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.BrainConsolidation,
        TemplateId = "template-1",
        TemplateName = "Test Template",
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration(),
        WorkspacePath = workspacePath
    };

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new TestableExecutor(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void ValidateJobId_ValidGuid_ReturnsNull()
    {
        var executor = CreateExecutor();
        var job = CreateJob(Guid.NewGuid().ToString());

        var result = executor.InvokeValidateJobId(job);

        result.Should().BeNull();
    }

    [Fact]
    public void ValidateJobId_InvalidGuid_ReturnsFailureResult()
    {
        var executor = CreateExecutor();
        var job = CreateJob("not-a-guid");

        var result = executor.InvokeValidateJobId(job);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.JobId.Should().Be("not-a-guid");
        result.ErrorMessage.Should().Be("Invalid JobId format");
    }

    [Fact]
    public void ValidateJobId_EmptyString_ReturnsFailureResult()
    {
        var executor = CreateExecutor();
        var job = CreateJob("");

        var result = executor.InvokeValidateJobId(job);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid JobId format");
    }

    [Fact]
    public void ResolveWorkspacePath_WithJobWorkspacePath_CombinesWithSuffix()
    {
        var executor = CreateExecutor();
        var job = CreateJob(workspacePath: "/tmp/my-workspace");

        var result = executor.InvokeResolveWorkspacePath(job);

        result.Should().Be(Path.Combine("/tmp/my-workspace", "test-suffix"));
    }

    [Fact]
    public void ResolveWorkspacePath_NullJobWorkspacePath_UsesTempPath()
    {
        var executor = CreateExecutor();
        var jobId = Guid.NewGuid().ToString();
        var job = CreateJob(jobId: jobId, workspacePath: null);

        var result = executor.InvokeResolveWorkspacePath(job);

        result.Should().Be(Path.Combine(Path.GetTempPath(), "consolidation", jobId, "test-suffix"));
    }

    [Fact]
    public void CreateFailureResult_SetsFieldsCorrectly()
    {
        var result = TestableExecutor.InvokeCreateFailureResult("job-123", "something went wrong");

        result.JobId.Should().Be("job-123");
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("something went wrong");
    }

    [Fact]
    public void CreateCancelledResult_SetsFieldsCorrectly()
    {
        var result = TestableExecutor.InvokeCreateCancelledResult("job-456");

        result.JobId.Should().Be("job-456");
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Consolidation run was cancelled");
    }

    [Fact]
    public async Task ExecuteAgentAndCheckAsync_Success_ReturnsResultWithNullFailure()
    {
        var executor = CreateExecutor();
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["done"] });

        var (result, failure) = await executor.InvokeExecuteAgentAndCheckAsync(
            mockAgent.Object, new AgentRequest { Prompt = "test", WorkspacePath = "/tmp" }, "job-1", CancellationToken.None);

        failure.Should().BeNull();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAgentAndCheckAsync_Failure_ReturnsFailureResult()
    {
        var executor = CreateExecutor();
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(x => x.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = [] });

        var (result, failure) = await executor.InvokeExecuteAgentAndCheckAsync(
            mockAgent.Object, new AgentRequest { Prompt = "test", WorkspacePath = "/tmp" }, "job-1", CancellationToken.None);

        failure.Should().NotBeNull();
        failure!.Success.Should().BeFalse();
        failure.JobId.Should().Be("job-1");
        failure.ErrorMessage.Should().Contain("1");
    }

    [Fact]
    public async Task WrapWithCancellationHandlingAsync_Success_ReturnsActionResult()
    {
        var executor = CreateExecutor();
        var expected = new ConsolidationJobResult { JobId = "job-1", Success = true, Summary = "ok" };

        var result = await executor.InvokeWrapWithCancellationHandlingAsync(
            "job-1", () => Task.FromResult(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task WrapWithCancellationHandlingAsync_Cancelled_ReturnsCancelledResult()
    {
        var executor = CreateExecutor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.InvokeWrapWithCancellationHandlingAsync(
            "job-1",
            () => throw new OperationCanceledException(),
            cts.Token);

        result.Success.Should().BeFalse();
        result.JobId.Should().Be("job-1");
        result.ErrorMessage.Should().Contain("cancelled");
    }

    [Fact]
    public async Task WrapWithCancellationHandlingAsync_Exception_ReturnsFailureResult()
    {
        var executor = CreateExecutor();

        var result = await executor.InvokeWrapWithCancellationHandlingAsync(
            "job-1",
            () => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.JobId.Should().Be("job-1");
        result.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task RunWithTracingAsync_Success_ReturnsValue()
    {
        var executor = CreateExecutor();

        var result = await executor.InvokeRunWithTracingAsync("Test.Step", "job-1", async _ =>
        {
            await Task.CompletedTask;
            return 42;
        });

        result.Should().Be(42);
    }

    [Fact]
    public async Task RunWithTracingAsync_NonOceException_RethrowsException()
    {
        var executor = CreateExecutor();

        var act = () => executor.InvokeRunWithTracingAsync<int>("Test.Step", "job-1", _ =>
        {
            throw new InvalidOperationException("test error");
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("test error");
    }

    [Fact]
    public async Task RunWithTracingAsync_OperationCanceledException_PropagatesWithoutErrorStatus()
    {
        var executor = CreateExecutor();

        var act = () => executor.InvokeRunWithTracingAsync<int>("Test.Step", "job-1", _ =>
        {
            throw new OperationCanceledException();
        });

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunWithTracingAsync_VoidOverload_CompletesSuccessfully()
    {
        var executor = CreateExecutor();
        var executed = false;

        await executor.InvokeRunWithTracingAsync("Test.Step", "job-1", async _ =>
        {
            await Task.CompletedTask;
            executed = true;
        });

        executed.Should().BeTrue();
    }

    [Fact]
    public async Task RunWithTracingAsync_VoidOverload_NonOceException_Rethrows()
    {
        var executor = CreateExecutor();

        var act = () => executor.InvokeRunWithTracingAsync("Test.Step", "job-1", async _ =>
        {
            await Task.CompletedTask;
            throw new InvalidOperationException("void error");
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("void error");
    }

    /// <summary>
    /// Concrete test subclass to expose protected members for testing.
    /// </summary>
    private sealed class TestableExecutor : ConsolidationExecutorBase
    {
        protected override string WorkspaceSuffix => "test-suffix";
        protected override string ExecutorName => "Test executor";

        public TestableExecutor(Serilog.ILogger logger) : base(logger) { }

        public ConsolidationJobResult? InvokeValidateJobId(ConsolidationJobMessage job) => ValidateJobId(job);
        public string InvokeResolveWorkspacePath(ConsolidationJobMessage job) => ResolveWorkspacePath(job);
        public static ConsolidationJobResult InvokeCreateFailureResult(string jobId, string msg) => CreateFailureResult(jobId, msg);
        public static ConsolidationJobResult InvokeCreateCancelledResult(string jobId) => CreateCancelledResult(jobId);

        public Task<(AgentResult Result, ConsolidationJobResult? Failure)> InvokeExecuteAgentAndCheckAsync(
            IAgentProvider agentProvider, AgentRequest request, string jobId, CancellationToken ct)
            => ExecuteAgentAndCheckAsync(agentProvider, request, jobId, ct);

        public Task<ConsolidationJobResult> InvokeWrapWithCancellationHandlingAsync(
            string jobId, Func<Task<ConsolidationJobResult>> action, CancellationToken ct)
            => WrapWithCancellationHandlingAsync(jobId, action, ct);

        public Task<T> InvokeRunWithTracingAsync<T>(string activityName, string jobId, Func<System.Diagnostics.Activity?, Task<T>> action)
            => RunWithTracingAsync(activityName, jobId, action);

        public Task InvokeRunWithTracingAsync(string activityName, string jobId, Func<System.Diagnostics.Activity?, Task> action)
            => RunWithTracingAsync(activityName, jobId, action);
    }
}
