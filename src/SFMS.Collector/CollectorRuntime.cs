using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SFMS.Domain;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Data;
using SFMS.Infrastructure.Realtime;
using SFMS.Protocols;
using SFMS.Shared;

namespace SFMS.Collector;

/// <summary>
/// 采集运行时：负责配置热加载、会话注册表、时间轮推进与诊断聚合。
/// 单进程部署时由 SFMS.Api 直接托管；拆分部署时由 Collector.Host 托管并通过 Redis 共享实时值。
/// </summary>
public sealed class CollectorRuntime : IAsyncDisposable
{
    private readonly IDbContextFactory<SfmsDbContext> _factory;
    private readonly SettingsService _settings;
    private readonly ILogger<CollectorRuntime> _logger;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private Dictionary<int, EndpointSession> _sessions = new();
    private Dictionary<int, Tag> _tags = new();
    private Dictionary<int, Device> _devices = new();
    private Dictionary<int, Endpoint> _endpoints = new();
    private Dictionary<int, ScanGroup> _groups = new();
    private long _configVersion = -1;
    private DateTimeOffset _lastTick = DateTimeOffset.UtcNow;
    private readonly Queue<double> _tickIntervals = new();

    public IRealtimeStore Store { get; }
    public AlarmEngine Alarms { get; }
    public HistoryWriter History { get; }

    public CollectorRuntime(IDbContextFactory<SfmsDbContext> factory, SettingsService settings,
        IRealtimeStore store, AlarmEngine alarms, HistoryWriter history, ILogger<CollectorRuntime> logger)
    {
        _factory = factory;
        _settings = settings;
        Store = store;
        Alarms = alarms;
        History = history;
        _logger = logger;
    }

    public IReadOnlyDictionary<int, EndpointSession> Sessions => _sessions;
    public IReadOnlyDictionary<int, Tag> Tags => _tags;
    public IReadOnlyDictionary<int, Device> Devices => _devices;
    public int SessionCount => _sessions.Count;
    public int TagCount => _tags.Count;
    public bool Started { get; private set; }

    public async Task ReloadAsync(bool force, CancellationToken ct)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            long version = long.TryParse(await _settings.GetAsync(SettingsService.ConfigVersionKey), out long v) ? v : 0;
            if (!force && version == _configVersion) return;
            _configVersion = version;

