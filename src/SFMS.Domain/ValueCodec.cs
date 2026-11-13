using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SFMS.Shared;

namespace SFMS.Domain;

/// <summary>
/// 字节序与数值编解码。四种排列按"线序 4 字节 → 规范大端 ABCD"的置换实现
/// （计划书 3.2.3 表），一律使用 BinaryPrimitives，不依赖机器端序。
/// </summary>
public static class ValueCodec
{
    public static int ByteLength(TagDataType type, int strLen = 0) => type switch
    {
        TagDataType.Bool => 1,
        TagDataType.Int16 or TagDataType.UInt16 => 2,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 4,
        TagDataType.String => Math.Clamp(strLen <= 0 ? 16 : strLen, 1, 64),
        _ => 2
    };

    /// <summary>Modbus 占用寄存器数。</summary>
    public static int RegisterCount(TagDataType type, int strLen = 0) =>
        type == TagDataType.Bool ? 1 : (ByteLength(type, strLen) + 1) / 2;

    /// <summary>把线序字节重排为规范大端 ABCD。</summary>
    public static void ToCanonical(ReadOnlySpan<byte> wire, ByteOrderKind order, int width, Span<byte> dest)
    {
        if (width == 2)
        {
            if (order is ByteOrderKind.BADC or ByteOrderKind.DCBA) { dest[0] = wire[1]; dest[1] = wire[0]; }
            else { dest[0] = wire[0]; dest[1] = wire[1]; }
            return;
        }
        switch (order)
        {
            case ByteOrderKind.ABCD:
                dest[0] = wire[0]; dest[1] = wire[1]; dest[2] = wire[2]; dest[3] = wire[3];
                break;
            case ByteOrderKind.CDAB:
                dest[0] = wire[2]; dest[1] = wire[3]; dest[2] = wire[0]; dest[3] = wire[1];
                break;
            case ByteOrderKind.BADC:
                dest[0] = wire[1]; dest[1] = wire[0]; dest[2] = wire[3]; dest[3] = wire[2];
                break;
            default: // DCBA
                dest[0] = wire[3]; dest[1] = wire[2]; dest[2] = wire[1]; dest[3] = wire[0];
                break;
        }
    }

    /// <summary>规范大端 ABCD → 目标线序。</summary>
    public static void FromCanonical(ReadOnlySpan<byte> abcd, ByteOrderKind order, Span<byte> wire)
    {
        switch (order)
        {
            case ByteOrderKind.ABCD:
                abcd[..4].CopyTo(wire);
                break;
            case ByteOrderKind.CDAB:
                wire[0] = abcd[2]; wire[1] = abcd[3]; wire[2] = abcd[0]; wire[3] = abcd[1];
                break;
            case ByteOrderKind.BADC:
                wire[0] = abcd[1]; wire[1] = abcd[0]; wire[2] = abcd[3]; wire[3] = abcd[2];
                break;
            default:
                wire[0] = abcd[3]; wire[1] = abcd[2]; wire[2] = abcd[1]; wire[3] = abcd[0];
                break;
        }
    }

    public static double? Decode(ReadOnlySpan<byte> wire, TagDataType type, ByteOrderKind order, int strLen = 0)
    {
        int need = ByteLength(type, strLen);
        if (wire.Length < need) return null;
        switch (type)
        {
            case TagDataType.Bool:
                return wire[0] != 0 ? 1 : 0;
            case TagDataType.Int16:
            {
                Span<byte> c = stackalloc byte[2];
                ToCanonical(wire, order, 2, c);
                return BinaryPrimitives.ReadInt16BigEndian(c);
            }
            case TagDataType.UInt16:
            {
                Span<byte> c = stackalloc byte[2];
                ToCanonical(wire, order, 2, c);
                return BinaryPrimitives.ReadUInt16BigEndian(c);
            }
            case TagDataType.Int32:
            {
                Span<byte> c = stackalloc byte[4];
                ToCanonical(wire, order, 4, c);
                return BinaryPrimitives.ReadInt32BigEndian(c);
            }
            case TagDataType.UInt32:
            {
                Span<byte> c = stackalloc byte[4];
                ToCanonical(wire, order, 4, c);
                return BinaryPrimitives.ReadUInt32BigEndian(c);
            }
            case TagDataType.Float32:
            {
                Span<byte> c = stackalloc byte[4];
                ToCanonical(wire, order, 4, c);
                double v = BinaryPrimitives.ReadSingleBigEndian(c);
                return double.IsFinite(v) ? Math.Round(v, 5) : null;
            }
            default:
                return null;
        }
    }

