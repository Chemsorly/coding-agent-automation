using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for the S107 parameter-object records introduced in this assembly:
/// <see cref="AgentWorkerServiceDependencies"/>, <see cref="WorkItemAgentServiceDependencies"/>,
/// and <see cref="LocalPipelineExecutorDependencies"/>.
/// Verifies property assignment and that the records are usable for construction.
/// </summary>
public class ParameterObjectTests
{
    // ── AgentWorkerServiceDependencies ─────────────────────────────────

    [Fact]
    public void AgentWorkerServiceDependencies_AllRequiredPropertiesAssigned()
    {
        // AgentConnectionLifecycle, AgentJobSlotManager, ChatJobHandler, ConsolidationJobHandler
        // are sealed — use null! for the dependency object tests (we test that the record stores
        // values, not that the classes are functional)
        AgentConnectionLifecycle connectionLifecycle = null!;
        AgentJobSlotManager slotManager = null!;
        ChatJobHandler chatHandler = null!;
        ConsolidationJobHandler consolidationHandler = null!;
        var agentId = new AgentId("test-agent");
        var executor = Mock.Of<IPipelineExecutor>();
        var completionReporter = Mock.Of<IJobCompletionReporter>();
        var hostLifetime = Mock.Of<IHostApplicationLifetime>();
        var logger = Mock.Of<Serilog.ILogger>();

        var deps = new AgentWorkerServiceDependencies(
            connectionLifecycle,
            slotManager,
            chatHandler,
            consolidationHandler,
            agentId,
            executor,
            completionReporter,
            hostLifetime,
            logger);

        deps.ConnectionLifecycle.Should().BeNull();
        deps.SlotManager.Should().BeNull();
        deps.ChatHandler.Should().BeNull();
        deps.ConsolidationHandler.Should().BeNull();
        deps.AgentId.Should().Be(agentId);
        deps.Executor.Should().BeSameAs(executor);
        deps.CompletionReporter.Should().BeSameAs(completionReporter);
        deps.HostApplicationLifetime.Should().BeSameAs(hostLifetime);
        deps.Logger.Should().BeSameAs(logger);
    }

    // ── WorkItemAgentServiceDependencies ───────────────────────────────

    [Fact]
    public void WorkItemAgentServiceDependencies_AllRequiredPropertiesAssigned()
    {
        var workItemClient = Mock.Of<IWorkItemLifecycleClient>();
        var connectionManager = Mock.Of<IAgentConnectionManager>();
        var executor = Mock.Of<IWorkItemExecutor>();
        var completionReporter = Mock.Of<IJobCompletionReporter>();
        var agentId = new AgentId("wi-agent");
        var lifetime = Mock.Of<IHostApplicationLifetime>();
        var logger = Mock.Of<Serilog.ILogger>();

        var deps = new WorkItemAgentServiceDependencies(
            WorkItemId: "wi-123",
            WorkItemClient: workItemClient,
            ConnectionManager: connectionManager,
            WorkItemExecutor: executor,
            CompletionReporter: completionReporter,
            AgentId: agentId,
            Lifetime: lifetime,
            Logger: logger);

        deps.WorkItemId.Should().Be("wi-123");
        deps.WorkItemClient.Should().BeSameAs(workItemClient);
        deps.ConnectionManager.Should().BeSameAs(connectionManager);
        deps.WorkItemExecutor.Should().BeSameAs(executor);
        deps.CompletionReporter.Should().BeSameAs(completionReporter);
        deps.AgentId.Should().Be(agentId);
        deps.Lifetime.Should().BeSameAs(lifetime);
        deps.Logger.Should().BeSameAs(logger);
    }

    [Fact]
    public void WorkItemAgentServiceDependencies_ServiceProvider_DefaultsToNull()
    {
        var deps = new WorkItemAgentServiceDependencies(
            WorkItemId: "wi-1",
            WorkItemClient: Mock.Of<IWorkItemLifecycleClient>(),
            ConnectionManager: Mock.Of<IAgentConnectionManager>(),
            WorkItemExecutor: Mock.Of<IWorkItemExecutor>(),
            CompletionReporter: Mock.Of<IJobCompletionReporter>(),
            AgentId: new AgentId("a"),
            Lifetime: Mock.Of<IHostApplicationLifetime>(),
            Logger: Mock.Of<Serilog.ILogger>());

        deps.ServiceProvider.Should().BeNull();
    }

    // ── LocalPipelineExecutorDependencies ──────────────────────────────

    [Fact]
    public void LocalPipelineExecutorDependencies_RequiredPropertiesAssigned()
    {
        var orchestrator = Mock.Of<IKiroCliOrchestrator>();
        var httpClientFactory = Mock.Of<System.Net.Http.IHttpClientFactory>();
        var config = new PipelineConfiguration();
        var qualityGateValidator = Mock.Of<IQualityGateValidator>();
        var logger = Mock.Of<Serilog.ILogger>();

        var deps = new LocalPipelineExecutorDependencies(
            Orchestrator: orchestrator,
            HttpClientFactory: httpClientFactory,
            DefaultPipelineConfig: config,
            QualityGateValidator: qualityGateValidator,
            Logger: logger);

        deps.Orchestrator.Should().BeSameAs(orchestrator);
        deps.HttpClientFactory.Should().BeSameAs(httpClientFactory);
        deps.DefaultPipelineConfig.Should().BeSameAs(config);
        deps.QualityGateValidator.Should().BeSameAs(qualityGateValidator);
        deps.Logger.Should().BeSameAs(logger);
    }

