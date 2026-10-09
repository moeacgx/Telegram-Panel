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
        migrationBuilder.DropIndex("IX_OutboundProxies_ManagedWgcfProfile", "OutboundProxies");
        migrationBuilder.DropColumn("ManagedWgcfProfile", "OutboundProxies");
    }
}
