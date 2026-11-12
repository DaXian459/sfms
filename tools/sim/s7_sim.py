"""S7 服务端模拟器（ISO-on-TCP，127.0.0.1:1103）。

基于 python-snap7 3.5.0 的纯 Python Server（无需 snap7.dll），注册：
  DB1（S7-200 SMART 的 V 区映射）、DB2/DB3（1500 两条产线）、DB10（1200）
  MK/PE/PA（位存储区 / 输入区 / 输出区）
数据由 sim_state.SimState 周期性演化，控制面可改写。
"""
from __future__ import annotations

from snap7.server import Server
from snap7.type import SrvArea

from sim_state import STATE

_server: Server | None = None


def start_s7_server(port: int = 1103) -> Server:
    global _server
    if _server is not None:
        return _server
    srv = Server()
    for db_no, buf in STATE.s7_db.items():
        srv.register_area(SrvArea.DB, db_no, buf)
    srv.register_area(SrvArea.MK, 0, STATE.s7_m)
    srv.register_area(SrvArea.PE, 0, STATE.s7_i)
    srv.register_area(SrvArea.PA, 0, STATE.s7_q)
    srv.start(tcp_port=port)
    print(f"[s7] S7 server listening on 0.0.0.0:{port} (DB1/DB2/DB3/DB10, M/I/Q)", flush=True)
    _server = srv
    return srv


def stop_s7_server() -> None:
    global _server
    if _server is not None:
        try:
            _server.stop()
        finally:
            _server = None
