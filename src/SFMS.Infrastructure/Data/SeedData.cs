using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SFMS.Domain;
using SFMS.Infrastructure.Auth;
using SFMS.Shared;

namespace SFMS.Infrastructure.Data;

/// <summary>
/// 首次启动的演示数据：覆盖计划书 3.3.2 的四种拓扑（Modbus 网关挂多表、S7-1500 多产线、
/// 串口服务器 485 总线、单机 S7-1200），并与 tools/sim 的模拟器寄存器映射一一对应。
/// </summary>
public static class SeedData
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task EnsureSeededAsync(SfmsDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct).ConfigureAwait(false))
            return;

        logger.LogInformation("初始化演示数据…");

        // ---------------- 用户与角色
        db.Users.AddRange(
            NewUser("admin", "admin123", "系统管理员", RoleLevel.Admin),
            NewUser("engineer", "eng123", "工艺工程师", RoleLevel.Engineer),
            NewUser("operator", "op1234", "值班操作员", RoleLevel.Operator),
            NewUser("observer", "ob1234", "观察者", RoleLevel.Observer));

        var gFast = new ScanGroup { Name = "快组", PeriodMs = 200, Priority = 9, MergeGap = 16 };
        var gMid = new ScanGroup { Name = "中组", PeriodMs = 1000, Priority = 5, MergeGap = 16 };
        var gSlow = new ScanGroup { Name = "慢组", PeriodMs = 5000, Priority = 2, MergeGap = 32 };
        var gEvent = new ScanGroup { Name = "事件组", PeriodMs = 60000, Priority = 1, MergeGap = 8 };
        db.ScanGroups.AddRange(gFast, gMid, gSlow, gEvent);

        // ---------------- 端点
        var epGw = new Endpoint
        {
            Code = "EP-GW-01", Name = "一号线 Modbus 网关", Protocol = ProtocolType.ModbusTcp,
            Ip = "127.0.0.1", Port = 1502, ConnParams = """{"timeoutMs":1000,"retry":1}""",
            Remark = "RS-485 总线经 Modbus 网关接入，挂 6 台电表（从站 1~6）"
        };
        var ep1500 = new Endpoint
        {
            Code = "EP-S7-1500", Name = "产线主控 S7-1500", Protocol = ProtocolType.S7,
            Ip = "127.0.0.1", Port = 1102, ConnParams = """{"rack":0,"slot":1,"cpu":"S71500","timeoutMs":1200,"retry":1}""",
            Remark = "经故障代理接入，用于验证超时/断链重连；一台 CPU 承载三条产线（DB2/DB3）"
        };
        var ep1200 = new Endpoint
        {
            Code = "EP-S7-1200", Name = "单机 S7-1200", Protocol = ProtocolType.S7,
            Ip = "127.0.0.1", Port = 1103, ConnParams = """{"rack":0,"slot":1,"cpu":"S71200","timeoutMs":1200,"retry":1}""",
            Remark = "单端点单设备，数据块 DB10"
        };
        var epSmart = new Endpoint
        {
            Code = "EP-S7-200S", Name = "S7-200 SMART", Protocol = ProtocolType.S7,
            Ip = "127.0.0.1", Port = 1103, ConnParams = """{"rack":0,"slot":1,"cpu":"S7200Smart","timeoutMs":1200,"retry":1}""",
            Remark = "V 区别名点位（VW100 自动归一为 DB1.DBW100）"
        };
        var epRtu = new Endpoint
        {
            Code = "EP-RTU-01", Name = "串口服务器 485 总线", Protocol = ProtocolType.ModbusRtuOverTcp,
            Ip = "127.0.0.1", Port = 1503, ConnParams = """{"timeoutMs":1500,"retry":2}""",
            Remark = "串口服务器 TCP Server 模式，RTU 帧透传"
        };
        db.Endpoints.AddRange(epGw, ep1500, ep1200, epSmart, epRtu);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ---------------- 设备
        var devices = new List<Device>();
        string[] meterNames = ["一号线总电表", "一号线分表-挤出机", "二号线总电表", "二号线分表-空压机", "三号线总电表", "车间照明回路"];
        for (int i = 1; i <= 6; i++)
        {
            devices.Add(new Device
            {
                EndpointId = epGw.Id, Code = $"METER-{i:00}", Name = meterNames[i - 1],
                DeviceKey = i.ToString(), Model = "DTSD1352", Workshop = i <= 2 ? "一号车间" : i <= 4 ? "二号车间" : "三号车间",
                Area = $"配电柜-{i}"
            });
        }

        var lineDevices = new[]
        {
            (Code: "LINE-01", Name: "一号产线", Db: 2, Area: "东区"),
            (Code: "LINE-02", Name: "二号产线", Db: 3, Area: "西区"),
        };
        foreach (var ld in lineDevices)
        {
            devices.Add(new Device
            {
                EndpointId = ep1500.Id, Code = ld.Code, Name = ld.Name, DeviceKey = "0/1",
                Model = "S7-1500", Workshop = "一号车间", Area = ld.Area
            });
        }

        devices.Add(new Device
        {
            EndpointId = ep1200.Id, Code = "PRESS-01", Name = "三号液压机", DeviceKey = "0/1",
            Model = "S7-1200", Workshop = "三号车间", Area = "压机区"
        });
        devices.Add(new Device
        {
            EndpointId = epSmart.Id, Code = "SMART-01", Name = "老线包装机", DeviceKey = "0/1",
            Model = "S7-200 SMART", Workshop = "二号车间", Area = "包装区"
        });
        devices.Add(new Device
        {
            EndpointId = epRtu.Id, Code = "RTU-METER-01", Name = "空压站电表", DeviceKey = "1",
            Model = "DTSD1352", Workshop = "公用工程", Area = "空压站"
        });
        devices.Add(new Device
        {
            EndpointId = epRtu.Id, Code = "RTU-METER-02", Name = "水泵房电表", DeviceKey = "2",
            Model = "DTSD1352", Workshop = "公用工程", Area = "水泵房"
        });
        db.Devices.AddRange(devices);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ---------------- 点位
        var tags = new List<Tag>();
        foreach (var dev in devices.Where(d => d.Code.StartsWith("METER-") || d.Code.StartsWith("RTU-METER")))
        {
            int slave = int.Parse(dev.DeviceKey);
            string p = "M" + slave;
            tags.AddRange(
            [
                TagT(dev.Id, "VOLT", "A 相电压", "HR:0", TagDataType.Float32, ByteOrderKind.CDAB, gMid.Id, unit: "V", min: 0, max: 300, scale: 1),
                TagT(dev.Id, "CURR", "A 相电流", "HR:2", TagDataType.Float32, ByteOrderKind.CDAB, gMid.Id, unit: "A", min: 0, max: 200, scale: 1),
                TagT(dev.Id, "POWER", "有功功率", "HR:4", TagDataType.Float32, ByteOrderKind.CDAB, gMid.Id, unit: "kW", min: 0, max: 200, scale: 1),
                TagT(dev.Id, "ENERGY", "累计电能", "HR:6", TagDataType.UInt32, ByteOrderKind.ABCD, gSlow.Id, unit: "kWh", scale: 0.01),
                TagT(dev.Id, "STATUS", "状态字", "HR:8", TagDataType.UInt16, ByteOrderKind.ABCD, gFast.Id),
                TagT(dev.Id, "FREQ", "频率", "HR:10", TagDataType.UInt16, ByteOrderKind.ABCD, gMid.Id, unit: "Hz", scale: 0.1),
                TagT(dev.Id, "TEMP", "柜内温度", "HR:12", TagDataType.Float32, ByteOrderKind.CDAB, gMid.Id, unit: "℃", min: -20, max: 120, scale: 1),
                TagT(dev.Id, "RUN", "运行反馈", "C:0", TagDataType.Bool, ByteOrderKind.ABCD, gFast.Id),
                TagT(dev.Id, "ESTOP", "急停", "C:2", TagDataType.Bool, ByteOrderKind.ABCD, gFast.Id),
                TagT(dev.Id, "SETPOINT", "功率设定值", "HR:20", TagDataType.Int16, ByteOrderKind.ABCD, gEvent.Id,
                    writable: true, unit: "kW", min: 0, max: 200, scale: 0.1)
            ]);
        }

        foreach (var dev in devices.Where(d => d.Code.StartsWith("LINE-")))
        {
            int dbNo = dev.Code == "LINE-01" ? 2 : 3;
            tags.AddRange(
            [
                TagT(dev.Id, "RPM", "主轴转速", $"DB{dbNo}.DBW0", TagDataType.Int16, ByteOrderKind.ABCD, gMid.Id, unit: "rpm"),
                TagT(dev.Id, "AMP", "电机电流", $"DB{dbNo}.DBW2", TagDataType.Int16, ByteOrderKind.ABCD, gMid.Id, unit: "A", scale: 0.1),
                TagT(dev.Id, "TEMP", "出口温度", $"DB{dbNo}.DBD4", TagDataType.Float32, ByteOrderKind.ABCD, gMid.Id, unit: "℃", min: 0, max: 200),
                TagT(dev.Id, "PRESS", "系统压力", $"DB{dbNo}.DBD8", TagDataType.Float32, ByteOrderKind.ABCD, gMid.Id, unit: "bar", min: 0, max: 20),
                TagT(dev.Id, "COUNT", "产量计数", $"DB{dbNo}.DBD12", TagDataType.Int32, ByteOrderKind.ABCD, gSlow.Id, unit: "件"),
                TagT(dev.Id, "RUN", "运行状态", $"DB{dbNo}.DBX16.0", TagDataType.Bool, ByteOrderKind.ABCD, gFast.Id),
                TagT(dev.Id, "OVERTEMP", "超温报警位", $"DB{dbNo}.DBX17.0", TagDataType.Bool, ByteOrderKind.ABCD, gFast.Id),
                TagT(dev.Id, "YIELD", "良品率", $"DB{dbNo}.DBW20", TagDataType.Int16, ByteOrderKind.ABCD, gMid.Id, unit: "%", scale: 0.1),
                TagT(dev.Id, "SETPOINT", "转速设定", $"DB{dbNo}.DBW22", TagDataType.Int16, ByteOrderKind.ABCD, gEvent.Id,
                    writable: true, unit: "rpm", min: 0, max: 1500)
            ]);
        }

        var press = devices.First(d => d.Code == "PRESS-01");
        tags.AddRange(
        [
            TagT(press.Id, "RPM", "主轴转速", "DB10.DBW0", TagDataType.Int16, ByteOrderKind.ABCD, gMid.Id, unit: "rpm"),
            TagT(press.Id, "AMP", "电机电流", "DB10.DBW2", TagDataType.Int16, ByteOrderKind.ABCD, gMid.Id, unit: "A", scale: 0.1),
            TagT(press.Id, "TEMP", "油温", "DB10.DBD4", TagDataType.Float32, ByteOrderKind.ABCD, gMid.Id, unit: "℃", min: 0, max: 150),
            TagT(press.Id, "PRESS", "液压压力", "DB10.DBD8", TagDataType.Float32, ByteOrderKind.ABCD, gMid.Id, unit: "bar", min: 0, max: 30),
            TagT(press.Id, "RUN", "运行状态", "DB10.DBX16.0", TagDataType.Bool, ByteOrderKind.ABCD, gFast.Id),
            TagT(press.Id, "SETPOINT", "压力设定", "DB10.DBW22", TagDataType.Int16, ByteOrderKind.ABCD, gEvent.Id,
                writable: true, unit: "bar", min: 0, max: 25, scale: 0.1)
        ]);

        var smart = devices.First(d => d.Code == "SMART-01");
        tags.AddRange(
        [
            TagT(smart.Id, "TEMP", "封口温度", "VW100", TagDataType.Int16, ByteOrderKind.ABCD, gMid.Id, unit: "℃", scale: 0.1),
            TagT(smart.Id, "PRESS", "气压", "VW102", TagDataType.Int16, ByteOrderKind.ABCD, gMid.Id, unit: "bar", scale: 0.01),
            TagT(smart.Id, "FLOW", "流量", "VD104", TagDataType.Float32, ByteOrderKind.ABCD, gMid.Id, unit: "m3/h"),
            TagT(smart.Id, "RUN", "运行标志", "DB1.DBX12.0", TagDataType.Bool, ByteOrderKind.ABCD, gFast.Id),
            TagT(smart.Id, "COUNT", "包装计数", "DB1.DBD8", TagDataType.Int32, ByteOrderKind.ABCD, gSlow.Id, unit: "件")
        ]);

        // 只读区（用于验证"只读区不可写"校验）
        tags.Add(TagT(smart.Id, "INPUT_BIT", "输入点 I0.0", "I0.0", TagDataType.Bool, ByteOrderKind.ABCD, gFast.Id));

        db.Tags.AddRange(tags);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ---------------- 报警规则
        foreach (var t in tags.Where(t => t.Code == "TEMP" && t.Unit == "℃"))
        {
            db.AlarmRules.Add(new AlarmRule { TagId = t.Id, Kind = AlarmKind.Hi, Threshold = 85, Hysteresis = 2, OnDelayMs = 3000, Severity = "Warn" });
            db.AlarmRules.Add(new AlarmRule { TagId = t.Id, Kind = AlarmKind.HiHi, Threshold = 92, Hysteresis = 2, OnDelayMs = 0, Severity = "Alarm" });
        }
        foreach (var t in tags.Where(t => t.Code == "VOLT"))
        {
            db.AlarmRules.Add(new AlarmRule { TagId = t.Id, Kind = AlarmKind.Hi, Threshold = 240, Hysteresis = 1, OnDelayMs = 2000, Severity = "Warn" });
            db.AlarmRules.Add(new AlarmRule { TagId = t.Id, Kind = AlarmKind.Lo, Threshold = 200, Hysteresis = 1, OnDelayMs = 2000, Severity = "Warn" });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ---------------- 画面
        var byCode = tags.GroupBy(t => t.DeviceId).ToDictionary(g => g.Key, g => g.ToDictionary(t => t.Code, t => t.Id));
        var screens = BuildScreens(devices, byCode);
        db.Screens.AddRange(screens);
        db.SystemSettings.Add(new SystemSetting { Key = SettingsService.ConfigVersionKey, Value = "1" });
        // 先落库拿到自增 Id，再按真实 Id 授权（否则 ScreenId 全为 0）
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 发布首个版本。只写 PublishedVersionNo 而不建 ScreenVersion 会导致：
        // 版本列表为空、运行态取不到 JSON（回落到草稿）、回滚无源版本。
        var versions = screens.Select(s => new ScreenVersion
        {
            ScreenId = s.Id,
            VersionNo = 1,
            Json = s.DraftJson,
            Note = "初始版本",
            CreatedBy = "system"
        }).ToList();
        db.ScreenVersions.AddRange(versions);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var s in screens)
        {
            s.PublishedVersionId = versions.First(v => v.ScreenId == s.Id).Id;
            db.ScreenRoles.Add(new ScreenRole { ScreenId = s.Id, RoleCode = "Observer", CanView = true, CanEdit = false });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("演示数据完成：{Endpoints} 端点 / {Devices} 设备 / {Tags} 点位 / {Screens} 画面",
            await db.Endpoints.CountAsync(ct), await db.Devices.CountAsync(ct),
            await db.Tags.CountAsync(ct), await db.Screens.CountAsync(ct));
    }

    private static AppUser NewUser(string username, string password, string display, RoleLevel role) => new()
    {
        Username = username,
        PasswordHash = PasswordHasher.Hash(password),
        DisplayName = display,
        RoleCode = role.ToString(),
        Enabled = true
    };

    private static Tag TagT(int deviceId, string code, string name, string expr, TagDataType type,
        ByteOrderKind order, int groupId, bool writable = false, string? unit = null,
        double? min = null, double? max = null, double scale = 1, double offset = 0, int strLen = 0)
    {
        // 按表达式形态判定协议：DB/V/M/I/Q 走 S7，其余走 Modbus
        bool isS7 = expr.Contains("DB", StringComparison.OrdinalIgnoreCase)
                    || expr.StartsWith('V') || expr.StartsWith('M') || expr.StartsWith('I') || expr.StartsWith('Q');
        var parsed = AddressParser.Parse(isS7 ? ProtocolType.S7 : ProtocolType.ModbusTcp, expr, type, strLen);
        return new Tag
        {
            DeviceId = deviceId,
            Code = code,
            Name = name,
            AddressExpr = expr,
            Area = parsed.Address.Area,
            AreaNo = parsed.Address.AreaNo,
            ByteOffset = parsed.Address.ByteOffset,
            BitOffset = parsed.Address.BitOffset,
            DataType = type,
            ByteOrder = order,
            StrLen = strLen,
            Writable = writable,
            Unit = unit,
            MinValue = min,
            MaxValue = max,
            Scale = scale,
            Offset = offset,
            ScanGroupId = groupId,
            Deadband = 0,
            Enabled = true,
            AddressKey = parsed.Address.Key
        };
    }

    // ---------------------------------------------------------------- 画面定义

    private static List<Screen> BuildScreens(List<Device> devices, Dictionary<int, Dictionary<string, int>> tagIds)
    {
        var list = new List<Screen>();

        var meterDevices = devices.Where(d => d.Code.StartsWith("METER-") || d.Code.StartsWith("RTU-METER")).OrderBy(d => d.Code).ToList();
        var line1 = devices.First(d => d.Code == "LINE-01");
        var line2 = devices.First(d => d.Code == "LINE-02");

        // 1) 厂区总览
        var overviewWidgets = new List<object>
        {
            Text("ov-title", 60, 40, 700, 48, "厂区设备总览", 34, "#e2e8f0", true),
            Text("ov-sub", 60, 92, 900, 30, "SFMS · 端点—逻辑设备—点位 三层模型实时数据", 16, "#7c8fa6", false)
        };
        var workshops = new[] { "一号车间", "二号车间", "三号车间", "公用工程" };
        for (int i = 0; i < workshops.Length; i++)
        {
            int x = 60 + i * 450;
            string id = $"ov-ws-{i}";
            overviewWidgets.Add(Rect(id, x, 160, 410, 260, "#101d2e", "#23384f", 8));
            overviewWidgets.Add(Text(id + "-t", x + 24, 186, 300, 34, workshops[i], 24, "#dbeafe", true));
            var dev = meterDevices.FirstOrDefault(d => d.Workshop == workshops[i]);
            if (dev is not null && tagIds.TryGetValue(dev.Id, out var map) && map.TryGetValue("RUN", out int runId))
            {
                overviewWidgets.Add(Lamp(id + "-lamp", x + 340, 184, 34, 34, 0, 1, "#f59e0b", "#16a34a", runId));
                if (map.TryGetValue("POWER", out int powerId))
                    overviewWidgets.Add(TextBinding(id + "-v", x + 24, 330, 340, 46, "", 30, "#38bdf8", powerId, "{v}", "kW"));
                if (map.TryGetValue("TEMP", out int tempId))
                    overviewWidgets.Add(TextBinding(id + "-t2", x + 24, 250, 340, 26, "柜内温度", 15, "#94a3b8", tempId, "{v}", "℃"));
            }
            overviewWidgets.Add(Text(id + "-hint", x + 24, 380, 340, 24, "点击进入车间", 13, "#64748b", false));
        }
        list.Add(NewScreen("overview", "厂区总览", "overview", overviewWidgets, "#0b1220"));

        // 2) 一号车间概览
        var wsWidgets = new List<object>
        {
            Text("ws-title", 60, 40, 700, 48, "一号车间 · 设备概览", 32, "#e2e8f0", true),
            Text("ws-back", 1560, 44, 300, 32, "← 返回厂区总览", 16, "#38bdf8", false, "overview")
        };
        for (int i = 0; i < meterDevices.Count; i++)
        {
            var d = meterDevices[i];
            int x = 60 + (i % 3) * 600;
            int y = 150 + (i / 3) * 300;
            wsWidgets.Add(Rect($"ws-d{i}", x, y, 560, 260, "#101d2e", "#23384f", 10));
            wsWidgets.Add(Text($"ws-d{i}-n", x + 24, y + 20, 380, 30, d.Name, 20, "#e2e8f0", true));
            wsWidgets.Add(Text($"ws-d{i}-c", x + 24, y + 56, 380, 24, $"{d.Code} · {d.Area}", 13, "#7c8fa6", false));
            if (tagIds.TryGetValue(d.Id, out var map))
            {
                if (map.TryGetValue("RUN", out int runId))
                    wsWidgets.Add(Lamp($"ws-d{i}-l", x + 490, y + 20, 40, 40, 0, 1, "#f59e0b", "#16a34a", runId));
                if (map.TryGetValue("VOLT", out int voltId))
                    wsWidgets.Add(TextBinding($"ws-d{i}-v", x + 24, y + 100, 240, 40, "", 30, "#38bdf8", voltId, "{v}", "V"));
                if (map.TryGetValue("POWER", out int pwId))
                    wsWidgets.Add(TextBinding($"ws-d{i}-p", x + 24, y + 150, 240, 40, "", 30, "#a78bfa", pwId, "{v}", "kW"));
                if (map.TryGetValue("TEMP", out int tpId))
                    wsWidgets.Add(TextBinding($"ws-d{i}-t", x + 24, y + 205, 300, 26, "柜内温度", 15, "#94a3b8", tpId, "{v}", "℃"));
            }
        }
        list.Add(NewScreen("workshop-a", "一号车间概览", "workshop", wsWidgets, "#0b1220"));

        // 3) 设备详情（电表）
        var meter1 = meterDevices.First();
        var m1 = tagIds[meter1.Id];
        var devWidgets = new List<object>
        {
            Text("dv-title", 60, 40, 900, 48, $"{meter1.Name} · 设备详情", 32, "#e2e8f0", true),
            Text("dv-back", 1560, 44, 300, 32, "← 返回车间概览", 16, "#38bdf8", false, "workshop-a"),
            Rect("dv-card1", 60, 150, 520, 300, "#101d2e", "#23384f", 10),
            Text("dv-c1t", 84, 170, 400, 28, "三相电参量", 20, "#e2e8f0", true),
            Text("dv-l1", 84, 220, 100, 26, "电压", 15, "#94a3b8", false),
            TextBinding("dv-v1", 180, 214, 260, 40, "", 32, "#38bdf8", m1["VOLT"], "{v}", "V"),
            Text("dv-l2", 84, 276, 100, 26, "电流", 15, "#94a3b8", false),
            TextBinding("dv-v2", 180, 270, 260, 40, "", 32, "#38bdf8", m1["CURR"], "{v}", "A"),
            Text("dv-l3", 84, 332, 100, 26, "功率", 15, "#94a3b8", false),
            TextBinding("dv-v3", 180, 326, 260, 40, "", 32, "#a78bfa", m1["POWER"], "{v}", "kW"),
            Text("dv-l4", 84, 388, 120, 26, "柜内温度", 15, "#94a3b8", false),
            TextBinding("dv-v4", 180, 382, 260, 40, "", 32, "#f472b6", m1["TEMP"], "{v}", "℃"),

            Rect("dv-card2", 620, 150, 520, 300, "#101d2e", "#23384f", 10),
            Text("dv-c2t", 644, 170, 400, 28, "工艺流程", 20, "#e2e8f0", true),
            Tank("dv-tank", 660, 220, 180, 190, "#132538", "#1d4ed8", 100, m1["CURR"]),
            Pipe("dv-pipe1", 840, 300, 150, 14, "#2b4a63"),
            Pump("dv-pump", 990, 262, 90, 90, "#1f6feb", m1["RUN"]),
            Pipe("dv-pipe2", 1080, 300, 40, 14, "#2b4a63"),
            TextBinding("dv-flow", 960, 370, 180, 30, "", 20, "#38bdf8", m1["POWER"], "{v}", "kW"),

            Rect("dv-card3", 60, 480, 1080, 220, "#101d2e", "#23384f", 10),
            Text("dv-c3t", 84, 500, 400, 28, "累计与状态", 20, "#e2e8f0", true),
            Text("dv-e1", 84, 550, 160, 26, "累计电能", 15, "#94a3b8", false),
            TextBinding("dv-ev", 250, 544, 240, 38, "", 28, "#fbbf24", m1["ENERGY"], "{v}", "kWh"),
            Text("dv-e2", 520, 550, 160, 26, "频率", 15, "#94a3b8", false),
            TextBinding("dv-fr", 660, 544, 200, 38, "", 28, "#34d399", m1["FREQ"], "{v}", "Hz"),
            Lamp("dv-run", 900, 540, 44, 44, 0, 1, "#f59e0b", "#16a34a", m1["RUN"]),
            Text("dv-run-t", 960, 550, 140, 26, "运行", 15, "#94a3b8", false)
        };
        list.Add(NewScreen("device-meter-1", $"{meter1.Name} · 设备详情", "device", devWidgets, "#0b1220"));

        // 4) 设备详情（产线）
        var l1 = tagIds[line1.Id];
        list.Add(NewScreen("device-line-1", $"{line1.Name} · 设备详情", "device",
        [
            Text("ln-title", 60, 40, 900, 48, $"{line1.Name} · 设备详情", 32, "#e2e8f0", true),
            Text("ln-back", 1560, 44, 300, 32, "← 返回厂区总览", 16, "#38bdf8", false, "overview"),
            Rect("ln-card1", 60, 150, 560, 280, "#101d2e", "#23384f", 10),
            Text("ln-c1t", 84, 170, 400, 28, "关键工艺参数", 20, "#e2e8f0", true),
            Text("ln-l1", 84, 220, 140, 26, "主轴转速", 15, "#94a3b8", false),
            TextBinding("ln-v1", 240, 214, 260, 40, "", 30, "#38bdf8", l1["RPM"], "{v}", "rpm"),
            Text("ln-l2", 84, 276, 140, 26, "出口温度", 15, "#94a3b8", false),
            TextBinding("ln-v2", 240, 270, 260, 40, "", 30, "#f472b6", l1["TEMP"], "{v}", "℃"),
            Text("ln-l3", 84, 332, 140, 26, "系统压力", 15, "#94a3b8", false),
            TextBinding("ln-v3", 240, 326, 260, 40, "", 30, "#a78bfa", l1["PRESS"], "{v}", "bar"),
            Text("ln-l4", 84, 388, 140, 26, "良品率", 15, "#94a3b8", false),
            TextBinding("ln-v4", 240, 382, 260, 40, "", 30, "#34d399", l1["YIELD"], "{v}", "%"),

            Gauge("ln-gauge", 700, 170, 260, 240, 0, 1200, "#38bdf8", l1["RPM"], "rpm"),
            Fan("ln-fan", 1030, 200, 120, 120, "#1f6feb", l1["RUN"]),
            Valve("ln-valve", 1030, 350, 120, 90, "#1f6feb", l1["RUN"]),
            TextBinding("ln-count", 700, 440, 300, 34, "", 22, "#fbbf24", l1["COUNT"], "{v}", "件"),
            Lamp("ln-run", 1060, 470, 40, 40, 0, 1, "#f59e0b", "#16a34a", l1["RUN"])
        ], "#0b1220"));

        // 5) 产线二（验证多设备独立组态）
        var l2 = tagIds[line2.Id];
        list.Add(NewScreen("device-line-2", $"{line2.Name} · 设备详情", "device",
        [
            Text("l2-title", 60, 40, 900, 48, $"{line2.Name} · 设备详情", 32, "#e2e8f0", true),
            Text("l2-back", 1560, 44, 300, 32, "← 返回厂区总览", 16, "#38bdf8", false, "overview"),
            Rect("l2-card", 60, 150, 560, 280, "#101d2e", "#23384f", 10),
            Text("l2-c1t", 84, 170, 400, 28, "关键工艺参数", 20, "#e2e8f0", true),
            Text("l2-l1", 84, 220, 140, 26, "主轴转速", 15, "#94a3b8", false),
            TextBinding("l2-v1", 240, 214, 260, 40, "", 30, "#38bdf8", l2["RPM"], "{v}", "rpm"),
            Text("l2-l2", 84, 276, 140, 26, "出口温度", 15, "#94a3b8", false),
            TextBinding("l2-v2", 240, 270, 260, 40, "", 30, "#f472b6", l2["TEMP"], "{v}", "℃"),
            Gauge("l2-gauge", 700, 170, 260, 240, 0, 20, "#a78bfa", l2["PRESS"], "bar"),
            Motor("l2-motor", 1000, 200, 120, 120, "#1f6feb", l2["RUN"])
        ], "#0b1220"));

        return list;
    }

    private static Screen NewScreen(string code, string name, string type, List<object> widgets, string background)
    {
        var doc = new
        {
            schemaVersion = 1,
            code,
            name,
            width = 1920,
            height = 1080,
            background,
            layers = new[]
            {
                new { id = "L1", name = "背景层", visible = true, locked = true, z = 1 },
                new { id = "L2", name = "动态层", visible = true, locked = false, z = 2 }
            },
            widgets
        };
        return new Screen
        {
            Code = code,
            Name = name,
            Type = type,
            Width = 1920,
            Height = 1080,
            Status = ScreenStatus.Published,
            DraftJson = JsonSerializer.Serialize(doc, Json),
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "system",
            PublishedVersionNo = 1
        };
    }

    // ---- 图元构造（props 与前端渲染器一一对应）

    private static object W(string id, string type, double x, double y, double w, double h,
        Dictionary<string, object?> props, List<object>? bindings = null, List<object>? animations = null,
        List<object>? events = null) => new
    {
        id,
        type,
        layerId = type is "text" or "rect" ? "L1" : "L2",
        x,
        y,
        w,
        h,
        rotation = 0,
        props,
        bindings = bindings ?? [],
        animations = animations ?? [],
        events = events ?? []
    };

    private static Dictionary<string, object?> P(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    private static object Rect(string id, double x, double y, double w, double h, string fill, string stroke, double radius) =>
        W(id, "rect", x, y, w, h, P(("fill", fill), ("stroke", stroke), ("strokeWidth", 1.5), ("radius", radius)));

    private static object Text(string id, double x, double y, double w, double h, string text, double fontSize,
        string color, bool bold, string? navigate = null) =>
        W(id, "text", x, y, w, h, P(("text", text), ("fontSize", fontSize), ("color", color), ("bold", bold)),
            events: navigate is null ? null : [new { kind = "click", action = "navigate", target = navigate }]);

    private static object TextBinding(string id, double x, double y, double w, double h, string label,
        double fontSize, string color, int tagId, string format, string unit) =>
        W(id, "text", x, y, w, h,
            P(("text", label), ("fontSize", fontSize), ("color", color), ("bold", true)),
            bindings: [new { prop = "text", tagId, mode = "direct", format, unit, label }],
            animations: [new { kind = "quality", tagId }]);

    private static object Lamp(string id, double x, double y, double w, double h, double minValue, double maxValue,
        string offColor, string onColor, int tagId) =>
        W(id, "lamp", x, y, w, h,
            P(("offColor", offColor), ("onColor", onColor), ("shape", "circle")),
            animations:
            [
                new { kind = "stateMap", prop = "fill", tagId, @default = offColor,
                      map = new object[] { new { op = "eq", value = 1, result = onColor } } },
                new { kind = "blink", tagId, when = 1, periodMs = 1000 }
            ]);

    private static object Tank(string id, double x, double y, double w, double h, string fill, string levelColor,
        double max, int tagId) =>
        W(id, "tank", x, y, w, h, P(("fill", fill), ("levelColor", levelColor), ("min", 0), ("max", max)),
            animations: [new { kind = "linear", prop = "level", tagId, min = 0, max, from = 0, to = 1 }]);

    private static object Pipe(string id, double x, double y, double w, double h, string stroke) =>
        W(id, "pipe", x, y, w, h, P(("stroke", stroke), ("strokeWidth", h)));

    private static object Pump(string id, double x, double y, double w, double h, string color, int tagId) =>
        W(id, "pump", x, y, w, h, P(("fill", color)),
            animations: [new { kind = "linear", prop = "rotate", tagId, min = 0, max = 1, from = 0, to = 360 }]);

    private static object Fan(string id, double x, double y, double w, double h, string color, int tagId) =>
        W(id, "fan", x, y, w, h, P(("fill", color)),
            animations: [new { kind = "linear", prop = "rotate", tagId, min = 0, max = 1, from = 0, to = 360 }]);

    private static object Valve(string id, double x, double y, double w, double h, string color, int tagId) =>
        W(id, "valve", x, y, w, h, P(("fill", color)),
            animations: [new { kind = "stateMap", prop = "fill", tagId, @default = "#475569",
                                map = new object[] { new { op = "eq", value = 1, result = color } } }]);

    private static object Motor(string id, double x, double y, double w, double h, string color, int tagId) =>
        W(id, "motor", x, y, w, h, P(("fill", color)),
            animations: [new { kind = "stateMap", prop = "fill", tagId, @default = "#475569",
                                map = new object[] { new { op = "eq", value = 1, result = color } } }]);

    private static object Gauge(string id, double x, double y, double w, double h, double min, double max,
        string color, int tagId, string unit) =>
        W(id, "gauge", x, y, w, h, P(("min", min), ("max", max), ("color", color), ("unit", unit)),
            bindings: [new { prop = "value", tagId, mode = "direct", format = "{v}", unit }],
            animations: [new { kind = "linear", prop = "needle", tagId, min, max, from = -120, to = 120 }]);
}
