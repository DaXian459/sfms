using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SFMS.Domain;
using SFMS.Infrastructure.Data;
using SFMS.Shared;

namespace SFMS.Collector;

/// <summary>
/// 历史数据写入（计划书 3.5）：变化存储 + 每 5 分钟强制快照。
/// 死区默认取量程的 0.5%；BOOL 仅在变化时记录；质量变化强制记录。
/// 批量 COPY 落库，DB 故障时写本地 spool 文件，恢复后回放。
/// </summary>
public sealed class HistoryWriter(HistoryRepository repository, ILogger<HistoryWriter> logger)
{
    private sealed record LastValue(double? Value, string? Text, DateTimeOffset At, Quality Quality);

    private readonly Channel<HistoryPoint> _queue = Channel.CreateBounded<HistoryPoint>(new BoundedChannelOptions(50_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Dictionary<int, LastValue> _last = new();
    private readonly object _lock = new();
    private readonly string _spoolDir = Path.Combine(AppContext.BaseDirectory, "spool");
    private long _written;
    private long _dropped;
    private DateTimeOffset _lastSpoolWarning = DateTimeOffset.MinValue;

    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);
    public int QueueDepth => _queue.Reader.Count;

    /// <summary>死区 + 快照判定，满足任一条件才落库。</summary>
    public void Offer(Tag tag, double? value, string? text, Quality quality, DateTimeOffset ts)
    {
        bool store;
        lock (_lock)
        {
            if (!_last.TryGetValue(tag.Id, out var prev))
            {
                store = true;
            }
            else
            {
                double deadband = tag.Deadband;
                if (tag.MinValue is { } min && tag.MaxValue is { } max && max > min)
                    deadband = Math.Max(deadband, (max - min) * 0.005);

                bool qualityChanged = prev.Quality != quality;
                bool boolChanged = tag.DataType == TagDataType.Bool && prev.Value != value;
                bool exceeded = value.HasValue && prev.Value.HasValue && Math.Abs(value.Value - prev.Value.Value) > deadband;
                bool snapshot = ts - prev.At >= TimeSpan.FromMinutes(5);
                store = qualityChanged || boolChanged || exceeded || snapshot;
            }

            if (store) _last[tag.Id] = new LastValue(value, text, ts, quality);
        }

        if (store && !_queue.Writer.TryWrite(new HistoryPoint(tag.Id, ts, value, text, (int)quality)))
            Interlocked.Increment(ref _dropped);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_spoolDir);
        await ReplaySpoolAsync(ct).ConfigureAwait(false);

        var buffer = new List<HistoryPoint>(1000);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                while (buffer.Count < 1000 && _queue.Reader.TryRead(out var p)) buffer.Add(p);
                if (buffer.Count == 0) continue;

                try
                {
                    await repository.WriteAsync(buffer, ct).ConfigureAwait(false);
                    Interlocked.Add(ref _written, buffer.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "历史落库失败，转写 spool（{Count} 条）", buffer.Count);
                    await SpoolAsync(buffer).ConfigureAwait(false);
                }
                buffer.Clear();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "历史写入循环异常");
            }
        }

        // 退出前尽量刷盘
        while (_queue.Reader.TryRead(out var p)) buffer.Add(p);
        if (buffer.Count > 0)
        {
            try { await repository.WriteAsync(buffer, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { await SpoolAsync(buffer).ConfigureAwait(false); }
        }
    }

    private async Task SpoolAsync(IReadOnlyList<HistoryPoint> points)
    {
        try
        {
            Directory.CreateDirectory(_spoolDir);
            string path = Path.Combine(_spoolDir, $"history-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            foreach (var p in points)
                await writer.WriteLineAsync(JsonSerializer.Serialize(p)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "写 spool 失败，{Count} 条历史数据丢失", points.Count);
        }
    }

    private async Task ReplaySpoolAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_spoolDir)) return;
        foreach (var file in Directory.GetFiles(_spoolDir, "history-*.jsonl"))
        {
            try
            {
                var points = new List<HistoryPoint>();
                foreach (var line in await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var p = JsonSerializer.Deserialize<HistoryPoint>(line);
                    if (p is not null) points.Add(p);
                }
                if (points.Count > 0)
                {
                    await repository.WriteAsync(points, ct).ConfigureAwait(false);
                    logger.LogInformation("回放 spool {File}：{Count} 条", Path.GetFileName(file), points.Count);
                }
                File.Delete(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "回放 spool 失败：{File}", file);
            }
        }
    }
}
