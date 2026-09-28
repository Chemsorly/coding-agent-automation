using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgent.Infrastructure.Persistence.Persistence.Migrations
{
    /// <summary>
    /// Makes a template's own project (<c>PipelineJobTemplates.ProjectId</c>) the only record of project
    /// membership and drops the <c>Projects.TemplateIds</c> list.
    /// </summary>
    /// <remarks>
    /// Before the drop, the two records are settled once, keeping what the loop did. The loop and the UI
    /// went by the list, so:
    /// <list type="number">
    /// <item>A template no project lists was never polled. It is disabled, so it stays inactive, but it shows up again.</item>
    /// <item>A template a project lists moves to that project. When several projects list it, a project
    /// other than Default wins, then the first by name.</item>
    /// <item>A template whose project no longer exists moves to the Default project.</item>
    /// </list>
    /// </remarks>
    public partial class RemoveProjectTemplateIds : Migration
    {
        private const string DefaultProjectId = "00000000-0000-0000-0000-000000000000";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "PipelineJobTemplates" AS t
                SET "Configuration" = jsonb_set(t."Configuration", '{enabled}', 'false'::jsonb)
                WHERE t."Configuration" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "Projects" AS p
                      CROSS JOIN LATERAL unnest(p."TemplateIds") AS member(template_id)
                      WHERE lower(member.template_id) = t."Id"::text);
                """);

            migrationBuilder.Sql(
                $"""
                UPDATE "PipelineJobTemplates" AS t
                SET "ProjectId" = listed."ProjectId"
                FROM (
                    SELECT DISTINCT ON (lower(member.template_id))
                           lower(member.template_id) AS template_id,
                           p."Id" AS "ProjectId"
                    FROM "Projects" AS p
                    CROSS JOIN LATERAL unnest(p."TemplateIds") AS member(template_id)
                    ORDER BY lower(member.template_id),
                             (p."Id" = '{DefaultProjectId}') ASC,
                             p."Name",
                             p."Id"
                ) AS listed
                WHERE t."Id"::text = listed.template_id
                  AND t."ProjectId" <> listed."ProjectId";
                """);

            migrationBuilder.Sql(
                $"""
                UPDATE "PipelineJobTemplates" AS t
                SET "ProjectId" = '{DefaultProjectId}'
                WHERE NOT EXISTS (SELECT 1 FROM "Projects" AS p WHERE p."Id" = t."ProjectId")
                  AND EXISTS (SELECT 1 FROM "Projects" AS d WHERE d."Id" = '{DefaultProjectId}');
                """);

            migrationBuilder.DropColumn(
                name: "TemplateIds",
                table: "Projects");

            // The project's Settings JSON carried a copy of the list.
            migrationBuilder.Sql(
                """
                UPDATE "Projects"
                SET "Settings" = "Settings" - 'templateIds' - 'TemplateIds'
                WHERE "Settings" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "TemplateIds",
                table: "Projects",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.Sql(
                """
                UPDATE "Projects" AS p
                SET "TemplateIds" = COALESCE((
                    SELECT array_agg(t."Id"::text ORDER BY lower(t."Name"), t."Name", t."Id")
                    FROM "PipelineJobTemplates" AS t
                    WHERE t."ProjectId" = p."Id"), '{}'::text[]);
                """);
        }
    }
}
