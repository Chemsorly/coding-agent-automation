using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Contract tests for <see cref="IConfigurationStore"/> implementations.
/// Postgres-backed store must satisfy these behavioral contracts.
/// 
/// Derived classes provide a concrete store instance via <see cref="CreateStore"/>.
/// </summary>
public abstract class ConfigurationStoreContractTests : IDisposable
{
    /// <summary>Create a fresh store instance for isolation between tests.</summary>
    protected abstract IConfigurationStore CreateStore();

    /// <summary>Cleanup resources after each test.</summary>
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    // ── Pipeline Configuration ──────────────────────────────────────────

    [Fact]
    public async Task PipelineConfig_EmptyStore_ReturnsDefaults()
    {
        var store = CreateStore();

        var config = await store.LoadPipelineConfigAsync(CancellationToken.None);

        config.Should().NotBeNull();
        config.MaxRetries.Should().Be(3);
        config.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task PipelineConfig_SaveThenLoad_RoundTrips()
    {
        var store = CreateStore();
        var original = new PipelineConfiguration
        {
            MaxRetries = 7,
            AgentTimeout = TimeSpan.FromMinutes(60),
            WorkspaceBaseDirectory = "/contract-test/workspaces",
            BlacklistedPaths = new[] { ".agent", ".custom" }
        };

        await store.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        loaded.MaxRetries.Should().Be(7);
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(60));
        loaded.WorkspaceBaseDirectory.Should().Be("/contract-test/workspaces");
        loaded.BlacklistedPaths.Should().BeEquivalentTo(new[] { ".agent", ".custom" });
    }

    [Fact]
    public async Task PipelineConfig_SaveOverwrites_PreviousValue()
    {
        var store = CreateStore();

        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 5, WorkspaceBaseDirectory = "/first" },
            CancellationToken.None);

        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 10, WorkspaceBaseDirectory = "/second" },
            CancellationToken.None);

        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(10);
        loaded.WorkspaceBaseDirectory.Should().Be("/second");
    }

    [Fact]
    public async Task PipelineConfig_Update_AppliesTransform()
    {
        var store = CreateStore();
        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 3, WorkspaceBaseDirectory = "/test" },
            CancellationToken.None);

        await store.UpdatePipelineConfigAsync(
            c => c with { MaxRetries = c.MaxRetries + 2 },
            CancellationToken.None);

        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(5);
    }

    // ── Provider Configurations ─────────────────────────────────────────

    [Fact]
    public async Task ProviderConfig_SaveThenLoadByKind_Returns()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Contract Test Repo",
            Settings = new Dictionary<string, string> { ["owner"] = "test-org" }
        };

        await store.SaveProviderConfigAsync(config, CancellationToken.None);
        var loaded = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        loaded.Should().Contain(c => c.Id == id);
        var match = loaded.First(c => c.Id == id);
        match.ProviderType.Should().Be("GitHub");
        match.DisplayName.Should().Be("Contract Test Repo");
        match.Settings["owner"].Should().Be("test-org");
    }

    [Fact]
    public async Task ProviderConfig_GetById_ReturnsCorrect()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string> { ["model"] = "claude-sonnet" }
        };

        await store.SaveProviderConfigAsync(config, CancellationToken.None);
        var loaded = await store.GetProviderConfigByIdAsync(id, ProviderKind.Agent, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(id);
        loaded.ProviderType.Should().Be("KiroCli");
    }

    [Fact]
    public async Task ProviderConfig_GetById_NonExistent_ReturnsNull()
    {
        var store = CreateStore();

        var loaded = await store.GetProviderConfigByIdAsync(Guid.NewGuid().ToString(), ProviderKind.Repository, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ProviderConfig_Delete_RemovesFromStore()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "To Delete",
            Settings = new Dictionary<string, string>()
        };

        await store.SaveProviderConfigAsync(config, CancellationToken.None);
        await store.DeleteProviderConfigAsync(id, ProviderKind.Repository, CancellationToken.None);

        var loaded = await store.GetProviderConfigByIdAsync(id, ProviderKind.Repository, CancellationToken.None);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ProviderConfig_LoadByKind_OnlyReturnsThatKind()
    {
        var store = CreateStore();
        var repoId = Guid.NewGuid().ToString();
        var agentId = Guid.NewGuid().ToString();

        await store.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = repoId, Kind = ProviderKind.Repository,
            ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string>()
        }, CancellationToken.None);

        await store.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = agentId, Kind = ProviderKind.Agent,
            ProviderType = "KiroCli", DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        }, CancellationToken.None);

        var repos = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        var agents = await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);

        repos.Should().Contain(c => c.Id == repoId);
        repos.Should().NotContain(c => c.Id == agentId);
        agents.Should().Contain(c => c.Id == agentId);
        agents.Should().NotContain(c => c.Id == repoId);
    }

    [Fact]
    public async Task ProviderConfig_Save_UpdatesExisting()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();
        var original = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Original Name",
            Settings = new Dictionary<string, string> { ["owner"] = "org1" }
        };

        await store.SaveProviderConfigAsync(original, CancellationToken.None);

        var updated = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Updated Name",
            Settings = new Dictionary<string, string> { ["owner"] = "org2" }
        };
        await store.SaveProviderConfigAsync(updated, CancellationToken.None);

        var loaded = await store.GetProviderConfigByIdAsync(id, ProviderKind.Repository, CancellationToken.None);
        loaded!.DisplayName.Should().Be("Updated Name");
        loaded.Settings["owner"].Should().Be("org2");
    }
}

