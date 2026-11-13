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

public sealed record EndpointInput(string Code, string Name, string Protocol, string Ip, int Port,
    string? ConnParams, bool ScanEnabled, bool Enabled, string? Remark);

public sealed record DeviceInput(int EndpointId, string Code, string Name, string DeviceKey,
    string? Model, string? Workshop, string? Area, bool Enabled);

public sealed record ScanGroupInput(string Name, int PeriodMs, int Priority, int MergeGap);

public sealed record TagInput(int DeviceId, string Code, string Name, string AddressExpr, string DataType,
    string ByteOrder, bool Writable, double? MinValue, double? MaxValue, double Scale, double Offset,
    string? Unit, int ScanGroupId, double Deadband, bool Enabled, int StrLen);

internal static class ApiHelpers
{
    public static string Ip(this HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "-";
}

[ApiController]
[Route("api/endpoints")]
[Authorize]
public sealed class EndpointsController(
    IDbContextFactory<SfmsDbContext> factory,
    SettingsService settings,
    AuditService audit,
    CollectorRuntime runtime) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EndpointDto>>> List(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var endpoints = await db.Endpoints.AsNoTracking().Where(e => !e.IsDeleted).OrderBy(e => e.Code).ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().Where(d => !d.IsDeleted).ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().Where(t => !t.IsDeleted).ToListAsync(ct);
        var diag = runtime.Diagnostics().ToDictionary(d => d.EndpointId);
        var result = endpoints.Select(e =>
        {
            var devIds = devices.Where(d => d.EndpointId == e.Id).Select(d => d.Id).ToHashSet();
            // 运行态以采集诊断为准，DB 中的 Status 仅作兜底
            diag.TryGetValue(e.Id, out var d);
            return new EndpointDto(e.Id, e.Code, e.Name, e.Protocol.ToText(), e.Ip, e.Port, e.ScanEnabled,
                d?.Status ?? e.Status.ToString(), d?.LastOkAt ?? e.LastOkAt, e.Remark, e.ConnParams,
                devIds.Count, tags.Count(t => devIds.Contains(t.DeviceId)), e.Enabled);
        }).ToList();
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<EndpointDto>> Get(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (e is null) return NotFound();
        int devices = await db.Devices.CountAsync(d => d.EndpointId == id && !d.IsDeleted, ct);
        var devIds = await db.Devices.Where(d => d.EndpointId == id && !d.IsDeleted).Select(d => d.Id).ToListAsync(ct);
        int tags = await db.Tags.CountAsync(t => devIds.Contains(t.DeviceId) && !t.IsDeleted, ct);
        return Ok(new EndpointDto(e.Id, e.Code, e.Name, e.Protocol.ToText(), e.Ip, e.Port, e.ScanEnabled,
            e.Status.ToString(), e.LastOkAt, e.Remark, e.ConnParams, devices, tags, e.Enabled));
    }

    [HttpPost]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<EndpointDto>> Create([FromBody] EndpointInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Endpoints.AnyAsync(e => e.Code == input.Code && !e.IsDeleted, ct))
            return BadRequest(ApiResult<object>.Fail($"端点编码 {input.Code} 已存在"));

        var ep = new SFMS.Domain.Endpoint
        {
            Code = input.Code,
            Name = input.Name,
            Protocol = ProtocolNames.Parse(input.Protocol),
            Ip = input.Ip,
            Port = input.Port <= 0 ? ProtocolNames.Parse(input.Protocol).DefaultPort() : input.Port,
            ConnParams = string.IsNullOrWhiteSpace(input.ConnParams) ? "{}" : input.ConnParams!,
            ScanEnabled = input.ScanEnabled,
            Enabled = input.Enabled,
            Remark = input.Remark
        };
        db.Endpoints.Add(ep);
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("endpoint.create", "endpoint", ep.Id.ToString(), null,
            new { ep.Code, ep.Name, ep.Ip, ep.Port }, username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ep.Id }));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Update(int id, [FromBody] EndpointInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ep = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, ct);
        if (ep is null) return NotFound();
        var before = new { ep.Code, ep.Name, ep.Ip, ep.Port, Protocol = ep.Protocol.ToText() };
        ep.Code = input.Code;
        ep.Name = input.Name;
        ep.Protocol = ProtocolNames.Parse(input.Protocol);
        ep.Ip = input.Ip;
        ep.Port = input.Port;
        ep.ConnParams = string.IsNullOrWhiteSpace(input.ConnParams) ? "{}" : input.ConnParams!;
        ep.ScanEnabled = input.ScanEnabled;
        ep.Enabled = input.Enabled;
        ep.Remark = input.Remark;
        ep.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("endpoint.update", "endpoint", id.ToString(), before,
            new { ep.Code, ep.Name, ep.Ip, ep.Port }, username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ep.Id }));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Delete(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ep = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, ct);
        if (ep is null) return NotFound();
        ep.IsDeleted = true;
        ep.Enabled = false;
        var devs = await db.Devices.Where(d => d.EndpointId == id && !d.IsDeleted).ToListAsync(ct);
        foreach (var d in devs) d.IsDeleted = true;
        var devIds = devs.Select(d => d.Id).ToHashSet();
        var tags = await db.Tags.Where(t => devIds.Contains(t.DeviceId) && !t.IsDeleted).ToListAsync(ct);
        foreach (var t in tags) t.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("endpoint.delete", "endpoint", id.ToString(), new { ep.Code, ep.Name }, null,
            username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { deleted = true }));
    }

    /// <summary>连接测试（FR-01）：独立建连，不影响采集会话。</summary>
    [HttpPost("{id:int}/test")]
    public async Task<ActionResult<object>> Test(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ep = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted, ct);
        if (ep is null) return NotFound();
        var (ok, message, pdu) = await runtime.TestConnectionAsync(ep, ct);
        await audit.WriteAsync("endpoint.test", "endpoint", id.ToString(), null, new { ok, message },
            ok ? "Success" : "Failed", message, User.UserId(), User.Username(), HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ok, message, maxPdu = pdu }));
    }
}

