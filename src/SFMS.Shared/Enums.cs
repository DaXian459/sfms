namespace SFMS.Shared;

/// <summary>端点承载协议（计划书 5.2 endpoint.protocol）。</summary>
public enum ProtocolType
{
    S7 = 0,
    ModbusTcp = 1,
    ModbusRtu = 2,
    ModbusRtuOverTcp = 3
}

/// <summary>点位数据类型。</summary>
public enum TagDataType
{
    Bool = 0,
    Int16 = 1,
    UInt16 = 2,
    Int32 = 3,
    UInt32 = 4,
    Float32 = 5,
    String = 6
}

/// <summary>32 位数据的四种寄存器排列（计划书 3.2.3 表）。</summary>
public enum ByteOrderKind
{
    ABCD = 0,
    CDAB = 1,
    BADC = 2,
    DCBA = 3
}

/// <summary>质量戳四档（计划书 3.4.4）。</summary>
public enum Quality
{
    Good = 0,
    Uncertain = 1,
    Bad = 2,
    Timeout = 3
}

/// <summary>地址区域。前四个为 S7，后四个为 Modbus。</summary>
public enum TagArea
{
    Db = 0,
    Memory = 1,
    Input = 2,
    Output = 3,
    Holding = 10,
    InputRegister = 11,
    Coil = 12,
    DiscreteInput = 13
}

/// <summary>四级角色（计划书 3.8），数值即权限等级，便于层级判定。</summary>
public enum RoleLevel
{
    Observer = 1,
    Operator = 2,
    Engineer = 3,
    Admin = 4
}

public enum EndpointStatus
{
    Offline = 0,
    Online = 1,
    Fault = 2
}

public enum AlarmKind
{
    Hi = 0,
    HiHi = 1,
    Lo = 2,
    LoLo = 3,
    Comm = 4,
    Overload = 5
}

public enum AlarmState
{
    Active = 0,
    Acked = 1,
    Cleared = 2
}

public enum ScreenStatus
{
    Draft = 0,
    Published = 1
}

public static class ProtocolNames
{
    public static string ToText(this ProtocolType p) => p switch
    {
        ProtocolType.S7 => "S7",
        ProtocolType.ModbusTcp => "MODBUS_TCP",
        ProtocolType.ModbusRtu => "MODBUS_RTU",
        ProtocolType.ModbusRtuOverTcp => "MODBUS_RTU_OVER_TCP",
        _ => p.ToString().ToUpperInvariant()
    };

    public static ProtocolType Parse(string? text) => (text ?? "").Trim().ToUpperInvariant() switch
    {
        "S7" or "SIEMENS" or "S7COMM" => ProtocolType.S7,
        "MODBUS_TCP" or "MODBUSTCP" or "TCP" => ProtocolType.ModbusTcp,
        "MODBUS_RTU" or "MODBUSRTU" or "RTU" => ProtocolType.ModbusRtu,
        "MODBUS_RTU_OVER_TCP" or "RTU_OVER_TCP" => ProtocolType.ModbusRtuOverTcp,
        _ => ProtocolType.ModbusTcp
    };

    public static bool IsModbus(this ProtocolType p) => p != ProtocolType.S7;

    public static int DefaultPort(this ProtocolType p) => p == ProtocolType.S7 ? 102 : 502;
}

public static class AreaNames
{
    public static string ToText(this TagArea a) => a switch
    {
        TagArea.Db => "DB",
        TagArea.Memory => "M",
        TagArea.Input => "I",
        TagArea.Output => "Q",
        TagArea.Holding => "HR",
        TagArea.InputRegister => "IR",
        TagArea.Coil => "C",
        TagArea.DiscreteInput => "DI",
        _ => a.ToString()
    };

    public static string ToText(this TagDataType t) => t switch
    {
        TagDataType.Bool => "BOOL",
        TagDataType.Int16 => "INT16",
        TagDataType.UInt16 => "UINT16",
        TagDataType.Int32 => "INT32",
        TagDataType.UInt32 => "UINT32",
        TagDataType.Float32 => "FLOAT32",
        TagDataType.String => "STRING",
        _ => t.ToString()
    };

    public static TagDataType ParseDataType(string? text) => (text ?? "").Trim().ToUpperInvariant() switch
    {
        "BOOL" or "BIT" or "BOOLEAN" => TagDataType.Bool,
        "INT16" or "INT" or "WORD" or "SHORT" => TagDataType.Int16,
        "UINT16" or "UINT" or "USINT" or "UWORD" => TagDataType.UInt16,
        "INT32" or "DINT" or "DWORD" => TagDataType.Int32,
        "UINT32" or "UDINT" or "DUINT" => TagDataType.UInt32,
        "FLOAT32" or "FLOAT" or "REAL" => TagDataType.Float32,
        "STRING" or "STR" => TagDataType.String,
        _ => throw new ArgumentException($"未知数据类型: {text}")
    };
}

/// <summary>质量戳的中文与颜色语义（前后端统一，避免各写一套）。</summary>
public static class QualityText
{
    public static string ToText(this Quality q) => q switch
    {
        Quality.Good => "良好",
        Quality.Uncertain => "不确定",
        Quality.Bad => "坏值",
        Quality.Timeout => "超时",
        _ => q.ToString()
    };
}
