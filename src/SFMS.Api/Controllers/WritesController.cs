using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SFMS.Collector;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Auth;
using SFMS.Infrastructure.Data;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

/// <summary>下发写值：准备（生成确认令牌）→ 执行（二次确认后写 + 回读校验，计划书 3.7）。</summary>
[ApiController]
[Route("api/writes")]
[Authorize(Policy = "TagWrite")]
public sealed class WritesController(
    WriteService writes,
    IDbContextFactory<SfmsDbContext> factory) : ControllerBase
{
    /// <summary>第一步：校验权限/类型/量程，返回改动预览与 60 秒有效的确认令牌。</summary>
    [HttpPost("prepare")]
    public async Task<ActionResult<WritePrepareResult>> Prepare([FromBody] WritePrepareRequest req, CancellationToken ct)
    {
        if (req?.Items is null || req.Items.Count == 0)
            return BadRequest(ApiResult<object>.Fail("写入清单为空"));
        if (req.Items.Count > 200)
            return BadRequest(ApiResult<object>.Fail("单次最多 200 个点位"));

        var result = await writes.PrepareAsync(User.UserId(), User.RoleLevel(), req.Items, req.Reason,
            HttpContext.Ip(), ct);
        return Ok(result);
    }

    /// <summary>第二步：凭令牌执行；失败自动逆序补偿回滚。</summary>
    [HttpPost("execute")]
    public async Task<ActionResult<WriteExecuteResult>> Execute([FromBody] WriteExecuteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.ConfirmToken))
            return BadRequest(ApiResult<object>.Fail("缺少确认令牌"));

        var result = await writes.ExecuteAsync(User.UserId(), User.Username(), User.RoleLevel(),
            req.ConfirmToken, HttpContext.Ip(), ct);
        return Ok(result);
    }

    /// <summary>写入批次历史（含每点位旧值/新值/回读值，便于追责复盘）。</summary>
    [HttpGet("batches")]
    public async Task<ActionResult<object>> Batches([FromQuery] int page = 1, [FromQuery] int size = 20,
        [FromQuery] string? status = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 200);
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.WriteBatches.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(b => b.Status == status);

        int total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * size).Take(size)
            .Include(b => b.Items).ToListAsync(ct);

        return Ok(new
        {
            total, page, size,
            items = rows.Select(b => new
            {
                b.BatchId, b.Username, b.Status, b.Message, b.CreatedAt,
                items = b.Items.Select(i => new { i.TagId, i.Code, i.OldValue, i.NewValue, i.ReadBack, i.Ok, i.Error })
            })
        });
    }
}
