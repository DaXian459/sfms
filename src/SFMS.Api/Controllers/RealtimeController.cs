using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SFMS.Collector;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Realtime;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

/// <summary>实时值快照（WebSocket 之外的 HTTP 兜底通道，计划书 3.5.3）。</summary>
[ApiController]
[Route("api/realtime")]
[Authorize]
public sealed class RealtimeController(
    IRealtimeStore store,
    CollectorRuntime runtime,
    IDbContextFactory<SfmsDbContext> factory) : ControllerBase
{
    /// <summary>按点位 ID 列表取快照；tagIds 为空时按 deviceId / endpointId / 全量返回。</summary>
    [HttpGet("snapshot")]
    public async Task<ActionResult<RealtimeSnapshot>> Snapshot(
        [FromQuery] string? tagIds, [FromQuery] int? deviceId, [FromQuery] int? endpointId,
        [FromQuery] bool onlyEnabled = true, CancellationToken ct = default)
    {
        var ids = ParseIds(tagIds);
        if (ids.Count == 0 && (deviceId is not null || endpointId is not null))
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.Tags.AsNoTracking().Where(t => !t.IsDeleted);
            if (deviceId is not null) query = query.Where(t => t.DeviceId == deviceId);
            if (endpointId is not null)
                query = query.Where(t => db.Devices.Any(d => d.Id == t.DeviceId && d.EndpointId == endpointId && !d.IsDeleted));
            if (onlyEnabled) query = query.Where(t => t.Enabled);
            ids = await query.Select(t => t.Id).ToListAsync(ct);
        }

        var items = ids.Count == 0 ? store.All().ToList() : store.GetMany(ids).ToList();
        return Ok(new RealtimeSnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, items));
    }

    /// <summary>单个点位最新值，供详情页轮询。</summary>
    [HttpGet("item/{tagId:int}")]
    public ActionResult<RealtimeItem> Item(int tagId)
    {
        if (!store.TryGet(tagId, out var item))
            return NotFound(ApiResult<object>.Fail($"点位 {tagId} 尚无实时值"));
        return Ok(item);
    }

    /// <summary>实时域版本号：前端可作为增量轮询的判据。</summary>
    [HttpGet("version")]
    public ActionResult<object> Version() => Ok(new
    {
        version = store.Version,
        count = store.Count,
        serverTime = DateTimeOffset.UtcNow,
        sessions = runtime.SessionCount,
        tags = runtime.TagCount,
        tickJitterMs = Math.Round(runtime.TickJitterMs, 2),
        queues = runtime.QueueDepths
    });

    /// <summary>在线率统计（端点 / 设备 / 点位三级的 Good 占比）。</summary>
    [HttpGet("health")]
    public async Task<ActionResult<object>> Health(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tags = await db.Tags.AsNoTracking().Where(t => !t.IsDeleted && t.Enabled)
            .Select(t => new { t.Id, t.DeviceId }).ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().Where(d => !d.IsDeleted)
            .Select(d => new { d.Id, d.EndpointId, d.Code, d.Name }).ToListAsync(ct);

        var all = store.All().ToDictionary(x => x.TagId);
        int good = 0, uncertain = 0, bad = 0, timeout = 0, missing = 0;
        foreach (var t in tags)
        {
            if (!all.TryGetValue(t.Id, out var item)) { missing++; continue; }
            switch ((Quality)item.Quality)
            {
                case Quality.Good: good++; break;
                case Quality.Uncertain: uncertain++; break;
                case Quality.Timeout: timeout++; break;
                default: bad++; break;
            }
        }

        var perDevice = devices.Select(d =>
        {
            var ids = tags.Where(t => t.DeviceId == d.Id).Select(t => t.Id).ToList();
            int ok = ids.Count(id => all.TryGetValue(id, out var i) && i.Quality == (int)Quality.Good);
            return new
            {
                d.Id, d.Code, d.Name, d.EndpointId,
                tags = ids.Count, good = ok,
                rate = ids.Count == 0 ? 0 : Math.Round(ok * 100.0 / ids.Count, 1)
            };
        }).OrderBy(x => x.rate).ToList();

        return Ok(new
        {
            total = tags.Count, good, uncertain, bad, timeout, missing,
            goodRate = tags.Count == 0 ? 0 : Math.Round(good * 100.0 / tags.Count, 1),
            devices = perDevice
        });
    }

    private static List<int> ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<int>();
        return csv.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v > 0)
            .Distinct()
            .Take(2000)
            .ToList();
    }
}
