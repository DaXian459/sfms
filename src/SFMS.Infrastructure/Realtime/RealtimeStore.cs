using System.Collections.Concurrent;
using SFMS.Domain;
using SFMS.Shared;

namespace SFMS.Infrastructure.Realtime;

/// <summary>
/// 实时值存储（对应计划书 3.5 的"实时数据：内存 + Redis，最近 30 分钟"）。
/// 单进程部署用内存实现；拆分部署时用 Redis 实现同一接口（见 RedisRealtimeStore）。
/// </summary>
public interface IRealtimeStore
{
    void Set(int tagId, double? value, string? text, Quality quality, DateTimeOffset ts, string source);
    bool TryGet(int tagId, out RealtimeItem item);
    IReadOnlyList<RealtimeItem> GetMany(IEnumerable<int> tagIds);
    IReadOnlyCollection<RealtimeItem> All();
    long Version { get; }
    int Count { get; }
    void Load(IEnumerable<TagLatest> rows);

    /// <summary>剔除已停用/删除的点位，避免实时存储与点位表长期不一致（快照数会多于点位总数）。</summary>
    void Prune(IReadOnlyCollection<int> aliveTagIds);
}

public sealed class InMemoryRealtimeStore : IRealtimeStore
{
    private readonly ConcurrentDictionary<int, RealtimeItem> _items = new();
    private long _version;

    public long Version => Interlocked.Read(ref _version);
    public int Count => _items.Count;

    public void Set(int tagId, double? value, string? text, Quality quality, DateTimeOffset ts, string source)
    {
        _items[tagId] = new RealtimeItem(tagId, value, text, (int)quality, ts);
        Interlocked.Increment(ref _version);
    }

    public bool TryGet(int tagId, out RealtimeItem item) => _items.TryGetValue(tagId, out item!);

    public IReadOnlyList<RealtimeItem> GetMany(IEnumerable<int> tagIds)
    {
        var list = new List<RealtimeItem>();
        foreach (int id in tagIds)
            if (_items.TryGetValue(id, out var item)) list.Add(item);
        return list;
    }

    public IReadOnlyCollection<RealtimeItem> All() => _items.Values.ToList();

    public void Load(IEnumerable<TagLatest> rows)
    {
        foreach (var r in rows)
            _items[r.TagId] = new RealtimeItem(r.TagId, r.Value, r.Text, (int)r.Quality, r.Ts);
    }

    public void Prune(IReadOnlyCollection<int> aliveTagIds)
    {
        var alive = aliveTagIds as ISet<int> ?? aliveTagIds.ToHashSet();
        foreach (int key in _items.Keys)
            if (!alive.Contains(key)) _items.TryRemove(key, out _);
    }
}

/// <summary>诊断计数：请求数、成功、超时、按错误码、平均/分位延迟（固定桶）。</summary>
public sealed class EndpointDiagnostics
{
    private static readonly double[] Buckets = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000];
    private readonly long[] _histogram = new long[Buckets.Length + 1];
    private long _requests, _success, _timeouts, _errors, _totalMs;
    private readonly ConcurrentDictionary<string, long> _errorCodes = new();
    private readonly object _lock = new();

    public long Requests => Interlocked.Read(ref _requests);
    public long Success => Interlocked.Read(ref _success);
    public long Timeouts => Interlocked.Read(ref _timeouts);
    public long Errors => Interlocked.Read(ref _errors);
    public double AvgMs => Requests == 0 ? 0 : (double)Interlocked.Read(ref _totalMs) / Requests;
    public IReadOnlyDictionary<string, long> ErrorCodes => _errorCodes;
    public DateTimeOffset? LastOkAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }

    public void RecordOk(double elapsedMs)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _success);
        Interlocked.Add(ref _totalMs, (long)elapsedMs);
        lock (_lock)
        {
            int idx = Array.FindIndex(Buckets, b => elapsedMs <= b);
            _histogram[idx < 0 ? Buckets.Length : idx]++;
        }
    }

    public void RecordFailure(DriverErrorLike error, double elapsedMs)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _totalMs, (long)elapsedMs);
        if (error.IsTimeout) Interlocked.Increment(ref _timeouts);
        else Interlocked.Increment(ref _errors);
        _errorCodes.AddOrUpdate(error.Code, 1, (_, v) => v + 1);
        LastError = error.Message;
        lock (_lock)
        {
            int idx = Array.FindIndex(Buckets, b => elapsedMs <= b);
            _histogram[idx < 0 ? Buckets.Length : idx]++;
        }
    }

    public double P95Ms()
    {
        long total;
        long[] snapshot;
        lock (_lock)
        {
            total = _histogram.Sum();
            snapshot = (long[])_histogram.Clone();
        }
        if (total == 0) return 0;
        long target = (long)Math.Ceiling(total * 0.95);
        long running = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            running += snapshot[i];
            if (running >= target) return i < Buckets.Length ? Buckets[i] : Buckets[^1];
        }
        return Buckets[^1];
    }

    public void ResetMinute()
    {
        // 分钟级窗口：保留累计值用于诊断页，这里只重置分位桶避免无限增长
        lock (_lock)
        {
            Array.Clear(_histogram);
        }
    }
}

public readonly record struct DriverErrorLike(bool IsTimeout, string Code, string Message);
