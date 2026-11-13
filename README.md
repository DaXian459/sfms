# SFMS 厂区设备检测系统

依据《SFMS 厂区设备检测系统开发计划书》实现的厂区设备数据采集与监控系统：西门子 S7-1200/1500/200 SMART
与 Modbus TCP / RTU 多协议接入，「端点 — 逻辑设备 — 点位」三层模型，采集调度、实时推送、历史趋势、
报警、下发写入与审计、SCADA 组态与运行态一体化。

- 详细部署与操作：`docs/运行说明.md`
- 需求逐条对照（FR-01~FR-14）：`docs/验收对照表.md`

## 技术栈

| 层次 | 选型 |
|---|---|
| 后端 | .NET 8 / ASP.NET Core（Controller + 后台采集服务 `BackgroundService`） |
| 协议 | S7comm（S7NetPlus 0.20）、Modbus TCP/RTU（自研驱动，含 RTU 帧边界与异常码分类） |
| 存储 | PostgreSQL 16（便携实例）+ EF Core 8；历史表原生月分区 + BRIN + `COPY BINARY` |
| 实时 | 进程内实时域 + WebSocket（snapshot/delta/heartbeat/resync） |
| 前端 | Vue 3 + TypeScript + Vite 5 + Element Plus + ECharts + Pinia |
| SCADA | 自研轻量组态：画面 JSON schema + SVG 图元渲染 + 四类动画，含设计器与运行态 |
| 验证 | 自研 Modbus/S7 双协议模拟器（Python）+ 故障注入代理 + 第三方客户端交叉自检 |

## 目录

| 路径 | 说明 |
|---|---|
| `src/SFMS.Shared` | DTO、枚举、协议名与质量戳文案 |
| `src/SFMS.Domain` | 实体、地址解析、字节序编解码、点位校验 |
| `src/SFMS.Protocols.Abstractions` | 驱动抽象：连接、读写跨度、错误分类、PDU 限额 |
| `src/SFMS.Protocols.Modbus` / `.S7` | 两个协议驱动 |
| `src/SFMS.Infrastructure` | EF Core 上下文、鉴权、实时域、历史仓储、Excel 导入、种子数据 |
| `src/SFMS.Collector` | 采集调度（时间轮/串行 Channel/过载跳周期）、报警引擎、历史写入、写入服务 |
| `src/SFMS.Api` | REST 接口、WebSocket、鉴权策略、静态前端托管 |
| `web/` | Vue 3 前端（监控、趋势、报警、诊断、配置、组态编辑器） |
| `tools/sim/` | Modbus TCP/RTU-over-TCP + S7 服务端 + 故障注入代理 + 控制面 |
| `tools/db/` | 便携 PostgreSQL 脚本与时序表 DDL |
| `tests/` | 领域层单元测试（地址解析、字节序、校验规则） |
| `docs/` | 运行说明、验收对照表 |

## 快速开始

```powershell
# 1) 依赖（一次性）
npm --prefix web install
python -m venv tools\sim\.venv
tools\sim\.venv\Scripts\pip install -r tools\sim\requirements.txt
.\tools\db\setup-pg.ps1            # 解压便携 PostgreSQL + initdb + 建库 sfms

# 2) 启动（数据库 → 模拟器 → API）
.\tools\start-all.ps1              # 或 -Dev 额外启动 Vite 热更新
```

打开 <http://127.0.0.1:5080>，用 `admin/admin123` 登录（`engineer/eng123`、`operator/op1234`、`observer/ob1234`）。

## 端口

| 服务 | 端口 |
|---|---|
| Web/API | 5080（开发模式前端 5173） |
| PostgreSQL | 5433 |
| Modbus TCP / RTU-over-TCP（模拟） | 1502 / 1503 |
| S7 服务端 / 故障代理 | 1103 / 1102（Modbus 代理 1504） |
| 模拟器控制面 | 18080 |

## 模拟器与自检

```powershell
tools\sim\.venv\Scripts\python.exe tools\sim\run_sim.py          # 手动启动
tools\sim\.venv\Scripts\python.exe tools\sim\verify_protocols.py # 第三方客户端交叉验证
```

故障注入（验证质量戳与通信报警）：

```powershell
curl -X POST http://127.0.0.1:18080/fault/clear
curl -X POST http://127.0.0.1:18080/fault -H "Content-Type: application/json" `
     -d '{"target":"modbus","unit":1,"kind":"exception","count":3}'
```

## 测试

```powershell
dotnet test tests\SFMS.Domain.Tests
```

覆盖：S7/Modbus 地址表达式解析与归一、四万地址换算、四种字节序编解码往返、
点位校验（区域-类型矩阵、只读区、量程、系数、重叠）、采集分组参数校验、线性换算。

## 说明

- 历史存储用 PostgreSQL 原生分区等价替代 TimescaleDB；已装 TimescaleDB 的环境可执行
  `tools/db/sql/090_timescale_optional.sql` 升级为 hypertable。
- 实时域默认进程内实现，多实例部署时把 `IRealtimeStore` 换成 Redis 实现即可。
- 本项目为闭环 MVP：功能链路完整可运行，生产部署前请替换默认口令与 JWT 密钥。
