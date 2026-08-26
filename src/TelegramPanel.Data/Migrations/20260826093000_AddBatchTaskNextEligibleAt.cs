using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260826093000_AddBatchTaskNextEligibleAt")]
    public partial class AddBatchTaskNextEligibleAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BatchTasks_ExecutionKind_Status",
                table: "BatchTasks");

            migrationBuilder.AddColumn<DateTime>(
                name: "NextEligibleAtUtc",
                table: "BatchTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BatchTasks_ExecutionKind_Status_NextEligibleAtUtc",
                table: "BatchTasks",
                columns: new[] { "ExecutionKind", "Status", "NextEligibleAtUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BatchTasks_ExecutionKind_Status_NextEligibleAtUtc",
                table: "BatchTasks");

            migrationBuilder.Sql(
                """
                ALTER TABLE "BatchTasks" DROP COLUMN "NextEligibleAtUtc";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BatchTasks_ExecutionKind_Status",
                table: "BatchTasks",
                columns: new[] { "ExecutionKind", "Status" });
        }
    }
}
