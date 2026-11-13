using SFMS.Domain;
using SFMS.Shared;
using Xunit;

namespace SFMS.Domain.Tests;

public class AddressParserTests
{
    [Fact]
    public void S7_DataBlock_Word_解析为结构化地址()
    {
        var p = AddressParser.Parse(ProtocolType.S7, "DB1.DBW0", TagDataType.Int16);
        Assert.Equal(TagArea.Db, p.Address.Area);
        Assert.Equal(1, p.Address.AreaNo);
        Assert.Equal(0, p.Address.ByteOffset);
        Assert.Equal(0, p.Address.BitOffset);
        Assert.Equal(TagDataType.Int16, p.SuggestedType);
        Assert.Equal("DB1.DBW0", p.NormalizedExpr);
    }

    [Fact]
    public void S7_DataBlock_DWord_默认浮点()
    {
        var p = AddressParser.Parse(ProtocolType.S7, "DB2.DBD4", TagDataType.Float32);
        Assert.Equal(4, p.Address.ByteOffset);
        Assert.Equal(TagDataType.Float32, p.SuggestedType);
    }

    [Fact]
    public void S7_DataBlock_Bit_取到位号()
    {
        var p = AddressParser.Parse(ProtocolType.S7, "DB1.DBX0.3", TagDataType.Bool);
        Assert.Equal(TagDataType.Bool, p.SuggestedType);
        Assert.Equal(3, p.Address.BitOffset);
        Assert.Equal("DB1.DBX0.3", p.NormalizedExpr);
    }

    [Theory]
    [InlineData("M10.0", TagArea.Memory, 10, 0)]
    [InlineData("I0.5", TagArea.Input, 0, 5)]
    [InlineData("Q2.1", TagArea.Output, 2, 1)]
    [InlineData("M100", TagArea.Memory, 100, 0)]
    public void S7_位存储区(string expr, TagArea area, int offset, int bit)
    {
        var p = AddressParser.Parse(ProtocolType.S7, expr, TagDataType.Bool);
        Assert.Equal(area, p.Address.Area);
        Assert.Equal(offset, p.Address.ByteOffset);
        Assert.Equal(bit, p.Address.BitOffset);
        Assert.Equal(TagDataType.Bool, p.SuggestedType);
    }

    [Fact]
    public void S7_200的V区归一为DB1()
    {
        var p = AddressParser.Parse(ProtocolType.S7, "VW100", TagDataType.Int16);
        Assert.Equal(TagArea.Db, p.Address.Area);
        Assert.Equal(1, p.Address.AreaNo);
        Assert.Equal(100, p.Address.ByteOffset);
        Assert.Contains("DB1", p.ProtocolWarnings);
    }

    [Theory]
    [InlineData("HR:0", TagArea.Holding, 0)]
    [InlineData("IR:10", TagArea.InputRegister, 10)]
    [InlineData("C:5", TagArea.Coil, 5)]
    [InlineData("DI:7", TagArea.DiscreteInput, 7)]
    [InlineData("HOLDING:100", TagArea.Holding, 100)]
    public void Modbus_区冒号偏移(string expr, TagArea area, int offset)
    {
        var p = AddressParser.Parse(ProtocolType.ModbusTcp, expr, TagDataType.Int16);
        Assert.Equal(area, p.Address.Area);
        Assert.Equal(offset, p.Address.ByteOffset);
    }

    [Theory]
    [InlineData(40001, TagArea.Holding, 0)]
    [InlineData(40010, TagArea.Holding, 9)]
    [InlineData(30001, TagArea.InputRegister, 0)]
    [InlineData(10001, TagArea.DiscreteInput, 0)]
    [InlineData(1, TagArea.Coil, 0)]
    [InlineData(400001, TagArea.Holding, 0)]
    public void Modbus_四五位地址归一(int digits, TagArea area, int offset)
    {
        var (a, o) = AddressParser.DigitsToArea(digits);
        Assert.Equal(area, a);
        Assert.Equal(offset, o);
    }

    [Fact]
    public void 无法识别的地址抛出异常()
    {
        Assert.Throws<AddressParseException>(() => AddressParser.Parse(ProtocolType.S7, "XYZ1", TagDataType.Int16));
        Assert.Throws<AddressParseException>(() => AddressParser.Parse(ProtocolType.ModbusTcp, "FOO", TagDataType.Int16));
        Assert.Throws<AddressParseException>(() => AddressParser.Parse(ProtocolType.S7, "  ", TagDataType.Int16));
    }

    [Fact]
    public void 位地址缺少位号时报错()
    {
        Assert.Throws<AddressParseException>(() => AddressParser.Parse(ProtocolType.S7, "DB1.DBX0", TagDataType.Bool));
    }

    [Fact]
    public void 结构化字段可回显为表达式()
    {
        var a = new TagAddress(TagArea.Db, 3, 12, 0);
        Assert.Equal("DB3.DBD12", AddressParser.ToExpression(ProtocolType.S7, a, TagDataType.Float32));
        Assert.Equal("DB3.DBW12", AddressParser.ToExpression(ProtocolType.S7, a, TagDataType.Int16));

        var b = new TagAddress(TagArea.Holding, 0, 25, 0);
        Assert.Equal("HR:25", AddressParser.ToExpression(ProtocolType.ModbusTcp, b, TagDataType.Int16));
    }

    [Fact]
    public void 四万地址显示换算()
    {
        var a = new TagAddress(TagArea.Holding, 0, 9, 0);
        Assert.StartsWith("40010", AddressParser.ToLegacyDisplay(a, TagDataType.Int16));
    }
}
