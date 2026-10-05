using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The whole schema. In October 2026 the 23 original migrations were squashed into this baseline. It
    /// reuses the ID of the newest original migration (20260930213632_DropConsolidationRuns), so a database that
    /// applied every original migration has nothing pending, and a build from before the squash still finds its
    /// migrations applied. DatabaseStartupService refuses a database that stopped before that migration.
    /// </summary>
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProfiles", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "KeyValueStore",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyValueStore", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "PipelineConfig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineJobTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineJobTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRuns",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssueIdentifier = table.Column<string>(type: "text", nullable: false),
                    IssueTitle = table.Column<string>(type: "text", nullable: true),
                    FinalStep = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    PullRequestUrl = table.Column<string>(type: "text", nullable: true),
                    ModelName = table.Column<string>(type: "text", nullable: true),
                    AgentId = table.Column<string>(type: "text", nullable: true),
                    ProjectId = table.Column<string>(type: "text", nullable: true),
                    ProjectName = table.Column<string>(type: "text", nullable: true),
                    RunType = table.Column<int>(type: "integer", nullable: false),
                    IssueProviderConfigId = table.Column<string>(type: "text", nullable: true),
                    SummaryJson = table.Column<string>(type: "jsonb", nullable: true),
                    HarnessVersion = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRuns", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    ProviderType = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityGateConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityGateConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReviewerConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewerConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskType = table.Column<int>(type: "integer", nullable: false),
                    IssueIdentifier = table.Column<string>(type: "text", nullable: false),
                    IssueProviderConfigId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    AgentSelector = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    K8sJobName = table.Column<string>(type: "text", nullable: true),
                    AssignedAgentId = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<int>(type: "integer", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalEnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClaimedPvcName = table.Column<string>(type: "text", nullable: true),
                    TraceParent = table.Column<string>(type: "text", nullable: true),
                    BranchName = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    FirstAssignmentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PriorityWeight = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItems_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_AgentId",
                table: "PipelineRuns",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_FinalStep_CompletedAt",
                table: "PipelineRuns",
                columns: new[] { "FinalStep", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_ProjectId_StartedAt",
                table: "PipelineRuns",
                columns: new[] { "ProjectId", "StartedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_StartedAt",
                table: "PipelineRuns",
                column: "StartedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_IssueIdentifier_IssueProviderConfigId",
                table: "WorkItems",
                columns: new[] { "IssueIdentifier", "IssueProviderConfigId" },
                unique: true,
                filter: "\"Status\" NOT IN (3, 4, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_IssueIdentifier_IssueProviderConfigId_Status",
                table: "WorkItems",
                columns: new[] { "IssueIdentifier", "IssueProviderConfigId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems",
                columns: new[] { "ProjectId", "CompletedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"Status\" IN (3, 4, 5) AND \"CompletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Status",
                table: "WorkItems",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentProfiles");

            migrationBuilder.DropTable(
                name: "FeedbackCommentOutbox");

            migrationBuilder.DropTable(
                name: "KeyValueStore");

            migrationBuilder.DropTable(
                name: "PipelineConfig");

            migrationBuilder.DropTable(
                name: "PipelineJobTemplates");

            migrationBuilder.DropTable(
                name: "PipelineRuns");

            migrationBuilder.DropTable(
                name: "ProviderConfigs");

            migrationBuilder.DropTable(
                name: "QualityGateConfigs");

            migrationBuilder.DropTable(
                name: "ReviewerConfigs");

            migrationBuilder.DropTable(
                name: "WorkItems");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
