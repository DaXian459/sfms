"""一键启动全部模拟器：

  Modbus TCP         0.0.0.0:1502   从站 1~6
  Modbus RTU over TCP 0.0.0.0:1503  从站 1~2
  S7 (ISO-on-TCP)    0.0.0.0:1103   DB1/DB2/DB3/DB10 + M/I/Q
  故障代理            127.0.0.1:1102 -> 1103（S7 端点用，可注入超时/断链）
  故障代理            127.0.0.1:1504 -> 1502（Modbus 端点用）
  控制面              0.0.0.0:18080

用法：python run_sim.py
"""
from __future__ import annotations

import signal
import sys
import time

from control_api import start_control_api
from fault_proxy import FaultProxy
from modbus_sim import start_servers
from s7_sim import start_s7_server, stop_s7_server
from sim_state import STATE


def main() -> int:
    modbus = start_servers()
    s7 = start_s7_server(1103)
    proxies = [FaultProxy(1102, 1103), FaultProxy(1504, 1502)]
    for p in proxies:
        p.start()
    control = start_control_api(18080)

    print("[sim] 模拟器就绪。Ctrl+C 退出。", flush=True)
    last = time.time()
    last_report = time.time()
    running = True

    def _stop(*_a):
        nonlocal running
        running = False

    signal.signal(signal.SIGINT, _stop)
    try:
        while running:
            now = time.time()
            dt = max(0.02, min(now - last, 1.0))
            last = now
            STATE.tick(dt)
            if now - last_report >= 30:
                last_report = now
                s = STATE.snapshot()
                m = s["meters"][0]
                print(f"[sim] tick={s['tick']} 上限={s['uptime_s']}s "
                      f"表1 电压={m['voltage']}V 功率={m['power']}kW "
                      f"DB1 温度={s['s7']['DB1']['temp_x10'] / 10}℃ "
                      f"故障={list(s['faults'].keys())}", flush=True)
            time.sleep(0.2)
    finally:
        for p in proxies:
            p.stop()
        for s in modbus:
            s.stop()
        stop_s7_server()
        control.shutdown()
        print("[sim] 已停止", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
