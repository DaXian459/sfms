using Microsoft.EntityFrameworkCore;
using SFMS.Domain;

namespace SFMS.Infrastructure;

public class SfmsDbContext(DbContextOptions<SfmsDbContext> options) : DbContext(options)
{
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ScanGroup> ScanGroups => Set<ScanGroup>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AlarmRule> AlarmRules => Set<AlarmRule>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();
    public DbSet<Screen> Screens => Set<Screen>();
    public DbSet<ScreenVersion> ScreenVersions => Set<ScreenVersion>();
    public DbSet<ScreenRole> ScreenRoles => Set<ScreenRole>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ConfirmToken> ConfirmTokens => Set<ConfirmToken>();
    public DbSet<WriteBatch> WriteBatches => Set<WriteBatch>();
    public DbSet<WriteBatchItem> WriteBatchItems => Set<WriteBatchItem>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportStaging> ImportStagings => Set<ImportStaging>();
    public DbSet<TagLatest> TagLatests => Set<TagLatest>();
    public DbSet<TagRuntimeState> TagRuntimeStates => Set<TagRuntimeState>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Endpoint>(e =>
        {
            e.ToTable("endpoint");
            e.HasIndex(x => x.Code).IsUnique().HasFilter("NOT \"IsDeleted\"");
            e.HasIndex(x => new { x.Ip, x.Port }).HasFilter("NOT \"IsDeleted\"");
            e.Property(x => x.ConnParams).HasColumnType("text");
            e.HasMany(x => x.Devices).WithOne(d => d.Endpoint!).HasForeignKey(d => d.EndpointId);
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("device");
            e.HasIndex(x => new { x.EndpointId, x.Code }).IsUnique().HasFilter("NOT \"IsDeleted\"");
            e.HasIndex(x => new { x.EndpointId, x.DeviceKey }).HasFilter("NOT \"IsDeleted\"");
        });

        b.Entity<ScanGroup>(e =>
        {
            e.ToTable("scan_group");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Tag>(e =>
        {
            e.ToTable("tag");
            e.HasIndex(x => new { x.DeviceId, x.Code }).IsUnique().HasFilter("NOT \"IsDeleted\"");
            e.HasIndex(x => new { x.DeviceId, x.AddressKey }).HasFilter("NOT \"IsDeleted\"");
            e.HasIndex(x => x.ScanGroupId);
            e.HasOne(x => x.Device).WithMany(d => d.Tags).HasForeignKey(x => x.DeviceId);
            e.HasOne(x => x.ScanGroup).WithMany().HasForeignKey(x => x.ScanGroupId);
        });

        b.Entity<AlarmRule>(e =>
        {
            e.ToTable("alarm_rule");
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId);
            e.HasIndex(x => x.TagId);
            e.HasIndex(x => x.EndpointId);
            e.Property(x => x.Kind).HasConversion<int>();
        });

        b.Entity<AlarmEvent>(e =>
        {
            e.ToTable("alarm_event");
            e.HasIndex(x => new { x.TagId, x.StartedAt });
            e.HasIndex(x => x.State);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.State).HasConversion<int>();
        });

        b.Entity<Screen>(e =>
        {
            e.ToTable("screen");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.DraftJson).HasColumnType("text");
        });

        b.Entity<ScreenVersion>(e =>
        {
            e.ToTable("screen_version");
            e.HasIndex(x => new { x.ScreenId, x.VersionNo }).IsUnique();
            e.Property(x => x.Json).HasColumnType("text");
        });

        b.Entity<ScreenRole>(e =>
        {
            e.ToTable("screen_role");
            e.HasIndex(x => new { x.ScreenId, x.RoleCode }).IsUnique();
        });

        b.Entity<AppUser>(e =>
        {
            e.ToTable("app_user");
            e.HasIndex(x => x.Username).IsUnique();
        });

        // 审计日志按月分区，由 SQL 脚本建表，EF 只负责读写
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Ts);
        });

        b.Entity<ConfirmToken>(e =>
        {
            e.ToTable("confirm_token");
            e.HasKey(x => x.Token);
        });

        b.Entity<WriteBatch>(e =>
        {
            e.ToTable("write_batch");
            e.HasKey(x => x.BatchId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.BatchId);
        });

        b.Entity<WriteBatchItem>(e =>
        {
            e.ToTable("write_batch_item");
            e.HasKey(x => x.Id);
        });

        b.Entity<ImportJob>(e =>
        {
            e.ToTable("import_job");
            e.HasMany(x => x.Rows).WithOne().HasForeignKey(r => r.JobId);
        });

        b.Entity<ImportStaging>(e =>
        {
            e.ToTable("import_staging");
            e.HasKey(x => x.Id);
            e.Property(x => x.PayloadJson).HasColumnType("text");
        });

        b.Entity<TagLatest>(e =>
        {
            e.ToTable("tag_latest");
            e.HasKey(x => x.TagId);
            e.Property(x => x.Quality).HasConversion<int>();
        });

        b.Entity<TagRuntimeState>(e =>
        {
            e.ToTable("tag_runtime_state");
            e.HasKey(x => x.TagId);
            e.Property(x => x.LastQuality).HasConversion<int>();
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_setting");
            e.HasKey(x => x.Key);
        });
    }
}

/// <summary>设计时工厂，供 dotnet ef migrations 使用（不启动 Web 主机）。</summary>
public sealed class DesignTimeDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<SfmsDbContext>
{
    public SfmsDbContext CreateDbContext(string[] args)
    {
        string cs = Environment.GetEnvironmentVariable("SFMS_CONN")
                      ?? "Host=127.0.0.1;Port=5433;Database=sfms;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<SfmsDbContext>()
            .UseNpgsql(cs)
            .Options;
        return new SfmsDbContext(options);
    }
}
