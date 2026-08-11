using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds <c>IssueProviderConfigId</c> (nullable text) to <c>PipelineRuns</c>.
    ///
    /// This column acts as a discriminator for consolidation runs in the column-fallback
    /// deserialization path of <c>PostgresPipelineRunHistoryService.DeserializeSummary</c>.
    /// When <c>SummaryJson</c> is null or corrupt, <c>InitiatedBy</c> cannot be recovered from
    /// JSON, so consolidation ghost entries previously leaked into user-facing run history.
    ///
    /// The column stores <c>ConsolidationConstants.ProviderConfigId</c> ("consolidation") for
    /// consolidation runs and NULL for all other runs. <c>DeserializeSummary</c> reads it to
    /// reconstruct <c>InitiatedBy</c> reliably without <c>SummaryJson</c>.
    ///
    /// Existing rows receive NULL (correct: they are non-consolidation legacy rows).
    /// </summary>
    public partial class AddIssueProviderConfigIdToPipelineRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IssueProviderConfigId",
                table: "PipelineRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IssueProviderConfigId",
                table: "PipelineRuns");
        }
    }
}
