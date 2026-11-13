using System.Buffers;
using Npgsql;
using NpgsqlTypes;
using SFMS.Shared;

namespace SFMS.Infrastructure.Data;

/// <summary>
/// 历史数据访问（时序四表由 SQL 脚本建成分区表，这里走原生 SQL 与 COPY，
/// 避免 EF 变更跟踪开销，也避免"EF 建普通表再改分区"的返工）。
/// </summary>
public sealed class HistoryRepository(NpgsqlDataSource dataSource)
{
    public async Task WriteAsync(IReadOnlyList<HistoryPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return;
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var importer = await conn.BeginBinaryImportAsync(
            "COPY tag_history (tag_id, ts, value_num, value_text, quality, seq) FROM STDIN (FORMAT BINARY)", ct)
            .ConfigureAwait(false);

        long seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var p in points)
        {
            await importer.StartRowAsync(ct).ConfigureAwait(false);
            await importer.WriteAsync(p.TagId, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
            await importer.WriteAsync(p.Ts.UtcDateTime, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            if (p.Value.HasValue)
                await importer.WriteAsync(p.Value.Value, NpgsqlDbType.Double, ct).ConfigureAwait(false);
            else
                await importer.WriteNullAsync(ct).ConfigureAwait(false);
            if (p.Text is null)
                await importer.WriteNullAsync(ct).ConfigureAwait(false);
            else
                await importer.WriteAsync(p.Text, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await importer.WriteAsync((short)p.Quality, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
            await importer.WriteAsync(seq++, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
        }
        await importer.CompleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>跨度 ≤24h 查原始表，>24h 查 1 分钟聚合表；超过 maxPoints 时按时间桶在库内降采样。</summary>
    public async Task<IReadOnlyList<HistoryPoint>> QueryAsync(IReadOnlyList<int> tagIds, DateTimeOffset from, DateTimeOffset to,
        int maxPoints, CancellationToken ct)
    {
        if (tagIds.Count == 0) return Array.Empty<HistoryPoint>();
        var span = to - from;
        bool useAgg = span.TotalHours > 24;

        double seconds = Math.Max(span.TotalSeconds, 1);
        double bucketSeconds = Math.Max(1, Math.Ceiling(seconds / Math.Max(50, maxPoints / Math.Max(1, tagIds.Count))));
        bool downsample = span.TotalSeconds / bucketSeconds > 50;

        string sql;
        if (useAgg)
        {
            sql = """
                SELECT tag_id, bucket AS ts, avg_value AS value_num, NULL::text AS value_text, worst_quality AS quality
                FROM tag_agg_1m
                WHERE tag_id = ANY(@tags) AND bucket >= @from AND bucket < @to
                ORDER BY tag_id, bucket
                """;
        }
        else if (downsample)
        {
            sql = $"""
                SELECT tag_id,
                       date_bin(@bucket::interval, ts, TIMESTAMPTZ '2000-01-01') AS ts,
                       avg(value_num) AS value_num,
                       NULL::text AS value_text,
                       max(quality)::smallint AS quality
                FROM tag_history
                WHERE tag_id = ANY(@tags) AND ts >= @from AND ts < @to
                GROUP BY tag_id, 2
                ORDER BY tag_id, 2
                """;
        }
        else
        {
            sql = """
                SELECT tag_id, ts, value_num, value_text, quality::smallint
                FROM tag_history
                WHERE tag_id = ANY(@tags) AND ts >= @from AND ts < @to
                ORDER BY tag_id, ts
                """;
        }

        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = tagIds.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.UtcDateTime });
        if (!useAgg && downsample)
            cmd.Parameters.Add(new NpgsqlParameter("bucket", NpgsqlDbType.Text) { Value = $"{bucketSeconds} seconds" });

        var list = new List<HistoryPoint>(1024);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HistoryPoint(
                reader.GetInt32(0),
                new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt16(4)));
        }
        return list;
    }

    public async Task<int> RollupMinuteAsync(DateTimeOffset bucket, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT sfms_rollup_minute(@b)");
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.TimestampTz) { Value = bucket.UtcDateTime });
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int i ? i : 0;
    }

    public async Task<int> EnsurePartitionsAsync(int monthsAhead, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT sfms_ensure_partitions(@m)");
        cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Integer) { Value = monthsAhead });
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int i ? i : 0;
    }

    public async Task<int> PurgeOldAsync(int monthsKeep, int auditMonths, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT sfms_drop_old_partitions(@k, @a)");
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Integer) { Value = monthsKeep });
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Integer) { Value = auditMonths });
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is int i ? i : 0;
    }

    public async Task WriteDiagMinuteAsync(DateTimeOffset bucket, int endpointId, long requests, long success,
        long timeouts, long errors, double avgMs, double p95Ms, string? errorCodes, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO endpoint_diag_minute (bucket, endpoint_id, requests, success, timeouts, errors, avg_ms, p95_ms, error_codes)
            VALUES (@b, @e, @r, @s, @t, @err, @avg, @p95, @codes)
            ON CONFLICT (bucket, endpoint_id) DO UPDATE
              SET requests = EXCLUDED.requests, success = EXCLUDED.success, timeouts = EXCLUDED.timeouts,
                  errors = EXCLUDED.errors, avg_ms = EXCLUDED.avg_ms, p95_ms = EXCLUDED.p95_ms,
                  error_codes = EXCLUDED.error_codes
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.TimestampTz) { Value = bucket.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Integer) { Value = endpointId });
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Bigint) { Value = requests });
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Bigint) { Value = success });
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Bigint) { Value = timeouts });
        cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlDbType.Bigint) { Value = errors });
        cmd.Parameters.Add(new NpgsqlParameter("avg", NpgsqlDbType.Double) { Value = avgMs });
        cmd.Parameters.Add(new NpgsqlParameter("p95", NpgsqlDbType.Double) { Value = p95Ms });
        cmd.Parameters.Add(new NpgsqlParameter("codes", NpgsqlDbType.Text) { Value = (object?)errorCodes ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
