using S7.Net;
using SFMS.Shared;

namespace SFMS.Protocols.Siemens;

/// <summary>
/// S7 驱动（S7NetPlus 0.20.0 封装），覆盖 S7-1200 / S7-1500 / S7-200 SMART。
///
/// 关键约束（计划书 3.2.1）：同一 CPU 必须"单连接、串行访问"，因此所有请求经同一信号量；
/// 库内对多变量请求有 PDU 硬校验，超出会直接抛异常，故块读大小的计算放到采集端聚合器里，
/// 本驱动只做"给定块读一次"。
/// </summary>
public sealed class S7Driver : IDeviceDriver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Plc? _plc;
    private EndpointConnection _conn = new(ProtocolType.S7, "127.0.0.1", 102);
    private bool _disposed;

    public ProtocolType Protocol => ProtocolType.S7;
    public bool IsConnected => _plc?.IsConnected == true;
    public int MaxPduSize => _plc?.MaxPDUSize ?? 240;

    public string Describe() =>
        $"S7 {_conn.CpuType} {_conn.Ip}:{_conn.Port} 机架={_conn.Rack} 槽={_conn.Slot} PDU={(IsConnected ? MaxPduSize : 0)} 超时={_conn.TimeoutMs}ms";

    public async Task<DriverError?> ConnectAsync(EndpointConnection conn, CancellationToken ct)
    {
        _conn = conn;
        var cpu = ParseCpu(conn.CpuType);
        if (cpu == CpuType.S7200 && string.IsNullOrWhiteSpace(conn.Serial))
        {
            return new DriverError(DriverErrorCategory.Unsupported, "S7_200_SERIAL",
                "经典 S7-200 无以太网口：请按计划书 3.2.2 路径 A 经串口服务器转 Modbus RTU over TCP 接入");
        }

        await DisconnectAsync().ConfigureAwait(false);
        try
        {
            // 必须使用带 port 的重载：Plc(cpu,ip,rack,slot) 的端口固定为 102，
            // 连不到串口服务器 / NAT 映射 / 故障代理端口（Plc.Port 为只读，无法后置赋值）。
            var plc = new Plc(cpu, conn.Ip, conn.Port, (short)conn.Rack, (short)conn.Slot)
            {
                ReadTimeout = conn.TimeoutMs,
                WriteTimeout = conn.TimeoutMs
            };
            await plc.OpenAsync(ct).ConfigureAwait(false);
            _plc = plc;
            return null;
        }
        catch (OperationCanceledException)
        {
            return new DriverError(DriverErrorCategory.Timeout, "S7_CONNECT_TIMEOUT", "连接超时");
        }
        catch (PlcException ex)
        {
            return MapException(ex, "连接");
        }
        catch (Exception ex)
        {
            return new DriverError(DriverErrorCategory.Network, "S7_CONNECT_EX", ex.Message);
        }
    }

    public Task DisconnectAsync()
    {
        try
        {
            _plc?.Close();
        }
        catch (Exception)
        {
            // 关闭异常忽略
        }
        _plc = null;
        return Task.CompletedTask;
    }

    private static CpuType ParseCpu(string? text) => (text ?? "").Trim().ToUpperInvariant() switch
    {
        "S7200" or "S7-200" => CpuType.S7200,
        "S7200SMART" or "S7-200 SMART" or "S7200 SMART" or "SMART200" => CpuType.S7200Smart,
        "S7300" => CpuType.S7300,
        "S7400" => CpuType.S7400,
        "S71200" or "S7-1200" => CpuType.S71200,
        "S7200_CP243" => CpuType.S7200,
        _ => CpuType.S71500
    };

    private static S7.Net.DataType MapArea(TagArea area) => area switch
    {
        TagArea.Db => S7.Net.DataType.DataBlock,
        TagArea.Memory => S7.Net.DataType.Memory,
        TagArea.Input => S7.Net.DataType.Input,
        TagArea.Output => S7.Net.DataType.Output,
        _ => S7.Net.DataType.DataBlock
    };

    public async Task<IReadOnlyList<ReadOutcome>> ReadAsync(IReadOnlyList<ReadSpan> spans, CancellationToken ct)
    {
        var results = new List<ReadOutcome>(spans.Count);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plc = _plc;
            if (plc is null || !plc.IsConnected)
            {
                var err = new DriverError(DriverErrorCategory.Network, "S7_NOT_CONNECTED", "未连接");
                return spans.Select(s => new ReadOutcome(s, null, err)).ToList();
            }

            foreach (var span in spans)
            {
                try
                {
                    var data = await plc.ReadBytesAsync(MapArea(span.Area), span.AreaNo, span.Offset, span.Length, ct)
                        .ConfigureAwait(false);
                    results.Add(new ReadOutcome(span, data, null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    results.Add(new ReadOutcome(span, null, MapException(ex, "读取")));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    public async Task<IReadOnlyList<WriteOutcome>> WriteAsync(IReadOnlyList<WriteCommand> commands, CancellationToken ct)
    {
        var results = new List<WriteOutcome>(commands.Count);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plc = _plc;
            if (plc is null || !plc.IsConnected)
            {
                var err = new DriverError(DriverErrorCategory.Network, "S7_NOT_CONNECTED", "未连接");
                return commands.Select(c => new WriteOutcome(c, false, err, null)).ToList();
            }

            foreach (var cmd in commands)
            {
                try
                {
                    if (cmd.DataType == TagDataType.Bool)
                    {
                        // 位写：读所在字节 → 置/清位 → 写回，避免覆盖同字节其它位
                        var current = await plc.ReadBytesAsync(MapArea(cmd.Area), cmd.AreaNo, cmd.Offset, 1, ct)
                            .ConfigureAwait(false);
                        byte b = current[0];
                        byte mask = (byte)(1 << cmd.BitOffset);
                        b = cmd.Data[0] != 0 ? (byte)(b | mask) : (byte)(b & ~mask);
                        await plc.WriteBytesAsync(MapArea(cmd.Area), cmd.AreaNo, cmd.Offset, new[] { b }, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await plc.WriteBytesAsync(MapArea(cmd.Area), cmd.AreaNo, cmd.Offset, cmd.Data, ct)
                            .ConfigureAwait(false);
                    }
                    results.Add(new WriteOutcome(cmd, true, null, null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    results.Add(new WriteOutcome(cmd, false, MapException(ex, "写入"), null));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    /// <summary>
    /// S7NetPlus 对错误码只给文本，这里按消息前缀归一为分类；
    /// 越界/权限类错误同时提示"非优化块 + PUT/GET"这一最常见的现场前置条件。
    /// </summary>
    private static DriverError MapException(Exception ex, string action)
    {
        string msg = ex.Message ?? "";
        string lower = msg.ToLowerInvariant();

        if (ex is PlcException pex)
        {
            string code = pex.ErrorCode.ToString();
            if (code.Contains("Connection", StringComparison.OrdinalIgnoreCase))
                return new DriverError(DriverErrorCategory.Network, "S7_CONNECTION", $"{action}失败：连接已断开（{msg}）");
            if (code.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                return new DriverError(DriverErrorCategory.Timeout, "S7_TIMEOUT", $"{action}超时（{msg}）");
            if (code.Contains("Address", StringComparison.OrdinalIgnoreCase))
                return new DriverError(DriverErrorCategory.Address, "S7_ADDRESS",
                    $"{action}失败：地址越界或不合法（{msg}）。请核对 DB 号/偏移，并确认数据块已关闭「优化的块访问」");
            return new DriverError(DriverErrorCategory.Protocol, "S7_" + code.ToUpperInvariant(), $"{action}失败：{msg}");
        }

        if (lower.Contains("out of range") || lower.Contains("address"))
            return new DriverError(DriverErrorCategory.Address, "S7_ADDRESS_OUT_OF_RANGE",
                $"{action}失败：地址越界（{msg}）。1200/1500 需确认数据块为非优化访问，并已勾选允许 PUT/GET");
        if (lower.Contains("not allowed") || lower.Contains("denied") || lower.Contains("access"))
            return new DriverError(DriverErrorCategory.Permission, "S7_ACCESS_DENIED",
                $"{action}失败：PLC 拒绝访问（{msg}）。请在 TIA Portal 勾选「允许来自远程对象的 PUT/GET 通信访问」");
        if (lower.Contains("timeout") || lower.Contains("timed out"))
            return new DriverError(DriverErrorCategory.Timeout, "S7_TIMEOUT", $"{action}超时（{msg}）");
        if (lower.Contains("socket") || lower.Contains("connect") || lower.Contains("network"))
            return new DriverError(DriverErrorCategory.Network, "S7_NETWORK", $"{action}失败：链路异常（{msg}）");
        return new DriverError(DriverErrorCategory.Protocol, "S7_ERROR", $"{action}失败：{msg}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
