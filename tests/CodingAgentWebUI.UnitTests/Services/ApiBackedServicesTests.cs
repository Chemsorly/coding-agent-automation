using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ApiBackedPendingWorkQuery"/> and <see cref="ApiChatJobDispatcher"/>.
/// Uses Moq to avoid needing WireMock in this project.
/// </summary>
public sealed class ApiBackedServicesTests
{
    private static readonly string[] KiroDotnetLabels = ["kiro", "dotnet"];
    private static readonly string[] KiroDotnetLinuxLabels = ["kiro", "dotnet", "linux"];
    // ── ApiBackedPendingWorkQuery ─────────────────────────────────────────

    [Fact]
    public async Task GetPendingJobsAsync_WithItems_MapsFieldsCorrectly()
    {
        var client = new Mock<IPipelineApiWorkItemClient>();
        var itemId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new()
                {
                    Id = itemId,
                    IssueIdentifier = "org/repo#42",
                    IssueProviderConfigId = "provider-1",
                    TaskType = WorkItemTaskType.Implementation,
                    CreatedAt = createdAt,
                    AgentSelector = "kiro,dotnet",
                    RetryCount = 0,
                    TimeoutSeconds = 0
                }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        result.Should().HaveCount(1);
        var job = result[0];
        job.WorkItemId.Should().Be(itemId.ToString());
        job.IssueIdentifier.Should().Be((IssueIdentifier)"org/repo#42");
        job.IssueProviderId.Should().Be(new ProviderConfigId("provider-1"));
        job.EnqueuedAt.Should().Be(createdAt);
        job.RequiredLabels.Should().BeEquivalentTo(KiroDotnetLabels);
        job.TaskType.Should().Be(WorkItemTaskType.Implementation);
        job.RunType.Should().Be(PipelineRunType.Implementation);
    }

