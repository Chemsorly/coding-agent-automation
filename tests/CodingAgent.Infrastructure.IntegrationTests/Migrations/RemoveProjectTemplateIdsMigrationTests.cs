using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CodingAgent.Infrastructure.IntegrationTests.Migrations;

/// <summary>
/// The <c>RemoveProjectTemplateIds</c> migration against a real PostgreSQL database. Before it drops the
/// <c>Projects.TemplateIds</c> list, it settles the list against each template's own project the way the
/// loop behaved (the loop followed the list):
/// <list type="bullet">
/// <item>a template a project lists moves to that project, a project other than Default first;</item>
/// <item>a template no project lists was never polled, so it is disabled;</item>
/// <item>a template whose project no longer exists moves to the Default project.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RemoveProjectTemplateIdsMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260918194915_AddBranchNameToWorkItems";
    private const string DefaultProject = "00000000-0000-0000-0000-000000000000";
    private const string ProjectA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string ProjectB = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string MissingProject = "dddddddd-0000-0000-0000-000000000004";

    private const string ListedByA = "10000000-0000-0000-0000-000000000001";
    private const string ListedByNobody = "20000000-0000-0000-0000-000000000002";
    private const string ListedByDefaultAndB = "30000000-0000-0000-0000-000000000003";
    private const string OrphanListedByNobody = "40000000-0000-0000-0000-000000000004";
    private const string Consistent = "50000000-0000-0000-0000-000000000005";

    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("membership_migration_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker is unavailable (e.g. the local agent quality-gate runner); the tests return early.
            _container = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task Up_SettlesTheListAgainstEachTemplatesProject_ThenDropsIt()
    {
        if (_container is null) return;

        await MigrateToAsync(PreviousMigration);
        await SeedPreMigrationStateAsync();

        await MigrateToAsync(null);

        (await ProjectOfAsync(ListedByA)).Should().Be(ProjectA, "the list the loop followed wins");
        (await ProjectOfAsync(ListedByDefaultAndB)).Should().Be(ProjectB, "a project other than Default wins");
        (await ProjectOfAsync(OrphanListedByNobody)).Should().Be(DefaultProject, "its project no longer exists");
        (await ProjectOfAsync(ListedByNobody)).Should().Be(ProjectA);
        (await ProjectOfAsync(Consistent)).Should().Be(ProjectB);

        (await EnabledAsync(ListedByNobody)).Should().Be("false", "no project listed it, so the loop never polled it");
        (await EnabledAsync(OrphanListedByNobody)).Should().Be("false");
        (await EnabledAsync(ListedByA)).Should().Be("true");
        (await EnabledAsync(ListedByDefaultAndB)).Should().Be("true");
        (await EnabledAsync(Consistent)).Should().Be("true");

        (await ScalarAsync<long>(
            """SELECT count(*) FROM information_schema.columns WHERE table_name = 'Projects' AND column_name = 'TemplateIds'"""))
            .Should().Be(0);
        (await ScalarAsync<bool>($"""SELECT ("Settings" -> 'templateIds') IS NOT NULL FROM "Projects" WHERE "Id" = '{ProjectA}'"""))
            .Should().BeFalse("the Settings JSON copy of the list is removed");
    }

    [Fact]
    public async Task Down_RestoresEachProjectsList_ByName()
    {
        if (_container is null) return;

        await MigrateToAsync(PreviousMigration);
        await SeedPreMigrationStateAsync();
        await MigrateToAsync(null);

        await MigrateToAsync(PreviousMigration);

        (await ScalarAsync<string[]>($"""SELECT "TemplateIds" FROM "Projects" WHERE "Id" = '{ProjectB}'"""))
            .Should().Equal(Consistent, ListedByDefaultAndB); // "Beta" before "Gamma"
    }

    private async Task SeedPreMigrationStateAsync()
    {
        await ExecuteAsync(
            $$"""
            INSERT INTO "Projects" ("Id", "Name", "Enabled", "Settings", "TemplateIds") VALUES
                ('{{DefaultProject}}', 'Default', true, '{"name":"Default","templateIds":["{{ListedByDefaultAndB}}"]}', ARRAY['{{ListedByDefaultAndB}}']),
                ('{{ProjectA}}', 'Alpha', true, '{"name":"Alpha","templateIds":["{{ListedByA}}"]}', ARRAY['{{ListedByA.ToUpperInvariant()}}']),
                ('{{ProjectB}}', 'Bravo', true, '{"name":"Bravo"}', ARRAY['{{ListedByDefaultAndB}}', '{{Consistent}}']);

            INSERT INTO "PipelineJobTemplates" ("Id", "ProjectId", "Name", "Configuration") VALUES
                ('{{ListedByA}}', '{{DefaultProject}}', 'Alpha one', '{"id":"{{ListedByA}}","name":"Alpha one","enabled":true}'),
                ('{{ListedByNobody}}', '{{ProjectA}}', 'Hidden', '{"id":"{{ListedByNobody}}","name":"Hidden","enabled":true}'),
                ('{{ListedByDefaultAndB}}', '{{DefaultProject}}', 'Gamma', '{"id":"{{ListedByDefaultAndB}}","name":"Gamma","enabled":true}'),
                ('{{OrphanListedByNobody}}', '{{MissingProject}}', 'Orphan', '{"id":"{{OrphanListedByNobody}}","name":"Orphan","enabled":true}'),
                ('{{Consistent}}', '{{ProjectB}}', 'Beta', '{"id":"{{Consistent}}","name":"Beta","enabled":true}');
            """);
    }

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(_container!.GetConnectionString())
            .Options;
        await using var db = new PipelineDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private Task<string> ProjectOfAsync(string templateId) =>
        ScalarAsync<string>($"""SELECT "ProjectId"::text FROM "PipelineJobTemplates" WHERE "Id" = '{templateId}'""");

    private Task<string> EnabledAsync(string templateId) =>
        ScalarAsync<string>($"""SELECT "Configuration"->>'enabled' FROM "PipelineJobTemplates" WHERE "Id" = '{templateId}'""");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
