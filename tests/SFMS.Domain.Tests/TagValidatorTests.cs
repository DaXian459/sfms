using SFMS.Domain;
using SFMS.Shared;
using Xunit;

namespace SFMS.Domain.Tests;

public class TagValidatorTests
{
    private static Tag NewTag(string expr, TagDataType type, bool writable = false,
        double scale = 1, double? min = null, double? max = null, string code = "T1") => new()
    {
        Code = code,
        Name = "测试点位",
        AddressExpr = expr,
        DataType = type,
        ByteOrder = ByteOrderKind.ABCD,
        Writable = writable,
        Scale = scale,
        MinValue = min,
        MaxValue = max,
        StrLen = 0
    };

    [Fact]
    public void 合规的S7浮点点位没有错误()
    {
        var issues = TagValidator.Validate(ProtocolType.S7, NewTag("DB1.DBD4", TagDataType.Float32));
        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void 合规的Modbus整型点位没有错误()
    {
        var issues = TagValidator.Validate(ProtocolType.ModbusTcp, NewTag("HR:0", TagDataType.Int16));
        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void 只读区不能标记为可写()
    {
        var issues = TagValidator.Validate(ProtocolType.ModbusTcp, NewTag("IR:10", TagDataType.Int16, writable: true));
        Assert.Contains(issues, i => i.IsError && i.Field == "writable");
    }

    [Fact]
    public void 换算系数不能为零()
    {
        var issues = TagValidator.Validate(ProtocolType.S7, NewTag("DB1.DBW0", TagDataType.Int16, scale: 0));
        Assert.Contains(issues, i => i.IsError && i.Field == "scale");
    }

    [Fact]
    public void 量程下限必须小于上限()
    {
        var issues = TagValidator.Validate(ProtocolType.S7, NewTag("DB1.DBW0", TagDataType.Int16, min: 100, max: 10));
        Assert.Contains(issues, i => i.IsError && i.Field == "minValue");
    }

    [Fact]
    public void 线圈区不支持32位类型()
    {
        var issues = TagValidator.Validate(ProtocolType.ModbusTcp, NewTag("C:0", TagDataType.Float32));
        Assert.Contains(issues, i => i.IsError && i.Field == "dataType");
    }

    [Fact]
    public void Modbus保持寄存器用BOOL只是提醒不是错误()
    {
        var issues = TagValidator.Validate(ProtocolType.ModbusTcp, NewTag("HR:0", TagDataType.Bool));
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => !i.IsError && i.Field == "dataType");
    }

    [Fact]
    public void 地址与类型不匹配报错()
    {
        // DB1.DBW0 的自然类型是 16 位，声明成 FLOAT32 属于不匹配
        var issues = TagValidator.Validate(ProtocolType.S7, NewTag("DB1.DBW0", TagDataType.Float32));
        Assert.Contains(issues, i => i.IsError && i.Field == "dataType");
    }

    [Fact]
    public void 非法地址表达式被拦截()
    {
        var issues = TagValidator.Validate(ProtocolType.S7, NewTag("NOT-AN-ADDRESS", TagDataType.Int16));
        Assert.Contains(issues, i => i.IsError && i.Field == "addressExpr");
    }

    [Fact]
    public void 空编码与空名称被拦截()
    {
        var tag = NewTag("DB1.DBW0", TagDataType.Int16, code: "");
        tag.Name = "";
        var issues = TagValidator.Validate(ProtocolType.S7, tag);
        Assert.Contains(issues, i => i.IsError && i.Field == "code");
        Assert.Contains(issues, i => i.IsError && i.Field == "name");
    }

    [Fact]
    public void 同设备内点位编码重复被拦截()
    {
        var tag = NewTag("DB1.DBW10", TagDataType.Int16, code: "TEMP");
        tag.Id = 10;
        var sibCode = NewTag("DB1.DBW0", TagDataType.Int16, code: "TEMP");
        sibCode.Id = 11;
        var siblings = new List<Tag> { sibCode };
        var (_, issues) = TagValidator.BuildAddressKeyAndCheck(ProtocolType.S7, tag, siblings);
        Assert.Contains(issues, i => i.IsError && i.Field == "code");
    }

    [Fact]
    public void 同地址同类型判为重复()
    {
        var tag = NewTag("DB1.DBW0", TagDataType.Int16, code: "A");
        tag.Id = 20;
        var sib = NewTag("DB1.DBW0", TagDataType.Int16, code: "B");
        sib.Id = 21;
        sib.AddressKey = AddressParser.Parse(ProtocolType.S7, "DB1.DBW0", TagDataType.Int16).Address.Key;
        var (key, issues) = TagValidator.BuildAddressKeyAndCheck(ProtocolType.S7, tag, new[] { sib });
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("重复"));
    }

    [Fact]
    public void 同地址不同类型判为重叠()
    {
        var tag = NewTag("DB1.DBD0", TagDataType.Float32, code: "A");
        tag.Id = 30;
        var sib = NewTag("DB1.DBD0", TagDataType.Int32, code: "B");
        sib.Id = 31;
        sib.AddressKey = AddressParser.Parse(ProtocolType.S7, "DB1.DBD0", TagDataType.Int32).Address.Key;
        var (_, issues) = TagValidator.BuildAddressKeyAndCheck(ProtocolType.S7, tag, new[] { sib });
        Assert.Contains(issues, i => i.Message.Contains("重叠"));
    }

    [Fact]
    public void 地址键区分区域与偏移()
    {
        var a = AddressParser.Parse(ProtocolType.S7, "DB1.DBW0", TagDataType.Int16).Address.Key;
        var b = AddressParser.Parse(ProtocolType.S7, "DB2.DBW0", TagDataType.Int16).Address.Key;
        var c = AddressParser.Parse(ProtocolType.S7, "DB1.DBW2", TagDataType.Int16).Address.Key;
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(1000, 16, true)]
    [InlineData(200, 0, true)]
    [InlineData(60000, 512, true)]
    [InlineData(100, 16, false)]
    [InlineData(70000, 16, false)]
    [InlineData(230, 16, false)]
    [InlineData(1000, 600, false)]
    public void 采集分组参数校验(int periodMs, int mergeGap, bool ok)
    {
        var issues = TagValidator.ValidateScanGroup(periodMs, mergeGap);
        Assert.Equal(ok, !issues.Any(i => i.IsError));
    }

    [Fact]
    public void 线性换算往返()
    {
        double raw = 1234;
        double eng = Scaling.ToEngineering(raw, 0.1, -5)!.Value;
        Assert.Equal(118.4, eng, 5);
        Assert.Equal(raw, Scaling.ToRaw(eng, 0.1, -5), 5);
    }

    [Fact]
    public void 零系数按1处理避免除零()
    {
        Assert.Equal(42, Scaling.ToEngineering(42, 0, 0)!.Value, 5);
    }
}