    public static string? DecodeString(ReadOnlySpan<byte> wire, TagDataType type, ByteOrderKind order, int strLen)
    {
        if (type != TagDataType.String) return null;
        int need = ByteLength(type, strLen);
        if (wire.Length < need) return null;
        var slice = wire[..need];
        int end = slice.IndexOf((byte)0);
        if (end >= 0) slice = slice[..end];
        return Encoding.ASCII.GetString(slice).Trim();
    }

    /// <summary>原始值 → 线序字节。</summary>
    public static byte[] Encode(double value, TagDataType type, ByteOrderKind order, int strLen = 0)
    {
        switch (type)
        {
            case TagDataType.Bool:
                return [value != 0 ? (byte)1 : (byte)0];
            case TagDataType.Int16:
            {
                Span<byte> c = stackalloc byte[2];
                Span<byte> w = stackalloc byte[2];
                BinaryPrimitives.WriteInt16BigEndian(c, (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
                ToWire(c, order, 2, w);
                return w.ToArray();
            }
            case TagDataType.UInt16:
            {
                Span<byte> c = stackalloc byte[2];
                Span<byte> w = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(c, (ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue));
                ToWire(c, order, 2, w);
                return w.ToArray();
            }
            case TagDataType.Int32:
            case TagDataType.UInt32:
            case TagDataType.Float32:
            {
                Span<byte> c = stackalloc byte[4];
                Span<byte> w = stackalloc byte[4];
                if (type == TagDataType.Float32)
                    BinaryPrimitives.WriteSingleBigEndian(c, (float)value);
                else if (type == TagDataType.Int32)
                    BinaryPrimitives.WriteInt32BigEndian(c, (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue));
                else
                    BinaryPrimitives.WriteUInt32BigEndian(c, (uint)Math.Clamp(Math.Round(value), 0, uint.MaxValue));
                FromCanonical(c, order, w);
                return w.ToArray();
            }
            default:
            {
                var bytes = new byte[ByteLength(TagDataType.String, strLen)];
                var text = Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture));
                Array.Copy(text, bytes, Math.Min(text.Length, bytes.Length));
                return bytes;
            }
        }
    }

    private static void ToWire(ReadOnlySpan<byte> abcd, ByteOrderKind order, int width, Span<byte> wire)
    {
        if (width == 2)
        {
            if (order is ByteOrderKind.BADC or ByteOrderKind.DCBA) { wire[0] = abcd[1]; wire[1] = abcd[0]; }
            else { wire[0] = abcd[0]; wire[1] = abcd[1]; }
            return;
        }
        FromCanonical(abcd, order, wire);
    }

    /// <summary>同一段原始字节在四种字节序下的解析值（在线调试页并排显示）。</summary>
    public static Dictionary<string, string?> InterpretFour(ReadOnlySpan<byte> raw, TagDataType type)
    {
        var dict = new Dictionary<string, string?>();
        // 缓冲区在循环外分配一次，避免 stackalloc 落在循环内（CA2014）
        var buffer = new byte[4];
        foreach (var o in new[] { ByteOrderKind.ABCD, ByteOrderKind.CDAB, ByteOrderKind.BADC, ByteOrderKind.DCBA })
        {
            try
            {
                string key = o.ToString();
                if (type == TagDataType.Float32)
                {
                    if (raw.Length < 4) { dict[key] = null; continue; }
                    ToCanonical(raw, o, 4, buffer);
                    dict[key] = BinaryPrimitives.ReadSingleBigEndian(buffer).ToString("0.###", CultureInfo.InvariantCulture);
                }
                else if (type is TagDataType.Int32 or TagDataType.UInt32)
                {
                    if (raw.Length < 4) { dict[key] = null; continue; }
                    ToCanonical(raw, o, 4, buffer);
                    dict[key] = type == TagDataType.Int32
                        ? BinaryPrimitives.ReadInt32BigEndian(buffer).ToString(CultureInfo.InvariantCulture)
                        : BinaryPrimitives.ReadUInt32BigEndian(buffer).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    if (raw.Length < 2) { dict[key] = null; continue; }
                    ToCanonical(raw, o, 2, buffer);
                    dict[key] = type == TagDataType.Int16
                        ? BinaryPrimitives.ReadInt16BigEndian(buffer).ToString(CultureInfo.InvariantCulture)
                        : BinaryPrimitives.ReadUInt16BigEndian(buffer).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                dict[o.ToString()] = null;
            }
        }
        return dict;
    }
}
