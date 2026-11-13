using System.Diagnostics;
using System.Threading.Channels;
using SFMS.Domain;
using SFMS.Infrastructure.Realtime;
using SFMS.Protocols;
using SFMS.Protocols.Modbus;
using SFMS.Protocols.Siemens;
using SFMS.Shared;

namespace SFMS.Collector;

public static class DriverFactory
{
    public static IDeviceDriver Create(ProtocolType protocol) =>
        protocol == ProtocolType.S7 ? new S7Driver() : new ModbusDriver();
}

public sealed record ScanJob(int GroupId, DateTimeOffset DueAt);
public sealed record WriteJob(WriteCommand Command, TaskCompletionSource<WriteOutcome> Completion);

/// <summary>一组点位的调度状态（时间轮到期 + 过载降级）。</summary>
public sealed class GroupPlan(ScanGroup group, List<Tag> tags)
{
    public ScanGroup Group { get; } = group;
    public List<Tag> Tags { get; } = tags;
    public DateTimeOffset NextDue { get; set; } = DateTimeOffset.UtcNow;
    public bool Running { get; set; }
    public int Misses { get; set; }
    public int SkipFactor { get; set; } = 1;
    public long Scans { get; set; }
    public long Overloads { get; set; }
}

/// <summary>
/// 一个端点的会话（计划书 3.4.1）：单连接、串行访问；
/// 读任务走周期队列，写任务走独立高优先队列，保证写入响应不受采集负载影响。
/// </summary>
public sealed class EndpointSession : IAsyncDisposable
{
    private readonly Channel<ScanJob> _scanQueue;
    private readonly Channel<WriteJob> _writeQueue;
    private readonly Dictionary<int, GroupPlan> _groups = new();
    private readonly Dictionary<int, Device> _devices;
    private readonly Dictionary<int, Tag> _tags;
    private readonly EndpointConnection _connection;
    private readonly Action<int, double?, string?, Quality, DateTimeOffset, string> _onValue;
    private readonly Action<Endpoint, Device?>? _onOffline;
    private readonly Action<Endpoint>? _onOnline;
    private readonly Action<Endpoint, int, string>? _onOverload;
    private readonly CancellationTokenSource _cts = new();

    private DateTimeOffset _nextConnectAt = DateTimeOffset.MinValue;
    private int _backoffSeconds = 1;
    private Task? _loop;

    public Endpoint Endpoint { get; }
    public IDeviceDriver Driver { get; }
    public EndpointDiagnostics Diag { get; } = new();
    public bool Online { get; private set; }
    public DateTimeOffset? LastOkAt => Diag.LastOkAt;
    public IReadOnlyDictionary<int, GroupPlan> Groups => _groups;
    public int TagCount => _tags.Count;
    public int QueueDepth => _scanQueue.Reader.Count;

