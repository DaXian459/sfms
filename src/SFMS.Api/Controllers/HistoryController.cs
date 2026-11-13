using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Data;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

/// <summary>历史数据查询与导出（计划书 3.6 历史存储、FR-06/FR-07）。</summary>
[ApiController]
[Route("api/history")]
[Authorize]
public sealed class HistoryController(
    IDbContextFactory<SfmsDbContext> factory,
    HistoryRepository history,
    Npgsql.NpgsqlDataSource dataSource) : ControllerBase
{
    /// <summary>多点多区间查询；&gt;24h 自动走 1 分钟聚合表。</summary>
    [HttpGet("query")]
    public async Task<ActionResult<IReadOnlyList<HistorySeries>>> Query(
        [FromQuery] string? tagIds, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int maxPoints = 2000, [FromQuery] int bucketSeconds = 0, CancellationToken ct = default)
    {
        var ids = ParseIds(tagIds);
        if (ids.Count == 0) return BadRequest(ApiResult<object>.Fail("请指定 tagIds"));
        if (ids.Count > 30) return BadRequest(ApiResult<object>.Fail("单次最多查询 30 个点位"));

        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-1);
        if (start >= end) return BadRequest(ApiResult<object>.Fail("起始时间必须早于结束时间"));
        if ((end - start).TotalDays > 366) return BadRequest(ApiResult<object>.Fail("查询跨度不得超过 366 天"));

        int points = Math.Clamp(maxPoints, 50, 50000);
        var pointsList = await history.QueryAsync(ids, start, end, points, ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        var meta = await db.Tags.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Code, t.Name, t.Unit }).ToListAsync(ct);
        var map = meta.ToDictionary(m => m.Id);
        string resolution = (end - start).TotalHours > 24 ? "1m" : "raw";

        var result = ids.Select(id =>
        {
            map.TryGetValue(id, out var m);
            var pts = pointsList.Where(p => p.TagId == id).OrderBy(p => p.Ts).ToList();
            return new HistorySeries(id, m?.Code, m?.Name, m?.Unit, pts, resolution);
        }).ToList();

        return Ok(result);
    }

    /// <summary>CSV 导出（UTF-8 BOM，便于 Excel 直接打开中文）。</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? tagIds, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        CancellationToken ct = default)
    {
        var ids = ParseIds(tagIds);
        if (ids.Count == 0) return BadRequest(ApiResult<object>.Fail("请指定 tagIds"));

        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-1);
        var points = await history.QueryAsync(ids, start, end, 200000, ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        var meta = await db.Tags.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Code, t.Name, t.Unit }).ToListAsync(ct);
        var map = meta.ToDictionary(m => m.Id, m => m.Code ?? m.Id.ToString());

        var sb = new StringBuilder();
        sb.Append("tag_code,ts,value,quality,quality_text\r\n");
        foreach (var p in points.OrderBy(p => p.TagId).ThenBy(p => p.Ts))
        {
            string code = map.TryGetValue((int)p.TagId, out var c) ? c : p.TagId.ToString();
            string value = p.Text ?? p.Value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
            sb.Append(Csv(code)).Append(',')
              .Append(p.Ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(value)).Append(',')
              .Append(p.Quality).Append(',')
              .Append(Csv(((Quality)p.Quality).ToText())).Append("\r\n");
        }

        string name = $"sfms-history-{start:yyyyMMddHHmm}-{end:yyyyMMddHHmm}.csv";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", name);
    }

    /// <summary>原始表与聚合表的覆盖范围、行数（运维自检）。</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<object>> Stats([FromQuery] int? tagId, CancellationToken ct = default)
    {
        await using var conn = dataSource.CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT count(*) FROM tag_history WHERE (@tag IS NULL OR tag_id = @tag)) AS raw_rows,
              (SELECT min(ts) FROM tag_history WHERE (@tag IS NULL OR tag_id = @tag)) AS raw_min,
              (SELECT max(ts) FROM tag_history WHERE (@tag IS NULL OR tag_id = @tag)) AS raw_max,
              (SELECT count(*) FROM tag_agg_1m WHERE (@tag IS NULL OR tag_id = @tag)) AS agg_rows,
              (SELECT count(*) FROM tag_history WHERE quality <> 0) AS bad_rows
            """;
        // 必须显式声明类型：DBNull 无法推断参数类型，否则报 42P08 could not determine data type of parameter
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("tag", NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = (object?)tagId ?? DBNull.Value
        });
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return Ok(new { });
        return Ok(new
        {
            rawRows = rd.GetInt64(0),
            rawMin = rd.IsDBNull(1) ? (DateTimeOffset?)null : rd.GetFieldValue<DateTimeOffset>(1),
            rawMax = rd.IsDBNull(2) ? (DateTimeOffset?)null : rd.GetFieldValue<DateTimeOffset>(2),
            aggRows = rd.GetInt64(3),
            badRows = rd.GetInt64(4)
        });
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static List<int> ParseIds(string? csv) =>
        (csv ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).Distinct().Take(30).ToList();
}
