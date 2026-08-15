using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data;

/// <summary>
/// 应用程序数据库上下文
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // 实体集合
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountCategory> AccountCategories => Set<AccountCategory>();
    public DbSet<AccountChannel> AccountChannels => Set<AccountChannel>();
    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelGroup> ChannelGroups => Set<ChannelGroup>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupCategory> GroupCategories => Set<GroupCategory>();
    public DbSet<BatchTask> BatchTasks => Set<BatchTask>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<DataDictionary> DataDictionaries => Set<DataDictionary>();
    public DbSet<DataDictionaryItem> DataDictionaryItems => Set<DataDictionaryItem>();
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<BotChannel> BotChannels => Set<BotChannel>();
    public DbSet<BotChannelCategory> BotChannelCategories => Set<BotChannelCategory>();
    public DbSet<BotChannelMember> BotChannelMembers => Set<BotChannelMember>();
    public DbSet<OutboundProxy> OutboundProxies => Set<OutboundProxy>();
    public DbSet<ProxyCategory> ProxyCategories => Set<ProxyCategory>();
    public DbSet<WarpProfile> WarpProfiles => Set<WarpProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Account配置
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Phone).IsRequired().HasMaxLength(20);
            entity.Property(e => e.SessionPath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ApiHash).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.Nickname).HasMaxLength(100);
            entity.Property(e => e.Remark).HasMaxLength(500);

            entity.Property(e => e.TelegramStatusSummary).HasMaxLength(200);
            entity.Property(e => e.TelegramStatusDetails).HasMaxLength(2000);
            entity.Property(e => e.UseGlobalProxy).HasDefaultValue(true);

            entity.HasIndex(e => e.Phone).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.ProxyId);

            entity.HasOne(e => e.Category)
                .WithMany(c => c.Accounts)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Proxy)
                .WithMany(p => p.Accounts)
                .HasForeignKey(e => e.ProxyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AccountCategory配置
        modelBuilder.Entity<AccountCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Color).HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ExcludeFromOperations).HasDefaultValue(false);

            entity.HasIndex(e => e.Name).IsUnique();
        });

        // Channel配置
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.About).HasMaxLength(1000);
            entity.Property(e => e.SystemCreatedAtUtc);

            entity.HasIndex(e => e.TelegramId).IsUnique();
            entity.HasIndex(e => e.Username);
            entity.HasIndex(e => e.CreatorAccountId);
            entity.HasIndex(e => e.GroupId);
            entity.HasIndex(e => e.SyncedAt);

            entity.HasOne(e => e.CreatorAccount)
                .WithMany(a => a.Channels)
                .HasForeignKey(e => e.CreatorAccountId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Group)
                .WithMany(g => g.Channels)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AccountChannel配置
        modelBuilder.Entity<AccountChannel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.AccountId, e.ChannelId }).IsUnique();
            entity.HasIndex(e => e.ChannelId);
            entity.HasIndex(e => new { e.ChannelId, e.IsCreator, e.IsAdmin });

            entity.HasOne(e => e.Account)
                .WithMany(a => a.AccountChannels)
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Channel)
                .WithMany(c => c.AccountChannels)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ChannelGroup配置
        modelBuilder.Entity<ChannelGroup>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasIndex(e => e.Name).IsUnique();
        });

        // Group配置
        modelBuilder.Entity<Group>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.About).HasMaxLength(1000);
            entity.Property(e => e.SystemCreatedAtUtc);

            entity.HasIndex(e => e.TelegramId).IsUnique();
            entity.HasIndex(e => e.Username);
            entity.HasIndex(e => e.CategoryId);
            entity.HasIndex(e => e.CreatorAccountId);
            entity.HasIndex(e => e.SyncedAt);

            entity.HasOne(e => e.CreatorAccount)
                .WithMany(a => a.Groups)
                .HasForeignKey(e => e.CreatorAccountId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Category)
                .WithMany(c => c.Groups)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AccountGroup配置
        modelBuilder.Entity<AccountGroup>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.AccountId, e.GroupId }).IsUnique();
            entity.HasIndex(e => e.GroupId);
            entity.HasIndex(e => new { e.GroupId, e.IsCreator, e.IsAdmin });

            entity.HasOne(e => e.Account)
                .WithMany(a => a.AccountGroups)
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Group)
                .WithMany(g => g.AccountGroups)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // GroupCategory配置
        modelBuilder.Entity<GroupCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasIndex(e => e.Name).IsUnique();
        });

        // BatchTask配置
        modelBuilder.Entity<BatchTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.TaskType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
        });

        // ScheduledTask配置
        modelBuilder.Entity<ScheduledTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TaskType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CronExpression).IsRequired().HasMaxLength(100);
            entity.Property(e => e.OwnedAssetScopeId).HasMaxLength(120);

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.NextRunAtUtc);
            entity.HasIndex(e => e.CreatedAt);
        });

        // DataDictionary配置
        modelBuilder.Entity<DataDictionary>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ReadMode).IsRequired().HasMaxLength(20);

            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.IsEnabled);
        });

        // DataDictionaryItem配置
        modelBuilder.Entity<DataDictionaryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TextValue).HasMaxLength(4000);
            entity.Property(e => e.AssetPath).HasMaxLength(500);
            entity.Property(e => e.FileName).HasMaxLength(255);

            entity.HasIndex(e => e.DataDictionaryId);
            entity.HasIndex(e => new { e.DataDictionaryId, e.SortOrder });

            entity.HasOne(e => e.Dictionary)
                .WithMany(d => d.Items)
                .HasForeignKey(e => e.DataDictionaryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Bot配置
        modelBuilder.Entity<Bot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Username).HasMaxLength(100);

            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.Username);
        });

        // BotChannelCategory配置
        modelBuilder.Entity<BotChannelCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasIndex(e => e.Name).IsUnique();
        });

        // BotChannel配置
        modelBuilder.Entity<BotChannel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.About).HasMaxLength(1000);
            entity.Property(e => e.ChannelStatusError).HasMaxLength(500);

            entity.HasIndex(e => e.TelegramId).IsUnique();
            entity.HasIndex(e => e.Username);
            entity.HasIndex(e => e.CategoryId);
            entity.HasIndex(e => e.ChannelStatusOk);

            entity.HasOne(e => e.Category)
                .WithMany(c => c.Channels)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // BotChannelMember 配置
        modelBuilder.Entity<BotChannelMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.BotId, e.BotChannelId }).IsUnique();
            entity.HasIndex(e => e.BotId);
            entity.HasIndex(e => e.BotChannelId);

            entity.HasOne(e => e.Bot)
                .WithMany(b => b.ChannelMembers)
                .HasForeignKey(e => e.BotId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.BotChannel)
                .WithMany(c => c.Members)
                .HasForeignKey(e => e.BotChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 代理分类配置
        modelBuilder.Entity<ProxyCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Color).HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasIndex(e => e.Name).IsUnique();
        });

        // 出站代理配置
        modelBuilder.Entity<OutboundProxy>(entity =>
        {
            entity.ToTable("OutboundProxies", table =>
            {
                table.HasCheckConstraint("CK_OutboundProxies_Kind", "\"Kind\" IN ('manual', 'resin', 'warp', 'wireguard_warp')");
                table.HasCheckConstraint("CK_OutboundProxies_Protocol", "\"Protocol\" IN ('http', 'socks5', 'mtproto')");
                table.HasCheckConstraint("CK_OutboundProxies_TestStatus", "\"TestStatus\" IN ('unknown', 'ok', 'fail')");
                table.HasCheckConstraint("CK_OutboundProxies_Port", "\"Port\" BETWEEN 1 AND 65535");
                table.HasCheckConstraint(
                    "CK_OutboundProxies_LastLatencyMs",
                    "\"LastLatencyMs\" IS NULL OR \"LastLatencyMs\" >= 0");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(20).HasDefaultValue("manual");
            entity.Property(e => e.Protocol).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Host).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Username).HasMaxLength(500);
            entity.Property(e => e.Password).HasMaxLength(500);
            entity.Property(e => e.Secret).HasMaxLength(500);
            entity.Property(e => e.ResinPlatform).HasMaxLength(100);
            entity.Property(e => e.ResinAdminUrl).HasMaxLength(500);
            entity.Property(e => e.ResinAdminToken).HasMaxLength(500);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.TestStatus).IsRequired().HasMaxLength(20).HasDefaultValue("unknown");
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.EgressIp).HasMaxLength(64);
            entity.Property(e => e.EgressCountry).HasMaxLength(100);
            entity.Property(e => e.EgressCity).HasMaxLength(100);
            entity.Property(e => e.EgressIsp).HasMaxLength(200);

            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.CategoryId);
            entity.HasIndex(e => new { e.IsEnabled, e.Kind });
            entity.HasIndex(e => e.Protocol);
            entity.HasIndex(e => e.TestStatus);
            entity.HasIndex(e => e.EgressIp);

            entity.HasOne(e => e.Category)
                .WithMany(c => c.Proxies)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // WARP 容器与代理的一对一映射
        modelBuilder.Entity<WarpProfile>(entity =>
        {
            entity.ToTable("WarpProfiles", table =>
            {
                table.HasCheckConstraint(
                    "CK_WarpProfiles_HostPort",
                    "\"HostPort\" BETWEEN 1 AND 65535");
            });

            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProfileId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.RequestId).HasMaxLength(128);
            entity.Property(e => e.ContainerName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ContainerId).HasMaxLength(128);
            entity.Property(e => e.VolumeName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(32).HasDefaultValue("pending");
            entity.Property(e => e.DesiredEnabled).HasDefaultValue(true);
            entity.Property(e => e.EgressIp).HasMaxLength(64);
            entity.Property(e => e.Country).HasMaxLength(100);
            entity.Property(e => e.WarpStatus).HasMaxLength(50);
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.ConsecutiveFailures).HasDefaultValue(0);
            entity.Property(e => e.RecoveryCount).HasDefaultValue(0);

            entity.HasIndex(e => e.ProfileId).IsUnique();
            entity.HasIndex(e => e.RequestId).IsUnique();
            entity.HasIndex(e => e.OutboundProxyId).IsUnique();
            entity.HasIndex(e => e.ContainerName).IsUnique();
            entity.HasIndex(e => e.ContainerId).IsUnique();
            entity.HasIndex(e => e.VolumeName).IsUnique();
            // 已删除或创建失败的历史记录保留端口用于审计，但不应阻止端口复用。
            entity.HasIndex(e => e.HostPort)
                .IsUnique()
                .HasFilter("\"Status\" NOT IN ('deleted', 'failed')");
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.DesiredEnabled);

            entity.HasOne(e => e.Proxy)
                .WithOne(p => p.WarpProfile)
                .HasForeignKey<WarpProfile>(e => e.OutboundProxyId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