            var endpoints = await db.Endpoints.AsNoTracking().Where(e => !e.IsDeleted && e.Enabled).ToListAsync(ct).ConfigureAwait(false);
            var devices = await db.Devices.AsNoTracking().Where(d => !d.IsDeleted && d.Enabled).ToListAsync(ct).ConfigureAwait(false);
            var tags = await db.Tags.AsNoTracking().Where(t => !t.IsDeleted && t.Enabled).ToListAsync(ct).ConfigureAwait(false);
            var groups = await db.ScanGroups.AsNoTracking().Where(g => g.Enabled).ToListAsync(ct).ConfigureAwait(false);
            var rules = await db.AlarmRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct).ConfigureAwait(false);

            _endpoints = endpoints.ToDictionary(e => e.Id);
            _devices = devices.ToDictionary(d => d.Id);
            _tags = tags.ToDictionary(t => t.Id);
            _groups = groups.ToDictionary(g => g.Id);

            // 重建会话：先停旧的
            foreach (var old in _sessions.Values)
                await old.DisposeAsync().ConfigureAwait(false);

            var sessions = new Dictionary<int, EndpointSession>();
            var groupMap = _groups;
            foreach (var ep in endpoints.Where(e => e.ScanEnabled))
            {
                var epDevices = devices.Where(d => d.EndpointId == ep.Id).ToList();
                var devIds = epDevices.Select(d => d.Id).ToHashSet();
                var epTags = tags.Where(t => devIds.Contains(t.DeviceId)).ToList();
                if (epTags.Count == 0) continue;

                EndpointSession? session = null;
                session = new EndpointSession(ep, epDevices, epTags, groupMap,
                    onValue: OnValue,
                    onOffline: (e, _) => Alarms.OnEndpointOffline(e, session?.Diag.LastError),
                    onOnline: e => Alarms.OnEndpointOnline(e),
                    onOverload: (e, gid, msg) => Alarms.OnOverload(e, gid, msg));
                sessions[ep.Id] = session;
                session.Start();
            }

            _sessions = sessions;
            Alarms.Load(rules, tags);
            // 配置变更可能删除/停用点位：同步剔除实时存储中的残留，否则快照条数会大于点位总数
            Store.Prune(_tags.Keys.ToArray());
            Started = true;
            _logger.LogInformation("采集配置已加载 v{Version}：{Endpoints} 端点 / {Tags} 点位 / {Groups} 分组",
                version, sessions.Count, tags.Count, groups.Count);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private void OnValue(int tagId, double? value, string? text, Quality quality, DateTimeOffset ts, string source)
    {
        Store.Set(tagId, value, text, quality, ts, source);
        if (!_tags.TryGetValue(tagId, out var tag)) return;
        History.Offer(tag, value, text, quality, ts);
        if (source == "scan") Alarms.OnValue(tag, value, quality);
    }

    /// <summary>时间轮推进（50ms 一次）。</summary>
    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        double interval = (now - _lastTick).TotalMilliseconds;
        _lastTick = now;
        lock (_tickIntervals)
        {
            _tickIntervals.Enqueue(interval);
            while (_tickIntervals.Count > 200) _tickIntervals.Dequeue();
        }

        foreach (var session in _sessions.Values)
            session.Tick(now);
    }

    public double TickJitterMs
    {
        get
        {
            lock (_tickIntervals)
            {
                if (_tickIntervals.Count == 0) return 0;
                return Math.Round(_tickIntervals.Max() - 50, 1);
            }
        }
    }

    /// <summary>在线端点数量。取自运行时连接状态——数据库 endpoint.status 是静态配置字段，不随采集更新。</summary>
    public int OnlineEndpointCount => _sessions.Values.Count(s => s.Online);

    public EndpointSession? SessionOfTag(int tagId) =>
        _tags.TryGetValue(tagId, out var tag)
            ? _sessions.Values.FirstOrDefault(s => s.Tags.Any(t => t.Id == tagId)) ?? null
            : null;

    public EndpointSession? SessionOfEndpoint(int endpointId) =>
        _sessions.TryGetValue(endpointId, out var s) ? s : null;

    /// <summary>端点连接测试（FR-01）：只建连不采集，返回协商信息。</summary>
    public async Task<(bool Ok, string Message, int MaxPdu)> TestConnectionAsync(Endpoint endpoint, CancellationToken ct)
    {
        var driver = DriverFactory.Create(endpoint.Protocol);
        try
        {
            var conn = EndpointConnection.From(endpoint.Protocol, endpoint.Ip, endpoint.Port, endpoint.ConnParams);
            var err = await driver.ConnectAsync(conn, ct).ConfigureAwait(false);
            return err is null
                ? (true, $"连接成功（{driver.Describe()}）", driver.MaxPduSize)
                : (false, err.Message, 0);
        }
        finally
        {
            await driver.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>在线调试（FR-13）：即时读取一个点位，给出原始字节与四种字节序解析值。</summary>
    public async Task<DebugReadResult> DebugReadAsync(int tagId, CancellationToken ct)
    {
        if (!_tags.TryGetValue(tagId, out var tag))
            throw new InvalidOperationException($"点位 {tagId} 不存在");
        var session = SessionOfTag(tagId) ?? throw new InvalidOperationException("该点位所属端点未启用采集");

        var (raw, err) = await session.ReadOneAsync(tag, ct).ConfigureAwait(false);
        if (raw is null || err is not null)
            return new DebugReadResult(tag.Id, tag.Code, tag.AddressExpr, tag.DataType.ToText(),
                tag.ByteOrder.ToString(), "", null, (int)Quality.Bad, err?.Message, new Dictionary<string, string?>());

        double? value = ValueCodec.Decode(raw, tag.DataType, tag.ByteOrder, tag.StrLen);
        double? eng = Scaling.ToEngineering(value, tag.Scale, tag.Offset);
        var interp = ValueCodec.InterpretFour(raw, tag.DataType);
        string? text = ValueCodec.DecodeString(raw, tag.DataType, tag.ByteOrder, tag.StrLen);
        return new DebugReadResult(tag.Id, tag.Code, tag.AddressExpr, tag.DataType.ToText(),
            tag.ByteOrder.ToString(), Convert.ToHexString(raw), eng?.ToString("0.###") ?? text,
            (int)Quality.Good, null, interp);
    }

    public IReadOnlyList<DiagEndpointDto> Diagnostics()
    {
        var list = new List<DiagEndpointDto>();
        foreach (var ep in _endpoints.Values.OrderBy(e => e.Code))
        {
            _sessions.TryGetValue(ep.Id, out var s);
            var devIds = _devices.Values.Where(d => d.EndpointId == ep.Id).Select(d => d.Id).ToHashSet();
            var tags = _tags.Values.Where(t => devIds.Contains(t.DeviceId)).ToList();
            int online = 0, bad = 0;
            foreach (var t in tags)
            {
                if (Store.TryGet(t.Id, out var item) && item.Quality == (int)Quality.Good) online++;
                else bad++;
            }

            list.Add(new DiagEndpointDto(
                ep.Id, ep.Code, ep.Name, ep.Protocol.ToText(), ep.Ip, ep.Port,
                s is null ? "未采集" : s.Online ? "在线" : "离线",
                s?.LastOkAt, s?.Diag.Requests ?? 0, s?.Diag.Success ?? 0, s?.Diag.Timeouts ?? 0,
                s?.Diag.Errors ?? 0, Math.Round(s?.Diag.AvgMs ?? 0, 1), s?.Diag.P95Ms() ?? 0,
                s?.Diag.ConsecutiveFailures ?? 0, s?.Diag.LastError, online, bad, tags.Count));
        }
        return list;
    }

    public IReadOnlyDictionary<int, int> QueueDepths =>
        _sessions.ToDictionary(kv => kv.Key, kv => kv.Value.QueueDepth);

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
            await s.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
        _reloadGate.Dispose();
    }
}
