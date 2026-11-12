"""模拟器控制面 HTTP 接口（127.0.0.1:18080）。

用于演示与联调时实时改变设备侧状态：
  GET  /health                     存活检查
  GET  /state                      当前全部模拟量
  GET  /profile                    寄存器映射说明
  POST /set                        {"slave":1,"kind":"holding","address":10,"value":505}
  POST /s7                         {"db":1,"offset":0,"hex":"0281"}
  POST /meter                      {"slave":1,"running":false}
  POST /fault                      {"port":1502,"mode":"exception","exception_code":4,"remaining":3}
  POST /fault/clear                {"port":1502} 或 {} 清全部
"""
from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

from sim_state import STATE

PROFILE = {
    "modbus": {
        "slaves": "1~6，从站地址即设备键",
        "regions": {
            "holding(FC03)": [
                "HR0-1 FLOAT32 电压(V) 字节序 CDAB",
                "HR2-3 FLOAT32 电流(A) CDAB",
                "HR4-5 FLOAT32 有功功率(kW) CDAB",
                "HR6-7 UINT32 电能(×0.01 kWh)",
                "HR8 UINT16 状态字 bit0 运行 bit1 就绪 bit2 过流",
                "HR9 UINT16 功率×100 低字",
                "HR10 UINT16 频率×10",
                "HR12-13 FLOAT32 温度(℃) CDAB",
                "HR20+ 可写设定区（演示参数下发）",
            ],
            "input(FC04)": ["IR0-1 FLOAT32 电压 ABCD", "IR2-3 FLOAT32 电流 ABCD", "IR4 频率×10", "IR5 运行标志"],
            "coil(FC01)": ["C0 运行指令", "C1 复位", "C2 急停"],
            "discrete(FC02)": ["DI0 运行反馈", "DI1 急停反馈", "DI2+ 周期性置位"],
        },
    },
    "s7": {
        "DB1": "S7-200 SMART V 区：DBW0 温度×10, DBW2 压力×100, DBD4 流量(AB), DBD8 计数(DINT), DBX12.0 运行位",
        "DB2/DB3": "1500 两条产线：DBW0 主轴转速, DBW2 电流×10, DBD4 出口温度, DBD8 压力, DBD12 产量(DINT), DBX16.0 运行, DBX17.0 超温报警, DBW20 良品率×10",
        "DB10": "1200 单机：布局同 DB2",
        "MK": "M0.1/M0.3 心跳位, M10.2",
        "PE/PA": "I/Q 区，用于只读区校验",
    },
}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # 静音访问日志
        pass

    def _json(self, code: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self) -> dict:
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        try:
            return json.loads(self.rfile.read(length).decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return {}

    def do_GET(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        if path == "/health":
            self._json(200, {"ok": True, "tick": STATE.tick_no})
        elif path == "/state":
            self._json(200, STATE.snapshot())
        elif path == "/profile":
            self._json(200, PROFILE)
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        body = self._body()
        if path == "/set":
            ok = STATE.set_register(
                int(body.get("slave", 1)), str(body.get("kind", "holding")),
                int(body.get("address", 0)), int(body.get("value", 0)))
            self._json(200 if ok else 400, {"ok": ok})
        elif path == "/s7":
            ok = STATE.set_s7(int(body.get("db", 1)), int(body.get("offset", 0)),
                              str(body.get("hex", "")))
            self._json(200 if ok else 400, {"ok": ok})
        elif path == "/meter":
            slave = int(body.get("slave", 1))
            running = bool(body.get("running", True))
            ok = slave in STATE.coils
            if ok:
                STATE.coils[slave][0] = 1 if running else 0
            self._json(200 if ok else 400, {"ok": ok})
        elif path == "/fault":
            port = int(body.get("port", 1502))
            cfg = STATE.set_fault(
                port,
                mode=body.get("mode"),
                delay_ms=body.get("delay_ms"),
                exception_code=body.get("exception_code"),
                function_code=body.get("function_code"),
                remaining=body.get("remaining"),
            )
            self._json(200, cfg.to_dict())
        elif path == "/fault/clear":
            STATE.clear_fault(body.get("port"))
            self._json(200, {"ok": True})
        else:
            self._json(404, {"error": "not found"})


def start_control_api(port: int = 18080) -> ThreadingHTTPServer:
    srv = ThreadingHTTPServer(("0.0.0.0", port), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    print(f"[control] http://127.0.0.1:{port}  (GET /state, POST /fault, POST /set)", flush=True)
    return srv
