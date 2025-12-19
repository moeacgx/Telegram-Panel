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
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelGroup> ChannelGroups => Set<ChannelGroup>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<BatchTask> BatchTasks => Set<BatchTask>();

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

            entity.HasIndex(e => e.Phone).IsUnique();
            entity.HasIndex(e => e.UserId).IsUnique();

            // 与AccountCategory的关系
            entity.HasOne(e => e.Category)
                .WithMany(c => c.Accounts)
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AccountCategory配置
        modelBuilder.Entity<AccountCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Color).HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasIndex(e => e.Name).IsUnique();
        });

        // Channel配置
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Username).HasMaxLength(100);
            entity.Property(e => e.About).HasMaxLength(1000);

            entity.HasIndex(e => e.TelegramId).IsUnique();
            entity.HasIndex(e => e.Username);

            // 与Account的关系（创建者）
            entity.HasOne(e => e.CreatorAccount)
                .WithMany(a => a.Channels)
                .HasForeignKey(e => e.CreatorAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // 与ChannelGroup的关系
            entity.HasOne(e => e.Group)
                .WithMany(g => g.Channels)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.SetNull);
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

            entity.HasIndex(e => e.TelegramId).IsUnique();
            entity.HasIndex(e => e.Username);

            // 与Account的关系（创建者）
            entity.HasOne(e => e.CreatorAccount)
                .WithMany(a => a.Groups)
                .HasForeignKey(e => e.CreatorAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BatchTask配置
        modelBuilder.Entity<BatchTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TaskType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}