[ApiController]
[Route("api/devices")]
[Authorize]
public sealed class DevicesController(
    IDbContextFactory<SfmsDbContext> factory,
    SettingsService settings,
    AuditService audit,
    CollectorRuntime runtime) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeviceDto>>> List([FromQuery] int? endpointId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Devices.AsNoTracking().Where(d => !d.IsDeleted);
        if (endpointId is { } eid) query = query.Where(d => d.EndpointId == eid);
        var devices = await query.OrderBy(d => d.Code).ToListAsync(ct);
        var ids = devices.Select(d => d.Id).ToHashSet();
        var tags = await db.Tags.AsNoTracking().Where(t => ids.Contains(t.DeviceId) && !t.IsDeleted).ToListAsync(ct);

        var result = devices.Select(d =>
        {
            var myTags = tags.Where(t => t.DeviceId == d.Id).ToList();
            int online = 0, bad = 0;
            foreach (var t in myTags)
            {
                if (runtime.Store.TryGet(t.Id, out var item) && item.Quality == (int)Quality.Good) online++;
                else bad++;
            }
            return new DeviceDto(d.Id, d.EndpointId, d.Code, d.Name, d.DeviceKey, d.Model, d.Workshop, d.Area,
                d.Enabled, myTags.Count, online, bad, d.Paused);
        }).ToList();
        return Ok(result);
    }

    [HttpPost]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Create([FromBody] DeviceInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Devices.AnyAsync(d => d.EndpointId == input.EndpointId && d.Code == input.Code && !d.IsDeleted, ct))
            return BadRequest(ApiResult<object>.Fail($"设备编码 {input.Code} 在该端点内已存在"));
        if (await db.Devices.AnyAsync(d => d.EndpointId == input.EndpointId && d.DeviceKey == input.DeviceKey && !d.IsDeleted, ct))
            return BadRequest(ApiResult<object>.Fail($"设备键 {input.DeviceKey} 在该端点内已存在（同 IP 多设备必须以从站地址/机架槽号区分）"));

        var dev = new Device
        {
            EndpointId = input.EndpointId,
            Code = input.Code,
            Name = input.Name,
            DeviceKey = input.DeviceKey,
            Model = input.Model,
            Workshop = input.Workshop,
            Area = input.Area,
            Enabled = input.Enabled
        };
        db.Devices.Add(dev);
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("device.create", "device", dev.Id.ToString(), null,
            new { dev.Code, dev.Name, dev.DeviceKey }, username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { dev.Id }));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Update(int id, [FromBody] DeviceInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var dev = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, ct);
        if (dev is null) return NotFound();
        var before = new { dev.Code, dev.Name, dev.DeviceKey, dev.Enabled };
        dev.Code = input.Code;
        dev.Name = input.Name;
        dev.DeviceKey = input.DeviceKey;
        dev.Model = input.Model;
        dev.Workshop = input.Workshop;
        dev.Area = input.Area;
        dev.Enabled = input.Enabled;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("device.update", "device", id.ToString(), before,
            new { dev.Code, dev.Name, dev.DeviceKey, dev.Enabled }, username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { dev.Id }));
    }

    /// <summary>单设备暂停（FR-05）：点位保持最后值，质量置 Uncertain。</summary>
    [HttpPost("{id:int}/pause")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Pause(int id, [FromQuery] bool paused, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var dev = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, ct);
        if (dev is null) return NotFound();
        dev.Paused = paused;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync(paused ? "device.pause" : "device.resume", "device", id.ToString(),
            null, new { paused }, username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { dev.Id, paused }));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Delete(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var dev = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, ct);
        if (dev is null) return NotFound();
        dev.IsDeleted = true;
        foreach (var t in await db.Tags.Where(t => t.DeviceId == id && !t.IsDeleted).ToListAsync(ct))
            t.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("device.delete", "device", id.ToString(), new { dev.Code, dev.Name }, null,
            username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { deleted = true }));
    }
}

