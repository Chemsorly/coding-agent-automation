using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Contract tests for <see cref="IConfigurationStore"/> implementations.
/// Both JSON-backed and Postgres-backed stores must satisfy these behavioral contracts.
/// Prevents behavioral drift between dev (JSON) and prod (Postgres) modes.
/// 
/// Derived classes provide a concrete store instance via <see cref="CreateStore"/>.
/// </summary>
public abstract class ConfigurationStoreContractTests : IDisposable
{
    /// <summary>Create a fresh store instance for isolation between tests.</summary>
    protected abstract IConfigurationStore CreateStore();

    /// <summary>Cleanup resources after each test.</summary>
    public virtual void Dispose() { }

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

// ── JSON-backed implementation ──────────────────────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="JsonConfigurationStore"/>.
/// </summary>
public class JsonConfigurationStoreContractTests : ConfigurationStoreContractTests
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"contract-json-{Guid.NewGuid()}");

    public JsonConfigurationStoreContractTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    protected override IConfigurationStore CreateStore() => new JsonConfigurationStore(_tempDir);

    public override void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
