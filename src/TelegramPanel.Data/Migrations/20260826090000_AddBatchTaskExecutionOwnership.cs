using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260826090000_AddBatchTaskExecutionOwnership")]
    public partial class AddBatchTaskExecutionOwnership : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionKind",
                table: "BatchTasks",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "batch");

            migrationBuilder.AddColumn<string>(
                name: "OwnerModuleId",
                table: "BatchTasks",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "host.legacy");

            migrationBuilder.AddColumn<DateTime>(
                name: "HeartbeatAtUtc",
                table: "BatchTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresAttention",
                table: "BatchTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeMessage",
                table: "BatchTasks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimePhase",
                table: "BatchTasks",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BatchTasks_ExecutionKind_Status",
                table: "BatchTasks",
                columns: new[] { "ExecutionKind", "Status" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BatchTasks_ExecutionKind_Status",
                table: "BatchTasks");

            migrationBuilder.DropColumn(
                name: "ExecutionKind",
                table: "BatchTasks");

            migrationBuilder.DropColumn(
                name: "OwnerModuleId",
                table: "BatchTasks");

            migrationBuilder.DropColumn(
                name: "HeartbeatAtUtc",
                table: "BatchTasks");

            migrationBuilder.DropColumn(
                name: "RequiresAttention",
                table: "BatchTasks");

            migrationBuilder.DropColumn(
                name: "RuntimeMessage",
                table: "BatchTasks");

            migrationBuilder.DropColumn(
                name: "RuntimePhase",
                table: "BatchTasks");
        }
    }
}