// ── Postgres-backed implementation (InMemory EF) ────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="PostgresConfigurationStore"/> using InMemory EF Core.
/// </summary>
public class PostgresConfigurationStoreContractTests : ConfigurationStoreContractTests
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresConfigurationStoreContractTests()
    {
        var dbName = $"ContractTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new ContractTestPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    protected override IConfigurationStore CreateStore()
    {
        var factory = new ContractTestDbContextFactory(_dbOptions);
        return new PostgresConfigurationStore(factory, cacheTtl: TimeSpan.FromMilliseconds(1));
    }

    public override void Dispose()
    {
        using var db = new ContractTestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        base.Dispose();
    }
}

/// <summary>Helper: InMemory EF context for test isolation.</summary>
file class ContractTestPipelineDbContext : PipelineDbContext
{
    public ContractTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
}

/// <summary>Helper: IDbContextFactory for InMemory provider.</summary>
file class ContractTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public ContractTestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new ContractTestPipelineDbContext(_options);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

// ── API-backed implementation ────────────────────────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="CodingAgentWebUI.Services.ApiConfigurationStore"/>.
///
/// This is the implementation used by the monolith frontend — a hot-path singleton that sits in
/// front of every config read the dispatch loop makes. Each test gets a fresh in-memory mock
/// client so the TTL cache cannot bleed state across tests.
/// </summary>
public sealed class ApiConfigurationStoreContractTests : ConfigurationStoreContractTests
{
    private PipelineConfiguration _pipelineConfig = new();
    private readonly Dictionary<string, ProviderConfig> _providerConfigs = [];
    private Mock<IPipelineApiConfigClient> _client = default!;

    protected override IConfigurationStore CreateStore()
    {
        // Reset per-test state so the contract base's isolation assumption holds
        _pipelineConfig = new PipelineConfiguration();
        _providerConfigs.Clear();
        _client = BuildClient();
        return BuildStore(_client);
    }

    // ── Mock client that simulates real in-memory persistence ────────────────

    private Mock<IPipelineApiConfigClient> BuildClient()
    {
        var mock = new Mock<IPipelineApiConfigClient>();

        // Pipeline config — read/write/update
        mock.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _pipelineConfig);

        mock.Setup(c => c.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineConfiguration, CancellationToken>((cfg, _) => _pipelineConfig = cfg)
            .Returns(Task.CompletedTask);

        // UpdatePipelineConfigAsync: read-modify-write delegated client-side (calls Get then Save)
        mock.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                _pipelineConfig = transform(_pipelineConfig);
                return Task.CompletedTask;
            });

        // Provider configs — store by composite key (id+kind)
        mock.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderKind kind, CancellationToken _) =>
                (IReadOnlyList<ProviderConfig>)_providerConfigs.Values.Where(p => p.Kind == kind).ToList());

        mock.Setup(c => c.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderConfig, CancellationToken>((cfg, _) => _providerConfigs[cfg.Id] = cfg)
            .Returns(Task.CompletedTask);

        mock.Setup(c => c.DeleteProviderConfigAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Callback<string, ProviderKind, CancellationToken>((id, _, __) => _providerConfigs.Remove(id))
            .Returns(Task.CompletedTask);

        // Stubs for the other sub-interfaces the composite store wraps
        mock.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mock.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mock.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        mock.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        mock.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());

        return mock;
    }

    private static CodingAgentWebUI.Services.ApiConfigurationStore BuildStore(Mock<IPipelineApiConfigClient> client)
    {
        // CacheTtlSeconds = 0 disables the TTL cache so each call goes through to the mock,
        // letting the contract tests observe real-time state changes without delay.
        var pipeline = new CodingAgentWebUI.Services.ApiPipelineConfigStore(client.Object) { CacheTtlSeconds = 0 };
        var providers = new CodingAgentWebUI.Services.ApiProviderConfigStore(client.Object) { CacheTtlSeconds = 0 };
        var projects = new CodingAgentWebUI.Services.ApiProjectStore(client.Object) { CacheTtlSeconds = 0 };
        return new CodingAgentWebUI.Services.ApiConfigurationStore(client.Object, pipeline, providers, projects)
            { CacheTtlSeconds = 0 };
    }
}

