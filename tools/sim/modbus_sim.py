"""Modbus 从站模拟器：Modbus TCP（1502）与 Modbus RTU over TCP（1503）。

自研实现而非套用框架，原因：需要按请求注入**指定异常码 / 静默丢包 / 定点延时**，
并通过控制面 HTTP 精确控制寄存器映射与数值，便于验证采集端的错误分类与重连逻辑。
pymodbus 仅作为独立验证客户端使用（见 verify_protocols.py）。
"""
from __future__ import annotations

import socket
import struct
import threading
import time
import traceback
from typing import Callable, Optional

from sim_state import FAULT_CLOSE, FAULT_DELAY, FAULT_DROP, FAULT_EXCEPTION, STATE

EX_ILLEGAL_FUNCTION = 0x01
EX_ILLEGAL_ADDRESS = 0x02
EX_ILLEGAL_VALUE = 0x03
EX_SLAVE_FAILURE = 0x04

MAX_READ_REGISTERS = 125
MAX_READ_BITS = 2000
MAX_WRITE_REGISTERS = 123
MAX_WRITE_BITS = 1968


def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


class ModbusError(Exception):
    def __init__(self, code: int, detail: str = "") -> None:
        super().__init__(detail or f"exception {code}")
        self.code = code


def _bits_to_bytes(bits: list[int]) -> bytes:
    out = bytearray((len(bits) + 7) // 8)
    for i, b in enumerate(bits):
        if b:
            out[i // 8] |= 1 << (i % 8)
    return bytes(out)


def _bytes_to_bits(data: bytes, count: int) -> list[int]:
    return [(data[i // 8] >> (i % 8)) & 1 for i in range(count)]


def handle_pdu(unit: int, pdu: bytes) -> bytes:
    """处理一个 PDU，返回响应 PDU；异常抛 ModbusError。"""
    if len(pdu) < 1:
        raise ModbusError(EX_ILLEGAL_FUNCTION)
    fc = pdu[0]

    coils = STATE.coils.get(unit)
    discrete = STATE.discrete.get(unit)
    holding = STATE.holding.get(unit)
    inputs = STATE.input_regs.get(unit)
    if holding is None:
        raise ModbusError(EX_ILLEGAL_ADDRESS, f"unknown slave {unit}")

    if fc in (0x01, 0x02):
        start, count = struct.unpack(">HH", pdu[1:5])
        if not 1 <= count <= MAX_READ_BITS:
            raise ModbusError(EX_ILLEGAL_VALUE)
        table = coils if fc == 0x01 else discrete
        if start + count > len(table):
            raise ModbusError(EX_ILLEGAL_ADDRESS)
        data = _bits_to_bytes(table[start:start + count])
        return bytes([fc, len(data)]) + data

    if fc in (0x03, 0x04):
        start, count = struct.unpack(">HH", pdu[1:5])
        if not 1 <= count <= MAX_READ_REGISTERS:
            raise ModbusError(EX_ILLEGAL_VALUE)
        table = holding if fc == 0x03 else inputs
        if start + count > len(table):
            raise ModbusError(EX_ILLEGAL_ADDRESS)
        payload = b"".join(struct.pack(">H", table[start + i]) for i in range(count))
        return bytes([fc, len(payload)]) + payload

    if fc == 0x05:
        start, value = struct.unpack(">HH", pdu[1:5])
        if value not in (0x0000, 0xFF00):
            raise ModbusError(EX_ILLEGAL_VALUE)
        if start >= len(coils):
            raise ModbusError(EX_ILLEGAL_ADDRESS)
        coils[start] = 1 if value == 0xFF00 else 0
        return pdu[:5]

    if fc == 0x06:
        start, value = struct.unpack(">HH", pdu[1:5])
        if start >= len(holding):
            raise ModbusError(EX_ILLEGAL_ADDRESS)
        if start in (0, 1, 2, 3, 4, 5, 6, 7):
            # 0~7 为模拟器实时演算区，写保护，避免破坏物理量语义
            raise ModbusError(EX_SLAVE_FAILURE, "live registers are read-only")
        holding[start] = value
        return pdu[:5]

    if fc == 0x0F:
        start, count = struct.unpack(">HH", pdu[1:5])
        bc = pdu[5]
        if count > MAX_WRITE_BITS or bc != (count + 7) // 8:
            raise ModbusError(EX_ILLEGAL_VALUE)
        if start + count > len(coils):
            raise ModbusError(EX_ILLEGAL_ADDRESS)
        bits = _bytes_to_bits(pdu[6:6 + bc], count)
        for i, b in enumerate(bits):
            coils[start + i] = b
        return pdu[:5]

    if fc == 0x10:
        start, count = struct.unpack(">HH", pdu[1:5])
        bc = pdu[5]
        if count > MAX_WRITE_REGISTERS or bc != count * 2:
            raise ModbusError(EX_ILLEGAL_VALUE)
        if start + count > len(holding):
            raise ModbusError(EX_ILLEGAL_ADDRESS)
        for i in range(count):
            holding[start + i] = struct.unpack(">H", pdu[6 + i * 2:8 + i * 2])[0]
        return pdu[:5]

    raise ModbusError(EX_ILLEGAL_FUNCTION, f"fc {fc} not supported")


class _BaseServer(threading.Thread):
    daemon = True

    def __init__(self, host: str, port: int, name: str, frame: str) -> None:
        super().__init__(name=name, daemon=True)
        self.host, self.port, self.frame_kind = host, port, frame
        self._sock: Optional[socket.socket] = None
        self._stop = threading.Event()
        self.connections = 0
        self.requests = 0
        self.errors = 0
        self.last_error = ""

    def stop(self) -> None:
        self._stop.set()
        try:
            if self._sock:
                self._sock.close()
        except OSError:
            pass

    def run(self) -> None:
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind((self.host, self.port))
        srv.listen(32)
        self._sock = srv
        print(f"[modbus] {self.name} listening on {self.host}:{self.port} ({self.frame_kind})", flush=True)
        while not self._stop.is_set():
            try:
                conn, addr = srv.accept()
            except OSError:
                break
            self.connections += 1
            threading.Thread(target=self._session, args=(conn, addr), daemon=True).start()

    def _session(self, conn: socket.socket, addr) -> None:
        conn.settimeout(300)
        try:
            while not self._stop.is_set():
                req = self._read_frame(conn)
                if req is None:
                    return
                unit, pdu, ctx = req
                fc = pdu[0] if pdu else 0
                self.requests += 1

                fault = STATE.take_fault(self.port, fc)
                if fault.mode == FAULT_DELAY:
                    time.sleep(fault.delay_ms / 1000.0)
                elif fault.mode == FAULT_DROP:
                    continue                      # 不回包，客户端超时
                elif fault.mode == FAULT_CLOSE:
                    return                        # 断链
                elif fault.mode == FAULT_EXCEPTION:
                    self._send(conn, unit, bytes([fc | 0x80, fault.exception_code]), ctx)
                    continue

                try:
                    resp = handle_pdu(unit, pdu)
                except ModbusError as ex:
                    self.errors += 1
                    self.last_error = str(ex)
                    resp = bytes([fc | 0x80, ex.code])
                except Exception as ex:            # noqa: BLE001
                    self.errors += 1
                    self.last_error = repr(ex)
                    traceback.print_exc()
                    resp = bytes([fc | 0x80, EX_SLAVE_FAILURE])
                self._send(conn, unit, resp, ctx)
        except (ConnectionResetError, socket.timeout, OSError):
            pass
        finally:
            try:
                conn.close()
            except OSError:
                pass

    def _read_frame(self, conn: socket.socket):
        raise NotImplementedError

    def _send(self, conn: socket.socket, unit: int, pdu: bytes, ctx=None) -> None:
        raise NotImplementedError


class ModbusTcpServer(_BaseServer):
    """标准 Modbus TCP：MBAP 头 + PDU。"""

    def __init__(self, host: str = "0.0.0.0", port: int = 1502) -> None:
        super().__init__(host, port, "modbus-tcp", "MBAP")

    def _read_frame(self, conn: socket.socket):
        head = _recv_exact(conn, 7)
        if head is None:
            return None
        tid, pid, length, unit = struct.unpack(">HHHB", head)
        if length < 2 or length > 260:
            return None
        body = _recv_exact(conn, length - 1)
        if body is None:
            return None
        return unit, body, tid

    def _send(self, conn: socket.socket, unit: int, pdu: bytes, ctx=None) -> None:
        tid = ctx or 0
        frame = struct.pack(">HHHB", tid, 0, len(pdu) + 1, unit) + pdu
        conn.sendall(frame)


class ModbusRtuOverTcpServer(_BaseServer):
    """RTU 帧（地址 + PDU + CRC16）透传到 TCP，对应串口服务器 TCP Server 模式。"""

    def __init__(self, host: str = "0.0.0.0", port: int = 1503) -> None:
        super().__init__(host, port, "modbus-rtu-over-tcp", "RTU")
        self._buf = b""
        self._lock = threading.Lock()

    def _read_frame(self, conn: socket.socket):
        while True:
            with self._lock:
                parsed = _try_parse_rtu(self._buf)
                if parsed:
                    frame, self._buf = parsed
                    unit, pdu = frame[0], frame[1:-2]
                    return unit, pdu, None
            chunk = conn.recv(512)
            if not chunk:
                return None
            with self._lock:
                self._buf += chunk
                if len(self._buf) > 1024:
                    self._buf = b""

    def _send(self, conn: socket.socket, unit: int, pdu: bytes, ctx=None) -> None:
        body = bytes([unit]) + pdu
        conn.sendall(body + struct.pack("<H", crc16(body)))


def _try_parse_rtu(buf: bytes):
    if len(buf) < 4:
        return None
    fc = buf[1]
    if fc in (0x01, 0x02, 0x03, 0x04, 0x05, 0x06):
        need = 8
    elif fc in (0x0F, 0x10):
        if len(buf) < 7:
            return None
        need = 9 + buf[6]
    elif fc & 0x80:
        need = 5
    else:
        need = 8
    if len(buf) < need:
        return None
    frame, rest = buf[:need], buf[need:]
    body, crc = frame[:-2], struct.unpack("<H", frame[-2:])[0]
    if crc16(body) != crc:
        return None
    return frame, rest


def _recv_exact(conn: socket.socket, n: int) -> Optional[bytes]:
    buf = b""
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def start_servers() -> list:
    servers = [ModbusTcpServer(port=1502), ModbusRtuOverTcpServer(port=1503)]
    for s in servers:
        s.start()
    return servers
