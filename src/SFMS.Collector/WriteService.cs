using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SFMS.Domain;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Data;
using SFMS.Protocols;
using SFMS.Shared;

namespace SFMS.Collector;

/// <summary>
/// 写入通道（计划书 3.4.5）：权限 → 类型量程 → 二次确认令牌 → 写入 → 回读校验 → 审计。
/// 批量写采用"全部成功或全部补偿回滚"：任一失败则把已成功点位按逆序写回旧值。
/// </summary>
public sealed class WriteService(
    IDbContextFactory<SfmsDbContext> factory,
    CollectorRuntime runtime,
    AuditService audit,
    ILogger<WriteService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<WritePrepareResult> PrepareAsync(int userId, RoleLevel role,
        IReadOnlyList<WriteItemRequest> items, string? reason, string ip, CancellationToken ct)
    {
        if (role < RoleLevel.Operator)
            throw new UnauthorizedAccessException("写入需要操作员及以上权限");
        if (items.Count == 0)
            throw new ArgumentException("写入清单为空");

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = items.Select(i => i.TagId).Distinct().ToList();
        var tags = await db.Tags.AsNoTracking().Include(t => t.Device)
            .Where(t => ids.Contains(t.Id) && !t.IsDeleted).ToListAsync(ct).ConfigureAwait(false);

        var previews = new List<WritePreview>();
        var blockers = new List<string>();

        foreach (var item in items)
        {
            var tag = tags.FirstOrDefault(t => t.Id == item.TagId);
            if (tag is null)
            {
                blockers.Add($"点位 {item.TagId} 不存在");
                continue;
            }

            double? old = runtime.Store.TryGet(tag.Id, out var rt) ? rt.Value : null;
            string? error = null;

            if (!tag.Writable) error = "点位未标记为可写";
            else if (item.NumericValue is null && item.TextValue is null) error = "写入值不能为空";
            else if (item.NumericValue is { } v)
            {
                if (tag.MinValue is { } min && v < min) error = $"低于允许下限 {min}{tag.Unit}";
                else if (tag.MaxValue is { } max && v > max) error = $"高于允许上限 {max}{tag.Unit}";
            }

            if (error is not null) blockers.Add($"{tag.Code}：{error}");
            previews.Add(new WritePreview(tag.Id, tag.Code, tag.Name, old, null,
                item.NumericValue, item.TextValue, rt?.Quality ?? (int)Quality.Bad, tag.Writable, error));
        }

        if (blockers.Count > 0)
            return new WritePrepareResult("", DateTimeOffset.UtcNow, previews, blockers);

        string payload = JsonSerializer.Serialize(items, Json);
        string hash = Hash(payload);
        string token = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddSeconds(60);

        db.ConfirmTokens.Add(new ConfirmToken
        {
            Token = token,
            UserId = userId,
            PayloadHash = hash,
            PayloadJson = payload,
            ExpiresAt = expires
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteAsync("write.prepare", "tag", string.Join(",", ids), null,
            new { items = previews.Select(p => new { p.TagId, p.NewValue, p.NewText }) },
            "Success", reason, userId, null, ip).ConfigureAwait(false);

        return new WritePrepareResult(token, expires, previews, blockers);
    }

    public async Task<WriteExecuteResult> ExecuteAsync(int userId, string username, RoleLevel role,
        string token, string ip, CancellationToken ct)
    {
        if (role < RoleLevel.Operator)
            throw new UnauthorizedAccessException("写入需要操作员及以上权限");

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.ConfirmTokens.FirstOrDefaultAsync(t => t.Token == token, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("确认令牌无效");
        if (record.UserId != userId) throw new UnauthorizedAccessException("确认令牌不属于当前用户");
        if (record.UsedAt is not null) throw new InvalidOperationException("确认令牌已被使用（防重放）");
        if (record.ExpiresAt < DateTimeOffset.UtcNow) throw new InvalidOperationException("确认令牌已过期，请重新确认");

        var items = JsonSerializer.Deserialize<List<WriteItemRequest>>(record.PayloadJson, Json) ?? [];
        if (Hash(record.PayloadJson) != record.PayloadHash)
            throw new InvalidOperationException("确认载荷被篡改，拒绝执行");

        var ids = items.Select(i => i.TagId).ToList();
        var tags = await db.Tags.AsNoTracking().Include(t => t.Device)
            .Where(t => ids.Contains(t.Id)).ToListAsync(ct).ConfigureAwait(false);

        var batchId = Guid.NewGuid().ToString("N");
        var results = new List<WriteItemResult>();
        var applied = new List<(Tag Tag, byte[] OldBytes, double? OldValue)>();
        string status = "Success";

        foreach (var item in items)
        {
            var tag = tags.FirstOrDefault(t => t.Id == item.TagId);
            if (tag is null)
            {
                results.Add(new WriteItemResult(item.TagId, "?", false, "点位不存在", null, item.NumericValue, null));
                status = "Failed";
                continue;
            }

            var session = runtime.SessionOfTag(tag.Id);
            if (session is null || !session.Driver.IsConnected)
            {
                results.Add(new WriteItemResult(tag.Id, tag.Code, false, "端点离线，写入快速失败", null, item.NumericValue, null));
                status = "Failed";
                continue;
            }

            // 权限 / 量程 / 类型再校验（令牌有效期内的配置可能变化）
            if (!tag.Writable)
            {
                results.Add(new WriteItemResult(tag.Id, tag.Code, false, "点位已变为不可写", null, item.NumericValue, null));
                status = "Failed";
                continue;
            }
            if (item.NumericValue is { } eng && ((tag.MinValue is { } min && eng < min) || (tag.MaxValue is { } max && eng > max)))
            {
                results.Add(new WriteItemResult(tag.Id, tag.Code, false, "写入值超出允许范围", null, item.NumericValue, null));
                status = "Failed";
                continue;
            }

            // 预读旧值（用于回读比对与补偿回滚）
            var (oldRaw, readErr) = await session.ReadOneAsync(tag, ct).ConfigureAwait(false);
            double? oldValue = oldRaw is null ? null : Scaling.ToEngineering(
                ValueCodec.Decode(oldRaw, tag.DataType, tag.ByteOrder, tag.StrLen), tag.Scale, tag.Offset);

            double raw = Scaling.ToRaw(item.NumericValue ?? 0, tag.Scale, tag.Offset);
            byte[] data = ValueCodec.Encode(raw, tag.DataType, tag.ByteOrder, tag.StrLen);
            int unit = int.TryParse(tag.Device?.DeviceKey, out int u) ? u : 1;
            var command = new WriteCommand(unit, tag.Area, tag.AreaNo, tag.ByteOffset, tag.BitOffset, tag.DataType, data);

            var outcome = await session.WriteAsync(command, ct).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                results.Add(new WriteItemResult(tag.Id, tag.Code, false,
                    $"写入失败：{outcome.Error?.Message ?? readErr?.Message ?? "未知错误"}", oldValue, item.NumericValue, null));
                status = "Failed";
                continue;
            }

            // 回读校验：不一致不重试，直接失败并给出可能原因
            var (backRaw, backErr) = await session.ReadOneAsync(tag, ct).ConfigureAwait(false);
            double? backValue = backRaw is null ? null
                : Scaling.ToEngineering(ValueCodec.Decode(backRaw, tag.DataType, tag.ByteOrder, tag.StrLen), tag.Scale, tag.Offset);

            bool matched = backRaw is not null && Matches(item.NumericValue, backValue, tag.DataType);
            if (!matched)
            {
                results.Add(new WriteItemResult(tag.Id, tag.Code, false,
                    backErr is not null
                        ? $"回读失败：{backErr.Message}"
                        : $"回读不一致（写入 {item.NumericValue}，回读 {backValue}）：可能为 PLC 程序覆盖、地址错误或被量程限制",
                    oldValue, item.NumericValue, backValue));
                status = "Failed";
                continue;
            }

            if (oldRaw is not null) applied.Add((tag, oldRaw, oldValue));
            results.Add(new WriteItemResult(tag.Id, tag.Code, true, null, oldValue, item.NumericValue, backValue));

            await audit.WriteAsync("write.execute", "tag", tag.Code,
                new { value = oldValue }, new { value = backValue }, "Success", null, userId, username, ip)
                .ConfigureAwait(false);
        }

        // 批量：任一失败 → 已成功点位按逆序补偿回滚
        if (status == "Failed" && applied.Count > 0)
        {
            bool rollbackOk = true;
            foreach (var (tag, oldBytes, oldValue) in Enumerable.Reverse(applied))
            {
                var session = runtime.SessionOfTag(tag.Id);
                if (session is null) { rollbackOk = false; continue; }
                int unit = int.TryParse(tag.Device?.DeviceKey, out int u) ? u : 1;
                var cmd = new WriteCommand(unit, tag.Area, tag.AreaNo, tag.ByteOffset, tag.BitOffset, tag.DataType, oldBytes);
                var res = await session.WriteAsync(cmd, ct).ConfigureAwait(false);
                if (!res.Ok) rollbackOk = false;
                await audit.WriteAsync("write.rollback", "tag", tag.Code, null,
                    new { value = oldValue }, res.Ok ? "Success" : "Failed",
                    res.Error?.Message, userId, username, ip).ConfigureAwait(false);
            }
            status = rollbackOk ? "RolledBack" : "RollbackFailed";
        }

        var batch = new WriteBatch
        {
            BatchId = batchId,
            UserId = userId,
            Username = username,
            Status = status,
            Message = status switch
            {
                "Success" => "全部写入成功并通过回读校验",
                "RolledBack" => "存在失败项，已补偿回滚到写入前的值",
                "RollbackFailed" => "存在失败项且补偿回滚未完全成功，请现场核对 PLC 值",
                _ => "写入失败"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            Items = results.Select(r => new WriteBatchItem
            {
                BatchId = batchId,
                TagId = r.TagId,
                Code = r.Code,
                OldValue = r.OldValue,
                NewValue = r.NewValue,
                ReadBack = r.ReadBack,
                Ok = r.Ok,
                Error = r.Error
            }).ToList()
        };
        db.WriteBatches.Add(batch);
        record.UsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("写入批次 {Batch}：{Status}（{Count} 项）", batchId, status, results.Count);
        return new WriteExecuteResult(batchId, status, batch.Message, results);
    }

    private static bool Matches(double? target, double? actual, TagDataType type)
    {
        if (target is null || actual is null) return false;
        double tol = type switch
        {
            TagDataType.Float32 => Math.Max(Math.Abs(target.Value) * 1e-6, 1e-4),
            TagDataType.Bool => 0,
            _ => 0.5
        };
        return Math.Abs(target.Value - actual.Value) <= tol;
    }

    private static string Hash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
