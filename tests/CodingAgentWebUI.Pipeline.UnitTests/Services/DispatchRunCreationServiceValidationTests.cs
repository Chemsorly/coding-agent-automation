using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="DispatchRunCreationService"/> rejects invalid
/// <see cref="ProviderConfigId"/> parameters (default struct, null Value, empty Value)
/// with descriptive <see cref="ArgumentException"/> messages.
/// </summary>
public class DispatchRunCreationServiceValidationTests : IAsyncDisposable
{
    private readonly DispatchRunCreationService _service;

    public DispatchRunCreationServiceValidationTests()
    {
        var mockConfigStore = new Mock<IProviderConfigStore>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();

        var lifecycle = new PipelineRunLifecycleService(
            mockHistoryService.Object, null, mockLogger.Object);

        _service = new DispatchRunCreationService(
            lifecycle,
            mockConfigStore.Object,
            mockFactory.Object,
            mockLogger.Object);
    }

    // ── CreateDispatchedRunAsync validation ──

    // TODO: The ParamName assertions below (and in ReserveRunIdAsync tests) check for "request.IssueProviderId.Value"
    // (and analogous RepoProviderId/AgentProviderId variants) — the [CallerArgumentExpression]-derived text of the
    // first argument to ThrowIfNullOrEmpty. The original explicit nameof(...) used "IssueProviderId" (the containing
    // property), which was more descriptive and hid the ProviderConfigId.Value implementation detail from callers.
    // If the production code is ever refactored to use a helper that captures only "request.IssueProviderId", these
    // tests will fail even though the behavior improved. Consider whether locking in the ".Value" expression text
    // is the right contract to enforce here. (Review finding: TestQualityReviewer [WARNING])
    [Fact]
    public async Task CreateDispatchedRunAsync_DefaultIssueProviderId_ThrowsArgumentException()
    {
        var defaultId = default(ProviderConfigId);

        var act = () => _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = defaultId, RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.IssueProviderId.Value");
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_DefaultRepoProviderId_ThrowsArgumentException()
    {
        var defaultId = default(ProviderConfigId);

        var act = () => _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = defaultId, IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.RepoProviderId.Value");
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_DefaultAgentProviderId_ThrowsArgumentException()
    {
        var defaultId = default(ProviderConfigId);

        var act = () => _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = defaultId, AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.AgentProviderId.Value");
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_EmptyIssueProviderId_ThrowsArgumentException()
    {
        var emptyId = new ProviderConfigId(string.Empty);

        var act = () => _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = emptyId, RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.IssueProviderId.Value");
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_EmptyRepoProviderId_ThrowsArgumentException()
    {
        var emptyId = new ProviderConfigId(string.Empty);

        var act = () => _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = emptyId, IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.RepoProviderId.Value");
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_EmptyAgentProviderId_ThrowsArgumentException()
    {
        var emptyId = new ProviderConfigId(string.Empty);

        var act = () => _service.CreateDispatchedRunAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = emptyId, AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.AgentProviderId.Value");
    }

    // ── ReserveRunIdAsync validation ──

    [Fact]
    public async Task ReserveRunIdAsync_DefaultIssueProviderId_ThrowsArgumentException()
    {
        var defaultId = default(ProviderConfigId);

        var act = () => _service.ReserveRunIdAsync(
            new DispatchRunRequest { IssueProviderId = defaultId, RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.IssueProviderId.Value");
    }

    [Fact]
    public async Task ReserveRunIdAsync_DefaultRepoProviderId_ThrowsArgumentException()
    {
        var defaultId = default(ProviderConfigId);

        var act = () => _service.ReserveRunIdAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = defaultId, IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.RepoProviderId.Value");
    }

    [Fact]
    public async Task ReserveRunIdAsync_DefaultAgentProviderId_ThrowsArgumentException()
    {
        var defaultId = default(ProviderConfigId);

        var act = () => _service.ReserveRunIdAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = defaultId, AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.AgentProviderId.Value");
    }

    [Fact]
    public async Task ReserveRunIdAsync_EmptyIssueProviderId_ThrowsArgumentException()
    {
        var emptyId = new ProviderConfigId(string.Empty);

        var act = () => _service.ReserveRunIdAsync(
            new DispatchRunRequest { IssueProviderId = emptyId, RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.IssueProviderId.Value");
    }

    [Fact]
    public async Task ReserveRunIdAsync_EmptyRepoProviderId_ThrowsArgumentException()
    {
        var emptyId = new ProviderConfigId(string.Empty);

        var act = () => _service.ReserveRunIdAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = emptyId, IssueIdentifier = "42", AgentProviderId = "agent-1", AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.RepoProviderId.Value");
    }

    [Fact]
    public async Task ReserveRunIdAsync_EmptyAgentProviderId_ThrowsArgumentException()
    {
        var emptyId = new ProviderConfigId(string.Empty);

        var act = () => _service.ReserveRunIdAsync(
            new DispatchRunRequest { IssueProviderId = "issue-1", RepoProviderId = "repo-1", IssueIdentifier = "42", AgentProviderId = emptyId, AgentId = "agent-x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.ParamName == "request.AgentProviderId.Value");
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _service.DisposeAsync();
    }
}
