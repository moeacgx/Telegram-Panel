using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TelegramPanel.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261009010000_AddManagedWgcfProfile")]
public sealed class AddManagedWgcfProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ManagedWgcfProfile", "OutboundProxies", type: "TEXT", maxLength: 48, nullable: true);
        migrationBuilder.CreateIndex("IX_OutboundProxies_ManagedWgcfProfile", "OutboundProxies", "ManagedWgcfProfile", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // EF Core 的 SQLite 生成器拒绝 DropColumn，但面板支持的 SQLite 已具备
        // ALTER TABLE DROP COLUMN。使用原生 SQL 让升级演练可以完整回滚。
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_OutboundProxies_ManagedWgcfProfile\";");
            migrationBuilder.Sql("ALTER TABLE \"OutboundProxies\" DROP COLUMN \"ManagedWgcfProfile\";");
            return;
        }

        migrationBuilder.DropIndex("IX_OutboundProxies_ManagedWgcfProfile", "OutboundProxies");
        migrationBuilder.DropColumn("ManagedWgcfProfile", "OutboundProxies");
    }
}
