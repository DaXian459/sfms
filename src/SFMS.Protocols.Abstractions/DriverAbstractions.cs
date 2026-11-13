using SFMS.Shared;

namespace SFMS.Protocols;

/// <summary>端点连接参数（机架/槽号、超时、重试、CPU 型号）。</summary>
public sealed record EndpointConnection(
    ProtocolType Protocol,
    string Ip,
    int Port,
    int Rack = 0,
    int Slot = 1,
    int TimeoutMs = 1000,
    int RetryCount = 1,
    string CpuType = "S71500",
    string? Serial = null)
{
    public static EndpointConnection From(ProtocolType protocol, string ip, int port, string? connParamsJson)
    {
        int rack = 0, slot = 1, timeout = 1000, retry = 1;
        string cpu = protocol == ProtocolType.S7 ? "S71500" : "";
        string? serial = null;
        if (!string.IsNullOrWhiteSpace(connParamsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(connParamsJson);
                var r = doc.RootElement;
                if (r.TryGetProperty("rack", out var v)) rack = v.GetInt32();
                if (r.TryGetProperty("slot", out v)) slot = v.GetInt32();
                if (r.TryGetProperty("timeoutMs", out v)) timeout = v.GetInt32();
                if (r.TryGetProperty("retry", out v)) retry = v.GetInt32();
                if (r.TryGetProperty("cpu", out v)) cpu = v.GetString() ?? cpu;
                if (r.TryGetProperty("serial", out v)) serial = v.GetString();
            }
            catch (System.Text.Json.JsonException)
            {
                // 连接参数格式错误时退化为默认值，由连接测试暴露问题
            }
        }
        return new EndpointConnection(protocol, ip, port, rack, slot, Math.Clamp(timeout, 200, 30000),
            Math.Clamp(retry, 0, 5), cpu, serial);
    }
}

/// <summary>一次块读：Unit 为 Modbus 从站地址，S7 忽略。</summary>
public sealed record ReadSpan(int Unit, TagArea Area, int AreaNo, int Offset, int Length)
{
    public string Key => $"{Unit}|{Area}|{AreaNo}|{Offset}|{Length}";
}

public enum DriverErrorCategory
{
    None = 0,
    Network = 1,
    Timeout = 2,
    Protocol = 3,
    Address = 4,
    Permission = 5,
    Unsupported = 6
}

public sealed record DriverError(DriverErrorCategory Category, string Code, string Message)
{
    public static readonly DriverError None = new(DriverErrorCategory.None, "OK", "OK");
    public bool IsFatal => Category is DriverErrorCategory.Network or DriverErrorCategory.Timeout;
}

public sealed record ReadOutcome(ReadSpan Span, byte[]? Data, DriverError? Error)
{
    public bool Ok => Error is null || Error.Category == DriverErrorCategory.None;
}

/// <summary>一次写命令；Bool 通过 BitOffset 定位字节内的位。</summary>
public sealed record WriteCommand(int Unit, TagArea Area, int AreaNo, int Offset, int BitOffset,
    TagDataType DataType, byte[] Data);

public sealed record WriteOutcome(WriteCommand Command, bool Ok, DriverError? Error, byte[]? ReadBack);

/// <summary>
/// 协议驱动统一接口（计划书 3.2）：上层调度器不感知具体协议，
/// 驱动只负责"读一批地址、写一个地址"，返回统一的质量与错误分类。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    ProtocolType Protocol { get; }
    bool IsConnected { get; }
    int MaxPduSize { get; }
    Task<DriverError?> ConnectAsync(EndpointConnection conn, CancellationToken ct);
    Task DisconnectAsync();
    Task<IReadOnlyList<ReadOutcome>> ReadAsync(IReadOnlyList<ReadSpan> spans, CancellationToken ct);
    Task<IReadOnlyList<WriteOutcome>> WriteAsync(IReadOnlyList<WriteCommand> commands, CancellationToken ct);
    /// <summary>连接建立后协商到的能力信息，用于诊断页展示。</summary>
    string Describe();
}

public static class DriverLimits
{
    public const int ModbusMaxReadRegisters = 125;
    public const int ModbusMaxReadBits = 2000;
    public const int ModbusMaxWriteRegisters = 123;
    public const int ModbusMaxWriteBits = 1968;

    /// <summary>按 S7 协商 PDU 预分包：请求 19+12n、响应 14+4n+数据（计划书 3.2.1）。</summary>
    public static int MaxBlockBytes(int maxPduSize)
    {
        int usable = maxPduSize - 32;
        return Math.Clamp(usable, 32, 900);
    }

    public static List<ReadSpan> SplitForModbus(ReadSpan span)
    {
        bool bitArea = span.Area is TagArea.Coil or TagArea.DiscreteInput;
        int unitSize = bitArea ? 8 : 2;                       // 位区按字节切
        int limit = bitArea ? ModbusMaxReadBits : ModbusMaxReadRegisters;
        var list = new List<ReadSpan>();
        if (span.Length <= limit)
        {
            list.Add(span);
            return list;
        }
        int offset = span.Offset;
        int remaining = span.Length;
        while (remaining > 0)
        {
            int take = Math.Min(remaining, limit);
            list.Add(span with { Offset = offset, Length = take });
            offset += take;
            remaining -= take;
        }
        return list;
    }
}
