using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SFMS.Collector;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Data;
using SFMS.Infrastructure.Realtime;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

/// <summary>诊断：端点通信指标、点位在线调试、系统指标与审计查询（计划书 3.11 / FR-10）。</summary>
[ApiController]
[Route("api/diagnostics")]
[Authorize]
public sealed class DiagnosticsController(
    CollectorRuntime runtime,
    IRealtimeStore store,
    IDbContextFactory<SfmsDbContext> factory,
    HistoryRepository history) : ControllerBase
{
    /// <summary>端点通信诊断（请求数、成功率、P95 时延、连续失败、最后一次错误）。</summary>
    [HttpGet("endpoints")]
    public ActionResult<IReadOnlyList<DiagEndpointDto>> Endpoints() => Ok(runtime.Diagnostics());

    /// <summary>在线调试读：绕过采集周期直读一次，并给出四种字节序的解析结果。</summary>
    [HttpPost("debug-read/{tagId:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<DebugReadResult>> DebugRead(int tagId, CancellationToken ct)
    {
        var result = await runtime.DebugReadAsync(tagId, ct);
        return Ok(result);
    }

    /// <summary>采集任务队列深度（端点 → 待处理作业数），用于判断是否过载。</summary>
    [HttpGet("queues")]
    public ActionResult<object> Queues() => Ok(new
    {
        tickJitterMs = Math.Round(runtime.TickJitterMs, 2),
        sessions = runtime.SessionCount,
        tags = runtime.TagCount,
        queues = runtime.QueueDepths
    });

    /// <summary>系统级指标（卡片/折线用）。</summary>
    [HttpGet("metrics")]
    public async Task<ActionResult<IReadOnlyList<MetricDto>>> Metrics(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);

        int tagTotal = await db.Tags.CountAsync(t => !t.IsDeleted && t.Enabled, ct);
        int epTotal = await db.Endpoints.CountAsync(e => !e.IsDeleted && e.Enabled, ct);
        // 状态取运行时：endpoint.status 列只是配置态默认值，采集并不会回写它
        int epOnline = Math.Min(runtime.OnlineEndpointCount, epTotal);
        int alarmActive = runtime.Alarms.ActiveCount;

        var items = store.All();
        // 与点位总数取小：配置刚变更时实时存储可能还残留已删除点位，避免良好率超过 100%
        int good = Math.Min(items.Count(i => i.Quality == (int)Quality.Good), tagTotal);

        var list = new List<MetricDto>
        {
            new("points.total", tagTotal, "个", now),
            new("points.good", good, "个", now),
            new("points.goodRate", tagTotal == 0 ? 0 : Math.Round(good * 100.0 / tagTotal, 1), "%", now),
            new("endpoint.total", epTotal, "个", now),
            new("endpoint.online", epOnline, "个", now),
            new("endpoint.onlineRate", epTotal == 0 ? 0 : Math.Round(epOnline * 100.0 / epTotal, 1), "%", now),
            new("alarm.active", alarmActive, "条", now),
            new("collector.jitterMs", Math.Round(runtime.TickJitterMs, 2), "ms", now),
            new("collector.queued", runtime.QueueDepths.Values.Sum(), "个", now),
            new("realtime.version", store.Version, null, now)
        };
        return Ok(list);
    }

    /// <summary>审计日志查询（按动作/用户/时间过滤）。</summary>
    [HttpGet("audit")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<PagedResult<AuditDto>>> Audit([FromQuery] string? action = null,
        [FromQuery] string? username = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1, [FromQuery] int size = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 500);
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.Contains(action));
        if (!string.IsNullOrWhiteSpace(username)) q = q.Where(a => a.Username == username);
        if (from is not null) q = q.Where(a => a.Ts >= from);
        if (to is not null) q = q.Where(a => a.Ts < to);

        int total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.Ts).Skip((page - 1) * size).Take(size)
            .Select(a => new AuditDto(a.Id, a.Ts, a.Username, a.Action, a.TargetType, a.TargetId,
                a.Before, a.After, a.Result, a.Reason, a.Ip))
            .ToListAsync(ct);
        return Ok(new PagedResult<AuditDto>(rows, total, page, size));
    }

    /// <summary>手工维护时序分区（管理员；正常由后台服务每小时自动执行）。</summary>
    [HttpPost("maintenance")]
    [Authorize(Policy = "UserAdmin")]
    public async Task<ActionResult<object>> Maintenance(CancellationToken ct)
    {
        int created = await history.EnsurePartitionsAsync(6, ct);
        int rolled = await history.RollupMinuteAsync(DateTimeOffset.UtcNow.AddMinutes(-2), ct);
        return Ok(new { partitionsCreated = created, rolledUpRows = rolled });
    }
}
