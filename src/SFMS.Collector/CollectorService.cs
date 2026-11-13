using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SFMS.Domain;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Data;
using SFMS.Infrastructure.Realtime;
using SFMS.Shared;

namespace SFMS.Collector;

/// <summary>
/// 采集服务宿主：50ms 时间轮推进、配置热加载、最新值持久化、诊断落库、
/// 1 分钟聚合与分区维护（对应计划书 3.4 与 3.5）。
/// </summary>
public sealed class CollectorService(
    CollectorRuntime runtime,
    IDbContextFactory<SfmsDbContext> factory,
    NpgsqlDataSource dataSource,
    HistoryRepository history,
    IRealtimeStore store,
    ILogger<CollectorService> logger) : BackgroundService
{
    /// <summary>时间轮节拍（ms），计划书 3.4 要求最小采集周期 50ms。</summary>
    private const int TickIntervalMs = 50;

    /// <summary>上次已落库的 (值, 文本, 质量)，用于跳过未变化的点位。</summary>
    private readonly Dictionary<int, (double? Value, string? Text, int Quality)> _persisted = new();

    private DateTimeOffset _lastLatestPersist = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastDiagPersist = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastRollup = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPartitionCheck = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(300, stoppingToken).ConfigureAwait(false);

        await RestoreLatestAsync(stoppingToken).ConfigureAwait(false);
        await runtime.ReloadAsync(true, stoppingToken).ConfigureAwait(false);

        var alarmTask = Task.Run(() => runtime.Alarms.RunAsync(stoppingToken), stoppingToken);
        var historyTask = Task.Run(() => runtime.History.RunAsync(stoppingToken), stoppingToken);
        var maintTask = Task.Run(() => MaintenanceLoopAsync(stoppingToken), stoppingToken);

        logger.LogInformation("采集服务已启动：{Sessions} 个端点会话", runtime.SessionCount);

        // 时间轮主循环：只做内存内的到期推进，不碰数据库。
        // 采用"绝对时刻"补偿——本次已耗时从等待时间里扣除，避免固定 Delay(50) 导致
        // 实际周期 = 50ms + 处理耗时（原实现的抖动正是由此产生）。
        TimerResolution.Raise();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var loopStart = DateTimeOffset.UtcNow;
                    runtime.Tick();
                    int spent = (int)(DateTimeOffset.UtcNow - loopStart).TotalMilliseconds;
                    await Task.Delay(Math.Max(1, TickIntervalMs - spent), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "采集主循环异常");
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            TimerResolution.Restore();
        }

        try { await Task.WhenAll(alarmTask, historyTask, maintTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 维护循环：配置热加载、最新值持久化、诊断落库、分钟聚合、分区维护与过期清理。
    /// 全部是有 DB 往返的慢操作，单独线程运行，避免阻塞 50ms 采集节拍。
    /// </summary>
    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        var lastConfigCheck = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                if (now - lastConfigCheck > TimeSpan.FromSeconds(2))
                {
                    lastConfigCheck = now;
                    await runtime.ReloadAsync(false, ct).ConfigureAwait(false);
                }

                if (now - _lastLatestPersist > TimeSpan.FromSeconds(10))
                {
                    _lastLatestPersist = now;
                    await PersistLatestAsync(ct).ConfigureAwait(false);
                }

                if (now - _lastDiagPersist > TimeSpan.FromMinutes(1))
                {
                    _lastDiagPersist = now;
                    await PersistDiagAsync(now, ct).ConfigureAwait(false);
                }

                if (now - _lastRollup > TimeSpan.FromMinutes(1))
                {
                    _lastRollup = now;
                    await RollupAsync(now, ct).ConfigureAwait(false);
                }

                if (now - _lastPartitionCheck > TimeSpan.FromHours(1))
                {
                    _lastPartitionCheck = now;
                    await history.EnsurePartitionsAsync(3, ct).ConfigureAwait(false);
                }

                if (now - _lastPurge > TimeSpan.FromDays(1))
                {
                    _lastPurge = now;
                    int dropped = await history.PurgeOldAsync(3, 12, ct).ConfigureAwait(false);
                    if (dropped > 0) logger.LogInformation("已清理 {Count} 个过期分区", dropped);
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "维护循环异常");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RestoreLatestAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rows = await db.TagLatests.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            store.Load(rows);
            // 预置"已落库"快照，避免启动后第一轮把全部未变化点位重写一遍
            foreach (var r in rows)
                _persisted[r.TagId] = (r.Value, r.Text, (int)r.Quality);
            if (rows.Count > 0) logger.LogInformation("已恢复 {Count} 个点位的最新值", rows.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "恢复最新值失败（首次启动或数据库未就绪）");
        }
    }

    /// <summary>
    /// 最新值落库：只写"值/文本/质量发生变化"的点位，且合并为单条 unnest 批量 upsert。
    /// 原实现每 10s 把全部点位做一次 EF 逐行 UPDATE，稳定工况下几乎全是无效写入。
    /// </summary>
    private async Task PersistLatestAsync(CancellationToken ct)
    {
        try
        {
            var items = store.All();
            if (items.Count == 0) return;

            var changed = new List<RealtimeItem>(items.Count);
            foreach (var item in items)
            {
                var key = (item.Value, item.Text, item.Quality);
                if (_persisted.TryGetValue(item.TagId, out var prev) && prev == key) continue;
                changed.Add(item);
            }
            if (changed.Count == 0) return;

            var ids = new int[changed.Count];
            var values = new double?[changed.Count];
            var texts = new string?[changed.Count];
            var quals = new int[changed.Count];
            var stamps = new DateTimeOffset[changed.Count];
            for (int i = 0; i < changed.Count; i++)
            {
                var it = changed[i];
                ids[i] = it.TagId;
                values[i] = it.Value;
                texts[i] = it.Text;
                quals[i] = it.Quality;
                stamps[i] = it.Ts;
            }

            const string sql = """
                INSERT INTO tag_latest ("TagId", "Value", "Text", "Quality", "Ts", "Source")
                SELECT u.id, u.val, u.txt, u.q, u.ts, 'scan'
                FROM unnest($1::int[], $2::float8[], $3::text[], $4::int[], $5::timestamptz[])
                     AS u(id, val, txt, q, ts)
                ON CONFLICT ("TagId") DO UPDATE
                   SET "Value" = EXCLUDED."Value",
                       "Text" = EXCLUDED."Text",
                       "Quality" = EXCLUDED."Quality",
                       "Ts" = EXCLUDED."Ts",
                       "Source" = EXCLUDED."Source"
                """;

            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = ids });
            cmd.Parameters.Add(new NpgsqlParameter { Value = values });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)texts ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter { Value = quals });
            cmd.Parameters.Add(new NpgsqlParameter { Value = stamps });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var it in changed)
                _persisted[it.TagId] = (it.Value, it.Text, it.Quality);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "最新值持久化失败");
        }
    }

    private async Task PersistDiagAsync(DateTimeOffset now, CancellationToken ct)
    {
        var bucket = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        foreach (var d in runtime.Diagnostics())
        {
            try
            {
                string? codes = null;
                if (runtime.SessionOfEndpoint(d.EndpointId) is { } s && s.Diag.ErrorCodes.Count > 0)
                    codes = string.Join(",", s.Diag.ErrorCodes.Select(kv => $"{kv.Key}={kv.Value}"));

                await history.WriteDiagMinuteAsync(bucket, d.EndpointId, d.Requests, d.Success,
                    d.Timeouts, d.Errors, d.AvgMs, d.P95Ms, codes, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "诊断落库失败：{Endpoint}", d.Code);
            }
        }
    }

    private async Task RollupAsync(DateTimeOffset now, CancellationToken ct)
    {
        // 聚合上一分钟（留 1 分钟缓冲，避免边界数据未落盘）
        var bucket = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero)
            .AddMinutes(-2);
        try
        {
            int rows = await history.RollupMinuteAsync(bucket, ct).ConfigureAwait(false);
            if (rows > 0) logger.LogDebug("聚合 {Bucket:HH:mm}：{Rows} 个点位", bucket, rows);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "1 分钟聚合失败");
        }
    }
}
