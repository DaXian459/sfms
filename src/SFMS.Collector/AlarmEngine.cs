using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SFMS.Domain;
using SFMS.Infrastructure;
using SFMS.Shared;

namespace SFMS.Collector;

/// <summary>
/// 报警引擎（计划书 4.3 报警系统）：阈值报警（HI/HIHI/LO/LOLO）、通信中断报警、过载报警。
/// 状态机 Active → Acked → Cleared；带死区与延时，避免抖动刷屏。
/// </summary>
public sealed class AlarmEngine(IDbContextFactory<SfmsDbContext> factory, ILogger<AlarmEngine> logger)
{
    private sealed record Pending(bool Active, DateTimeOffset Since, long? EventId, double? Value);

    private readonly Dictionary<int, List<AlarmRule>> _rulesByTag = new();
    private readonly Dictionary<int, AlarmRule> _rules = new();
    private readonly Dictionary<string, Pending> _state = new();
    private readonly Dictionary<int, Tag> _tags = new();
    private readonly Channel<Func<SfmsDbContext, CancellationToken, Task>> _persist = Channel.CreateUnbounded<Func<SfmsDbContext, CancellationToken, Task>>();
    private CancellationToken _ct = CancellationToken.None;

    public event Action<AlarmEvent>? Raised;

    public void Load(IReadOnlyList<AlarmRule> rules, IReadOnlyList<Tag> tags)
    {
        _rules.Clear();
        _rulesByTag.Clear();
        _tags.Clear();
        foreach (var t in tags) _tags[t.Id] = t;
        foreach (var r in rules.Where(r => r.Enabled))
        {
            _rules[r.Id] = r;
            if (r.TagId is { } tagId)
            {
                if (!_rulesByTag.TryGetValue(tagId, out var list)) _rulesByTag[tagId] = list = new List<AlarmRule>();
                list.Add(r);
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ct = ct;
        await foreach (var action in _persist.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await action(db, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "报警事件落库失败");
            }
        }
    }

    /// <summary>采集值进入报警判定。onDelay 未满足时不产生事件，只在状态里记录起始时间。</summary>
    public void OnValue(Tag tag, double? value, Quality quality)
    {
        if (value is null || quality == Quality.Timeout) return;
        if (!_rulesByTag.TryGetValue(tag.Id, out var rules)) return;

        foreach (var rule in rules)
        {
            double v = value.Value;
            bool over = rule.Kind switch
            {
                AlarmKind.Hi or AlarmKind.HiHi => v > rule.Threshold,
                AlarmKind.Lo or AlarmKind.LoLo => v < rule.Threshold,
                _ => false
            };

            string key = $"rule:{rule.Id}";
            _state.TryGetValue(key, out var state);
            bool active = state?.Active == true;

            if (over && !active)
            {
                var since = state?.Since ?? DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - since < TimeSpan.FromMilliseconds(rule.OnDelayMs))
                {
                    _state[key] = new Pending(false, since, state?.EventId, v);
                    continue;
                }
                Raise(key, rule, tag, v, $"{tag.Name} {(rule.Kind is AlarmKind.Hi or AlarmKind.HiHi ? "高于" : "低于")} {rule.Threshold}{tag.Unit}（当前 {v:0.###}{tag.Unit}）");
            }
            else if (!over && active)
            {
                // 死区：回落到 阈值∓hysteresis 才恢复
                bool cleared = rule.Kind switch
                {
                    AlarmKind.Hi or AlarmKind.HiHi => v < rule.Threshold - rule.Hysteresis,
                    AlarmKind.Lo or AlarmKind.LoLo => v > rule.Threshold + rule.Hysteresis,
                    _ => true
                };
                if (cleared) Clear(key);
            }
        }
    }

    private void Raise(string key, AlarmRule? rule, Tag? tag, double? value, string message)
    {
        var evt = new AlarmEvent
        {
            RuleId = rule?.Id,
            TagId = tag?.Id,
            DeviceId = tag?.DeviceId,
            Kind = rule?.Kind ?? AlarmKind.Comm,
            Severity = rule?.Severity ?? "Warn",
            State = AlarmState.Active,
            StartedAt = DateTimeOffset.UtcNow,
            TriggerValue = value,
            Message = message
        };
        _state[key] = new Pending(true, evt.StartedAt, null, value);
        Raised?.Invoke(evt);
        _persist.Writer.TryWrite((db, _) =>
        {
            db.AlarmEvents.Add(evt);
            return Task.CompletedTask;
        });
    }

    private void Clear(string key)
    {
        if (!_state.TryGetValue(key, out var state) || state is null) return;
        _state[key] = state with { Active = false, Since = DateTimeOffset.UtcNow };
        var endedAt = DateTimeOffset.UtcNow;

        _persist.Writer.TryWrite(async (db, ct) =>
        {
            if (key.StartsWith("rule:", StringComparison.Ordinal))
            {
                int ruleId = int.Parse(key[5..]);
                var evt = await db.AlarmEvents
                    .Where(e => e.RuleId == ruleId && e.State != AlarmState.Cleared)
                    .OrderByDescending(e => e.StartedAt)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (evt is not null)
                {
                    evt.State = AlarmState.Cleared;
                    evt.EndedAt = endedAt;
                }
            }
            else if (key.StartsWith("comm:", StringComparison.Ordinal))
            {
                int endpointId = int.Parse(key[5..]);
                var evt = await db.AlarmEvents
                    .Where(e => e.Kind == AlarmKind.Comm && e.DeviceId == endpointId && e.State != AlarmState.Cleared)
                    .OrderByDescending(e => e.StartedAt)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (evt is not null)
                {
                    evt.State = AlarmState.Cleared;
                    evt.EndedAt = endedAt;
                }
            }
        });
    }

    public void OnEndpointOffline(Endpoint endpoint, string? error)
    {
        string key = $"comm:{endpoint.Id}";
        if (_state.TryGetValue(key, out var st) && st.Active) return;
        var evt = new AlarmEvent
        {
            Kind = AlarmKind.Comm,
            Severity = "Alarm",
            State = AlarmState.Active,
            StartedAt = DateTimeOffset.UtcNow,
            DeviceId = endpoint.Id,       // 借用 DeviceId 关联端点，便于查询
            Message = $"端点 {endpoint.Name}({endpoint.Ip}:{endpoint.Port}) 通信中断：{error ?? "连接失败"}"
        };
        _state[key] = new Pending(true, evt.StartedAt, null, null);
        Raised?.Invoke(evt);
        _persist.Writer.TryWrite((db, _) => { db.AlarmEvents.Add(evt); return Task.CompletedTask; });
    }

    public void OnEndpointOnline(Endpoint endpoint)
    {
        string key = $"comm:{endpoint.Id}";
        if (_state.TryGetValue(key, out var st) && st.Active) Clear(key);
    }

    public void OnOverload(Endpoint endpoint, int groupId, string message)
    {
        var evt = new AlarmEvent
        {
            Kind = AlarmKind.Overload,
            Severity = "Warn",
            State = AlarmState.Active,
            StartedAt = DateTimeOffset.UtcNow,
            Message = $"端点 {endpoint.Name} 分组 {groupId}：{message}"
        };
        Raised?.Invoke(evt);
        _persist.Writer.TryWrite((db, _) => { db.AlarmEvents.Add(evt); return Task.CompletedTask; });
    }

    /// <summary>当前内存中的活动报警数（用于系统监控）。</summary>
    public int ActiveCount => _state.Values.Count(s => s.Active);
}
