using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;
using SFMS.Domain;
using SFMS.Infrastructure.Auth;
using SFMS.Shared;

namespace SFMS.Infrastructure.Data;

/// <summary>审计：配置变更、权限变更、写入、登录登出四类事件（计划书 3.8）。</summary>
public sealed class AuditService(IDbContextFactory<SfmsDbContext> factory, ILogger<AuditService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task WriteAsync(string action, string? targetType, string? targetId,
        object? before, object? after, string result = "Success", string? reason = null,
        int? userId = null, string? username = null, string? ip = null, string? traceId = null)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
            db.AuditLogs.Add(new AuditLog
            {
                Ts = DateTimeOffset.UtcNow,
                UserId = userId,
                Username = username,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Before = before is null ? null : JsonSerializer.Serialize(before, Json),
                After = after is null ? null : JsonSerializer.Serialize(after, Json),
                Result = result,
                Reason = reason,
                Ip = ip,
                TraceId = traceId
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 审计失败不能阻断主流程，但要留痕
            logger.LogError(ex, "写入审计日志失败：{Action} {Target}", action, targetId);
        }
    }
}

/// <summary>系统参数（含配置版本号，用于采集服务热加载）。</summary>
public sealed class SettingsService(IDbContextFactory<SfmsDbContext> factory)
{
    public const string ConfigVersionKey = "config_version";

    public async Task<string?> GetAsync(string key)
    {
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var row = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key).ConfigureAwait(false);
        return row?.Value;
    }

    public async Task SetAsync(string key, string value)
    {
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var row = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key).ConfigureAwait(false);
        if (row is null)
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>配置变更后调用：版本号递增，采集服务在 1 秒内感知并重建计划。</summary>
    public async Task BumpConfigVersionAsync()
    {
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var row = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == ConfigVersionKey).ConfigureAwait(false);
        long next = 1;
        if (row is null)
            db.SystemSettings.Add(new SystemSetting { Key = ConfigVersionKey, Value = "1", UpdatedAt = DateTimeOffset.UtcNow });
        else
        {
            long.TryParse(row.Value, out long cur);
            next = cur + 1;
            row.Value = next.ToString();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}

/// <summary>启动引导：迁移配置表 → 建时序分区表 → 维护分区 → 首次种子数据。</summary>
public sealed class DbBootstrapper(
    SfmsDbContext db,
    NpgsqlDataSource dataSource,
    ILogger<DbBootstrapper> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 有迁移文件时走标准迁移；没有迁移时按当前模型生成 DDL（仅首次建表，幂等）
        if (db.Database.GetMigrations().Any())
        {
            await EnsureMigrationBaselineAsync(ct).ConfigureAwait(false);
            logger.LogInformation("应用数据库迁移…");
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // 用 EF 映射出的实际表名探测：本项目配置表为单数（tag / device / endpoint …），
            // 若写成 tags 会永远判定为"不存在"，导致重启时重复建表报 42P07。
            bool exists;
            await using (var check = dataSource.CreateCommand("SELECT to_regclass('public.tag') IS NOT NULL"))
                exists = (bool)(await check.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? false);

            if (!exists)
            {
                logger.LogInformation("未发现迁移文件，按当前模型创建业务表…");
                string ddl = db.Database.GenerateCreateScript();
                await using var cmd = dataSource.CreateCommand(ddl);
                cmd.CommandTimeout = 180;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        logger.LogInformation("创建时序/审计/诊断分区表…");
        string script = ReadEmbedded("sql.001_schema.sql");
        await using (var cmd = dataSource.CreateCommand(script))
        {
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = dataSource.CreateCommand("SELECT sfms_ensure_partitions(3)"))
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        await SeedData.EnsureSeededAsync(db, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 迁移基线：早期版本是用 GenerateCreateScript 直接建表的，库里已有业务表但没有
    /// __EFMigrationsHistory 记录。此时直接 Migrate 会重复建表报 42P07。
    /// 这里在"历史表为空 + 业务表已存在"时，把现有迁移全部登记为已应用（baseline），
    /// 后续新增的迁移仍会正常增量执行。
    /// </summary>
    private async Task EnsureMigrationBaselineAsync(CancellationToken ct)
    {
        var history = db.GetService<IHistoryRepository>();

        string createHistory = history.GetCreateIfNotExistsScript();
        await using (var cmd = dataSource.CreateCommand(createHistory))
        {
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var applied = await history.GetAppliedMigrationsAsync(ct).ConfigureAwait(false);
        if (applied.Any()) return;

        bool hasTables;
        await using (var check = dataSource.CreateCommand("SELECT to_regclass('public.tag') IS NOT NULL"))
            hasTables = (bool)(await check.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? false);
        if (!hasTables) return;

        var migrations = db.Database.GetMigrations().ToList();
        if (migrations.Count == 0) return;

        logger.LogWarning("检测到已存在的库结构但无迁移记录，登记迁移基线 {Count} 条", migrations.Count);
        foreach (string id in migrations)
        {
            string insert = history.GetInsertScript(new HistoryRow(id, ProductInfo.GetVersion()));
            await using var cmd = dataSource.CreateCommand(insert);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static string ReadEmbedded(string name)
    {
        var asm = typeof(DbBootstrapper).Assembly;
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少内嵌资源 {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
