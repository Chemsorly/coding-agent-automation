using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ConfigImportExportEndpoints"/>.
/// Validates export produces a complete bundle and import correctly upserts.
/// </summary>
public class ConfigImportExportEndpointsTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;

    public ConfigImportExportEndpointsTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"ConfigImportExport-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task Export_ReturnsJsonBundle_WithAllConfigTypes()
    {
        // Arrange: seed DB with various config types
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.PipelineConfig.Add(new PipelineConfigEntity
            {
                Id = Guid.NewGuid(),
                Configuration = JsonSerializer.Serialize(new PipelineConfiguration { MaxRetries = 3 }, PipelineJsonOptions.Default)
            });
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = Guid.NewGuid(),
                Kind = ProviderKind.Issue,
                DisplayName = "Test Provider",
                ProviderType = "GitHub",
                Enabled = true,
                Configuration = "{\"owner\":\"test\"}"
            });
            db.AgentProfiles.Add(new AgentProfileEntity
            {
                Id = Guid.NewGuid(),
                Name = "Test Profile",
                Configuration = "{\"agentProviderConfigId\":\"x\"}"
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await ConfigImportExportEndpoints.ExportConfigAsync(_dbFactory, CancellationToken.None);

        // Assert: result is a file
        var fileResult = result as Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult;
        fileResult.Should().NotBeNull();
        fileResult!.ContentType.Should().Be("application/json");
        fileResult.FileDownloadName.Should().Be("pipeline-config-export.json");

        // Parse the bundle
        var json = System.Text.Encoding.UTF8.GetString(fileResult.FileContents.ToArray());
        var bundle = JsonSerializer.Deserialize<ConfigBundle>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        bundle.Should().NotBeNull();
        bundle!.PipelineConfig.Should().NotBeNull();
        bundle.ProviderConfigs.Should().HaveCount(1);
        bundle.AgentProfiles.Should().HaveCount(1);
    }

    [Fact]
    public async Task Export_ExcludesRuns()
    {
        // Arrange: seed with config + a pipeline run
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.PipelineConfig.Add(new PipelineConfigEntity
            {
                Id = Guid.NewGuid(),
                Configuration = "{}"
            });
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = Guid.NewGuid(),
                IssueIdentifier = "owner/repo#1",
                IssueTitle = "Test",
                StartedAt = DateTimeOffset.UtcNow,
                FinalStep = PipelineStep.Completed,
                RunType = PipelineRunType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await ConfigImportExportEndpoints.ExportConfigAsync(_dbFactory, CancellationToken.None);

        // Assert: bundle has no runs field
        var fileResult = (Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult)result;
        var json = System.Text.Encoding.UTF8.GetString(fileResult.FileContents.ToArray());
        json.Should().NotContain("pipelineRuns");
        json.Should().NotContain("owner/repo#1");
    }

    [Fact]
    public async Task Import_ClearsExistingConfig_AndInsertsBundle()
    {
        // Arrange: existing config in DB
        var existingProfileId = Guid.NewGuid();
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.PipelineConfig.Add(new PipelineConfigEntity { Id = Guid.NewGuid(), Configuration = "{}" });
            db.AgentProfiles.Add(new AgentProfileEntity { Id = existingProfileId, Name = "Old", Configuration = "{}" });
            await db.SaveChangesAsync();
        }

        // Create bundle to import
        var newProfileId = Guid.NewGuid();
        var bundle = new ConfigBundle
        {
            PipelineConfig = JsonSerializer.Serialize(new PipelineConfiguration { MaxRetries = 7 }, PipelineJsonOptions.Default),
            AgentProfiles = [new NamedConfigDto { Id = newProfileId, Name = "New Profile", Configuration = "{}" }],
            ProviderConfigs = [],
            QualityGateConfigs = [],
            ReviewerConfigs = [],
            Projects = [],
            JobTemplates = []
        };

        var bundleJson = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Create a mock IFormFile
        var file = CreateFormFile(bundleJson, "config.json");

        // Act
        var result = await ConfigImportExportEndpoints.ImportConfigAsync(file, _dbFactory, new Mock<IConfigurationStore>().Object, CancellationToken.None);

        // Assert
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<ImportExportResult>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Success.Should().BeTrue();

        await using (var db = _dbFactory.CreateDbContext())
        {
            // Old profile gone
            var oldProfile = await db.AgentProfiles.FindAsync(existingProfileId);
            oldProfile.Should().BeNull();

            // New profile present
            var newProfile = await db.AgentProfiles.FindAsync(newProfileId);
            newProfile.Should().NotBeNull();
            newProfile!.Name.Should().Be("New Profile");

            // Pipeline config updated
            var config = await db.PipelineConfig.FirstAsync();
            config.Configuration.Should().Contain("\"maxRetries\": 7");
        }
    }

    [Fact]
    public async Task Import_InvalidJson_ReturnsBadRequest()
    {
        var file = CreateFormFile("not valid json {{{", "bad.json");

        var result = await ConfigImportExportEndpoints.ImportConfigAsync(file, _dbFactory, new Mock<IConfigurationStore>().Object, CancellationToken.None);

        var badRequest = result as Microsoft.AspNetCore.Http.HttpResults.BadRequest<ImportExportResult>;
        badRequest.Should().NotBeNull();
        badRequest!.Value!.Success.Should().BeFalse();
        badRequest.Value.Message.Should().Contain("Invalid JSON");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Microsoft.AspNetCore.Http.IFormFile CreateFormFile(string content, string fileName)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return new Microsoft.AspNetCore.Http.FormFile(stream, 0, stream.Length, "file", fileName)
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
            ContentType = "application/json"
        };
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
    }
}
