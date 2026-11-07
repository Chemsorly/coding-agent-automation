using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Infrastructure;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Spec 056 DD13, DD16, req 6.1, 9.4 (tasks 3.4–3.6): the decorator records the first quota or login failure of the
/// job, masked and cut; the completion carries it only when the run ended Failed.
/// </summary>
public class ProviderFailureRecorderTests
{
    private static readonly AgentRequest Request = new() { Prompt = "p", WorkspacePath = "/ws" };

    private static Mock<IAgentProvider> Provider(params AgentResult[] results)
    {
        var provider = new Mock<IAgentProvider>();
        var queue = new Queue<AgentResult>(results);
        provider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() => queue.Dequeue());
        provider.SetupGet(p => p.ProviderType).Returns(AgentProviderType.ClaudeCode);
        return provider;
    }

    private static AgentResult Result(AgentErrorCategory category, AgentProviderFailure? failure = null) => new()
    {
        ExitCode = category == AgentErrorCategory.None ? 0 : 1,
        OutputLines = [],
        ErrorCategory = category,
        ProviderFailure = failure,
    };

    private static JobAssignmentMessage Assignment() => new()
    {
        JobId = "job-1",
        IssueIdentifier = "owner/repo#42",
        IssueDetail = new IssueDetail { Identifier = "owner/repo#42", Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
        IssueComments = [],
        RepoProviderConfigId = "repo-1",
        AgentProviderConfigId = "agent-1",
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration(),
        InitiatedBy = "test",
        QualityGateConfigs = [],
    };

    private static readonly AgentProviderFailure Quota = new(AgentProviderFailure.QuotaExhausted, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), "usage limit");

    [Fact]
    public async Task Execute_RecordsTheFirstQuotaFailureOnly_AndPassesResultsThrough()
    {
        var recorder = new ProviderFailureRecorder();
        var auth = new AgentProviderFailure(AgentProviderFailure.AuthFailed, null, "login rejected");
        var first = Result(AgentErrorCategory.ProviderQuotaExhausted, Quota);
        var second = Result(AgentErrorCategory.PermanentAuthFailure, auth);
        var decorator = new ProviderFailureRecordingAgentProvider(Provider(first, second).Object, recorder);

        (await decorator.ExecuteAsync(Request, CancellationToken.None)).Should().BeSameAs(first);
        (await decorator.ExecuteAsync(Request, CancellationToken.None)).Should().BeSameAs(second);

        recorder.Failure.Should().Be(Quota);
    }

    [Theory]
    [InlineData(AgentErrorCategory.None)]
    [InlineData(AgentErrorCategory.ProviderRateLimit)]
    [InlineData(AgentErrorCategory.ProviderOverload)]
    [InlineData(AgentErrorCategory.ProviderMisconfigured)]
    public async Task Execute_IgnoresTransientAndMisconfiguredResults(AgentErrorCategory category)
    {
        var recorder = new ProviderFailureRecorder();
        var decorator = new ProviderFailureRecordingAgentProvider(Provider(Result(category, Quota)).Object, recorder);

        await decorator.ExecuteAsync(Request, CancellationToken.None);

        recorder.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Execute_AnAuthResultWithoutDetail_IsStillRecorded()
    {
        var recorder = new ProviderFailureRecorder();
        var decorator = new ProviderFailureRecordingAgentProvider(Provider(Result(AgentErrorCategory.PermanentAuthFailure)).Object, recorder);

        await decorator.ExecuteAsync(Request, CancellationToken.None);

        recorder.Failure!.Category.Should().Be(AgentProviderFailure.AuthFailed);
        recorder.Failure.Detail.Should().Contain("PermanentAuthFailure");
    }

    [Fact]
    public async Task Execute_ExceptionsPassThrough()
    {
        var provider = new Mock<IAgentProvider>();
        provider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var decorator = new ProviderFailureRecordingAgentProvider(provider.Object, new ProviderFailureRecorder());

        await decorator.Invoking(d => d.ExecuteAsync(Request, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    // T4 canary (req 9.4): a key inside the provider's line never reaches the detail.
    [Fact]
    public async Task Execute_MasksTheRunsSecretsAndKeyShapes_AndCutsTo300_T4()
    {
        const string projectSecret = "proj-secret-CANARY-123456";
        const string apiKey = "sk-ant-api03-CANARYcanaryCANARYcanary0000";
        var recorder = new ProviderFailureRecorder();
        var line = $"billing_error: key {apiKey} and {projectSecret} rejected " + new string('x', 400);
        var decorator = new ProviderFailureRecordingAgentProvider(
            Provider(Result(AgentErrorCategory.ProviderQuotaExhausted, Quota with { Detail = line })).Object, recorder);

        await decorator.ExecuteAsync(
            new AgentRequest
            {
                Prompt = "p", WorkspacePath = "/ws",
                EnvironmentVariables = new Dictionary<string, string> { ["PROJECT_TOKEN"] = projectSecret },
            },
            CancellationToken.None);

        recorder.Failure!.Detail.Should().NotContain(apiKey).And.NotContain(projectSecret);
        recorder.Failure.Detail.Length.Should().BeLessThanOrEqualTo(AgentProviderFailure.MaxDetailLength);
    }

    [Fact]
    public async Task Validate_AMissingCredential_IsRecordedAsAuthFailed_AndRethrown_DD16()
    {
        var provider = new Mock<IAgentProvider>();
        provider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProviderCredentialMissingException(AgentCredentials.ClaudeSubscription));
        var recorder = new ProviderFailureRecorder();
        var decorator = new ProviderFailureRecordingAgentProvider(provider.Object, recorder);

        await decorator.Invoking(d => d.ValidateAsync(CancellationToken.None))
            .Should().ThrowAsync<ProviderCredentialMissingException>();

        recorder.Failure.Should().Be(new AgentProviderFailure(AgentProviderFailure.AuthFailed, null, "No Claude subscription is configured"));
    }

    [Fact]
    public async Task Validate_OtherFailures_RecordNothing()
    {
        var provider = new Mock<IAgentProvider>();
        provider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no cli"));
        var recorder = new ProviderFailureRecorder();

        await new ProviderFailureRecordingAgentProvider(provider.Object, recorder)
            .Invoking(d => d.ValidateAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        recorder.Failure.Should().BeNull();
    }

    [Theory]
    [InlineData(PipelineStep.Failed, true)]
    [InlineData(PipelineStep.Completed, false)]
    [InlineData(PipelineStep.Cancelled, false)]
    public void Attach_AddsTheFailureToAFailedPayloadOnly(PipelineStep step, bool attached)
    {
        var recorder = new ProviderFailureRecorder();
        recorder.Record(Quota);

        var payload = recorder.Attach(new JobCompletionPayload { FinalStep = step, CompletedAt = DateTimeOffset.UtcNow });

        payload.ProviderFailure.Should().Be(attached ? Quota : null);
    }

    [Fact]
    public void Attach_WithoutARecordedFailure_ChangesNothing()
    {
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Failed, CompletedAt = DateTimeOffset.UtcNow };

        new ProviderFailureRecorder().Attach(payload).Should().BeSameAs(payload);
    }

    [Fact]
    public void Factory_WrapsEveryAgentProvider_WhenItHasARecorder()
    {
        var factory = new AgentProviderFactory(
            new Mock<KiroCliLib.Core.IKiroCliOrchestrator>().Object, new Mock<IHttpClientFactory>().Object,
            new PipelineConfiguration(), failureRecorder: new ProviderFailureRecorder());

        var provider = factory.CreateAgentProvider(new ProviderConfig
        {
            DisplayName = "claude", Kind = ProviderKind.Agent, ProviderType = ProviderTypes.ClaudeCode,
            Settings = new Dictionary<string, string>(),
        });

        provider.Should().BeOfType<ProviderFailureRecordingAgentProvider>()
            .Which.Inner.ProviderType.Should().Be(AgentProviderType.ClaudeCode);
    }

    // V11 (DD16): a provider that fails validation ends the job Failed through AgentJobRunner's catch; the
    // completion carries the recorded auth failure.
    [Fact]
    public async Task JobRunner_WhenTheProviderFailsValidation_ReportsFailedWithTheAuthFailure_V11()
    {
        var recorder = new ProviderFailureRecorder();
        var provider = new Mock<IAgentProvider>();
        provider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProviderCredentialMissingException(AgentCredentials.ClaudeSubscription));
        var decorator = new ProviderFailureRecordingAgentProvider(provider.Object, recorder);

        var payload = await AgentJobRunner.ExecuteAsync(new AgentJobExecutionRequest(
            async (_, _, _, _, ct) =>
            {
                await decorator.ValidateAsync(ct);
                return new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };
            },
            Assignment(),
            null!,
            new OutputBatcher(),
            null,
            ProviderFailureRecorder: recorder));

        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.ProviderFailure!.Category.Should().Be(AgentProviderFailure.AuthFailed);
    }

    [Fact]
    public async Task JobRunner_ASuccessfulRunAfterARecordedFailure_ReportsNone()
    {
        var recorder = new ProviderFailureRecorder();
        recorder.Record(Quota);

        var payload = await AgentJobRunner.ExecuteAsync(new AgentJobExecutionRequest(
            (_, _, _, _, _) => Task.FromResult(new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow }),
            Assignment(),
            null!,
            new OutputBatcher(),
            null,
            ProviderFailureRecorder: recorder));

        payload.ProviderFailure.Should().BeNull();
    }
}
