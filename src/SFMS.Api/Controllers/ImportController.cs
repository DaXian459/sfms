using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Auth;
using SFMS.Infrastructure.Data;
using SFMS.Infrastructure.Excel;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

/// <summary>点位批量导入：模板下载 → 校验（不落库）→ 提交（落库），见附录 C 模板（FR-03）。</summary>
[ApiController]
[Route("api/import")]
[Authorize(Policy = "ConfigEdit")]
public sealed class ImportController(
    PointImportService import,
    IDbContextFactory<SfmsDbContext> factory,
    AuditService audit) : ControllerBase
{
    private const long MaxBytes = 8 * 1024 * 1024;

    [HttpGet("template")]
    [AllowAnonymous]
    public IActionResult Template()
    {
        var bytes = import.BuildTemplate();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "sfms-点位导入模板.xlsx");
    }

    /// <summary>上传并校验：只写 ImportJob / ImportStaging，不触碰正式点位表。</summary>
    [HttpPost("validate")]
    [RequestSizeLimit(MaxBytes)]
    public async Task<ActionResult<ImportResult>> Validate(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(ApiResult<object>.Fail("请选择 .xlsx 文件"));
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiResult<object>.Fail("仅支持 .xlsx 格式，请使用模板"));
        if (file.Length > MaxBytes) return BadRequest(ApiResult<object>.Fail("文件超过 8MB 上限"));

        await using var stream = file.OpenReadStream();
        var result = await import.ValidateAsync(stream, file.FileName, User.Username(), ct);
        await audit.WriteAsync("import.validate", "import", result.JobId.ToString(), null,
            new { result.Total, result.Valid, result.Invalid, file.FileName },
            userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(result);
    }

    /// <summary>提交校验通过的行；冲突行会返回错误且不影响其余行。</summary>
    [HttpPost("commit/{jobId:int}")]
    public async Task<ActionResult<ImportResult>> Commit(int jobId, CancellationToken ct)
    {
        var result = await import.CommitAsync(jobId, ct);
        await audit.WriteAsync("import.commit", "import", jobId.ToString(), null,
            new { result.Total, result.Valid, result.Invalid }, userId: User.UserId(),
            username: User.Username(), ip: HttpContext.Ip());
        return Ok(result);
    }

    [HttpGet("jobs")]
    public async Task<ActionResult<object>> Jobs([FromQuery] int size = 20, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ImportJobs.AsNoTracking().OrderByDescending(j => j.Id).Take(Math.Clamp(size, 1, 100))
            .Select(j => new { j.Id, j.FileName, j.Status, j.Total, j.Valid, j.Invalid, j.CreatedAt, j.CreatedBy })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>导出点位表（可作为备份或迁移源）。</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] int? deviceId, CancellationToken ct)
    {
        var bytes = await import.ExportAsync(deviceId, ct);
        string name = deviceId is null ? "sfms-点位表.xlsx" : $"sfms-点位表-设备{deviceId}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }
}
