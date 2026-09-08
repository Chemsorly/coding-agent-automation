using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillZeroTimeoutSeconds : Migration
    {
        // 1800 seconds = 30 minutes = PipelineConstants.DefaultAgentTimeout
        private const int DefaultTimeoutSeconds = 1800;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Back-fill legacy rows created before #2179 that stored TimeoutSeconds = 0.
            // Those rows pre-dated the per-item timeout feature; 1800 s (30 min) matches
            // PipelineConstants.DefaultAgentTimeout and preserves the existing sentinel fallback
            // behaviour. The UPDATE is idempotent: rows already > 0 are unaffected.
            migrationBuilder.Sql(
                $"""
                UPDATE "WorkItems"
                SET "TimeoutSeconds" = {DefaultTimeoutSeconds}
                WHERE "TimeoutSeconds" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down does not restore the original 0 values — those were invalid sentinel rows and
            // restoring them would reintroduce the silent fallback behaviour. A rollback of this
            // migration leaves TimeoutSeconds at 1800 for previously-zeroed rows, which is safe.
        }
    }
}
