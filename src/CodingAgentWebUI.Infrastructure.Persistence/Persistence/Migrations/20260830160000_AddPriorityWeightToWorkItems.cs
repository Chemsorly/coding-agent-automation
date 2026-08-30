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
            migrationBuilder.AddColumn<int>(
                name: "PriorityWeight",
                table: "WorkItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);
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