    public EndpointSession(
        Endpoint endpoint,
        IReadOnlyList<Device> devices,
        IReadOnlyList<Tag> tags,
        IReadOnlyDictionary<int, ScanGroup> groups,
        Action<int, double?, string?, Quality, DateTimeOffset, string> onValue,
        Action<Endpoint, Device?>? onOffline = null,
        Action<Endpoint>? onOnline = null,
        Action<Endpoint, int, string>? onOverload = null)
    {
        Endpoint = endpoint;
        _onValue = onValue;
        _onOffline = onOffline;
        _onOnline = onOnline;
        _onOverload = onOverload;
        _devices = devices.ToDictionary(d => d.Id);
        _tags = tags.Where(t => t.Enabled).ToDictionary(t => t.Id);
        _connection = EndpointConnection.From(endpoint.Protocol, endpoint.Ip, endpoint.Port, endpoint.ConnParams);
        Driver = DriverFactory.Create(endpoint.Protocol);

        _scanQueue = Channel.CreateBounded<ScanJob>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _writeQueue = Channel.CreateBounded<WriteJob>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        foreach (var g in tags.Where(t => t.Enabled).GroupBy(t => t.ScanGroupId))
        {
            if (!groups.TryGetValue(g.Key, out var sg)) continue;
            _groups[g.Key] = new GroupPlan(sg, g.ToList());
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>时间轮每 50ms 调用一次：到期分组入队；上一次未完成则计 miss。</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var plan in _groups.Values)
        {
            if (plan.NextDue > now) continue;
            if (plan.Running)
            {
                plan.Misses++;
                if (plan.Misses >= 3 && plan.SkipFactor < 8)
                {
                    plan.SkipFactor = Math.Min(8, plan.SkipFactor * 2);
                    plan.Overloads++;
                    _onOverload?.Invoke(Endpoint, plan.Group.Id, $"采集过载：分组 {plan.Group.Name} 连续 {plan.Misses} 个周期未完成，降级为每 {plan.SkipFactor} 个周期执行");
                    plan.Misses = 0;
                }
                plan.NextDue = now.AddMilliseconds(plan.Group.PeriodMs);
                continue;
            }

            plan.Running = true;
            plan.NextDue = now.AddMilliseconds(plan.Group.PeriodMs * plan.SkipFactor);
            if (!_scanQueue.Writer.TryWrite(new ScanJob(plan.Group.Id, now)))
            {
                plan.Running = false;                     // 队列满：视为一次错过
                plan.Misses++;
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);

                bool didWork = false;
                while (_writeQueue.Reader.TryRead(out var write))
                {
                    await ExecuteWriteAsync(write, ct).ConfigureAwait(false);
                    didWork = true;
                }
                while (_scanQueue.Reader.TryRead(out var scan))
                {
                    await ExecuteScanAsync(scan, ct).ConfigureAwait(false);
                    didWork = true;
                    if (_writeQueue.Reader.TryPeek(out _)) break;   // 有写任务插入时优先处理
                }
                if (!didWork)
                    await Task.Delay(10, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Diag.LastError = ex.Message;
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (Driver.IsConnected) return;
        var now = DateTimeOffset.UtcNow;
        if (now < _nextConnectAt) return;

        var sw = Stopwatch.StartNew();
        var err = await Driver.ConnectAsync(_connection, ct).ConfigureAwait(false);
        sw.Stop();

        if (err is null)
        {
            bool wasOffline = !Online;
            Online = true;
            _backoffSeconds = 1;
            Diag.ConsecutiveFailures = 0;
            Diag.LastOkAt = DateTimeOffset.UtcNow;
            Diag.LastError = null;
            if (wasOffline) _onOnline?.Invoke(Endpoint);
            return;
        }

        Online = false;
        Diag.ConsecutiveFailures++;
        Diag.RecordFailure(new DriverErrorLike(err.Category == DriverErrorCategory.Timeout, err.Code, err.Message), sw.Elapsed.TotalMilliseconds);
        _nextConnectAt = DateTimeOffset.UtcNow.AddSeconds(_backoffSeconds);
        _backoffSeconds = Math.Min(30, _backoffSeconds * 2);

        MarkAllBad(err);
        foreach (var plan in _groups.Values)
        {
            plan.Running = false;
            plan.NextDue = DateTimeOffset.UtcNow.AddMilliseconds(plan.Group.PeriodMs);
        }
        _onOffline?.Invoke(Endpoint, null);
    }

    private void MarkAllBad(DriverError err)
    {
        var quality = err.Category == DriverErrorCategory.Timeout ? Quality.Timeout : Quality.Bad;
        var now = DateTimeOffset.UtcNow;
        foreach (var tag in _tags.Values)
            _onValue(tag.Id, null, null, quality, now, "offline");
    }

    private async Task ExecuteScanAsync(ScanJob job, CancellationToken ct)
    {
        if (!_groups.TryGetValue(job.GroupId, out var plan)) return;
        var sw = Stopwatch.StartNew();
        try
        {
            var alive = plan.Tags.Where(t =>
                _devices.TryGetValue(t.DeviceId, out var d) && d.Enabled && !d.Paused).ToList();

            var paused = plan.Tags.Except(alive).ToList();
            foreach (var t in paused)
                _onValue(t.Id, null, null, Quality.Uncertain, DateTimeOffset.UtcNow, "paused");

            if (alive.Count == 0) return;

            var withUnits = alive.Select(t => (Tag: t, Unit: UnitOf(t))).ToList();
            var (spans, slices) = SpanBuilder.Build(withUnits, Endpoint.Protocol,
                plan.Group.MergeGap, Math.Max(32, DriverLimits.MaxBlockBytes(Driver.MaxPduSize)));

            var outcomes = await Driver.ReadAsync(spans, ct).ConfigureAwait(false);
            var dataBySpan = outcomes.ToDictionary(o => o.Span.Key, o => o);

            DriverError? fatal = null;
            foreach (var slice in slices)
            {
                var span = spans[slice.SpanIndex];
                if (!dataBySpan.TryGetValue(span.Key, out var outcome) || outcome.Error is not null)
                {
                    var err = outcome?.Error ?? new DriverError(DriverErrorCategory.Protocol, "NO_RESULT", "无读取结果");
                    if (err.IsFatal) fatal = err;
                    var q = err.Category == DriverErrorCategory.Timeout ? Quality.Timeout : Quality.Bad;
                    _onValue(slice.Tag.Id, null, null, q, DateTimeOffset.UtcNow, "scan");
                    continue;
                }

                int byteLen = ValueCodec.ByteLength(slice.Tag.DataType, slice.Tag.StrLen);
                if (slice.Tag.DataType == TagDataType.Bool) byteLen = 1;
                if (!SpanBuilder.TryExtract(outcome.Data, slice, slice.Tag.Area, byteLen, out var raw, out _))
                {
                    _onValue(slice.Tag.Id, null, null, Quality.Bad, DateTimeOffset.UtcNow, "scan");
                    continue;
                }

                double? value = ValueCodec.Decode(raw, slice.Tag.DataType, slice.Tag.ByteOrder, slice.Tag.StrLen);
                string? text = ValueCodec.DecodeString(raw, slice.Tag.DataType, slice.Tag.ByteOrder, slice.Tag.StrLen);
                double? eng = Scaling.ToEngineering(value, slice.Tag.Scale, slice.Tag.Offset);

                var quality = Quality.Good;
                if (eng is { } v2)
                {
                    if (slice.Tag.MinValue is { } min && v2 < min - 1e-9) quality = Quality.Uncertain;
                    else if (slice.Tag.MaxValue is { } max && v2 > max + 1e-9) quality = Quality.Uncertain;
                }
                _onValue(slice.Tag.Id, eng, text, quality, DateTimeOffset.UtcNow, "scan");
            }

            sw.Stop();
            Diag.RecordOk(sw.Elapsed.TotalMilliseconds);
            Diag.LastOkAt = DateTimeOffset.UtcNow;
            Online = true;
            plan.Scans++;

            if (fatal is not null && fatal.Category == DriverErrorCategory.Network)
            {
                Online = false;
                MarkAllBad(fatal);
                _onOffline?.Invoke(Endpoint, null);
            }
            if (plan.Misses > 0) plan.Misses = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Diag.RecordFailure(new DriverErrorLike(false, "SCAN_EX", ex.Message), sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            plan.Running = false;
            if (plan.Misses == 0 && plan.SkipFactor > 1) plan.SkipFactor = Math.Max(1, plan.SkipFactor / 2);
        }
    }

    private int UnitOf(Tag tag)
    {
        if (Endpoint.Protocol == ProtocolType.S7) return 0;
        if (_devices.TryGetValue(tag.DeviceId, out var d) && int.TryParse(d.DeviceKey, out int unit)) return unit;
        return 1;
    }

    // ---------------------------------------------------------------- 写
    public async Task<WriteOutcome> WriteAsync(WriteCommand command, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<WriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeQueue.Writer.TryWrite(new WriteJob(command, tcs)))
            return new WriteOutcome(command, false, new DriverError(DriverErrorCategory.Protocol, "WRITE_QUEUE_FULL", "写队列已满"), null);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ExecuteWriteAsync(WriteJob job, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!Driver.IsConnected)
            {
                job.Completion.TrySetResult(new WriteOutcome(job.Command, false,
                    new DriverError(DriverErrorCategory.Network, "OFFLINE", "端点离线，写入快速失败"), null));
                return;
            }

            var results = await Driver.WriteAsync([job.Command], ct).ConfigureAwait(false);
            var result = results.FirstOrDefault() ?? new WriteOutcome(job.Command, false,
                new DriverError(DriverErrorCategory.Protocol, "NO_RESULT", "无写入结果"), null);
            sw.Stop();
            if (result.Ok) Diag.RecordOk(sw.Elapsed.TotalMilliseconds);
            else if (result.Error is not null)
                Diag.RecordFailure(new DriverErrorLike(result.Error.Category == DriverErrorCategory.Timeout, result.Error.Code, result.Error.Message), sw.Elapsed.TotalMilliseconds);
            job.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            job.Completion.TrySetResult(new WriteOutcome(job.Command, false,
                new DriverError(DriverErrorCategory.Protocol, "WRITE_EX", ex.Message), null));
        }
    }

    /// <summary>直读一个点位（绕过缓存与合并），用于写后回读校验与在线调试。</summary>
    public async Task<(byte[]? Raw, DriverError? Error)> ReadOneAsync(Tag tag, CancellationToken ct)
    {
        int byteLen = tag.DataType == TagDataType.Bool ? 1 : ValueCodec.ByteLength(tag.DataType, tag.StrLen);
        var span = Endpoint.Protocol == ProtocolType.S7
            ? new ReadSpan(0, tag.Area, tag.AreaNo, tag.ByteOffset, byteLen)
            : SpanBuilder.IsBitArea(Endpoint.Protocol, tag.Area)
                ? new ReadSpan(UnitOf(tag), tag.Area, 0, tag.ByteOffset, 1)
                : new ReadSpan(UnitOf(tag), tag.Area, 0, tag.ByteOffset, ValueCodec.RegisterCount(tag.DataType, tag.StrLen));

        var outcomes = await Driver.ReadAsync([span], ct).ConfigureAwait(false);
        var o = outcomes.FirstOrDefault();
        if (o is null) return (null, new DriverError(DriverErrorCategory.Protocol, "NO_RESULT", "无读取结果"));
        if (o.Error is not null) return (null, o.Error);
        if (o.Data is null) return (null, new DriverError(DriverErrorCategory.Protocol, "NO_DATA", "无数据"));
        if (SpanBuilder.IsBitArea(Endpoint.Protocol, tag.Area))
        {
            int bit = tag.ByteOffset - span.Offset;
            byte bitValue = (byte)(((o.Data[bit / 8] >> (bit % 8)) & 1) == 1 ? 1 : 0);
            return ([bitValue], null);
        }
        if (tag.DataType == TagDataType.Bool && tag.BitOffset > 0)
        {
            byte bitValue = (byte)(((o.Data[0] >> tag.BitOffset) & 1) == 1 ? 1 : 0);
            return ([bitValue], null);
        }
        return (o.Data, null);
    }

    public IReadOnlyList<Tag> Tags => _tags.Values.ToList();

    public Device? DeviceOf(int tagId) =>
        _tags.TryGetValue(tagId, out var tag) && _devices.TryGetValue(tag.DeviceId, out var d) ? d : null;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await Driver.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
