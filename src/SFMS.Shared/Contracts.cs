namespace SFMS.Shared;

/// <summary>API 统一响应包装，便于前端集中处理错误码。</summary>
public sealed record ApiResult<T>(bool Ok, T? Data, string? Error, string? TraceId)
{
    public static ApiResult<T> Success(T data) => new(true, data, null, null);
    public static ApiResult<T> Fail(string error, string? traceId = null) => new(false, default, error, traceId);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int Size);

public sealed record EndpointDto(
    int Id, string Code, string Name, string Protocol, string Ip, int Port,
    bool ScanEnabled, string Status, DateTimeOffset? LastOkAt, string? Remark,
    string? ConnParams, int DeviceCount, int TagCount, bool Enabled);

public sealed record DeviceDto(
    int Id, int EndpointId, string Code, string Name, string DeviceKey, string? Model,
    string? Workshop, string? Area, bool Enabled, int TagCount, int OnlineTags, int BadTags,
    bool Paused);

public sealed record ScanGroupDto(int Id, string Name, int PeriodMs, int Priority, int MergeGap, int TagCount);

public sealed record TagDto(
    int Id, int DeviceId, string DeviceCode, string Code, string Name,
    string AddressExpr, string Area, int AreaNo, int ByteOffset, int BitOffset,
    string DataType, string ByteOrder, int StrLen, bool Writable,
    double? MinValue, double? MaxValue, double Scale, double Offset,
    string? Unit, int ScanGroupId, string ScanGroupName, double Deadband, bool Enabled,
    double? LatestValue, string? LatestText, int Quality, DateTimeOffset? LatestAt);

/// <summary>实时帧中的单个点位。t=tagId，v=值，q=质量，ts=服务器时间戳。</summary>
public sealed record RealtimeItem(int TagId, double? Value, string? Text, int Quality, DateTimeOffset Ts);

public sealed record RealtimeSnapshot(string SubId, DateTimeOffset ServerTime, IReadOnlyList<RealtimeItem> Items);

public sealed record HistoryPoint(long TagId, DateTimeOffset Ts, double? Value, string? Text, int Quality);

public sealed record HistorySeries(int TagId, string? Code, string? Name, string? Unit,
    IReadOnlyList<HistoryPoint> Points, string Resolution);

public sealed record WriteItemRequest(int TagId, double? NumericValue, string? TextValue);

public sealed record WritePrepareRequest(IReadOnlyList<WriteItemRequest> Items, string? Reason);

public sealed record WritePreview(int TagId, string Code, string Name,
    double? OldValue, string? OldText, double? NewValue, string? NewText,
    int Quality, bool Writable, string? Error);

public sealed record WritePrepareResult(string ConfirmToken, DateTimeOffset ExpiresAt,
    IReadOnlyList<WritePreview> Items, IReadOnlyList<string> Blockers);

public sealed record WriteExecuteRequest(string ConfirmToken);

public sealed record WriteItemResult(int TagId, string Code, bool Ok, string? Error,
    double? OldValue, double? NewValue, double? ReadBack);

public sealed record WriteExecuteResult(string BatchId, string Status, string Message,
    IReadOnlyList<WriteItemResult> Items);

public sealed record AlarmEventDto(long Id, int? RuleId, int? TagId, string? TagCode, string? TagName,
    int? DeviceId, string? DeviceName, string Kind, string Severity, string State,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, double? TriggerValue,
    string? AckBy, DateTimeOffset? AckAt, string? AckNote, string Message);

public sealed record AlarmRuleDto(int Id, int? TagId, int? EndpointId, string Kind, double Threshold,
    double Hysteresis, int OnDelayMs, string Severity, bool Enabled, string? TagCode, string? TagName);

public sealed record ScreenDto(int Id, string Code, string Name, string Type, int Width, int Height,
    string Status, int? PublishedVersionId, int PublishedVersionNo, DateTimeOffset UpdatedAt,
    string? UpdatedBy, bool CanView, bool CanEdit);

public sealed record ScreenVersionDto(int Id, int ScreenId, int VersionNo, string Note,
    DateTimeOffset CreatedAt, string CreatedBy, string Json);

public sealed record DiagEndpointDto(int EndpointId, string Code, string Name, string Protocol,
    string Ip, int Port, string Status, DateTimeOffset? LastOkAt, long Requests, long Success,
    long Timeouts, long Errors, double AvgMs, double P95Ms, int ConsecutiveFailures,
    string? LastError, int OnlineTags, int BadTags, int TagCount);

public sealed record DebugReadResult(int TagId, string Code, string AddressExpr, string DataType,
    string ByteOrder, string RawHex, string? Value, int Quality, string? Error,
    IReadOnlyDictionary<string, string?> Interpretations);

public sealed record MetricDto(string Name, double Value, string? Unit, DateTimeOffset At);

public sealed record AuditDto(long Id, DateTimeOffset Ts, string? Username, string Action,
    string? TargetType, string? TargetId, string? Before, string? After, string Result,
    string? Reason, string? Ip);
