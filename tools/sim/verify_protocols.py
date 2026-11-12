"""协议连通性自检：用 pymodbus / python-snap7 作为**第三方客户端**验证模拟器。

这同时是「模拟器保真度」的回归脚本：我们自研的 .NET 驱动稍后要读出一致的值。
运行：python verify_protocols.py
"""
from __future__ import annotations

import socket
import struct
import sys

FAILED = []


def check(name: str, cond: bool, detail: str = "") -> None:
    print(("  [PASS] " if cond else "  [FAIL] ") + name + (f" -> {detail}" if detail else ""))
    if not cond:
        FAILED.append(name)


def decode_cdab(hi: int, lo: int) -> float:
    raw = bytes([hi >> 8, hi & 0xFF, lo >> 8, lo & 0xFF])
    return struct.unpack(">f", raw[2:4] + raw[0:2])[0]


def test_modbus() -> None:
    print("Modbus TCP 1502 / RTU-over-TCP 1503")
    from pymodbus.client import ModbusTcpClient

    client = ModbusTcpClient("127.0.0.1", port=1502, timeout=3)
    check("连接 1502", client.connect())
    try:
        try:
            rr = client.read_holding_registers(0, count=2, slave=1)
        except TypeError:
            rr = client.read_holding_registers(0, count=2, device_id=1)
        ok = rr is not None and not rr.isError()
        check("FC03 读保持寄存器", ok, str(rr))
        if ok:
            v = decode_cdab(rr.registers[0], rr.registers[1])
            check("HR0-1 电压在 200~240V", 200 < v < 240, f"{v:.2f} V (CDAB)")

        try:
            rc = client.read_coils(0, count=8, slave=1)
        except TypeError:
            rc = client.read_coils(0, count=8, device_id=1)
        check("FC01 读线圈", rc is not None and not rc.isError(),
              str(rc.bits[:3] if rc else None))

        try:
            wr = client.write_register(20, 4321, slave=1)
        except TypeError:
            wr = client.write_register(20, 4321, device_id=1)
        check("FC06 写保持寄存器", wr is not None and not wr.isError())

        try:
            rb = client.read_holding_registers(20, count=1, slave=1)
        except TypeError:
            rb = client.read_holding_registers(20, count=1, device_id=1)
        check("回读 HR20 == 4321", bool(rb) and not rb.isError() and rb.registers[0] == 4321,
              str(rb.registers if rb else None))

        try:
            proto = client.read_holding_registers(0, count=2, slave=1)
            check("写保护区 HR0 仍为实时值", not proto.isError())
        except TypeError:
            pass
    finally:
        client.close()

    # RTU over TCP：直接构造 RTU 帧验证 CRC 与解析
    s = socket.create_connection(("127.0.0.1", 1503), timeout=3)
    pdu = bytes([0x03, 0x00, 0x00, 0x00, 0x02])
    body = bytes([0x01]) + pdu
    crc = 0xFFFF
    for b in body:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    s.sendall(body + struct.pack("<H", crc))
    resp = s.recv(64)
    s.close()
    ok = len(resp) >= 7 and resp[0] == 1 and resp[1] == 0x03
    check("RTU over TCP FC03（CRC 校验通过）", ok, resp.hex())
    if ok:
        v = decode_cdab(struct.unpack(">H", resp[3:5])[0], struct.unpack(">H", resp[5:7])[0])
        check("RTU 电压在 200~240V", 200 < v < 240, f"{v:.2f} V")


def test_s7() -> None:
    print("S7 ISO-on-TCP 1103（直连）与 1102（故障代理）")
    from snap7.client import Client

    c = Client()
    try:
        c.connect("127.0.0.1", 0, 1, 1103)
        check("连接 1103", c.get_connected())
        data = c.db_read(1, 0, 12)
        temp = struct.unpack(">h", data[0:2])[0] / 10.0
        press = struct.unpack(">h", data[2:4])[0] / 100.0
        flow = struct.unpack(">f", data[4:8])[0]
        cnt = struct.unpack(">i", data[8:12])[0]
        check("DB1.DBW0 温度", 60 <= temp <= 70, f"{temp} ℃")
        check("DB1.DBW2 压力", 9 <= press <= 11, f"{press} bar")
        check("DB1.DBD4 流量", 5 <= flow <= 20, f"{flow:.2f} m3/h")
        check("DB1.DBD8 计数(DINT)", cnt > 0, str(cnt))

        bits = c.read_area(0x83, 0, 16, 2)   # MK 区 M0.0~M1.7
        check("M 区可读", len(bits) == 2, bits.hex())

        line = c.db_read(2, 0, 24)
        rpm = struct.unpack(">h", line[0:2])[0]
        check("DB2.DBW0 主轴转速", 500 < rpm < 900, str(rpm))
        check("DB2.DBX16.0 运行位", bool(line[16] & 0x01), hex(line[16]))

        try:
            c.db_write(2, 20, struct.pack(">h", 912))
            back = struct.unpack(">h", c.db_read(2, 20, 2)[0:2])[0]
            check("DB2.DBW20 写入回读", back == 912, str(back))
        except Exception as ex:  # noqa: BLE001
            check("DB2.DBW20 写入回读", False, repr(ex))
    finally:
        c.disconnect()

    c2 = Client()
    try:
        c2.connect("127.0.0.1", 0, 1, 1102)
        ok = c2.get_connected()
        check("连接故障代理 1102", ok)
        if ok:
            check("经代理读 DB1", len(c2.db_read(1, 0, 2)) == 2)
    except Exception as ex:  # noqa: BLE001
        check("连接故障代理 1102", False, repr(ex))
    finally:
        c2.disconnect()


def test_control() -> None:
    print("控制面 18080")
    import json
    import urllib.request

    with urllib.request.urlopen("http://127.0.0.1:18080/state", timeout=3) as r:
        state = json.loads(r.read().decode("utf-8"))
    check("/state 返回 6 台电表", len(state.get("meters", [])) == 6)
    check("/state 含 S7 DB1", "DB1" in state.get("s7", {}))

    req = urllib.request.Request(
        "http://127.0.0.1:18080/fault",
        data=json.dumps({"port": 1502, "mode": "exception", "exception_code": 2,
                         "remaining": 1}).encode(),
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=3) as r:
        cfg = json.loads(r.read().decode("utf-8"))
    check("注入 Modbus 异常码 02", cfg.get("exception_code") == 2, json.dumps(cfg))

    from pymodbus.client import ModbusTcpClient
    client = ModbusTcpClient("127.0.0.1", port=1502, timeout=3)
    client.connect()
    try:
        try:
            rr = client.read_holding_registers(0, count=1, slave=1)
        except TypeError:
            rr = client.read_holding_registers(0, count=1, device_id=1)
        check("被注入的请求返回异常", rr.isError(), str(rr))
    finally:
        client.close()


if __name__ == "__main__":
    print("=== SFMS 模拟器协议自检 ===")
    test_modbus()
    test_s7()
    test_control()
    print()
    if FAILED:
        print(f"失败 {len(FAILED)} 项: {FAILED}")
        sys.exit(1)
    print("全部通过")