    [Fact]
    public void LocalPipelineExecutorDependencies_OptionalMembers_DefaultToNull()
    {
        var deps = new LocalPipelineExecutorDependencies(
            Orchestrator: Mock.Of<IKiroCliOrchestrator>(),
            HttpClientFactory: Mock.Of<System.Net.Http.IHttpClientFactory>(),
            DefaultPipelineConfig: new PipelineConfiguration(),
            QualityGateValidator: Mock.Of<IQualityGateValidator>(),
            Logger: Mock.Of<Serilog.ILogger>());

        deps.BrainUpdateService.Should().BeNull();
        deps.HistoryService.Should().BeNull();
        deps.OpenIssueContextWriter.Should().BeNull();
        deps.AgentIdentity.Should().BeNull();
        deps.ReporterFactory.Should().BeNull();
    }

    [Fact]
    public void LocalPipelineExecutorDependencies_OptionalMembers_CanBeOverridden()
    {
        var brainUpdateService = Mock.Of<IBrainUpdateService>();
        var historyService = Mock.Of<IPipelineRunHistoryService>();
        var agentId = new AgentId("override-agent");
        var reporterFactory = Mock.Of<IPipelineReporterFactory>();

        var deps = new LocalPipelineExecutorDependencies(
            Orchestrator: Mock.Of<IKiroCliOrchestrator>(),
            HttpClientFactory: Mock.Of<System.Net.Http.IHttpClientFactory>(),
            DefaultPipelineConfig: new PipelineConfiguration(),
            QualityGateValidator: Mock.Of<IQualityGateValidator>(),
            Logger: Mock.Of<Serilog.ILogger>(),
            BrainUpdateService: brainUpdateService,
            HistoryService: historyService,
            AgentIdentity: agentId,
            ReporterFactory: reporterFactory);

        deps.BrainUpdateService.Should().BeSameAs(brainUpdateService);
        deps.HistoryService.Should().BeSameAs(historyService);
        deps.AgentIdentity.Should().Be(agentId);
        deps.ReporterFactory.Should().BeSameAs(reporterFactory);
    }

    // ── AgentJobExecutionRequest ───────────────────────────────────────

    [Fact]
    public void AgentJobExecutionRequest_RequiredPropertiesAssigned()
    {
        var assignment = new JobAssignmentMessage
        {
            JobId = "j-1",
            IssueIdentifier = "x",
            IssueDetail = new IssueDetail { Identifier = "x", Title = "", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
            IssueComments = [],
            RepoProviderConfigId = "rp",
            AgentProviderConfigId = "ap",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration(),
            InitiatedBy = "test",
            QualityGateConfigs = []
        };

        AgentJobRunner.PipelineExecuteDelegate executeFn =
            (_, _, _, _, ct) => Task.FromResult(new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow });

        // TODO: This test uses named arguments (Ct:, CancelledLabel:, RethrowOnSigterm:) which are
        // position-independent, so it does not verify the CA1068-fixed parameter ordering on the record.
        // Consider switching to positional arguments for CancelledLabel/Ct/RethrowOnSigterm to ensure
        // any future revert of the parameter order would cause a compile or runtime failure here.
        var req = new AgentJobExecutionRequest(
            Execute: executeFn,
            Assignment: assignment,
            Connection: null!,
            OutputBatcher: new OutputBatcher(),
            OnStepChanged: null,
            Ct: CancellationToken.None,
            RethrowOnSigterm: default,
            CancelledLabel: "agent:cancelled");

        req.Assignment.Should().BeSameAs(assignment);
        req.CancelledLabel.Should().Be("agent:cancelled");
        req.RethrowOnSigterm.Should().Be(default(CancellationToken));
    }

    [Fact]
    public void AgentJobExecutionRequest_DefaultValues_AreCorrect()
    {
        var assignment = new JobAssignmentMessage
        {
            JobId = "j-2",
            IssueIdentifier = "x",
            IssueDetail = new IssueDetail { Identifier = "x", Title = "", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
            IssueComments = [],
            RepoProviderConfigId = "rp",
            AgentProviderConfigId = "ap",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration(),
            InitiatedBy = "test",
            QualityGateConfigs = []
        };

        AgentJobRunner.PipelineExecuteDelegate executeFn =
            (_, _, _, _, ct) => Task.FromResult(new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow });

        var req = new AgentJobExecutionRequest(executeFn, assignment, null!, new OutputBatcher(), null, null, CancellationToken.None);

        req.RethrowOnSigterm.Should().Be(default(CancellationToken));
        req.CancelledLabel.Should().BeNull();
    }
}