[ApiController]
[Route("api/scan-groups")]
[Authorize]
public sealed class ScanGroupsController(
    IDbContextFactory<SfmsDbContext> factory,
    SettingsService settings,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScanGroupDto>>> List(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var groups = await db.ScanGroups.AsNoTracking().OrderBy(g => g.PeriodMs).ToListAsync(ct);
        var counts = await db.Tags.Where(t => !t.IsDeleted).GroupBy(t => t.ScanGroupId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return Ok(groups.Select(g => new ScanGroupDto(g.Id, g.Name, g.PeriodMs, g.Priority, g.MergeGap,
            counts.GetValueOrDefault(g.Id))).ToList());
    }

    [HttpPost]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Create([FromBody] ScanGroupInput input, CancellationToken ct)
    {
        var issues = TagValidator.ValidateScanGroup(input.PeriodMs, input.MergeGap);
        if (issues.Any(i => i.IsError))
            return BadRequest(ApiResult<object>.Fail(string.Join("；", issues.Select(i => i.Message))));

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.ScanGroups.AnyAsync(g => g.Name == input.Name, ct))
            return BadRequest(ApiResult<object>.Fail($"分组 {input.Name} 已存在"));
        var group = new ScanGroup { Name = input.Name, PeriodMs = input.PeriodMs, Priority = input.Priority, MergeGap = input.MergeGap };
        db.ScanGroups.Add(group);
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("scangroup.create", "scan_group", group.Id.ToString(), null, input,
            username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { group.Id }));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Update(int id, [FromBody] ScanGroupInput input, CancellationToken ct)
    {
        var issues = TagValidator.ValidateScanGroup(input.PeriodMs, input.MergeGap);
        if (issues.Any(i => i.IsError))
            return BadRequest(ApiResult<object>.Fail(string.Join("；", issues.Select(i => i.Message))));

        await using var db = await factory.CreateDbContextAsync(ct);
        var group = await db.ScanGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();
        var before = new { group.Name, group.PeriodMs, group.MergeGap };
        group.Name = input.Name;
        group.PeriodMs = input.PeriodMs;
        group.Priority = input.Priority;
        group.MergeGap = input.MergeGap;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("scangroup.update", "scan_group", id.ToString(), before, input,
            username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { group.Id }));
    }
}

