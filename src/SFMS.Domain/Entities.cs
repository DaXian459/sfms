using SFMS.Shared;

namespace SFMS.Domain;

/// <summary>端点：一次物理通信连接（IP + 端口 + 协议 + 连接参数）。</summary>
public class Endpoint
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public ProtocolType Protocol { get; set; } = ProtocolType.ModbusTcp;
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    /// <summary>机架号/槽号、串口参数、超时、重试次数等，JSON 文本。</summary>
    public string ConnParams { get; set; } = "{}";
    public bool ScanEnabled { get; set; } = true;
    public EndpointStatus Status { get; set; } = EndpointStatus.Offline;
    public DateTimeOffset? LastOkAt { get; set; }
    public string? Remark { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; set; }

    public List<Device> Devices { get; set; } = new();
}

/// <summary>逻辑设备：端点内的可寻址单元，device_key 为从站地址或机架/槽号组合。</summary>
public class Device
{
    public int Id { get; set; }
    public int EndpointId { get; set; }
    public Endpoint? Endpoint { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceKey { get; set; } = "1";
    public string? Model { get; set; }
    public string? Workshop { get; set; }
    public string? Area { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>单设备暂停采集（点位保持最后值，质量置 Uncertain）。</summary>
    public bool Paused { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; set; }

    public List<Tag> Tags { get; set; } = new();
}

/// <summary>采集分组：独立扫描周期与合并阈值。</summary>
public class ScanGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int PeriodMs { get; set; } = 1000;
    public int Priority { get; set; } = 5;
    public int MergeGap { get; set; } = 16;
    public bool Enabled { get; set; } = true;
}

/// <summary>点位：设备内的一个数据项，结构化地址 + 解析规则。</summary>
public class Tag
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string AddressExpr { get; set; } = "";
    public TagArea Area { get; set; } = TagArea.Holding;
    public int AreaNo { get; set; }
    public int ByteOffset { get; set; }
    public int BitOffset { get; set; }
    public TagDataType DataType { get; set; } = TagDataType.Int16;
    public ByteOrderKind ByteOrder { get; set; } = ByteOrderKind.ABCD;
    public int StrLen { get; set; }
    public bool Writable { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string? Unit { get; set; }
    public int ScanGroupId { get; set; }
    public ScanGroup? ScanGroup { get; set; }
    public double Deadband { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>区域|块号|偏移|位|类型，用于唯一性与重叠校验。</summary>
    public string AddressKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; set; }
}

public class AlarmRule
{
    public int Id { get; set; }
    public int? TagId { get; set; }
    public Tag? Tag { get; set; }
    public int? EndpointId { get; set; }
    public AlarmKind Kind { get; set; } = AlarmKind.Hi;
    public double Threshold { get; set; }
    public double Hysteresis { get; set; }
    public int OnDelayMs { get; set; }
    public string Severity { get; set; } = "Warn";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AlarmEvent
{
    public long Id { get; set; }
    public int? RuleId { get; set; }
    public int? TagId { get; set; }
    public int? DeviceId { get; set; }
    public AlarmKind Kind { get; set; }
    public string Severity { get; set; } = "Warn";
    public AlarmState State { get; set; } = AlarmState.Active;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public double? TriggerValue { get; set; }
    public string Message { get; set; } = "";
    public string? AckBy { get; set; }
    public DateTimeOffset? AckAt { get; set; }
    public string? AckNote { get; set; }
}

public class Screen
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>overview / workshop / device / params / trend / alarm / diag / custom</summary>
    public string Type { get; set; } = "custom";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public ScreenStatus Status { get; set; } = ScreenStatus.Draft;
    public int? PublishedVersionId { get; set; }
    public int PublishedVersionNo { get; set; }
    public string DraftJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}

public class ScreenVersion
{
    public int Id { get; set; }
    public int ScreenId { get; set; }
    public int VersionNo { get; set; }
    public string Json { get; set; } = "{}";
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedBy { get; set; } = "";
}

public class ScreenRole
{
    public int Id { get; set; }
    public int ScreenId { get; set; }
    public string RoleCode { get; set; } = "";
    public bool CanView { get; set; } = true;
    public bool CanEdit { get; set; }
}

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RoleCode { get; set; } = "Observer";
    public bool Enabled { get; set; } = true;
    public int FailedCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>审计日志（按月分区，由 SQL 脚本建表）。</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;
    public int? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string Result { get; set; } = "Success";
    public string? Reason { get; set; }
    public string? Ip { get; set; }
    public string? TraceId { get; set; }
}

/// <summary>写操作二次确认令牌。</summary>
public class ConfirmToken
{
    public string Token { get; set; } = "";
    public int UserId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public class WriteBatch
{
    public string BatchId { get; set; } = "";
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WriteBatchItem> Items { get; set; } = new();
}

public class WriteBatchItem
{
    public long Id { get; set; }
    public string BatchId { get; set; } = "";
    public int TagId { get; set; }
    public string Code { get; set; } = "";
    public double? OldValue { get; set; }
    public double? NewValue { get; set; }
    public double? ReadBack { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

public class ImportJob
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string Status { get; set; } = "Validated";
    public int Total { get; set; }
    public int Valid { get; set; }
    public int Invalid { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedBy { get; set; } = "";
    public List<ImportStaging> Rows { get; set; } = new();
}

public class ImportStaging
{
    public long Id { get; set; }
    public int JobId { get; set; }
    public int RowNo { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? ErrorText { get; set; }
    public bool IsValid { get; set; }
}

/// <summary>点位最新值（实时域，重启后可恢复）。</summary>
public class TagLatest
{
    public int TagId { get; set; }
    public double? Value { get; set; }
    public string? Text { get; set; }
    public Quality Quality { get; set; } = Quality.Bad;
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "scan";
}

/// <summary>历史落库辅助状态：上次落库值与时间，用于死区与 5 分钟快照判定。</summary>
public class TagRuntimeState
{
    public int TagId { get; set; }
    public double? LastStoredValue { get; set; }
    public string? LastStoredText { get; set; }
    public DateTimeOffset? LastStoredAt { get; set; }
    public Quality LastQuality { get; set; } = Quality.Bad;
}

public class SystemSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
