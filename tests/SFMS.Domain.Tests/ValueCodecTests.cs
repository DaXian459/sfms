using SFMS.Domain;
using SFMS.Shared;
using Xunit;

namespace SFMS.Domain.Tests;

/// <summary>四种字节序与数值编解码（计划书 3.2.3）。220.5f 的规范大端为 43 5C 80 00。</summary>
public class ValueCodecTests
{
    private static readonly byte[] Abcd220 = [0x43, 0x5C, 0x80, 0x00];

    [Theory]
    [InlineData(TagDataType.Bool, 1)]
    [InlineData(TagDataType.Int16, 2)]
    [InlineData(TagDataType.UInt16, 2)]
    [InlineData(TagDataType.Int32, 4)]
    [InlineData(TagDataType.UInt32, 4)]
    [InlineData(TagDataType.Float32, 4)]
    public void 字节长度(TagDataType type, int expected)
    {
        Assert.Equal(expected, ValueCodec.ByteLength(type));
    }

    [Fact]
    public void 字符串长度按声明截断到上限()
    {
        Assert.Equal(8, ValueCodec.ByteLength(TagDataType.String, 8));
        Assert.Equal(16, ValueCodec.ByteLength(TagDataType.String, 0));
        Assert.Equal(64, ValueCodec.ByteLength(TagDataType.String, 999));
    }

    [Fact]
    public void 寄存器占用数()
    {
        Assert.Equal(1, ValueCodec.RegisterCount(TagDataType.Bool));
        Assert.Equal(1, ValueCodec.RegisterCount(TagDataType.Int16));
        Assert.Equal(2, ValueCodec.RegisterCount(TagDataType.Float32));
        Assert.Equal(3, ValueCodec.RegisterCount(TagDataType.String, 6));
    }

    [Theory]
    [InlineData(ByteOrderKind.ABCD, new byte[] { 0x43, 0x5C, 0x80, 0x00 })]
    [InlineData(ByteOrderKind.CDAB, new byte[] { 0x80, 0x00, 0x43, 0x5C })]
    [InlineData(ByteOrderKind.BADC, new byte[] { 0x5C, 0x43, 0x00, 0x80 })]
    [InlineData(ByteOrderKind.DCBA, new byte[] { 0x00, 0x80, 0x5C, 0x43 })]
    public void 四种字节序都还原为同一浮点值(ByteOrderKind order, byte[] wire)
    {
        Assert.Equal(220.5, ValueCodec.Decode(wire, TagDataType.Float32, order)!.Value, 5);
    }

    [Theory]
    [InlineData(ByteOrderKind.ABCD, new byte[] { 0x43, 0x5C, 0x80, 0x00 })]
    [InlineData(ByteOrderKind.CDAB, new byte[] { 0x80, 0x00, 0x43, 0x5C })]
    [InlineData(ByteOrderKind.BADC, new byte[] { 0x5C, 0x43, 0x00, 0x80 })]
    [InlineData(ByteOrderKind.DCBA, new byte[] { 0x00, 0x80, 0x5C, 0x43 })]
    public void 编码输出符合目标线序(ByteOrderKind order, byte[] expected)
    {
        Assert.Equal(expected, ValueCodec.Encode(220.5, TagDataType.Float32, order));
    }

    [Fact]
    public void 规范大端与线序互为逆变换()
    {
        foreach (var order in Enum.GetValues<ByteOrderKind>())
        {
            var wire = ValueCodec.Encode(1234.5, TagDataType.Float32, order);
            Span<byte> canonical = stackalloc byte[4];
            ValueCodec.ToCanonical(wire, order, 4, canonical);
            Assert.Equal(Abcd220.Length, canonical.Length);
            Assert.Equal(ValueCodec.Encode(1234.5, TagDataType.Float32, ByteOrderKind.ABCD), canonical.ToArray());
        }
    }

    [Fact]
    public void 有符号16位负数往返()
    {
        var wire = ValueCodec.Encode(-1234, TagDataType.Int16, ByteOrderKind.ABCD);
        Assert.Equal(new byte[] { 0xFB, 0x2E }, wire);
        Assert.Equal(-1234, ValueCodec.Decode(wire, TagDataType.Int16, ByteOrderKind.ABCD)!.Value, 5);
    }

    [Fact]
    public void 十六位数据只区分寄存器内字节序()
    {
        // BADC 与 DCBA 对 16 位等价：都按字节对调处理
        var a = ValueCodec.Encode(4660, TagDataType.UInt16, ByteOrderKind.BADC);
        var b = ValueCodec.Encode(4660, TagDataType.UInt16, ByteOrderKind.DCBA);
        Assert.Equal(a, b);
        Assert.Equal(4660, ValueCodec.Decode(a, TagDataType.UInt16, ByteOrderKind.BADC)!.Value, 5);
    }

    [Fact]
    public void 无符号32位()
    {
        var wire = ValueCodec.Encode(4000000000, TagDataType.UInt32, ByteOrderKind.CDAB);
        Assert.Equal(4000000000, ValueCodec.Decode(wire, TagDataType.UInt32, ByteOrderKind.CDAB)!.Value, 0);
    }

    [Fact]
    public void 布尔编码与解码()
    {
        Assert.Equal(new byte[] { 1 }, ValueCodec.Encode(1, TagDataType.Bool, ByteOrderKind.ABCD));
        Assert.Equal(new byte[] { 0 }, ValueCodec.Encode(0, TagDataType.Bool, ByteOrderKind.ABCD));
        Assert.Equal(1, ValueCodec.Decode(new byte[] { 7 }, TagDataType.Bool, ByteOrderKind.ABCD)!.Value, 5);
    }

    [Fact]
    public void 字节不足返回空()
    {
        Assert.Null(ValueCodec.Decode(new byte[] { 0x43 }, TagDataType.Float32, ByteOrderKind.ABCD));
        Assert.Null(ValueCodec.Decode(new byte[] { 0xFB }, TagDataType.Int16, ByteOrderKind.ABCD));
    }

    [Fact]
    public void 字符串解码遇到零截断()
    {
        var raw = new byte[] { (byte)'A', (byte)'B', 0, (byte)'C', 0, 0 };
        Assert.Equal("AB", ValueCodec.DecodeString(raw, TagDataType.String, ByteOrderKind.ABCD, 6));
    }

    [Fact]
    public void 四解析同时给出四种结果()
    {
        var dict = ValueCodec.InterpretFour(Abcd220, TagDataType.Float32);
        Assert.Equal(4, dict.Count);
        Assert.Equal("220.5", dict["ABCD"]);
        Assert.NotEqual(dict["ABCD"], dict["CDAB"]);
    }

    [Fact]
    public void 十六位四解析只有两种不同结果()
    {
        var dict = ValueCodec.InterpretFour(new byte[] { 0x01, 0x02 }, TagDataType.Int16);
        Assert.Equal(4, dict.Count);
        Assert.Equal(dict["ABCD"], dict["CDAB"]);
        Assert.NotEqual(dict["ABCD"], dict["BADC"]);
    }
}
