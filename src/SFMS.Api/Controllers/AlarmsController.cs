using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SFMS.Collector;
using SFMS.Domain;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Auth;
using SFMS.Infrastructure.Data;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

public sealed record AckRequest(string? Note);

public sealed record AckBatchRequest(IReadOnlyList<long> Ids, string? Note);

public sealed record AlarmRuleInput(int? TagId, int? EndpointId, string Kind, double Threshold,
    double Hysteresis, int OnDelayMs, string Severity, bool Enabled);

/// <summary>报警查询、确认与规则管理（计划书 3.9 / FR-09）。</summary>
[ApiController]
[Route("api/alarms")]
[Authorize]
public sealed class AlarmsController(
    IDbContextFactory<SfmsDbContext> factory,
    AlarmEngine engine,
    SettingsService settings,
    AuditService audit) : ControllerBase
{
    /// <summary>事件列表：state=active|acked|cleared|all，since 用于增量拉取。</summary>
    [HttpGet]
    public async Task<ActionResult<object>> List([FromQuery] string? state = "active", [FromQuery] int? deviceId = null,
        [FromQuery] DateTimeOffset? since = null, [FromQuery] int page = 1, [FromQuery] int size = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 500);
        await using var db = await factory.CreateDbContextAsync(ct);

        var query = db.AlarmEvents.AsNoTracking().AsQueryable();
        string s = (state ?? "active").ToLowerInvariant();
        query = s switch
        {
            "active" => query.Where(e => e.State == AlarmState.Active),
            "acked" => query.Where(e => e.State == AlarmState.Acked),
            "cleared" => query.Where(e => e.State == AlarmState.Cleared),
            "uncleared" => query.Where(e => e.State != AlarmState.Cleared),
            _ => query
        };
        if (deviceId is not null) query = query.Where(e => e.DeviceId == deviceId);
        if (since is not null) query = query.Where(e => e.StartedAt >= since);

        int total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.StartedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);

        var tagIds = rows.Where(r => r.TagId is not null).Select(r => r.TagId!.Value).Distinct().ToList();
        var tags = await db.Tags.AsNoTracking().Where(t => tagIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code, t.Name, t.DeviceId }).ToListAsync(ct);
        var deviceIds = tags.Select(t => t.DeviceId).Distinct().ToList();
        var devices = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name }).ToListAsync(ct);

        var tagMap = tags.ToDictionary(t => t.Id);
        var devMap = devices.ToDictionary(d => d.Id, d => d.Name);

        var items = rows.Select(e =>
        {
            tagMap.TryGetValue(e.TagId ?? 0, out var t);
            int? devId = e.DeviceId ?? t?.DeviceId;
            return new AlarmEventDto(e.Id, e.RuleId, e.TagId, t?.Code, t?.Name, devId,
                devId is null ? null : devMap.GetValueOrDefault(devId.Value, $"设备#{devId}"),
                e.Kind.ToString(), e.Severity, e.State.ToString(), e.StartedAt, e.EndedAt,
                e.TriggerValue, e.AckBy, e.AckAt, e.AckNote, e.Message);
        }).ToList();

        var counts = await db.AlarmEvents.AsNoTracking()
            .GroupBy(e => e.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return Ok(new
        {
            total, page, size, items,
            activeCount = engine.ActiveCount,
            counts = counts.ToDictionary(c => c.State.ToString(), c => c.Count)
        });
    }

    /// <summary>确认报警：Active → Acked，记录确认人与备注。</summary>
    [HttpPost("{id:long}/ack")]
    [Authorize(Policy = "TagWrite")]
    public async Task<ActionResult<object>> Ack(long id, [FromBody] AckRequest? req, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ev = await db.AlarmEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null) return NotFound(ApiResult<object>.Fail("报警事件不存在"));
        if (ev.State == AlarmState.Cleared)
            return BadRequest(ApiResult<object>.Fail("该报警已恢复，无需确认"));

        ev.State = AlarmState.Acked;
        ev.AckBy = User.Username();
        ev.AckAt = DateTimeOffset.UtcNow;
        ev.AckNote = req?.Note;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("alarm.ack", "alarm", id.ToString(), new { state = "Active" },
            new { state = "Acked", note = req?.Note }, userId: User.UserId(), username: User.Username(),
            ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ev.Id, state = ev.State.ToString() }));
    }

    /// <summary>批量确认。</summary>
    [HttpPost("ack-batch")]
    [Authorize(Policy = "TagWrite")]
    public async Task<ActionResult<object>> AckBatch([FromBody] AckBatchRequest req, CancellationToken ct)
    {
        var ids = req?.Ids;
        if (ids is null || ids.Count == 0) return BadRequest(ApiResult<object>.Fail("未选择报警"));
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AlarmEvents.Where(e => ids.Contains(e.Id) && e.State == AlarmState.Active).ToListAsync(ct);
        foreach (var ev in rows)
        {
            ev.State = AlarmState.Acked;
            ev.AckBy = User.Username();
            ev.AckAt = DateTimeOffset.UtcNow;
            ev.AckNote = req?.Note;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("alarm.ack-batch", "alarm", string.Join(',', ids), null,
            new { count = rows.Count }, userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { acked = rows.Count }));
    }

    // ---------------- 规则管理
    [HttpGet("rules")]
    public async Task<ActionResult<IReadOnlyList<AlarmRuleDto>>> Rules(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rules = await db.AlarmRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        var tagIds = rules.Where(r => r.TagId is not null).Select(r => r.TagId!.Value).Distinct().ToList();
        var tags = await db.Tags.AsNoTracking().Where(t => tagIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code, t.Name }).ToListAsync(ct);
        var map = tags.ToDictionary(t => t.Id);
        return Ok(rules.Select(r =>
        {
            map.TryGetValue(r.TagId ?? 0, out var t);
            return new AlarmRuleDto(r.Id, r.TagId, r.EndpointId, r.Kind.ToString(), r.Threshold,
                r.Hysteresis, r.OnDelayMs, r.Severity, r.Enabled, t?.Code, t?.Name);
        }).ToList());
    }

    [HttpPost("rules")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> CreateRule([FromBody] AlarmRuleInput input, CancellationToken ct)
    {
        var err = Validate(input);
        if (err is not null) return BadRequest(ApiResult<object>.Fail(err));

        await using var db = await factory.CreateDbContextAsync(ct);
        var rule = new AlarmRule
        {
            TagId = input.TagId,
            EndpointId = input.EndpointId,
            Kind = ParseKind(input.Kind),
            Threshold = input.Threshold,
            Hysteresis = input.Hysteresis,
            OnDelayMs = input.OnDelayMs,
            Severity = string.IsNullOrWhiteSpace(input.Severity) ? "Warn" : input.Severity,
            Enabled = input.Enabled
        };
        db.AlarmRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("alarm-rule.create", "alarm-rule", rule.Id.ToString(), null, input,
            userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { rule.Id }));
    }

    [HttpPut("rules/{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> UpdateRule(int id, [FromBody] AlarmRuleInput input, CancellationToken ct)
    {
        var err = Validate(input);
        if (err is not null) return BadRequest(ApiResult<object>.Fail(err));

        await using var db = await factory.CreateDbContextAsync(ct);
        var rule = await db.AlarmRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();
        var before = new { rule.TagId, rule.EndpointId, rule.Kind, rule.Threshold, rule.Hysteresis, rule.OnDelayMs, rule.Severity, rule.Enabled };

        rule.TagId = input.TagId;
        rule.EndpointId = input.EndpointId;
        rule.Kind = ParseKind(input.Kind);
        rule.Threshold = input.Threshold;
        rule.Hysteresis = input.Hysteresis;
        rule.OnDelayMs = input.OnDelayMs;
        rule.Severity = string.IsNullOrWhiteSpace(input.Severity) ? "Warn" : input.Severity;
        rule.Enabled = input.Enabled;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("alarm-rule.update", "alarm-rule", id.ToString(), before, input,
            userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { rule.Id }));
    }

    [HttpDelete("rules/{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> DeleteRule(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rule = await db.AlarmRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();
        db.AlarmRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("alarm-rule.delete", "alarm-rule", id.ToString(),
            new { rule.TagId, rule.Kind, rule.Threshold }, null,
            userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ok = true }));
    }

    private static string? Validate(AlarmRuleInput input)
    {
        if (input.TagId is null && input.EndpointId is null) return "必须指定点位或端点";
        if (input.OnDelayMs < 0 || input.OnDelayMs > 3_600_000) return "延时需在 0~3600 秒之间";
        if (input.Hysteresis < 0) return "回差不能为负";
        return null;
    }

    private static AlarmKind ParseKind(string? text) =>
        Enum.TryParse<AlarmKind>(text, true, out var k) ? k : AlarmKind.Hi;
}
