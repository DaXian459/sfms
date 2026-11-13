using SFMS.Shared;

namespace SFMS.Domain;

public sealed record ValidationIssue(string Field, string Message, bool IsError);

/// <summary>线性换算：工程值 = 原始值 × scale + offset。</summary>
public static class Scaling
{
    public static double? ToEngineering(double? raw, double scale, double offset)
    {
        if (raw is null) return null;
        double s = Math.Abs(scale) < 1e-9 ? 1 : scale;
        return Math.Round(raw.Value * s + offset, 5);
    }

    public static double ToRaw(double engineering, double scale, double offset)
    {
        double s = Math.Abs(scale) < 1e-9 ? 1 : scale;
        return (engineering - offset) / s;
    }
}

/// <summary>
/// 点位校验（在线保存与导入共用一套规则，计划书 5.3 与 6.3 的校验口径）。
/// </summary>
public static class TagValidator
{
    public const int ModbusMaxRegistersPerRead = 125;
    public const int ModbusMaxBitsPerRead = 2000;
    public const int ModbusMaxRegistersPerWrite = 123;

    public static List<ValidationIssue> Validate(ProtocolType protocol, Tag tag, string? endpointIp = null)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(tag.Code)) issues.Add(new("code", "点位编码不能为空", true));
        if (string.IsNullOrWhiteSpace(tag.Name)) issues.Add(new("name", "点位名称不能为空", true));
        if (string.IsNullOrWhiteSpace(tag.AddressExpr)) issues.Add(new("addressExpr", "地址表达式不能为空", true));

        if (issues.Any(i => i.IsError)) return issues;

        ParsedAddress parsed;
        try
        {
            parsed = AddressParser.Parse(protocol, tag.AddressExpr, tag.DataType, tag.StrLen);
        }
        catch (AddressParseException ex)
        {
            issues.Add(new("addressExpr", ex.Message, true));
            return issues;
        }

        if (parsed.SuggestedType is { } suggested && !IsCompatible(parsed.Address, suggested, tag.DataType))
        {
            issues.Add(new("dataType",
                $"地址 {parsed.NormalizedExpr} 的自然类型为 {suggested.ToText()}，与所选 {tag.DataType.ToText()} 不匹配", true));
        }

        var a = parsed.Address;
        int spanBytes = ValueCodec.ByteLength(tag.DataType, tag.StrLen);
        int spanRegs = ValueCodec.RegisterCount(tag.DataType, tag.StrLen);

        // 区域与数据类型的矩阵
        if (protocol == ProtocolType.S7)
        {
            if (a.Area == TagArea.Db)
            {
                if (a.AreaNo is < 1 or > 65535) issues.Add(new("addressExpr", "数据块号需在 1~65535", true));
                if (a.ByteOffset > 65535) issues.Add(new("addressExpr", "偏移需在 0~65535", true));
            }
            else
            {
                if (a.ByteOffset > 65535) issues.Add(new("addressExpr", "偏移需在 0~65535", true));
                if (tag.DataType == TagDataType.String) issues.Add(new("dataType", "位存储区不支持 STRING", true));
            }
            if (tag.DataType == TagDataType.Bool && a.BitOffset is < 0 or > 7)
                issues.Add(new("addressExpr", "位号需在 0~7", true));
            if (tag.DataType != TagDataType.Bool && a.BitOffset > 0)
                issues.Add(new("addressExpr", "非 BOOL 点位不应带位号", true));
        }
        else
        {
            if (a.ByteOffset > 65535) issues.Add(new("addressExpr", "偏移需在 0~65535", true));
            if (a.ByteOffset + spanRegs > 65536)
                issues.Add(new("addressExpr", $"地址越界：偏移 {a.ByteOffset} + {spanRegs} 个寄存器超过 65536", true));

            bool bitArea = a.Area is TagArea.Coil or TagArea.DiscreteInput;
            if (bitArea && tag.DataType != TagDataType.Bool && tag.DataType != TagDataType.UInt16)
                issues.Add(new("dataType", "线圈/离散输入区只支持 BOOL（或按寄存器读的 UINT16）", true));
            if (!bitArea && tag.DataType == TagDataType.Bool)
                issues.Add(new("dataType", "BOOL 点位应使用 C:/DI: 区域（或 HR 区带位号）", false));
            if (tag.DataType == TagDataType.String && spanRegs > ModbusMaxRegistersPerRead)
                issues.Add(new("dataType", "STRING 长度超过单次读取上限", true));
        }

        // 只读区
        if (tag.Writable)
        {
            var readOnlyAreas = new[] { TagArea.Input, TagArea.InputRegister, TagArea.DiscreteInput };
            if (readOnlyAreas.Contains(a.Area))
                issues.Add(new("writable", $"{a.Area.ToText()} 区为只读区，不能标记为可写", true));
        }

        // 工程参数
        if (tag.MinValue is { } min && tag.MaxValue is { } max && min >= max)
            issues.Add(new("minValue", "量程下限必须小于上限", true));
        if (Math.Abs(tag.Scale) < 1e-9)
            issues.Add(new("scale", "换算系数不能为 0", true));
        if (tag.Deadband < 0)
            issues.Add(new("deadband", "死区不能为负", true));

        // 32 位必须显式字节序
        if (tag.DataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32)
        {
            if (protocol != ProtocolType.S7 && tag.ByteOrder == ByteOrderKind.ABCD && tag.Writable)
                issues.Add(new("byteOrder", "32 位可写点位的字节序请按设备实际排列确认（默认 ABCD 可能不匹配）", false));
        }
        else if (protocol != ProtocolType.S7 && tag.ByteOrder is ByteOrderKind.CDAB or ByteOrderKind.DCBA)
        {
            issues.Add(new("byteOrder", "16 位数据只需区分寄存器内字节序（ABCD 或 BADC），已按 BADC 处理", false));
        }

        return issues;
    }

    private static bool IsCompatible(TagAddress address, TagDataType natural, TagDataType declared)
    {
        if (natural == declared) return true;
        if (natural == TagDataType.Bool) return declared == TagDataType.Bool;
        // 16 位区间内互转（INT16/UINT16）允许
        if (natural is TagDataType.Int16 or TagDataType.UInt16) return declared is TagDataType.Int16 or TagDataType.UInt16;
        // 32 位区间内互转允许（INT32/UINT32/FLOAT32）
        return declared is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32;
    }

    /// <summary>保存前的地址键与唯一性检查（同设备内 code / 地址重复）。</summary>
    public static (string Key, List<ValidationIssue> Issues) BuildAddressKeyAndCheck(
        ProtocolType protocol, Tag tag, IEnumerable<Tag> siblings)
    {
        var issues = new List<ValidationIssue>();
        ParsedAddress parsed;
        try
        {
            parsed = AddressParser.Parse(protocol, tag.AddressExpr, tag.DataType, tag.StrLen);
        }
        catch (AddressParseException ex)
        {
            issues.Add(new("addressExpr", ex.Message, true));
            return ("", issues);
        }

        var key = parsed.Address.Key;
        foreach (var sib in siblings.Where(s => s.Id != tag.Id && !s.IsDeleted))
        {
            if (string.Equals(sib.Code, tag.Code, StringComparison.OrdinalIgnoreCase))
                issues.Add(new("code", $"点位编码 {tag.Code} 在本设备内已存在", true));

            if (sib.AddressKey == key && sib.DataType == tag.DataType)
                issues.Add(new("addressExpr", $"地址与点位 {sib.Code} 重复", true));
            else if (sib.AddressKey == key)
                issues.Add(new("addressExpr", $"地址与点位 {sib.Code} 重叠（同地址不同类型解析）", tag.Writable));
        }

        return (key, issues);
    }

    public static List<ValidationIssue> ValidateScanGroup(int periodMs, int mergeGap)
    {
        var issues = new List<ValidationIssue>();
        if (periodMs is < 200 or > 60000) issues.Add(new("periodMs", "采集周期需在 200~60000 毫秒", true));
        else if (periodMs % 50 != 0) issues.Add(new("periodMs", "采集周期需为 50 毫秒的整数倍", true));
        if (mergeGap is < 0 or > 512) issues.Add(new("mergeGap", "合并阈值需在 0~512 字节", true));
        return issues;
    }
}
