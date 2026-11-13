using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SFMS.Infrastructure.Auth;
using SFMS.Infrastructure.Realtime;

namespace SFMS.Api.Realtime;

/// <summary>
/// 实时推送（计划书 3.6 / 6.4）：WebSocket 订阅模型。
/// 客户端提交订阅 → 服务端按 1 秒组装数据帧，只推变化点位；未变化的以心跳标记存活；
/// 单连接慢消费者触发 resync，由前端重新拉全量快照，避免数据空洞。
/// </summary>
public static class RealtimeWebSocketHandler
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("需要 WebSocket 连接");
            return;
        }

        var tickets = context.RequestServices.GetRequiredService<WsTicketService>();
        string? ticket = context.Request.Query["ticket"];
        int? userId = string.IsNullOrEmpty(ticket) ? null : tickets.Consume(ticket);
        if (userId is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var store = context.RequestServices.GetRequiredService<IRealtimeStore>();
        var connection = new Connection(socket, store, userId.Value);
        await connection.RunAsync(context.RequestAborted);
    }

    private sealed class Subscription(string id, int[] tags, int rateMs)
    {
        public string Id { get; } = id;
        public int[] Tags { get; } = tags;
        public int RateMs { get; } = Math.Clamp(rateMs, 200, 10000);
        public Dictionary<int, (double? Value, int Quality)> Last { get; } = new();
        public DateTimeOffset NextDue { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class Connection(WebSocket socket, IRealtimeStore store, int userId)
    {
        private readonly Dictionary<string, Subscription> _subs = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private string _sessionId = Guid.NewGuid().ToString("N")[..8];

        public async Task RunAsync(CancellationToken ct)
        {
            var receive = ReceiveLoopAsync(ct);
            var push = PushLoopAsync(ct);
            try
            {
                await Task.WhenAny(receive, push);
            }
            finally
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch (Exception) { /* 关闭异常忽略 */ }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8 * 1024];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                string text = sb.ToString();
                sb.Clear();
                try
                {
                    await HandleMessageAsync(text, ct);
                }
                catch (Exception ex)
                {
                    await SendAsync(new { type = "error", code = "BAD_MESSAGE", msg = ex.Message }, ct);
                }
            }
        }

        private async Task HandleMessageAsync(string text, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            switch (type)
            {
                case "subscribe":
                {
                    string subId = root.GetProperty("subId").GetString() ?? "s1";
                    var tags = root.GetProperty("tags").EnumerateArray().Select(x => x.GetInt32()).Distinct().ToArray();
                    int rate = root.TryGetProperty("rateMs", out var r) ? r.GetInt32() : 1000;
                    if (tags.Length == 0)
                    {
                        await SendAsync(new { type = "error", subId, code = "EMPTY_SUB", msg = "订阅点位为空" }, ct);
                        return;
                    }
                    if (tags.Length > 2000)
                    {
                        await SendAsync(new { type = "error", subId, code = "TOO_MANY_TAGS", msg = "单订阅点位上限 2000，请按区域拆分订阅" }, ct);
                        return;
                    }

                    var sub = new Subscription(subId, tags, rate);
                    foreach (var item in store.GetMany(tags))
                        sub.Last[item.TagId] = (item.Value, item.Quality);
                    _subs[subId] = sub;

                    await SendAsync(new
                    {
                        type = "snapshot",
                        subId,
                        serverTime = DateTimeOffset.UtcNow,
                        items = tags.Select(id => store.TryGet(id, out var it)
                            ? new { t = id, v = it.Value, txt = it.Text, q = it.Quality, ts = it.Ts }
                            : new { t = id, v = (double?)null, txt = (string?)null, q = 2, ts = DateTimeOffset.UtcNow })
                    }, ct);
                    break;
                }
                case "unsubscribe":
                {
                    string subId = root.GetProperty("subId").GetString() ?? "";
                    _subs.Remove(subId);
                    break;
                }
                case "snapshot":
                {
                    string subId = root.GetProperty("subId").GetString() ?? "";
                    if (!_subs.TryGetValue(subId, out var sub)) return;
                    await SendFullSnapshotAsync(sub, ct);
                    break;
                }
                case "ping":
                    await SendAsync(new { type = "pong", serverTime = DateTimeOffset.UtcNow }, ct);
                    break;
                default:
                    await SendAsync(new { type = "error", code = "UNKNOWN_TYPE", msg = $"未知消息类型 {type}" }, ct);
                    break;
            }
        }

        private async Task SendFullSnapshotAsync(Subscription sub, CancellationToken ct)
        {
            var items = sub.Tags.Select(id => store.TryGet(id, out var it)
                ? new { t = id, v = it.Value, txt = it.Text, q = it.Quality, ts = it.Ts }
                : new { t = id, v = (double?)null, txt = (string?)null, q = 2, ts = DateTimeOffset.UtcNow }).ToList();
            foreach (var item in store.GetMany(sub.Tags))
                sub.Last[item.TagId] = (item.Value, item.Quality);
            await SendAsync(new { type = "snapshot", subId = sub.Id, serverTime = DateTimeOffset.UtcNow, items }, ct);
        }

        private async Task PushLoopAsync(CancellationToken ct)
        {
            await SendAsync(new { type = "welcome", sessionId = _sessionId, userId, serverTime = DateTimeOffset.UtcNow }, ct);

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(200, ct);
                    var now = DateTimeOffset.UtcNow;

                    foreach (var sub in _subs.Values)
                    {
                        if (now < sub.NextDue) continue;
                        sub.NextDue = now.AddMilliseconds(sub.RateMs);

                        var changed = new List<object>();
                        var alive = new List<int>();
                        foreach (int tagId in sub.Tags)
                        {
                            if (!store.TryGet(tagId, out var item)) continue;
                            alive.Add(tagId);
                            sub.Last.TryGetValue(tagId, out var prev);
                            bool isNew = !sub.Last.ContainsKey(tagId);
                            if (isNew || prev.Value != item.Value || prev.Quality != item.Quality)
                            {
                                changed.Add(new { t = tagId, v = item.Value, txt = item.Text, q = item.Quality, ts = item.Ts });
                                sub.Last[tagId] = (item.Value, item.Quality);
                            }
                        }

                        if (changed.Count > 0)
                        {
                            // 单帧上限 256KB：超出拆分
                            const int chunk = 500;
                            for (int i = 0; i < changed.Count; i += chunk)
                            {
                                var slice = changed.Skip(i).Take(chunk).ToList();
                                await SendAsync(new { type = "delta", subId = sub.Id, serverTime = now, items = slice }, ct);
                            }
                        }
                        else if (sub.Tags.Length > 0)
                        {
                            await SendAsync(new { type = "heartbeat", subId = sub.Id, serverTime = now, alive = alive }, ct);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }

        private async Task SendAsync(object payload, CancellationToken ct)
        {
            if (socket.State != WebSocketState.Open) return;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            if (!await _sendLock.WaitAsync(300, ct))
            {
                // 慢消费者：丢弃本帧并要求前端重新拉快照
                await SendResyncAsync(ct);
                return;
            }
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception)
            {
                // 连接已断开
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendResyncAsync(CancellationToken ct)
        {
            if (!await _sendLock.WaitAsync(100, ct)) return;
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "resync", reason = "slow_consumer" }, Json);
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception) { /* 忽略 */ }
            finally { _sendLock.Release(); }
        }
    }
}
