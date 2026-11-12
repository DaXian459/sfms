# SFMS 厂区设备检测系统

依据《SFMS 厂区设备检测系统开发计划书》实现：西门子 S7-1200/1500/200 SMART 与 Modbus TCP/RTU 多协议接入，
「端点—逻辑设备—点位」三层模型，采集调度、实时推送、历史趋势、报警、写入与审计、SCADA 组态与运行态。

> 详细启动与验收见 `docs/运行说明.md`、`docs/验收对照表.md`（随开发进度补全）。

## 目录

| 路径 | 说明 |
|---|---|
| `src/` | .NET 8 服务端（Domain / Protocols / Infrastructure / Collector / Api） |
| `web/` | Vue 3 + TypeScript 前端（含 SCADA 组态编辑器与运行态） |
| `tools/sim/` | Modbus / S7 双协议模拟器与故障注入（Python） |
| `tools/db/` | PostgreSQL 便携实例脚本与时序表 DDL |
| `docs/` | 运行说明、验收对照表 |

## 模拟器（已可运行）

```bash
cd tools/sim && ./.venv/Scripts/python.exe run_sim.py
```
端口：Modbus TCP 1502、RTU-over-TCP 1503、S7 1103（代理 1102）、控制面 18080。
自检：`./.venv/Scripts/python.exe verify_protocols.py`