    [Fact]
    public async Task GetPendingJobsAsync_ConsolidationItem_SetsConsolidationRunType()
    {
        var client = new Mock<IPipelineApiWorkItemClient>();

        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "org/repo#99",
                    IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Consolidation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro",
                    RetryCount = 0,
                    TimeoutSeconds = 0
                }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        result.Should().HaveCount(1);
        result[0].RunType.Should().Be(PipelineRunType.Consolidation,
            "consolidation task type maps to Consolidation run type");
        result[0].TaskType.Should().Be(WorkItemTaskType.Consolidation);
    }

    [Fact]
    public async Task GetPendingJobsAsync_EmptyList_ReturnEmptyAndSetsPendingCountZero()
    {
        var client = new Mock<IPipelineApiWorkItemClient>();
        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>());

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        result.Should().BeEmpty();
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task GetPendingJobsAsync_UpdatesPendingCount()
    {
        var client = new Mock<IPipelineApiWorkItemClient>();
        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new() { Id = Guid.NewGuid(), IssueIdentifier = "a#1", IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro", RetryCount = 0, TimeoutSeconds = 0 },
                new() { Id = Guid.NewGuid(), IssueIdentifier = "a#2", IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro", RetryCount = 0, TimeoutSeconds = 0 },
                new() { Id = Guid.NewGuid(), IssueIdentifier = "a#3", IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro", RetryCount = 0, TimeoutSeconds = 0 }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        await sut.GetPendingJobsAsync();

        sut.PendingCount.Should().Be(3);
    }

    [Fact]
    public async Task GetPendingJobsAsync_MultiLabelSelector_SplitsOnComma()
    {
        var client = new Mock<IPipelineApiWorkItemClient>();
        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new() { Id = Guid.NewGuid(), IssueIdentifier = "a#1", IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro , dotnet , linux", RetryCount = 0, TimeoutSeconds = 0 }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        result[0].RequiredLabels.Should().BeEquivalentTo(KiroDotnetLinuxLabels,
            "spaces around commas must be trimmed");
    }

    [Fact]
    public void PendingCount_InitialValue_IsZero()
    {
        var client = new Mock<IPipelineApiWorkItemClient>();
        var sut = new ApiBackedPendingWorkQuery(client.Object);

        sut.PendingCount.Should().Be(0, "no calls made yet — count starts at 0");
    }

    [Fact]
    public async Task GetPendingJobsAsync_WithInitiatedByAndIssueTitle_MapsFields()
    {
        // Arrange: DTO with all display fields populated
        var client = new Mock<IPipelineApiWorkItemClient>();
        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "org/repo#10",
                    IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro",
                    RetryCount = 0,
                    TimeoutSeconds = 0,
                    InitiatedBy = "manual",
                    IssueTitle = "Fix the bug",
                    ProjectName = "MyProject",
                    ProjectId = new Guid("42420000-0000-0000-0000-000000000001")
                }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        result.Should().HaveCount(1);
        var job = result[0];
        job.InitiatedBy.Should().Be("manual");
        job.IssueTitle.Should().Be("Fix the bug");
        job.Project.Should().NotBeNull();
        job.Project!.Name.Should().Be("MyProject");
        job.Project.Id.Should().Be(new Guid("42420000-0000-0000-0000-000000000001").ToString());
    }

    [Fact]
    public async Task GetPendingJobsAsync_WithNullInitiatedBy_FallsBackToEmptyString()
    {
        // Arrange: null InitiatedBy simulates a payload where the key is absent.
        // System.Text.Json does not enforce `required` at runtime — missing keys produce null.
        var client = new Mock<IPipelineApiWorkItemClient>();
        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "a#1",
                    IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro",
                    RetryCount = 0,
                    TimeoutSeconds = 0,
                    InitiatedBy = null  // legacy/edge-case item — payload absent or key missing
                }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        // PendingJob.InitiatedBy is required string — must not be null
        result[0].InitiatedBy.Should().Be("", "null InitiatedBy must fall back to empty string");
    }

    [Fact]
    public async Task GetPendingJobsAsync_WithProjectIdButNoProjectName_UsesProjectIdAsName()
    {
        // Arrange: ProjectName is null but ProjectId is set — Name falls back to ProjectId
        var client = new Mock<IPipelineApiWorkItemClient>();
        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "a#1",
                    IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro",
                    RetryCount = 0,
                    TimeoutSeconds = 0,
                    ProjectName = null,
                    ProjectId = new Guid("abc12300-0000-0000-0000-000000000001")
                }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        var job = result[0];
        job.Project.Should().NotBeNull("ProjectId is set so Project must be non-null");
        job.Project!.Id.Should().Be(new Guid("abc12300-0000-0000-0000-000000000001").ToString());
        job.Project.Name.Should().Be(new Guid("abc12300-0000-0000-0000-000000000001").ToString(), "ProjectId is used as fallback Name when ProjectName is null");
    }

    [Fact]
    public async Task GetPendingJobsAsync_WithNoProjectFields_LeavesProjectNull()
    {
        // Arrange: both ProjectName and ProjectId are null — Project must remain null
        var client = new Mock<IPipelineApiWorkItemClient>();
        client.Setup(c => c.GetPendingAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "a#1",
                    IssueProviderConfigId = "p1",
                    TaskType = WorkItemTaskType.Implementation,
                    CreatedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "kiro",
                    RetryCount = 0,
                    TimeoutSeconds = 0,
                    ProjectName = null,
                    ProjectId = null
                }
            });

        var sut = new ApiBackedPendingWorkQuery(client.Object);
        var result = await sut.GetPendingJobsAsync();

        result[0].Project.Should().BeNull("when both ProjectName and ProjectId are null the Razor renders '—'");
    }

    // ── ApiChatJobDispatcher ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_Success_ReturnsAgentId()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync("kiro,dotnet", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("agent-123");

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var result = await sut.DispatchChatPodAsync("kiro,dotnet", null, null, CancellationToken.None);

        result.Should().Be("agent-123");
    }

    [Fact]
    public async Task DispatchChatPodAsync_Conflict409_ThrowsChatAlreadyActiveException()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Conflict", null, System.Net.HttpStatusCode.Conflict));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatAlreadyActiveException>(
            "409 Conflict must be mapped to ChatAlreadyActiveException");
    }

    [Fact]
    public async Task DispatchChatPodAsync_ServiceUnavailable503_ThrowsNoPvcAvailableException()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<NoPvcAvailableException>(
            "503 ServiceUnavailable must be mapped to NoPvcAvailableException");
    }

    [Fact]
    public async Task DispatchChatPodAsync_GatewayTimeout504_ThrowsChatPodTimeoutException()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway Timeout", null, System.Net.HttpStatusCode.GatewayTimeout));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatPodTimeoutException>(
            "504 GatewayTimeout must be mapped to ChatPodTimeoutException");
    }

    [Fact]
    public async Task DispatchChatPodAsync_ChatPodTimeout_HasUnknownTimeoutSeconds()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Gateway Timeout", null, System.Net.HttpStatusCode.GatewayTimeout));

        var sut = new ApiChatJobDispatcher(chatClient.Object);

        var ex = await Assert.ThrowsAsync<ChatPodTimeoutException>(
            () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None));

        ex.TimeoutSeconds.Should().Be(-1, "API timeout is unknown — sentinel value -1 is used");
    }

    [Fact]
    public async Task DispatchChatPodAsync_OtherHttpError_Propagates()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Internal Server Error", null, System.Net.HttpStatusCode.InternalServerError));

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var act = () => sut.DispatchChatPodAsync("kiro", null, null, CancellationToken.None);

        // 500 is not remapped — should propagate as HttpRequestException
        await act.Should().ThrowAsync<HttpRequestException>(
            "non-remapped status codes must propagate as-is");
    }

    [Fact]
    public async Task TerminateChatSessionAsync_DelegatesToClient()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.TerminateChatSessionAsync("agent-abc", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        await sut.TerminateChatSessionAsync(new AgentId { Value = "agent-abc" }, CancellationToken.None);

        chatClient.Verify(c => c.TerminateChatSessionAsync("agent-abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchChatPodAsync_WithModelAndEffort_PassesThrough()
    {
        var chatClient = new Mock<IPipelineApiChatClient>();
        chatClient
            .Setup(c => c.DispatchChatPodAsync("kiro,dotnet", "claude-4", "high", It.IsAny<CancellationToken>()))
            .ReturnsAsync("agent-xyz");

        var sut = new ApiChatJobDispatcher(chatClient.Object);
        var result = await sut.DispatchChatPodAsync("kiro,dotnet", "claude-4", "high", CancellationToken.None);

        result.Should().Be("agent-xyz");
        chatClient.Verify(c => c.DispatchChatPodAsync("kiro,dotnet", "claude-4", "high", It.IsAny<CancellationToken>()), Times.Once);
    }
}
