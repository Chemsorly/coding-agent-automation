using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Persistence.Migrations
{
    /// <summary>
    /// Migrates <c>WorkItems."ProjectId"</c> from <c>text</c> to <c>uuid</c> and adds a foreign key
    /// constraint to <c>Projects."Id"</c> (ON DELETE SET NULL).
    ///
    /// All existing <c>ProjectId</c> values are valid UUID strings (confirmed by DB audit prior to
    /// this migration). The <c>USING "ProjectId"::uuid</c> clause casts existing text values to uuid
    /// in-place — no rows are deleted or modified.
    ///
    /// The retention partial index <c>IX_WorkItems_ProjectId_CompletedAt_Terminal</c> is dropped and
    /// recreated because Postgres requires the index to be rebuilt when the column type changes.
    ///
    /// Cascade: <c>ON DELETE SET NULL</c> — deleting a Project clears the FK on its WorkItems rather
    /// than deleting them. This is the safe default chosen for this system.
    /// </summary>
    public partial class WorkItemProjectIdToUuidWithFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the retention partial index before altering the column type.
            // Postgres will refuse to alter the column type while a dependent index exists.
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems");

            // Alter the column type from text to uuid.
            // USING "ProjectId"::uuid is required for Postgres to cast existing non-null text values.
            // All existing values are confirmed valid UUID strings — no data loss.
            migrationBuilder.Sql(
                @"ALTER TABLE ""WorkItems"" ALTER COLUMN ""ProjectId"" TYPE uuid USING ""ProjectId""::uuid;");

            // Recreate the retention partial index with uuid column type.
            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems",
                columns: new[] { "ProjectId", "CompletedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"Status\" IN (3, 4, 5) AND \"CompletedAt\" IS NOT NULL");

            // Add the FK constraint. Uses ON DELETE SET NULL so deleting a Project
            // clears ProjectId on its WorkItems rather than cascading deletes.
            migrationBuilder.AddForeignKey(
                name: "FK_WorkItems_Projects_ProjectId",
                table: "WorkItems",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // TODO: This migration is missing a CreateIndex call for IX_WorkItems_ProjectId (the simple
            // single-column FK index). The EF model snapshot and migration designer both declare
            // b.HasIndex("ProjectId"), but this Up() method never emits CreateIndex for it.
            // The raw SQL ALTER COLUMN bypassed EF's automatic FK-index generation.
            // Impact: (1) FK-based queries and ON DELETE SET NULL cascades lack index support;
            // (2) the next `dotnet ef migrations add` run will emit a spurious CreateIndex migration.
            // Fix: add migrationBuilder.CreateIndex(name: "IX_WorkItems_ProjectId", table: "WorkItems",
            // column: "ProjectId") here, and a matching DropIndex in Down() before the FK is dropped.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the FK constraint first
            migrationBuilder.DropForeignKey(
                name: "FK_WorkItems_Projects_ProjectId",
                table: "WorkItems");

            // Drop the retention index before reverting the column type
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems");

            // Revert column type from uuid back to text
            migrationBuilder.Sql(
                @"ALTER TABLE ""WorkItems"" ALTER COLUMN ""ProjectId"" TYPE text USING ""ProjectId""::text;");

            // Recreate the retention partial index with text column type
            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems",
                columns: new[] { "ProjectId", "CompletedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"Status\" IN (3, 4, 5) AND \"CompletedAt\" IS NOT NULL");
        }
    }
}
