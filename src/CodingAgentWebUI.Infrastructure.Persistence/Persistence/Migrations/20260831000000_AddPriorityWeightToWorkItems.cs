using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Persistence.Migrations
{
    /// <summary>
    /// Adds a <c>PriorityWeight</c> column (int NOT NULL DEFAULT 0) to the <c>WorkItems</c> table.
    /// Manual dispatches receive weight 100; closed-loop dispatches receive 0.
    /// Pending items are ordered <c>PriorityWeight DESC, CreatedAt ASC</c> so manual items
    /// are claimed before loop items within the same dispatch cycle.
    /// </summary>
    public partial class AddPriorityWeightToWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DEFAULT 0 is specified to backfill existing rows; the DB-level default is
            // immediately dropped afterwards so the EF model (no HasDefaultValue) stays
            // in sync with the schema. The application always supplies the value explicitly.
            migrationBuilder.AddColumn<int>(
                name: "PriorityWeight",
                table: "WorkItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "PriorityWeight",
                table: "WorkItems",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            // TODO: Consider adding a composite index to support the new sort order efficiently.
            // Three queries now execute WHERE Status = Pending ORDER BY PriorityWeight DESC, CreatedAt ASC.
            // The existing IX_WorkItems_Status index supports the filter, but PostgreSQL must sort the
            // entire pending set in memory on every dispatch cycle. A covering index such as:
            //   CREATE INDEX IX_WorkItems_Status_PriorityWeight_CreatedAt
            //     ON "WorkItems" ("Status", "PriorityWeight" DESC, "CreatedAt" ASC)
            //     WHERE "Status" = 0  -- Pending
            // would allow an index scan in sort order. Evaluate once queue depths are large enough
            // to make the in-memory sort observable.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriorityWeight",
                table: "WorkItems");
        }
    }
}