// ── InMemoryConfigurationStore (TestUtilities) ───────────────────────────────

/// <summary>
/// Targeted behavioral tests for <see cref="InMemoryConfigurationStore"/>.
///
/// Unlike <c>ConfigurationStoreContractTests</c> (which checks production defaults),
/// this class tests behaviors relevant to how the in-memory store is used in unit tests:
/// save/load round-trips, kind filtering, and delete. The "empty defaults" contract
/// asserts a 2-minute test timeout rather than the 30-minute production default because
/// InMemoryConfigurationStore is a pre-seeded test double, not a clean-slate store.
/// </summary>
public sealed class InMemoryConfigurationStoreBehaviorTests
{
    private static CodingAgentWebUI.TestUtilities.InMemoryConfigurationStore CreateStore()
        => new();

    [Fact]
    public async Task PipelineConfig_EmptyStore_ReturnsSeedDefaults()
    {
        var store = CreateStore();
        var config = await store.LoadPipelineConfigAsync(CancellationToken.None);

        config.Should().NotBeNull();
        config.MaxRetries.Should().Be(3);
        config.AgentTimeout.Should().Be(TimeSpan.FromMinutes(2),
            "InMemoryConfigurationStore is pre-seeded with a 2-minute test timeout (not the 30-min production default)");
    }

    [Fact]
    public async Task PipelineConfig_SaveThenLoad_RoundTrips()
    {
        var store = CreateStore();
        var original = new PipelineConfiguration
        {
            MaxRetries = 7,
            AgentTimeout = TimeSpan.FromMinutes(60),
            WorkspaceBaseDirectory = "/test/workspaces"
        };

        await store.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        loaded.MaxRetries.Should().Be(7);
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(60));
        loaded.WorkspaceBaseDirectory.Should().Be("/test/workspaces");
    }

    [Fact]
    public async Task ProviderConfig_SaveThenLoadByKind_Returns()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();
        var config = new ProviderConfig
        {
            Id = id, Kind = ProviderKind.Repository,
            ProviderType = "GitHub", DisplayName = "Test Repo",
            Settings = new Dictionary<string, string> { ["owner"] = "test-org" }
        };

        await store.SaveProviderConfigAsync(config, CancellationToken.None);
        var loaded = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        loaded.Should().Contain(c => c.Id == id);
        var match = loaded.First(c => c.Id == id);
        match.Settings["owner"].Should().Be("test-org");
    }

    [Fact]
    public async Task ProviderConfig_Delete_RemovesFromStore()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();

        await store.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = id, Kind = ProviderKind.Repository,
            ProviderType = "GitHub", DisplayName = "ToDelete",
            Settings = new Dictionary<string, string>()
        }, CancellationToken.None);

        await store.DeleteProviderConfigAsync(id, ProviderKind.Repository, CancellationToken.None);
        var loaded = await store.GetProviderConfigByIdAsync(id, ProviderKind.Repository, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ProviderConfig_LoadByKind_OnlyReturnsThatKind()
    {
        var store = CreateStore();
        var repoId = Guid.NewGuid().ToString();
        var agentId = Guid.NewGuid().ToString();

        await store.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = repoId, Kind = ProviderKind.Repository,
            ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string>()
        }, CancellationToken.None);

        await store.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = agentId, Kind = ProviderKind.Agent,
            ProviderType = "KiroCli", DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        }, CancellationToken.None);

        var repos = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        var agents = await store.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);

        repos.Should().Contain(c => c.Id == repoId);
        repos.Should().NotContain(c => c.Id == agentId);
        agents.Should().Contain(c => c.Id == agentId);
        agents.Should().NotContain(c => c.Id == repoId);
    }
}

