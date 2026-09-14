using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgent.Infrastructure.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackCommentOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedbackCommentOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: false),
                    IssueProviderConfigId = table.Column<string>(type: "text", nullable: false),
                    IssueIdentifier = table.Column<string>(type: "text", nullable: false),
                    RepoProviderConfigId = table.Column<string>(type: "text", nullable: false),
                    PullRequestNumber = table.Column<string>(type: "text", nullable: true),
                    FeedbackJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackCommentOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackCommentOutbox_RunId",
                table: "FeedbackCommentOutbox",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackCommentOutbox_Status_AttemptCount_CreatedAt",
                table: "FeedbackCommentOutbox",
                columns: new[] { "Status", "AttemptCount", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedbackCommentOutbox");
        }
    }
}