[ApiController]
[Route("api/tags")]
[Authorize]
public sealed class TagsController(
    IDbContextFactory<SfmsDbContext> factory,
    SettingsService settings,
    AuditService audit,
    CollectorRuntime runtime) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<TagDto>>> List([FromQuery] int? deviceId, [FromQuery] string? q,
        [FromQuery] string? ids, [FromQuery] int page = 1, [FromQuery] int size = 200, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = from t in db.Tags.AsNoTracking()
                    join d in db.Devices.AsNoTracking() on t.DeviceId equals d.Id
                    join g in db.ScanGroups.AsNoTracking() on t.ScanGroupId equals g.Id
                    where !t.IsDeleted && !d.IsDeleted
                    select new { t, d, g };
        if (deviceId is { } did) query = query.Where(x => x.t.DeviceId == did);
        if (!string.IsNullOrWhiteSpace(ids))
        {
            var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).Distinct().Take(2000).ToList();
            if (idList.Count > 0) query = query.Where(x => idList.Contains(x.t.Id));
        }
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(x => x.t.Code.Contains(q) || x.t.Name.Contains(q) || x.t.AddressExpr.Contains(q));

        int total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.d.Code).ThenBy(x => x.t.Code)
            .Skip((Math.Max(1, page) - 1) * Math.Clamp(size, 1, 2000)).Take(Math.Clamp(size, 1, 2000))
            .ToListAsync(ct);

        var result = rows.Select(x =>
        {
            var item = runtime.Store.TryGet(x.t.Id, out var it) ? it : null;
            return new TagDto(x.t.Id, x.t.DeviceId, x.d.Code, x.t.Code, x.t.Name, x.t.AddressExpr,
                x.t.Area.ToText(), x.t.AreaNo, x.t.ByteOffset, x.t.BitOffset, x.t.DataType.ToText(),
                x.t.ByteOrder.ToString(), x.t.StrLen, x.t.Writable, x.t.MinValue, x.t.MaxValue,
                x.t.Scale, x.t.Offset, x.t.Unit, x.t.ScanGroupId, x.g.Name, x.t.Deadband, x.t.Enabled,
                item?.Value, item?.Text, item?.Quality ?? (int)Quality.Bad, item?.Ts);
        }).ToList();
        return Ok(new PagedResult<TagDto>(result, total, page, size));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TagDto>> Get(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var x = await (from t in db.Tags.AsNoTracking()
                       join d in db.Devices.AsNoTracking() on t.DeviceId equals d.Id
                       join g in db.ScanGroups.AsNoTracking() on t.ScanGroupId equals g.Id
                       where t.Id == id && !t.IsDeleted
                       select new { t, d, g }).FirstOrDefaultAsync(ct);
        if (x is null) return NotFound();
        var item = runtime.Store.TryGet(id, out var it) ? it : null;
        return Ok(new TagDto(x.t.Id, x.t.DeviceId, x.d.Code, x.t.Code, x.t.Name, x.t.AddressExpr,
            x.t.Area.ToText(), x.t.AreaNo, x.t.ByteOffset, x.t.BitOffset, x.t.DataType.ToText(),
            x.t.ByteOrder.ToString(), x.t.StrLen, x.t.Writable, x.t.MinValue, x.t.MaxValue,
            x.t.Scale, x.t.Offset, x.t.Unit, x.t.ScanGroupId, x.g.Name, x.t.Deadband, x.t.Enabled,
            item?.Value, item?.Text, item?.Quality ?? (int)Quality.Bad, item?.Ts));
    }

    /// <summary>保存前校验（在线保存与导入共用规则），返回结构化问题列表。</summary>
    [HttpPost("validate")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Validate([FromBody] TagInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var dev = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == input.DeviceId, ct);
        if (dev is null) return BadRequest(ApiResult<object>.Fail("设备不存在"));
        var ep = await db.Endpoints.AsNoTracking().FirstAsync(e => e.Id == dev.EndpointId, ct);

        var tag = ToTag(input);
        var issues = TagValidator.Validate(ep.Protocol, tag);
        var siblings = await db.Tags.AsNoTracking().Where(t => t.DeviceId == input.DeviceId && !t.IsDeleted).ToListAsync(ct);
        var (key, dupIssues) = TagValidator.BuildAddressKeyAndCheck(ep.Protocol, tag, siblings);
        issues.AddRange(dupIssues);
        return Ok(ApiResult<object>.Success(new
        {
            ok = !issues.Any(i => i.IsError),
            addressKey = key,
            issues = issues.Select(i => new { i.Field, i.Message, i.IsError })
        }));
    }

    [HttpPost]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Create([FromBody] TagInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var dev = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == input.DeviceId && !d.IsDeleted, ct);
        if (dev is null) return BadRequest(ApiResult<object>.Fail("设备不存在"));
        var ep = await db.Endpoints.AsNoTracking().FirstAsync(e => e.Id == dev.EndpointId, ct);

        var tag = ToTag(input);
        var issues = TagValidator.Validate(ep.Protocol, tag);
        var siblings = await db.Tags.Where(t => t.DeviceId == input.DeviceId && !t.IsDeleted).ToListAsync(ct);
        var (key, dupIssues) = TagValidator.BuildAddressKeyAndCheck(ep.Protocol, tag, siblings);
        issues.AddRange(dupIssues);
        if (issues.Any(i => i.IsError))
            return BadRequest(ApiResult<object>.Fail(string.Join("；", issues.Where(i => i.IsError).Select(i => i.Message))));

        var parsed = AddressParser.Parse(ep.Protocol, input.AddressExpr, tag.DataType, input.StrLen);
        tag.AddressExpr = parsed.NormalizedExpr;
        tag.AddressKey = key;
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("tag.create", "tag", tag.Id.ToString(), null,
            new { tag.Code, tag.Name, tag.AddressExpr }, username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { tag.Id, tag.AddressExpr, warnings = issues.Where(i => !i.IsError).Select(i => i.Message) }));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Update(int id, [FromBody] TagInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted, ct);
        if (tag is null) return NotFound();
        var dev = await db.Devices.AsNoTracking().FirstAsync(d => d.Id == tag.DeviceId, ct);
        var ep = await db.Endpoints.AsNoTracking().FirstAsync(e => e.Id == dev.EndpointId, ct);

        var candidate = ToTag(input);
        candidate.Id = id;
        var issues = TagValidator.Validate(ep.Protocol, candidate);
        var siblings = await db.Tags.Where(t => t.DeviceId == tag.DeviceId && !t.IsDeleted).ToListAsync(ct);
        var (key, dupIssues) = TagValidator.BuildAddressKeyAndCheck(ep.Protocol, candidate, siblings);
        issues.AddRange(dupIssues);
        if (issues.Any(i => i.IsError))
            return BadRequest(ApiResult<object>.Fail(string.Join("；", issues.Where(i => i.IsError).Select(i => i.Message))));

        var before = new { tag.Code, tag.Name, tag.AddressExpr, DataType = tag.DataType.ToText() };
        var parsed = AddressParser.Parse(ep.Protocol, input.AddressExpr, candidate.DataType, input.StrLen);
        tag.Code = input.Code;
        tag.Name = input.Name;
        tag.AddressExpr = parsed.NormalizedExpr;
        tag.Area = parsed.Address.Area;
        tag.AreaNo = parsed.Address.AreaNo;
        tag.ByteOffset = parsed.Address.ByteOffset;
        tag.BitOffset = parsed.Address.BitOffset;
        tag.AddressKey = key;
        tag.DataType = candidate.DataType;
        tag.ByteOrder = candidate.ByteOrder;
        tag.StrLen = candidate.StrLen;
        tag.Writable = input.Writable;
        tag.MinValue = input.MinValue;
        tag.MaxValue = input.MaxValue;
        tag.Scale = input.Scale == 0 ? 1 : input.Scale;
        tag.Offset = input.Offset;
        tag.Unit = input.Unit;
        tag.ScanGroupId = input.ScanGroupId;
        tag.Deadband = input.Deadband;
        tag.Enabled = input.Enabled;
        tag.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("tag.update", "tag", id.ToString(), before,
            new { tag.Code, tag.Name, tag.AddressExpr }, username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { tag.Id, tag.AddressExpr }));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Delete(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted, ct);
        if (tag is null) return NotFound();
        tag.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("tag.delete", "tag", id.ToString(), new { tag.Code, tag.Name }, null,
            username: User.Username(), userId: User.UserId(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { deleted = true }));
    }

    private static Tag ToTag(TagInput input) => new()
    {
        DeviceId = input.DeviceId,
        Code = input.Code,
        Name = input.Name,
        AddressExpr = input.AddressExpr,
        DataType = AreaNames.ParseDataType(input.DataType),
        ByteOrder = Enum.TryParse<ByteOrderKind>(input.ByteOrder, true, out var o) ? o : ByteOrderKind.ABCD,
        StrLen = input.StrLen,
        Writable = input.Writable,
        MinValue = input.MinValue,
        MaxValue = input.MaxValue,
        Scale = input.Scale == 0 ? 1 : input.Scale,
        Offset = input.Offset,
        Unit = input.Unit,
        ScanGroupId = input.ScanGroupId,
        Deadband = input.Deadband,
        Enabled = input.Enabled
    };
}
