using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SFMS.Domain;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Auth;
using SFMS.Infrastructure.Data;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

public sealed record ScreenInput(string Code, string Name, string Type, int Width, int Height);

public sealed record ScreenSaveRequest(string Json, string? Note);

public sealed record ScreenRoleInput(string RoleCode, bool CanView, bool CanEdit);

/// <summary>SCADA 画面：草稿、发布、版本回滚与角色可见性（计划书 3.10 / FR-11~FR-13）。</summary>
[ApiController]
[Route("api/screens")]
[Authorize]
public sealed class ScreensController(
    IDbContextFactory<SfmsDbContext> factory,
    SettingsService settings,
    AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScreenDto>>> List(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var screens = await db.Screens.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);
        var roles = await db.ScreenRoles.AsNoTracking().ToListAsync(ct);
        var role = User.RoleLevel().ToString();
        bool isAdmin = User.RoleLevel() == RoleLevel.Admin;

        var list = screens.Select(s =>
        {
            var mine = roles.FirstOrDefault(r => r.ScreenId == s.Id && r.RoleCode.Equals(role, StringComparison.OrdinalIgnoreCase));
            bool canView = isAdmin || mine?.CanView != false;      // 未配置时默认可见
            bool canEdit = isAdmin || mine?.CanEdit == true;
            return new ScreenDto(s.Id, s.Code, s.Name, s.Type, s.Width, s.Height, s.Status.ToString(),
                s.PublishedVersionId, s.PublishedVersionNo, s.UpdatedAt, s.UpdatedBy, canView, canEdit);
        }).Where(x => x.CanView).ToList();

        return Ok(list);
    }

    /// <summary>取画面内容：默认取已发布版本，mode=draft 取草稿（编辑器用）。</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> Get(int id, [FromQuery] string? mode = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var screen = await db.Screens.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (screen is null) return NotFound(ApiResult<object>.Fail("画面不存在"));

        string json = screen.DraftJson;
        string source = "draft";
        int versionNo = screen.PublishedVersionNo;

        if (!string.Equals(mode, "draft", StringComparison.OrdinalIgnoreCase) && screen.PublishedVersionId is { } vid)
        {
            var ver = await db.ScreenVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct);
            if (ver is not null)
            {
                json = ver.Json;
                source = "published";
                versionNo = ver.VersionNo;
            }
        }

        return Ok(new
        {
            screen.Id, screen.Code, screen.Name, screen.Type, screen.Width, screen.Height,
            status = screen.Status.ToString(), screen.PublishedVersionNo, source, versionNo,
            screen.UpdatedAt, screen.UpdatedBy, json
        });
    }

    [HttpPost]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Create([FromBody] ScreenInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Screens.AnyAsync(s => s.Code == input.Code, ct))
            return BadRequest(ApiResult<object>.Fail($"画面编码 {input.Code} 已存在"));

        var screen = new Screen
        {
            Code = input.Code,
            Name = input.Name,
            Type = string.IsNullOrWhiteSpace(input.Type) ? "custom" : input.Type,
            Width = input.Width <= 0 ? 1920 : input.Width,
            Height = input.Height <= 0 ? 1080 : input.Height,
            DraftJson = EmptyJson(input.Code, input.Name, input.Width, input.Height),
            Status = ScreenStatus.Draft,
            UpdatedBy = User.Username()
        };
        db.Screens.Add(screen);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("screen.create", "screen", screen.Id.ToString(), null,
            new { screen.Code, screen.Name, screen.Type }, userId: User.UserId(),
            username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { screen.Id }));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Update(int id, [FromBody] ScreenInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var screen = await db.Screens.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (screen is null) return NotFound();
        var before = new { screen.Code, screen.Name, screen.Type, screen.Width, screen.Height };

        screen.Name = input.Name;
        screen.Type = input.Type;
        screen.Width = input.Width;
        screen.Height = input.Height;
        screen.UpdatedAt = DateTimeOffset.UtcNow;
        screen.UpdatedBy = User.Username();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("screen.update", "screen", id.ToString(), before,
            new { screen.Name, screen.Type, screen.Width, screen.Height }, userId: User.UserId(),
            username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { screen.Id }));
    }

    /// <summary>保存草稿（不发布）。JSON 结构由前端保证，这里只做最小合法性校验。</summary>
    [HttpPut("{id:int}/draft")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> SaveDraft(int id, [FromBody] ScreenSaveRequest req, CancellationToken ct)
    {
        var err = ValidateJson(req?.Json);
        if (err is not null) return BadRequest(ApiResult<object>.Fail(err));

        await using var db = await factory.CreateDbContextAsync(ct);
        var screen = await db.Screens.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (screen is null) return NotFound();

        screen.DraftJson = req!.Json;
        screen.Status = ScreenStatus.Draft;
        screen.UpdatedAt = DateTimeOffset.UtcNow;
        screen.UpdatedBy = User.Username();
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        return Ok(ApiResult<object>.Success(new { screen.Id, screen.UpdatedAt }));
    }

    /// <summary>发布：把草稿固化为新版本并切换运行态。</summary>
    [HttpPost("{id:int}/publish")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Publish(int id, [FromBody] ScreenSaveRequest? req, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var screen = await db.Screens.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (screen is null) return NotFound();

        if (req is not null && !string.IsNullOrWhiteSpace(req.Json))
        {
            var err = ValidateJson(req.Json);
            if (err is not null) return BadRequest(ApiResult<object>.Fail(err));
            screen.DraftJson = req.Json;
        }
        if (string.IsNullOrWhiteSpace(screen.DraftJson) || screen.DraftJson.Trim() is "{}" or "")
            return BadRequest(ApiResult<object>.Fail("画面内容为空，无法发布"));

        int nextNo = await db.ScreenVersions.Where(v => v.ScreenId == id)
            .Select(v => (int?)v.VersionNo).MaxAsync(ct) ?? 0;
        nextNo++;

        var version = new ScreenVersion
        {
            ScreenId = id,
            VersionNo = nextNo,
            Json = screen.DraftJson,
            Note = req?.Note ?? $"发布版本 v{nextNo}",
            CreatedBy = User.Username()
        };
        db.ScreenVersions.Add(version);
        await db.SaveChangesAsync(ct);

        screen.PublishedVersionId = version.Id;
        screen.PublishedVersionNo = nextNo;
        screen.Status = ScreenStatus.Published;
        screen.UpdatedAt = DateTimeOffset.UtcNow;
        screen.UpdatedBy = User.Username();
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();

        await audit.WriteAsync("screen.publish", "screen", id.ToString(), null,
            new { versionNo = nextNo, note = version.Note }, userId: User.UserId(),
            username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { screen.Id, versionNo = nextNo, versionId = version.Id }));
    }

    /// <summary>版本列表（不含 JSON 大字段，减少加载体积）。</summary>
    [HttpGet("{id:int}/versions")]
    public async Task<ActionResult<IReadOnlyList<object>>> Versions(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ScreenVersions.AsNoTracking().Where(v => v.ScreenId == id)
            .OrderByDescending(v => v.VersionNo)
            .Select(v => new { v.Id, v.ScreenId, v.VersionNo, v.Note, v.CreatedAt, v.CreatedBy })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{id:int}/versions/{versionId:int}")]
    public async Task<ActionResult<object>> Version(int id, int versionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var v = await db.ScreenVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == versionId && x.ScreenId == id, ct);
        if (v is null) return NotFound();
        return Ok(new ScreenVersionDto(v.Id, v.ScreenId, v.VersionNo, v.Note ?? "", v.CreatedAt, v.CreatedBy, v.Json));
    }

    /// <summary>回滚：把历史版本内容写回草稿，再由调用方决定是否发布。</summary>
    [HttpPost("{id:int}/rollback/{versionId:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Rollback(int id, int versionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var screen = await db.Screens.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (screen is null) return NotFound();
        var v = await db.ScreenVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == versionId && x.ScreenId == id, ct);
        if (v is null) return NotFound(ApiResult<object>.Fail("版本不存在"));

        screen.DraftJson = v.Json;
        screen.Status = ScreenStatus.Draft;
        screen.UpdatedAt = DateTimeOffset.UtcNow;
        screen.UpdatedBy = User.Username();
        await db.SaveChangesAsync(ct);
        await settings.BumpConfigVersionAsync();
        await audit.WriteAsync("screen.rollback", "screen", id.ToString(), null,
            new { versionNo = v.VersionNo }, userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { screen.Id, fromVersion = v.VersionNo }));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = "ConfigEdit")]
    public async Task<ActionResult<object>> Delete(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var screen = await db.Screens.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (screen is null) return NotFound();
        db.Screens.Remove(screen);
        db.ScreenVersions.RemoveRange(db.ScreenVersions.Where(v => v.ScreenId == id));
        db.ScreenRoles.RemoveRange(db.ScreenRoles.Where(r => r.ScreenId == id));
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("screen.delete", "screen", id.ToString(), new { screen.Code, screen.Name }, null,
            userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ok = true }));
    }

    [HttpGet("{id:int}/roles")]
    public async Task<ActionResult<IReadOnlyList<ScreenRoleInput>>> Roles(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ScreenRoles.AsNoTracking().Where(r => r.ScreenId == id).ToListAsync(ct);
        var existing = rows.ToDictionary(r => r.RoleCode, r => r);
        var result = Enum.GetNames<RoleLevel>().Select(role =>
        {
            var r = existing.GetValueOrDefault(role);
            return new ScreenRoleInput(role, r?.CanView ?? true, r?.CanEdit ?? false);
        }).ToList();
        return Ok(result);
    }

    [HttpPut("{id:int}/roles")]
    [Authorize(Policy = "UserAdmin")]
    public async Task<ActionResult<object>> SetRoles(int id, [FromBody] IReadOnlyList<ScreenRoleInput> input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Screens.AnyAsync(s => s.Id == id, ct)) return NotFound();
        var rows = await db.ScreenRoles.Where(r => r.ScreenId == id).ToListAsync(ct);
        db.ScreenRoles.RemoveRange(rows);
        foreach (var r in input ?? Array.Empty<ScreenRoleInput>())
        {
            if (!Enum.TryParse<RoleLevel>(r.RoleCode, true, out var role)) continue;
            db.ScreenRoles.Add(new ScreenRole
            {
                ScreenId = id,
                RoleCode = role.ToString(),
                CanView = r.CanView,
                CanEdit = r.CanEdit
            });
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("screen.roles", "screen", id.ToString(), null, input,
            userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ok = true }));
    }

    /// <summary>画面 JSON 最小校验：必须是包含 schemaVersion 与 widgets 的对象。</summary>
    private static string? ValidateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "画面内容为空";
        if (json.Length > 2 * 1024 * 1024) return "画面内容超过 2MB 上限";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "画面内容必须是 JSON 对象";
            if (!doc.RootElement.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array)
                return "画面内容缺少 widgets 数组";
        }
        catch (JsonException ex)
        {
            return "画面 JSON 解析失败：" + ex.Message;
        }
        return null;
    }

    private static string EmptyJson(string code, string name, int w, int h) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        code,
        name,
        width = w <= 0 ? 1920 : w,
        height = h <= 0 ? 1080 : h,
        background = "#0b1220",
        layers = new[] { new { id = "L1", name = "主图层", visible = true, locked = false, opacity = 1.0 } },
        widgets = Array.Empty<object>()
    });
}