// ── InMemoryConfigurationStore — 3 missing property invariants ───────────────

/// <summary>
/// Adds the three property invariants that <c>PostgresConfigurationStorePropertyTests</c>
/// covers (P3 idempotent-save, P5 GetById, P1-UpdateAsync) but that were absent from
/// <c>InMemoryConfigurationStoreBehaviorTests</c>.
/// Inherits nothing — standalone Facts to avoid the xUnit1024 duplicate-name restriction.
/// </summary>
public sealed class InMemoryConfigurationStorePropertyInvariantTests
{
    private static CodingAgentWebUI.TestUtilities.InMemoryConfigurationStore CreateStore()
        => new();

    /// <summary>
    /// P3 equivalent: saving PipelineConfiguration twice does not create duplicates —
    /// the second save wins (upsert semantics).
    /// </summary>
    [Fact]
    public async Task PipelineConfig_SaveTwice_IsIdempotent()
    {
        var store = CreateStore();

        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 5 }, CancellationToken.None);
        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 99 }, CancellationToken.None);

        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(99, "second save must overwrite the first (idempotent upsert)");
    }

    /// <summary>
    /// P5 equivalent: GetProviderConfigByIdAsync returns the exact config that was saved.
    /// </summary>
    [Fact]
    public async Task ProviderConfig_GetById_ReturnsSavedConfig()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();
        var original = new ProviderConfig
        {
            Id = id,
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "GetById Test Agent",
            Settings = new Dictionary<string, string> { ["model"] = "claude-sonnet" }
        };

        await store.SaveProviderConfigAsync(original, CancellationToken.None);
        var loaded = await store.GetProviderConfigByIdAsync(id, ProviderKind.Agent, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(id);
        loaded.DisplayName.Should().Be("GetById Test Agent");
        loaded.Settings["model"].Should().Be("claude-sonnet");
    }

    /// <summary>
    /// P3a: GetProviderConfigByIdAsync returns null for a non-existent ID.
    /// </summary>
    [Fact]
    public async Task ProviderConfig_GetById_NonExistentId_ReturnsNull()
    {
        var store = CreateStore();
        var loaded = await store.GetProviderConfigByIdAsync(
            Guid.NewGuid().ToString(), ProviderKind.Repository, CancellationToken.None);

        loaded.Should().BeNull();
    }

    /// <summary>
    /// UpdateAsync equivalent: UpdatePipelineConfigAsync applies the transform function.
    /// </summary>
    [Fact]
    public async Task PipelineConfig_Update_AppliesTransform()
    {
        var store = CreateStore();
        await store.SavePipelineConfigAsync(
            new PipelineConfiguration { MaxRetries = 3, WorkspaceBaseDirectory = "/test" },
            CancellationToken.None);

        await store.UpdatePipelineConfigAsync(
            c => c with { MaxRetries = c.MaxRetries + 2 },
            CancellationToken.None);

        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);
        loaded.MaxRetries.Should().Be(5, "transform MaxRetries += 2 must be applied");
        loaded.WorkspaceBaseDirectory.Should().Be("/test", "other fields must be preserved by the transform");
    }

    /// <summary>
    /// Save existing ID with different DisplayName updates it (not creates duplicate).
    /// </summary>
    [Fact]
    public async Task ProviderConfig_SaveExistingId_UpdatesDisplayName()
    {
        var store = CreateStore();
        var id = Guid.NewGuid().ToString();

        await store.SaveProviderConfigAsync(
            new ProviderConfig
            {
                Id = id, Kind = ProviderKind.Repository,
                ProviderType = "GitHub", DisplayName = "Original",
                Settings = new Dictionary<string, string> { ["owner"] = "org1" }
            }, CancellationToken.None);

        await store.SaveProviderConfigAsync(
            new ProviderConfig
            {
                Id = id, Kind = ProviderKind.Repository,
                ProviderType = "GitHub", DisplayName = "Updated",
                Settings = new Dictionary<string, string> { ["owner"] = "org2" }
            }, CancellationToken.None);

        var loaded = await store.GetProviderConfigByIdAsync(id, ProviderKind.Repository, CancellationToken.None);
        loaded!.DisplayName.Should().Be("Updated");
        loaded.Settings["owner"].Should().Be("org2");

        // Confirm no duplicate was created
        var all = await store.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        all.Count(c => c.Id == id).Should().Be(1, "upsert must not create a duplicate");
    }
}
