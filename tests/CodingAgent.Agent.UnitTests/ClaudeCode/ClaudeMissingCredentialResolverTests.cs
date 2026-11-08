using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Agent.ClaudeCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

/// <summary>
/// Spec 056 DD16, task 9.4: a Claude Code profile whose credential is not in the agent Secret fails in
/// <see cref="ClaudeCodeAgentProvider.ValidateAsync"/>, which <see cref="AgentProviderResolver"/> calls before any agent
/// call. Through the real resolver and the provider failure decorator, the job's failure carries <c>auth_failed</c>, so
/// the API pauses that credential instead of failing every issue (the upgrade case of a former <c>auto</c> profile that
/// only had an API key).
/// </summary>
public sealed class ClaudeMissingCredentialResolverTests
{
    private static readonly ProviderConfig RepoConfig = new()
    {
        Id = "repo-1",
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "Repo",
        Settings = new Dictionary<string, string>(),
    };

    private static readonly ProviderConfig AgentConfig = new()
    {
        Id = "agent-1",
        Kind = ProviderKind.Agent,
        ProviderType = "Claude",
        DisplayName = "Claude",
        Settings = new Dictionary<string, string>(),
    };

    private static JobAssignmentMessage Job() => new()
    {
        JobId = "job-claude-missing-token",
        IssueIdentifier = "owner/repo#1",
        IssueDetail = new IssueDetail { IsAuthorVerified = true, Identifier = "owner/repo#1", Title = "T", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        RepoProviderConfigId = RepoConfig.Id,
        AgentProviderConfigId = AgentConfig.Id,
        PipelineConfiguration = new PipelineConfiguration(),
        ProviderConfigs = [RepoConfig, AgentConfig],
        ReviewerConfigs = [],
        QualityGateConfigs = [],
        IssueComments = [],
        InitiatedBy = "test",
    };

    [Theory]
    [InlineData(ClaudeCodeAuthModes.Subscription, "claude-oauth-token")]
    [InlineData(ClaudeCodeAuthModes.ApiKey, "claude-api-key")]
    public async Task AProfileWithoutItsCredential_FailsBeforeAnyAgentCall_AndRecordsAuthFailed(string authMode, string secretKey)
    {
        var recorder = new ProviderFailureRecorder();
        var launcher = new FakeClaudeProcessLauncher();
        var claude = new ClaudeCodeAgentProvider(
            new Mock<Serilog.ILogger>().Object,
            new ClaudeCodeSettings("claude-opus-5-5", "/opt/claude", AgentEffortLevel.High, authMode, "/tmp/pipeline-mcp.json"),
            launcher, _ => null); // the agent Secret has neither credential
        var repo = new Mock<IRepositoryProvider>();
        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(repo.Object);
        factory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Returns(new ProviderFailureRecordingAgentProvider(claude, recorder));

        var act = () => new AgentProviderResolver(new Mock<Serilog.ILogger>().Object)
            .ResolveAsync(Job(), factory.Object, factory.Object, RepoConfig, AgentConfig, CancellationToken.None);

        await act.Should().ThrowAsync<ProviderCredentialMissingException>();
        launcher.Started.Should().BeEmpty("no agent call and no version check runs without the credential");
        recorder.Failure.Should().NotBeNull();
        recorder.Failure!.Category.Should().Be(AgentProviderFailure.AuthFailed);
        recorder.Failure.Detail.Should().Contain(secretKey);

        // The job's Failed completion carries it (AgentJobRunner attaches the recorder's failure).
        var payload = recorder.Attach(new JobCompletionPayload { FinalStep = PipelineStep.Failed, CompletedAt = DateTimeOffset.UtcNow });
        payload.ProviderFailure!.Category.Should().Be(AgentProviderFailure.AuthFailed);
    }
}
