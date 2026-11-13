using System.Buffers.Binary;
using System.Net.Sockets;
using SFMS.Shared;
using ProtocolType = SFMS.Shared.ProtocolType;

namespace SFMS.Protocols.Modbus;

/// <summary>
/// Modbus 驱动（自研封装，支持 Modbus TCP 与 RTU-over-TCP）。
///
/// 为什么自研而不直接用 NModbus：现场需要按**请求级**区分超时/异常码/链路错误，
/// 并对同一端点的读写严格串行；同时 RTU-over-TCP 需要自定义帧边界。自研约 300 行，
/// 换来对事务号、超时、异常码映射的完全控制。
/// </summary>
public sealed class ModbusDriver : IDeviceDriver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private EndpointConnection _conn = new(ProtocolType.ModbusTcp, "127.0.0.1", 502);
    private ushort _tid;
    private bool _disposed;

    public ProtocolType Protocol => _conn.Protocol;
    public bool IsConnected => _client?.Connected == true && _stream is not null;
    public int MaxPduSize => 253;

    public string Describe() =>
        $"{Protocol.ToText()} {_conn.Ip}:{_conn.Port} 超时={_conn.TimeoutMs}ms 重试={_conn.RetryCount}";

    public async Task<DriverError?> ConnectAsync(EndpointConnection conn, CancellationToken ct)
    {
        _conn = conn;
        if (conn.Protocol == ProtocolType.ModbusRtu)
            return new DriverError(DriverErrorCategory.Unsupported, "MB_RTU_SERIAL",
                "本期不接物理串口，请使用串口服务器转 RTU over TCP（协议选 MODBUS_RTU_OVER_TCP）");

        await DisposeSocketAsync().ConfigureAwait(false);
        try
        {
            var client = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_conn.TimeoutMs * 2);
            await client.ConnectAsync(_conn.Ip, _conn.Port, cts.Token).ConfigureAwait(false);
            client.ReceiveTimeout = _conn.TimeoutMs;
            client.SendTimeout = _conn.TimeoutMs;
            _client = client;
            _stream = client.GetStream();
            return null;
        }
        catch (OperationCanceledException)
        {
            return new DriverError(DriverErrorCategory.Timeout, "MB_CONNECT_TIMEOUT", $"连接 {_conn.Ip}:{_conn.Port} 超时");
        }
        catch (SocketException ex)
        {
            return new DriverError(DriverErrorCategory.Network, "MB_CONNECT_SOCKET", $"连接失败：{ex.Message}");
        }
    }

    public Task DisconnectAsync() => DisposeSocketAsync();

    private async Task DisposeSocketAsync()
    {
        try { _stream?.Dispose(); } catch (Exception) { /* 关闭异常忽略 */ }
        try { _client?.Close(); } catch (Exception) { /* 关闭异常忽略 */ }
        _stream = null;
        _client = null;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReadOutcome>> ReadAsync(IReadOnlyList<ReadSpan> spans, CancellationToken ct)
    {
        var results = new List<ReadOutcome>(spans.Count);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var span in spans)
            {
                foreach (var sub in DriverLimits.SplitForModbus(span))
                {
                    results.Add(await ReadOneAsync(sub, ct).ConfigureAwait(false));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    private async Task<ReadOutcome> ReadOneAsync(ReadSpan span, CancellationToken ct)
    {
        if (!IsConnected) return new ReadOutcome(span, null, new DriverError(DriverErrorCategory.Network, "MB_NOT_CONNECTED", "未连接"));
        byte fc = span.Area switch
        {
            TagArea.Holding => 0x03,
            TagArea.InputRegister => 0x04,
            TagArea.Coil => 0x01,
            TagArea.DiscreteInput => 0x02,
            _ => 0x03
        };
        int count = span.Length;
        var pdu = new byte[5];
        pdu[0] = fc;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)span.Offset);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)count);

        var (resp, err) = await TransactAsync(span.Unit, pdu, ct).ConfigureAwait(false);
        if (err is not null) return new ReadOutcome(span, null, err);
        if (resp.Length < 2) return new ReadOutcome(span, null, ProtocolErr("MB_SHORT_RESP", "响应过短"));

        if (resp[0] == (fc | 0x80))
            return new ReadOutcome(span, null, MapException(resp.Length > 1 ? resp[1] : (byte)4));

        if (resp[0] != fc) return new ReadOutcome(span, null, ProtocolErr("MB_FC_MISMATCH", $"功能码不匹配：期望 {fc} 实际 {resp[0]}"));

        int byteCount = resp[1];
        if (resp.Length < 2 + byteCount)
            return new ReadOutcome(span, null, ProtocolErr("MB_LEN_MISMATCH", "响应长度不足"));
        var data = resp.AsSpan(2, byteCount).ToArray();
        return new ReadOutcome(span, data, null);
    }

    public async Task<IReadOnlyList<WriteOutcome>> WriteAsync(IReadOnlyList<WriteCommand> commands, CancellationToken ct)
    {
        var results = new List<WriteOutcome>(commands.Count);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var cmd in commands)
                results.Add(await WriteOneAsync(cmd, ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    private async Task<WriteOutcome> WriteOneAsync(WriteCommand cmd, CancellationToken ct)
    {
        if (!IsConnected)
            return new WriteOutcome(cmd, false, new DriverError(DriverErrorCategory.Network, "MB_NOT_CONNECTED", "未连接"), null);

        try
        {
            byte[] pdu;
            if (cmd.Area == TagArea.Coil)
            {
                pdu = new byte[5];
                pdu[0] = 0x05;
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)cmd.Offset);
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), cmd.Data[0] != 0 ? (ushort)0xFF00 : (ushort)0x0000);
            }
            else if (cmd.Area == TagArea.Holding && cmd.BitOffset > 0)
            {
                // 寄存器内位写：读-改-写（满足"同字节多位不被覆盖"）
                var readSpan = new ReadSpan(cmd.Unit, TagArea.Holding, 0, cmd.Offset, 1);
                var readBack = await ReadOneAsync(readSpan, ct).ConfigureAwait(false);
                if (!readBack.Ok || readBack.Data is null || readBack.Data.Length < 2)
                    return new WriteOutcome(cmd, false, readBack.Error ?? ProtocolErr("MB_RMW_READ", "位写前读取失败"), null);
                ushort reg = BinaryPrimitives.ReadUInt16BigEndian(readBack.Data);
                ushort mask = (ushort)(1 << cmd.BitOffset);
                reg = cmd.Data[0] != 0 ? (ushort)(reg | mask) : (ushort)(reg & ~mask);
                pdu = new byte[5];
                pdu[0] = 0x06;
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)cmd.Offset);
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), reg);
            }
            else if (cmd.Area == TagArea.Holding)
            {
                if (cmd.Data.Length == 2)
                {
                    pdu = new byte[5];
                    pdu[0] = 0x06;
                    BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)cmd.Offset);
                    pdu[3] = cmd.Data[0];
                    pdu[4] = cmd.Data[1];
                }
                else
                {
                    // 32 位数据用 FC16 写 2 个寄存器
                    int regs = (cmd.Data.Length + 1) / 2;
                    pdu = new byte[6 + regs * 2];
                    pdu[0] = 0x10;
                    BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), (ushort)cmd.Offset);
                    BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)regs);
                    pdu[5] = (byte)(regs * 2);
                    Array.Copy(cmd.Data, 0, pdu, 6, cmd.Data.Length);
                }
            }
            else
            {
                return new WriteOutcome(cmd, false, new DriverError(DriverErrorCategory.Address, "MB_READONLY_AREA",
                    $"{cmd.Area.ToText()} 区不支持写入"), null);
            }

            var (resp, err) = await TransactAsync(cmd.Unit, pdu, ct).ConfigureAwait(false);
            if (err is not null) return new WriteOutcome(cmd, false, err, null);
            if (resp.Length >= 2 && resp[0] == (pdu[0] | 0x80))
                return new WriteOutcome(cmd, false, MapException(resp[1]), null);
            if (resp.Length < 5 || resp[0] != pdu[0])
                return new WriteOutcome(cmd, false, ProtocolErr("MB_WRITE_RESP", "写响应异常"), null);
            return new WriteOutcome(cmd, true, null, cmd.Data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WriteOutcome(cmd, false, new DriverError(DriverErrorCategory.Protocol, "MB_WRITE_EX", ex.Message), null);
        }
    }

    // ------------------------------------------------------------ 事务
    private async Task<(byte[] Resp, DriverError? Err)> TransactAsync(int unit, byte[] pdu, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return (Array.Empty<byte>(), new DriverError(DriverErrorCategory.Network, "MB_NO_STREAM", "连接不可用"));

        int attempt = 0;
        DriverError? last = null;
        while (attempt <= _conn.RetryCount)
        {
            attempt++;
            try
            {
                if (_conn.Protocol == ProtocolType.ModbusRtuOverTcp)
                    await stream.WriteAsync(BuildRtu(unit, pdu), ct).ConfigureAwait(false);
                else
                    await stream.WriteAsync(BuildMbap(unit, pdu), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                var resp = await ReadResponseAsync(stream, ct).ConfigureAwait(false);
                if (_conn.Protocol == ProtocolType.ModbusRtuOverTcp)
                {
                    if (resp.Length < 4)
                        return (Array.Empty<byte>(), new DriverError(DriverErrorCategory.Protocol, "MB_RTU_SHORT", "RTU 帧过短"));
                    // 注意：异步方法里不能声明 Span 局部变量，这里用数组切片与手工拼 CRC
                    byte[] body = new byte[resp.Length - 2];
                    Array.Copy(resp, body, body.Length);
                    ushort crc = (ushort)(resp[^2] | (resp[^1] << 8));
                    if (Crc16(body) != crc)
                        return (Array.Empty<byte>(), new DriverError(DriverErrorCategory.Protocol, "MB_RTU_CRC", "RTU 帧 CRC 校验失败"));
                    resp = body[1..];                              // 去掉从站地址，统一按 PDU 处理
                }
                return (resp, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                last = new DriverError(DriverErrorCategory.Timeout, "MB_TIMEOUT", "响应超时");
            }
            catch (IOException ex)
            {
                last = new DriverError(DriverErrorCategory.Network, "MB_IO", $"链路异常：{ex.Message}");
                await DisposeSocketAsync().ConfigureAwait(false);
                return (Array.Empty<byte>(), last);
            }
            catch (SocketException ex)
            {
                last = new DriverError(DriverErrorCategory.Network, "MB_SOCKET", $"链路异常：{ex.Message}");
                await DisposeSocketAsync().ConfigureAwait(false);
                return (Array.Empty<byte>(), last);
            }
        }
        return (Array.Empty<byte>(), last);
    }

    private byte[] BuildMbap(int unit, byte[] pdu)
    {
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, ++_tid);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = (byte)unit;
        pdu.CopyTo(frame, 7);
        return frame;
    }

    private static byte[] BuildRtu(int unit, byte[] pdu)
    {
        var body = new byte[1 + pdu.Length + 2];
        body[0] = (byte)unit;
        pdu.CopyTo(body, 1);
        ushort crc = Crc16(body.AsSpan(0, body.Length - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(body.Length - 2), crc);
        return body;
    }

    private async Task<byte[]> ReadResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_conn.TimeoutMs);

        if (_conn.Protocol == ProtocolType.ModbusRtuOverTcp)
        {
            // RTU 无长度字段：先读地址 + 功能码，再按功能码推断剩余长度
            var buf = new List<byte>(32);
            var two = new byte[2];
            await ReadExactAsync(stream, two, cts.Token).ConfigureAwait(false);
            buf.AddRange(two);
            byte fc = buf[1];
            if ((fc & 0x80) != 0)
            {
                var rest = new byte[3];                       // 异常码 + CRC
                await ReadExactAsync(stream, rest, cts.Token).ConfigureAwait(false);
                buf.AddRange(rest);
            }
            else if (fc is 0x01 or 0x02 or 0x03 or 0x04)
            {
                var bc = new byte[1];
                await ReadExactAsync(stream, bc, cts.Token).ConfigureAwait(false);
                buf.Add(bc[0]);
                var rest = new byte[bc[0] + 2];               // 数据 + CRC
                await ReadExactAsync(stream, rest, cts.Token).ConfigureAwait(false);
                buf.AddRange(rest);
            }
            else
            {
                var rest = new byte[6];                       // addr2 + qty2 + CRC2
                await ReadExactAsync(stream, rest, cts.Token).ConfigureAwait(false);
                buf.AddRange(rest);
            }
            return buf.ToArray();
        }

        var head = new byte[7];
        await ReadExactAsync(stream, head, cts.Token).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        if (len < 2 || len > 260) throw new IOException($"MBAP 长度非法：{len}");
        var body = new byte[len - 1];
        await ReadExactAsync(stream, body, cts.Token).ConfigureAwait(false);
        return body;
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("对端关闭连接");
            total += n;
        }
        return total;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    private static DriverError ProtocolErr(string code, string msg) =>
        new(DriverErrorCategory.Protocol, code, msg);

    /// <summary>Modbus 异常码分类（计划书 3.2.3）：01/02 配置错误不重试，03/04 设备侧。</summary>
    public static DriverError MapException(byte code) => code switch
    {
        0x01 => new DriverError(DriverErrorCategory.Address, "MB_EX_ILLEGAL_FUNCTION", "非法功能码（01）"),
        0x02 => new DriverError(DriverErrorCategory.Address, "MB_EX_ILLEGAL_ADDRESS", "非法数据地址（02）"),
        0x03 => new DriverError(DriverErrorCategory.Protocol, "MB_EX_ILLEGAL_VALUE", "非法数据值（03）"),
        0x04 => new DriverError(DriverErrorCategory.Protocol, "MB_EX_SLAVE_FAILURE", "从站设备故障（04）"),
        0x05 => new DriverError(DriverErrorCategory.Protocol, "MB_EX_ACK", "确认（05）"),
        0x06 => new DriverError(DriverErrorCategory.Protocol, "MB_EX_BUSY", "从站忙（06）"),
        0x0A => new DriverError(DriverErrorCategory.Network, "MB_EX_GATEWAY_PATH", "网关路径不可用（0A）"),
        0x0B => new DriverError(DriverErrorCategory.Network, "MB_EX_GATEWAY_TARGET", "网关目标设备无响应（0B）"),
        _ => new DriverError(DriverErrorCategory.Protocol, $"MB_EX_{code:X2}", $"从站异常码 {code}")
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeSocketAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
