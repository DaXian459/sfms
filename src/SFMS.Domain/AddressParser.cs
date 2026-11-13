using System.Globalization;
using System.Text.RegularExpressions;
using SFMS.Shared;

namespace SFMS.Domain;

/// <summary>结构化地址（数据库实际存储形式）。</summary>
public readonly record struct TagAddress(TagArea Area, int AreaNo, int ByteOffset, int BitOffset)
{
    public string Key => $"{Area}|{AreaNo}|{ByteOffset}|{BitOffset}";
    public override string ToString() => $"{Area.ToText()}:{AreaNo}:{ByteOffset}.{BitOffset}";
}

public sealed record ParsedAddress(TagAddress Address, string NormalizedExpr, TagDataType? SuggestedType, string ProtocolWarnings);

public sealed class AddressParseException(string message) : Exception(message);

/// <summary>
/// 地址表达式解析：字符串表达式是工程师的快捷输入，保存时解析为结构化字段（计划书 3.3.3）。
/// 支持 S7 的 DB/M/I/Q、S7-200 的 V 区别名、Modbus 的 区:偏移 与四/五/六位地址。
/// </summary>
public static class AddressParser
{
    private static readonly Regex S7Db = new(@"^DB(?<db>\d{1,5})\.(?<kind>DBX|DBB|DBW|DBD)(?<off>\d{1,5})(?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex S7Area = new(@"^(?<area>M|I|Q)(?<kind>B|W|D|X)?(?<off>\d{1,5})(?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex S7V = new(@"^V(?<kind>B|W|D|X)?(?<off>\d{1,5})(?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MbColon = new(@"^(?<area>HR|IR|C|DI|HOLDING|INPUT|COIL|DISCRETE)\s*:\s*(?<off>\d{1,5})(?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedAddress Parse(ProtocolType protocol, string expr, TagDataType declared, int strLen = 0)
    {
        var text = (expr ?? "").Trim();
        if (text.Length == 0) throw new AddressParseException("地址表达式不能为空");
        return protocol == ProtocolType.S7 ? ParseS7(text, declared, strLen) : ParseModbus(text, declared, strLen);
    }

    private static ParsedAddress ParseS7(string text, TagDataType declared, int strLen)
    {
        var m = S7Db.Match(text);
        if (m.Success)
        {
            int db = int.Parse(m.Groups["db"].Value);
            int off = int.Parse(m.Groups["off"].Value);
            string kind = m.Groups["kind"].Value.ToUpperInvariant();
            int bit = m.Groups["bit"].Success ? int.Parse(m.Groups["bit"].Value) : 0;
            var (type, isBit) = KindToType(kind, declared);
            if (isBit && !m.Groups["bit"].Success)
                throw new AddressParseException($"位地址需写成 DB{db}.DBX{off}.0 的形式");
            if (!isBit && m.Groups["bit"].Success)
                throw new AddressParseException($"{kind} 是整字访问，不能带位号");
            string norm = kind == "DBX" ? $"DB{db}.DBX{off}.{bit}" : $"DB{db}.{kind}{off}";
            return new ParsedAddress(new TagAddress(TagArea.Db, db, off, isBit ? bit : 0), norm, type, "");
        }

        m = S7Area.Match(text);
        if (m.Success)
        {
            var area = m.Groups["area"].Value.ToUpperInvariant() switch
            {
                "M" => TagArea.Memory,
                "I" => TagArea.Input,
                _ => TagArea.Output
            };
            int off = int.Parse(m.Groups["off"].Value);
            string kind = m.Groups["kind"].Value.ToUpperInvariant();
            int bit = m.Groups["bit"].Success ? int.Parse(m.Groups["bit"].Value) : 0;
            var (type, isBit) = kind switch
            {
                "" or "X" => (TagDataType.Bool, true),
                "B" => (TagDataType.UInt16, false),
                "W" => (TagDataType.Int16, false),
                _ => (TagDataType.Int32, false)
            };
            if (!isBit && m.Groups["bit"].Success)
                throw new AddressParseException("整字地址不能带位号");
            string areaText = area.ToText();
            string norm = isBit ? $"{areaText}{off}.{bit}" : $"{areaText}{(kind == "" ? "W" : kind)}{off}";
            return new ParsedAddress(new TagAddress(area, 0, off, isBit ? bit : 0), norm, type, "");
        }

        m = S7V.Match(text);
        if (m.Success)
        {
            // S7-200 SMART：V 区在 S7 协议中映射为 DB1（VW100 -> DB1.DBW100）
            int off = int.Parse(m.Groups["off"].Value);
            string kind = m.Groups["kind"].Value.ToUpperInvariant();
            int bit = m.Groups["bit"].Success ? int.Parse(m.Groups["bit"].Value) : 0;
            var (type, isBit) = kind switch
            {
                "" or "X" => (TagDataType.Bool, true),
                "B" => (TagDataType.UInt16, false),
                "W" => (TagDataType.Int16, false),
                _ => (TagDataType.Int32, false)
            };
            if (!isBit && m.Groups["bit"].Success) throw new AddressParseException("整字地址不能带位号");
            string dbKind = isBit ? "DBX" : kind == "" ? "DBW" : "DB" + kind;
            string norm = isBit ? $"V{off}.{bit}" : $"V{kind}{off}";
            return new ParsedAddress(new TagAddress(TagArea.Db, 1, off, isBit ? bit : 0), norm, type,
                $"V 区别名已归一为 DB1.{dbKind}{off}");
        }

        throw new AddressParseException($"无法识别的 S7 地址：{text}（示例 DB1.DBW0 / DB1.DBD4 / DB1.DBX0.3 / M10.0 / VW100）");
    }

    private static (TagDataType, bool) KindToType(string kind, TagDataType declared) => kind switch
    {
        "DBX" => (TagDataType.Bool, true),
        "DBB" => (TagDataType.UInt16, false),
        "DBW" => (declared is TagDataType.UInt16 or TagDataType.Int16 ? declared : TagDataType.Int16, false),
        _ => (declared is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 ? declared : TagDataType.Float32, false)
    };

    private static ParsedAddress ParseModbus(string text, TagDataType declared, int strLen)
    {
        var m = MbColon.Match(text);
        if (m.Success)
        {
            var area = MapMbArea(m.Groups["area"].Value);
            int off = int.Parse(m.Groups["off"].Value);
            int bit = m.Groups["bit"].Success ? int.Parse(m.Groups["bit"].Value) : 0;
            return new ParsedAddress(new TagAddress(area, 0, off, bit), $"{area.ToText()}:{off}" + (bit > 0 ? $".{bit}" : ""),
                null, "");
        }

        if (int.TryParse(text, out int digits))
        {
            var (area, offset) = DigitsToArea(digits);
            return new ParsedAddress(new TagAddress(area, 0, offset, 0), $"{area.ToText()}:{offset}", null,
                $"五/六位地址 {digits} 已归一为 {area.ToText()}:{offset}");
        }

        throw new AddressParseException($"无法识别的 Modbus 地址：{text}（示例 HR:0 / IR:10 / C:0 / 40001）");
    }

    private static TagArea MapMbArea(string a) => a.ToUpperInvariant() switch
    {
        "HR" or "HOLDING" => TagArea.Holding,
        "IR" or "INPUT" => TagArea.InputRegister,
        "C" or "COIL" => TagArea.Coil,
        _ => TagArea.DiscreteInput
    };

    /// <summary>四万/三万/一万地址表示法归一（计划书 3.2.3）。</summary>
    public static (TagArea Area, int Offset) DigitsToArea(int digits)
    {
        // 注意匹配顺序：先长前缀再短前缀，否则 40001 会被 1~65536 的裸地址规则吃掉
        if (digits is >= 400001 and <= 465536) return (TagArea.Holding, digits - 400001);
        if (digits is >= 300001 and <= 365536) return (TagArea.InputRegister, digits - 300001);
        if (digits is >= 100001 and <= 165536) return (TagArea.DiscreteInput, digits - 100001);
        if (digits is >= 40001 and <= 49999) return (TagArea.Holding, digits - 40001);
        if (digits is >= 30001 and <= 39999) return (TagArea.InputRegister, digits - 30001);
        if (digits is >= 10001 and <= 19999) return (TagArea.DiscreteInput, digits - 10001);
        if (digits is >= 1 and <= 65536) return (TagArea.Coil, digits - 1);
        throw new AddressParseException($"地址数字 {digits} 超出 Modbus 可表示范围");
    }

    /// <summary>结构化字段回显为表达式（编辑界面用）。</summary>
    public static string ToExpression(ProtocolType protocol, TagAddress a, TagDataType type)
    {
        if (protocol != ProtocolType.S7) return $"{a.Area.ToText()}:{a.ByteOffset}" + (a.BitOffset > 0 ? $".{a.BitOffset}" : "");
        if (a.Area == TagArea.Db)
            return type == TagDataType.Bool
                ? $"DB{a.AreaNo}.DBX{a.ByteOffset}.{a.BitOffset}"
                : type is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32
                    ? $"DB{a.AreaNo}.DBD{a.ByteOffset}"
                    : $"DB{a.AreaNo}.DBW{a.ByteOffset}";
        var area = a.Area.ToText();
        if (type == TagDataType.Bool) return $"{area}{a.ByteOffset}.{a.BitOffset}";
        return type is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32
            ? $"{area}D{a.ByteOffset}"
            : $"{area}W{a.ByteOffset}";
    }

    /// <summary>四万地址换算显示，便于与现场习惯对照。</summary>
    public static string ToLegacyDisplay(TagAddress a, TagDataType type)
    {
        int span = type is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 ? 2 : 1;
        return a.Area switch
        {
            TagArea.Holding => $"{40001 + a.ByteOffset}",
            TagArea.InputRegister => $"{30001 + a.ByteOffset}",
            TagArea.Coil => $"{1 + a.ByteOffset}",
            TagArea.DiscreteInput => $"{10001 + a.ByteOffset}",
            _ => ""
        } + (span > 1 ? "（占 2 个寄存器）" : "");
    }

    public static string Format(double? value, TagDataType type, string? unit)
    {
        if (value is null) return "-";
        string s = type switch
        {
            TagDataType.Bool => value.Value != 0 ? "ON" : "OFF",
            TagDataType.Float32 => value.Value.ToString("0.###", CultureInfo.InvariantCulture),
            TagDataType.String => value.Value.ToString(CultureInfo.InvariantCulture),
            _ => value.Value.ToString("0", CultureInfo.InvariantCulture)
        };
        return string.IsNullOrWhiteSpace(unit) ? s : $"{s} {unit}";
    }
}
