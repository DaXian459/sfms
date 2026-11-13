using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SFMS.Domain;
using SFMS.Shared;

namespace SFMS.Infrastructure.Excel;

public sealed record ImportRowResult(int RowNo, bool Valid, string Message, string DeviceCode, string TagCode,
    string AddressExpr, string DataType, string RawJson);

public sealed record ImportResult(int JobId, int Total, int Valid, int Invalid, IReadOnlyList<ImportRowResult> Rows);

/// <summary>
/// 点位批量导入（计划书 5.3 + 附录 C）：两阶段——先校验不写库，全部通过再落地，
/// 保证导入原子性；落地阶段再次全量校验，冲突行返回错误而不是静默覆盖。
/// </summary>
public sealed class PointImportService(IDbContextFactory<SfmsDbContext> factory)
{
    public static readonly string[] Columns =
    [
        "端点IP", "端点端口", "设备编码", "设备名称", "从站地址", "机架号", "槽号",
        "点位编码", "点位名称", "地址表达式", "数据类型", "字节序", "读写属性",
        "工程单位", "量程下限", "量程上限", "换算系数", "换算偏移", "采集分组", "备注"
    ];

    public byte[] BuildTemplate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("点位导入模板");
        for (int i = 0; i < Columns.Length; i++)
        {
            ws.Cell(1, i + 1).Value = Columns[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1f6feb");
            ws.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
            ws.Column(i + 1).Width = i is 2 or 3 or 8 or 9 ? 20 : 14;
        }
        // 示例行
        var sample = new object[]
        {
            "127.0.0.1", 1502, "METER-01", "一号线总电表", 1, "", "",
            "VOLT_B", "B 相电压", "HR:30", "FLOAT32", "CDAB", "R",
            "V", 0, 300, 1, 0, "中组", "导入示例"
        };
        for (int i = 0; i < sample.Length; i++) ws.Cell(2, i + 1).Value = XLCellValue.FromObject(sample[i]);
        ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<ImportResult> ValidateAsync(Stream xlsx, string fileName, string username, CancellationToken ct)
    {
        var drafts = ReadRows(xlsx);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var endpoints = await db.Endpoints.Where(e => !e.IsDeleted).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var devices = await db.Devices.Where(d => !d.IsDeleted).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var existingTags = await db.Tags.Where(t => !t.IsDeleted).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var groups = await db.ScanGroups.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        var results = new List<ImportRowResult>();
        var seenCode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAddr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var draft in drafts)
        {
            var errors = new List<string>();
            var ep = endpoints.FirstOrDefault(e => e.Ip == draft.EndpointIp && e.Port == draft.EndpointPort);
            if (ep is null) errors.Add($"端点 {draft.EndpointIp}:{draft.EndpointPort} 不存在");
            if (string.IsNullOrWhiteSpace(draft.DeviceCode)) errors.Add("设备编码不能为空");
            if (string.IsNullOrWhiteSpace(draft.TagCode)) errors.Add("点位编码不能为空");

            var dev = devices.FirstOrDefault(d => d.EndpointId == ep?.Id && string.Equals(d.Code, draft.DeviceCode, StringComparison.OrdinalIgnoreCase));
            if (dev is null && ep is not null)
            {
                if (string.IsNullOrWhiteSpace(draft.DeviceKey)) errors.Add("设备不存在时需提供从站地址或机架/槽号");
                if (string.IsNullOrWhiteSpace(draft.DeviceName)) errors.Add("设备不存在时需提供设备名称");
            }

            TagDataType type = TagDataType.Int16;
            try
            {
                type = AreaNames.ParseDataType(draft.DataType);
            }
            catch (ArgumentException ex)
            {
                errors.Add(ex.Message);
            }

            ByteOrderKind order = Enum.TryParse<ByteOrderKind>(draft.ByteOrder, true, out var o) ? o : ByteOrderKind.ABCD;
            int groupId = groups.FirstOrDefault(g => g.Name == draft.ScanGroup)?.Id ?? groups.FirstOrDefault()?.Id ?? 0;
            if (groupId == 0) errors.Add($"采集分组 {draft.ScanGroup} 不存在");

            Tag? tag = null;
            if (ep is not null && errors.Count == 0)
            {
                tag = new Tag
                {
                    DeviceId = dev?.Id ?? 0,
                    Code = draft.TagCode,
                    Name = draft.TagName,
                    AddressExpr = draft.AddressExpr,
                    DataType = type,
                    ByteOrder = order,
                    Writable = string.Equals(draft.Writable, "RW", StringComparison.OrdinalIgnoreCase),
                    Unit = draft.Unit,
                    MinValue = draft.Min,
                    MaxValue = draft.Max,
                    Scale = draft.Scale,
                    Offset = draft.Offset,
                    ScanGroupId = groupId,
                    Deadband = 0
                };
                var issues = TagValidator.Validate(ep.Protocol, tag);
                errors.AddRange(issues.Where(i => i.IsError).Select(i => i.Message));

                if (tag.Writable && ep.Protocol != ProtocolType.S7 && (tag.Area is TagArea.InputRegister or TagArea.DiscreteInput))
                    errors.Add("该区域为只读区，不能导入为 RW");

                string codeKey = $"{ep.Id}|{draft.DeviceCode}|{draft.TagCode}";
                if (!seenCode.Add(codeKey)) errors.Add("文件内点位编码重复");
                var sibling = existingTags.FirstOrDefault(t => t.DeviceId == dev?.Id && t.Code == draft.TagCode);
                if (sibling is not null) errors.Add($"点位编码 {draft.TagCode} 已存在于设备 {draft.DeviceCode}");

                try
                {
                    var parsed = AddressParser.Parse(ep.Protocol, draft.AddressExpr, type);
                    string addrKey = $"{ep.Id}|{draft.DeviceCode}|{parsed.Address.Key}";
                    if (!seenAddr.Add(addrKey)) errors.Add("文件内地址重复");
                    if (existingTags.Any(t => t.DeviceId == dev?.Id && t.AddressKey == parsed.Address.Key))
                        errors.Add("地址与已有点位重叠");
                    tag.Area = parsed.Address.Area;
                    tag.AreaNo = parsed.Address.AreaNo;
                    tag.ByteOffset = parsed.Address.ByteOffset;
                    tag.BitOffset = parsed.Address.BitOffset;
                    tag.AddressKey = parsed.Address.Key;
                    tag.AddressExpr = parsed.NormalizedExpr;
                }
                catch (AddressParseException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            results.Add(new ImportRowResult(draft.RowNo, errors.Count == 0, string.Join("；", errors),
                draft.DeviceCode, draft.TagCode, draft.AddressExpr, draft.DataType, draft.RawJson));
        }

        var job = new ImportJob
        {
            FileName = fileName,
            Status = results.All(r => r.Valid) ? "Validated" : "ValidatedWithErrors",
            Total = results.Count,
            Valid = results.Count(r => r.Valid),
            Invalid = results.Count(r => !r.Valid),
            CreatedBy = username,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var r in results)
        {
            db.ImportStagings.Add(new ImportStaging
            {
                JobId = job.Id,
                RowNo = r.RowNo,
                PayloadJson = r.RawJson,
                ErrorText = r.Valid ? null : r.Message,
                IsValid = r.Valid
            });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ImportResult(job.Id, job.Total, job.Valid, job.Invalid, results);
    }

    public async Task<ImportResult> CommitAsync(int jobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var job = await db.ImportJobs.Include(j => j.Rows).FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"导入任务 {jobId} 不存在");

        var endpoints = await db.Endpoints.Where(e => !e.IsDeleted).ToListAsync(ct).ConfigureAwait(false);
        var devices = await db.Devices.Where(d => !d.IsDeleted).ToListAsync(ct).ConfigureAwait(false);
        var groups = await db.ScanGroups.ToListAsync(ct).ConfigureAwait(false);
        var results = new List<ImportRowResult>();
        int created = 0;

        foreach (var row in job.Rows.OrderBy(r => r.RowNo))
        {
            if (!row.IsValid)
            {
                results.Add(new ImportRowResult(row.RowNo, false, row.ErrorText ?? "校验未通过", "", "", "", "", row.PayloadJson));
                continue;
            }

            var d = ParsePayload(row.PayloadJson);
            var ep = endpoints.FirstOrDefault(e => e.Ip == d.EndpointIp && e.Port == d.EndpointPort);
            if (ep is null)
            {
                results.Add(new ImportRowResult(row.RowNo, false, "端点已不存在", d.DeviceCode, d.TagCode, d.AddressExpr, d.DataType, row.PayloadJson));
                continue;
            }

            var dev = devices.FirstOrDefault(x => x.EndpointId == ep.Id && x.Code == d.DeviceCode);
            if (dev is null)
            {
                dev = new Device
                {
                    EndpointId = ep.Id,
                    Code = d.DeviceCode,
                    Name = d.DeviceName,
                    DeviceKey = d.DeviceKey,
                    Enabled = true
                };
                db.Devices.Add(dev);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                devices.Add(dev);
            }

            if (await db.Tags.AnyAsync(t => t.DeviceId == dev.Id && t.Code == d.TagCode && !t.IsDeleted, ct).ConfigureAwait(false))
            {
                results.Add(new ImportRowResult(row.RowNo, false, "点位编码已存在（落地阶段冲突）", d.DeviceCode, d.TagCode, d.AddressExpr, d.DataType, row.PayloadJson));
                continue;
            }

            var type = AreaNames.ParseDataType(d.DataType);
            var order = Enum.TryParse<ByteOrderKind>(d.ByteOrder, true, out var o) ? o : ByteOrderKind.ABCD;
            var parsed = AddressParser.Parse(ep.Protocol, d.AddressExpr, type);
            var tag = new Tag
            {
                DeviceId = dev.Id,
                Code = d.TagCode,
                Name = d.TagName,
                AddressExpr = parsed.NormalizedExpr,
                Area = parsed.Address.Area,
                AreaNo = parsed.Address.AreaNo,
                ByteOffset = parsed.Address.ByteOffset,
                BitOffset = parsed.Address.BitOffset,
                DataType = type,
                ByteOrder = order,
                Writable = string.Equals(d.Writable, "RW", StringComparison.OrdinalIgnoreCase),
                Unit = d.Unit,
                MinValue = d.Min,
                MaxValue = d.Max,
                Scale = d.Scale,
                Offset = d.Offset,
                ScanGroupId = groups.FirstOrDefault(g => g.Name == d.ScanGroup)?.Id ?? groups[0].Id,
                Deadband = 0,
                Enabled = true,
                AddressKey = parsed.Address.Key
            };
            db.Tags.Add(tag);
            created++;
            results.Add(new ImportRowResult(row.RowNo, true, "已导入", d.DeviceCode, d.TagCode, parsed.NormalizedExpr, d.DataType, row.PayloadJson));
        }

        job.Status = results.All(r => r.Valid) ? "Committed" : "CommittedWithErrors";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ImportResult(job.Id, results.Count, results.Count(r => r.Valid), results.Count(r => !r.Valid), results);
    }

    public async Task<byte[]> ExportAsync(int? deviceId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = from t in db.Tags.AsNoTracking()
                    join d in db.Devices.AsNoTracking() on t.DeviceId equals d.Id
                    join e in db.Endpoints.AsNoTracking() on d.EndpointId equals e.Id
                    join g in db.ScanGroups.AsNoTracking() on t.ScanGroupId equals g.Id
                    where !t.IsDeleted && !d.IsDeleted
                    select new { t, d, e, g };
        if (deviceId is { } id) query = query.Where(x => x.d.Id == id);
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("点位清单");
        for (int i = 0; i < Columns.Length; i++) ws.Cell(1, i + 1).Value = Columns[i];
        int r = 2;
        foreach (var x in rows)
        {
            ws.Cell(r, 1).Value = x.e.Ip;
            ws.Cell(r, 2).Value = x.e.Port;
            ws.Cell(r, 3).Value = x.d.Code;
            ws.Cell(r, 4).Value = x.d.Name;
            ws.Cell(r, 5).Value = x.e.Protocol == ProtocolType.S7 ? "" : x.d.DeviceKey;
            ws.Cell(r, 6).Value = x.e.Protocol == ProtocolType.S7 ? x.d.DeviceKey.Split('/')[0] : "";
            ws.Cell(r, 7).Value = x.e.Protocol == ProtocolType.S7 && x.d.DeviceKey.Contains('/') ? x.d.DeviceKey.Split('/')[1] : "";
            ws.Cell(r, 8).Value = x.t.Code;
            ws.Cell(r, 9).Value = x.t.Name;
            ws.Cell(r, 10).Value = x.t.AddressExpr;
            ws.Cell(r, 11).Value = x.t.DataType.ToText();
            ws.Cell(r, 12).Value = x.t.ByteOrder.ToString();
            ws.Cell(r, 13).Value = x.t.Writable ? "RW" : "R";
            ws.Cell(r, 14).Value = x.t.Unit ?? "";
            if (x.t.MinValue.HasValue) ws.Cell(r, 15).Value = x.t.MinValue.Value; else ws.Cell(r, 15).Value = "";
            if (x.t.MaxValue.HasValue) ws.Cell(r, 16).Value = x.t.MaxValue.Value; else ws.Cell(r, 16).Value = "";
            ws.Cell(r, 17).Value = x.t.Scale;
            ws.Cell(r, 18).Value = x.t.Offset;
            ws.Cell(r, 19).Value = x.g.Name;
            ws.Cell(r, 20).Value = "";
            r++;
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- 读表

    private sealed record Draft(int RowNo, string EndpointIp, int EndpointPort, string DeviceCode, string DeviceName,
        string DeviceKey, int? Rack, int? Slot, string TagCode, string TagName, string AddressExpr, string DataType,
        string ByteOrder, string Writable, string? Unit, double? Min, double? Max, double Scale, double Offset,
        string ScanGroup, string RawJson);

    private static List<Draft> ReadRows(Stream xlsx)
    {
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheets.First();
        var headerRow = ws.Row(1);
        var map = new Dictionary<string, int>();
        int lastCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            string h = headerRow.Cell(c).GetString().Trim();
            if (!string.IsNullOrEmpty(h)) map[h] = c;
        }

        string Get(int row, string name) =>
            map.TryGetValue(name, out int col) ? ws.Cell(row, col).GetString().Trim() : "";

        var list = new List<Draft>();
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= lastRow; r++)
        {
            if (string.IsNullOrWhiteSpace(ws.Cell(r, 1).GetString())) continue;
            string ip = Get(r, "端点IP");
            _ = int.TryParse(Get(r, "端点端口"), out int port);
            int? rack = int.TryParse(Get(r, "机架号"), out int rk) ? rk : null;
            int? slot = int.TryParse(Get(r, "槽号"), out int sl) ? sl : null;
            string deviceKey = Get(r, "从站地址");
            if (string.IsNullOrWhiteSpace(deviceKey) && (rack.HasValue || slot.HasValue))
                deviceKey = $"{rack ?? 0}/{slot ?? 1}";
            double? min = double.TryParse(Get(r, "量程下限"), NumberStyles.Any, CultureInfo.InvariantCulture, out double mn) ? mn : null;
            double? max = double.TryParse(Get(r, "量程上限"), NumberStyles.Any, CultureInfo.InvariantCulture, out double mx) ? mx : null;
            double scale = double.TryParse(Get(r, "换算系数"), NumberStyles.Any, CultureInfo.InvariantCulture, out double sc) && Math.Abs(sc) > 1e-9 ? sc : 1;
            double offset = double.TryParse(Get(r, "换算偏移"), NumberStyles.Any, CultureInfo.InvariantCulture, out double of) ? of : 0;

            var draft = new Draft(r, ip, port == 0 ? 502 : port, Get(r, "设备编码"), Get(r, "设备名称"), deviceKey,
                rack, slot, Get(r, "点位编码"), Get(r, "点位名称"), Get(r, "地址表达式"), Get(r, "数据类型"),
                Get(r, "字节序"), Get(r, "读写属性"), NullIfEmpty(Get(r, "工程单位")), min, max, scale, offset,
                Get(r, "采集分组"), "");
            list.Add(draft with { RawJson = System.Text.Json.JsonSerializer.Serialize(draft) });
        }
        return list;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static Draft ParsePayload(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<Draft>(json) ?? throw new InvalidOperationException("导入行数据损坏");
}
