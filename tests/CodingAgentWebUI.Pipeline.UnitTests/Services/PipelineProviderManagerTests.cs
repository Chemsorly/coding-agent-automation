using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class PipelineProviderManagerTests
{
    private readonly Mock<IProviderConfigStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<Serilog.ILogger> _mockLogger;

    public PipelineProviderManagerTests()
    {
        _mockConfigStore = new Mock<IProviderConfigStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockLogger = new Mock<Serilog.ILogger> { DefaultValue = DefaultValue.Mock };
    }

    private PipelineProviderManager CreateSut() =>
        new(_mockConfigStore.Object, _mockFactory.Object, _mockLogger.Object);

    private static ProviderConfig MakeConfig(ProviderKind kind, string id = "test-id") =>
        new() { DisplayName = $"Test-{kind}", Kind = kind, ProviderType = "GitHub", Id = id };

    // ─── Constructor Guards ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullConfigStore_ThrowsArgumentNullException()
    {
        var act = () => new PipelineProviderManager(null!, _mockFactory.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("configStore");
    }

    [Fact]
    public void Constructor_NullProviderFactory_ThrowsArgumentNullException()
    {
        var act = () => new PipelineProviderManager(_mockConfigStore.Object, null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("providerFactory");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new PipelineProviderManager(_mockConfigStore.Object, _mockFactory.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ─── ResolveProviderConfigAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ResolveProviderConfigAsync_ConfigExists_ReturnsConfig()
    {
        var expected = MakeConfig(ProviderKind.Repository, "repo-1");
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = CreateSut();
        var result = await sut.ResolveProviderConfigAsync("repo-1", ProviderKind.Repository, CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task ResolveProviderConfigAsync_ConfigNotFound_ThrowsInvalidOperationException()
    {
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("missing", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var sut = CreateSut();

        var act = () => sut.ResolveProviderConfigAsync("missing", ProviderKind.Issue, CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not found*");
    }

    // ─── CreateCoreProvidersAsync — Happy Path ───────────────────────────────────

    [Fact]
    public async Task CreateCoreProvidersAsync_SetsAllActiveProviders_AndNullsBrainAndPipeline()
    {
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockIssue = new Mock<IIssueProvider>();
        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();

        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        sut.ActiveIssueProvider.Should().Be(mockIssue.Object);
        sut.ActiveRepoProvider.Should().Be(mockRepo.Object);
        sut.ActiveAgentProvider.Should().Be(mockAgent.Object);
        sut.ActiveBrainProvider.Should().BeNull();
        sut.ActivePipelineProvider.Should().BeNull();
    }

    // ─── CreateCoreProvidersAsync — Partial Failure & Disposal ────────────────────

    [Fact]
    public async Task CreateCoreProvidersAsync_DisposesPreviousProviders_BeforeCreatingNew()
    {
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        // First set of providers
        var mockIssue1 = new Mock<IIssueProvider>();
        var mockRepo1 = new Mock<IRepositoryProvider>();
        var mockAgent1 = new Mock<IAgentProvider>();
        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue1.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo1.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent1.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        // Second set of providers
        var mockIssue2 = new Mock<IIssueProvider>();
        var mockRepo2 = new Mock<IRepositoryProvider>();
        var mockAgent2 = new Mock<IAgentProvider>();
        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue2.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo2.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent2.Object);

        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        // First set should have been disposed
        mockIssue1.Verify(p => p.DisposeAsync(), Times.Once);
        mockRepo1.Verify(p => p.DisposeAsync(), Times.Once);
        mockAgent1.Verify(p => p.DisposeAsync(), Times.Once);

        // Second set should now be active
        sut.ActiveIssueProvider.Should().Be(mockIssue2.Object);
        sut.ActiveRepoProvider.Should().Be(mockRepo2.Object);
        sut.ActiveAgentProvider.Should().Be(mockAgent2.Object);
    }

    // TODO(#959): This test documents that partial disposal does NOT happen on mid-creation failure.
    // When #959 is fixed to dispose already-created providers on partial failure, flip the assertion
    // from Times.Never to Times.Once and rename the test to reflect the corrected behavior.
    [Fact]
    public async Task CreateCoreProvidersAsync_FactoryThrowsOnRepoCreation_IssueProviderNotDisposed()
    {
        // Documents the known gap: if CreateRepositoryProvider throws after CreateIssueProvider
        // succeeds, the issue provider is NOT disposed within this call.
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockIssue = new Mock<IIssueProvider>();
        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Throws(new InvalidOperationException("creation failed"));

        var sut = CreateSut();

        var act = () => sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Issue provider was assigned but NOT disposed — known gap
        sut.ActiveIssueProvider.Should().Be(mockIssue.Object);
        mockIssue.Verify(p => p.DisposeAsync(), Times.Never);
    }

    // ─── CreateBrainProviderAsync ────────────────────────────────────────────────

    [Fact]
    public async Task CreateBrainProviderAsync_Success_SetsActiveBrainProvider()
    {
        var brainConfig = MakeConfig(ProviderKind.Repository, "brain-1");
        var mockBrain = new Mock<IRepositoryProvider>();
        mockBrain.Setup(b => b.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("brain-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(brainConfig)).Returns(mockBrain.Object);

        var sut = CreateSut();
        await sut.CreateBrainProviderAsync("brain-1", CancellationToken.None);

        sut.ActiveBrainProvider.Should().Be(mockBrain.Object);
    }

    [Fact]
    public async Task CreateBrainProviderAsync_ValidationFails_DisposesBrainAndSetsNull()
    {
        var brainConfig = MakeConfig(ProviderKind.Repository, "brain-1");
        var mockBrain = new Mock<IRepositoryProvider>();
        mockBrain.Setup(b => b.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("validation error"));

        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("brain-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(brainConfig)).Returns(mockBrain.Object);

        var sut = CreateSut();
        await sut.CreateBrainProviderAsync("brain-1", CancellationToken.None);

        sut.ActiveBrainProvider.Should().BeNull();
        mockBrain.Verify(b => b.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateBrainProviderAsync_ResolutionFails_SetsNull_NoThrow()
    {
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("bad-id", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var sut = CreateSut();

        // Should not throw — silently sets null
        await sut.CreateBrainProviderAsync("bad-id", CancellationToken.None);

        sut.ActiveBrainProvider.Should().BeNull();
    }

    // ─── CreatePipelineProviderAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CreatePipelineProviderAsync_WithExplicitId_ResolvesAndCreates()
    {
        var pipelineConfig = MakeConfig(ProviderKind.Pipeline, "ci-1");
        var mockPipeline = new Mock<IPipelineProvider>();

        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ci-1", ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);
        _mockFactory
            .Setup(f => f.CreatePipelineProviderAsync(pipelineConfig, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockPipeline.Object);

        var sut = CreateSut();
        var result = await sut.CreatePipelineProviderAsync("ci-1", CancellationToken.None);

        result.Should().Be("ci-1");
        sut.ActivePipelineProvider.Should().Be(mockPipeline.Object);
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_NullId_FallsBackToFirstAvailable()
    {
        var pipelineConfig = MakeConfig(ProviderKind.Pipeline, "fallback-ci");
        var mockPipeline = new Mock<IPipelineProvider>();

        _mockConfigStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { pipelineConfig });
        _mockFactory
            .Setup(f => f.CreatePipelineProviderAsync(pipelineConfig, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockPipeline.Object);

        var sut = CreateSut();
        var result = await sut.CreatePipelineProviderAsync(null, CancellationToken.None);

        result.Should().Be("fallback-ci");
        sut.ActivePipelineProvider.Should().Be(mockPipeline.Object);
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_NoConfigsAvailable_ReturnsNull()
    {
        _mockConfigStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var sut = CreateSut();
        var result = await sut.CreatePipelineProviderAsync(null, CancellationToken.None);

        result.Should().BeNull();
        sut.ActivePipelineProvider.Should().BeNull();
    }

    // ─── ValidateProvidersAsync ──────────────────────────────────────────────────

    // TODO: Add a test for ValidateProvidersAsync called before CreateCoreProvidersAsync to document
    // the failure mode (NullReferenceException from ActiveRepoProvider! null-forgiving dereference).
    // This is an important edge case for callers.

    [Fact]
    public async Task ValidateProvidersAsync_AllPass_NoException()
    {
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();
        mockRepo.Setup(r => r.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockAgent.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(Mock.Of<IIssueProvider>());
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(MakeConfig(ProviderKind.Issue), repoConfig, agentConfig, CancellationToken.None);

        // Should not throw
        await sut.ValidateProvidersAsync(repoConfig, agentConfig, CancellationToken.None);
    }

    [Fact]
    public async Task ValidateProvidersAsync_RepoValidationFails_ThrowsInvalidOperationException()
    {
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();
        mockRepo.Setup(r => r.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("connection refused"));

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(Mock.Of<IIssueProvider>());
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(MakeConfig(ProviderKind.Issue), repoConfig, agentConfig, CancellationToken.None);

        var act = () => sut.ValidateProvidersAsync(repoConfig, agentConfig, CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*validation failed*");
    }

    [Fact]
    public async Task ValidateProvidersAsync_PipelineProviderNull_SkipsValidation()
    {
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();
        mockRepo.Setup(r => r.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockAgent.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(Mock.Of<IIssueProvider>());
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(MakeConfig(ProviderKind.Issue), repoConfig, agentConfig, CancellationToken.None);

        // Pipeline provider is null after CreateCoreProvidersAsync
        sut.ActivePipelineProvider.Should().BeNull();

        // Should complete without error — no attempt to validate null pipeline provider
        await sut.ValidateProvidersAsync(repoConfig, agentConfig, CancellationToken.None);
    }

    // ─── DisposePreviousProvidersAsync — Exception Resilience ─────────────────────

    [Fact]
    public async Task DisposePreviousProvidersAsync_AllProviders_DisposesEach()
    {
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockIssue = new Mock<IIssueProvider>();
        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();

        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        await sut.DisposePreviousProvidersAsync();

        mockIssue.Verify(p => p.DisposeAsync(), Times.Once);
        mockRepo.Verify(p => p.DisposeAsync(), Times.Once);
        mockAgent.Verify(p => p.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposePreviousProvidersAsync_OneThrows_OthersStillDisposed()
    {
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockIssue = new Mock<IIssueProvider>();
        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();

        // Agent provider throws during disposal — should not block others
        mockAgent.Setup(a => a.DisposeAsync()).Throws(new InvalidOperationException("disposal failed"));

        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        // Should NOT throw even though agent disposal failed
        await sut.DisposePreviousProvidersAsync();

        // Agent disposal was attempted (it threw)
        mockAgent.Verify(a => a.DisposeAsync(), Times.Once);
        // Issue and repo should still be disposed despite agent's failure
        mockIssue.Verify(p => p.DisposeAsync(), Times.Once);
        mockRepo.Verify(p => p.DisposeAsync(), Times.Once);
    }

    // ─── DisposeAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DisposesAllActiveProviders()
    {
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockIssue = new Mock<IIssueProvider>();
        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();

        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        await sut.DisposeAsync();

        mockIssue.Verify(p => p.DisposeAsync(), Times.Once);
        mockRepo.Verify(p => p.DisposeAsync(), Times.Once);
        mockAgent.Verify(p => p.DisposeAsync(), Times.Once);
    }

    // TODO: This test relies on Moq's inherent tolerance for repeated calls. Consider adding a
    // variant where mock providers throw ObjectDisposedException on second DisposeAsync() call
    // to verify that the SUT's DisposeProviderAsync catch block handles real double-dispose safely.
    [Fact]
    public async Task DisposeAsync_CalledTwice_NoObjectDisposedException()
    {
        // DisposePreviousProvidersAsync does NOT null property references after disposal.
        // Second call re-disposes the same mock instances — Moq mocks tolerate this by default.
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockIssue = new Mock<IIssueProvider>();
        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();

        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        await sut.DisposeAsync();

        // Second dispose should not throw
        await sut.DisposeAsync();

        // Each provider disposed twice (once per DisposeAsync call) — no exception
        mockIssue.Verify(p => p.DisposeAsync(), Times.Exactly(2));
        mockRepo.Verify(p => p.DisposeAsync(), Times.Exactly(2));
        mockAgent.Verify(p => p.DisposeAsync(), Times.Exactly(2));
    }

    // ─── Reset ───────────────────────────────────────────────────────────────────

    // TODO: The issue requirement says "Reset() disposes current providers before allowing new creation"
    // but the implementation only nulls references without disposing. Either the requirement is wrong
    // (callers should call DisposePreviousProvidersAsync before Reset) or the implementation has a
    // resource leak. Clarify intent and update test accordingly.
    [Fact]
    public async Task Reset_NullsAllProviders_WithoutDisposing()
    {
        var issueConfig = MakeConfig(ProviderKind.Issue);
        var repoConfig = MakeConfig(ProviderKind.Repository);
        var agentConfig = MakeConfig(ProviderKind.Agent);

        var mockIssue = new Mock<IIssueProvider>();
        var mockRepo = new Mock<IRepositoryProvider>();
        var mockAgent = new Mock<IAgentProvider>();

        _mockFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssue.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepo.Object);
        _mockFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(mockAgent.Object);

        var sut = CreateSut();
        await sut.CreateCoreProvidersAsync(issueConfig, repoConfig, agentConfig, CancellationToken.None);

        sut.Reset();

        // All references nulled
        sut.ActiveIssueProvider.Should().BeNull();
        sut.ActiveRepoProvider.Should().BeNull();
        sut.ActiveAgentProvider.Should().BeNull();
        sut.ActiveBrainProvider.Should().BeNull();
        sut.ActivePipelineProvider.Should().BeNull();

        // Dispose was NOT called — Reset only nulls, does not dispose
        mockIssue.Verify(p => p.DisposeAsync(), Times.Never);
        mockRepo.Verify(p => p.DisposeAsync(), Times.Never);
        mockAgent.Verify(p => p.DisposeAsync(), Times.Never);
    }
}
