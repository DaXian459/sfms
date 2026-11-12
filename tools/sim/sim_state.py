"""SFMS 模拟器共享状态：寄存器映射、故障注入、实时数据。

设计要点：
- 所有 Modbus 从站与 S7 数据块共用一份内存状态，控制面 HTTP 直接改这里的值。
- 采集端看到的"物理量"由本模块的 tick() 按正弦/斜坡演化，用于验证趋势与报警。
- 故障注入按端口隔离：none / delay / exception / drop / close。
"""
from __future__ import annotations

import math
import random
import threading
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional

# ---------------------------------------------------------------- 故障注入

FAULT_NONE = "none"
FAULT_DELAY = "delay"
FAULT_EXCEPTION = "exception"
FAULT_DROP = "drop"          # 静默丢弃请求（不回包），用于触发客户端超时
FAULT_CLOSE = "close"        # 立即断开连接


@dataclass
class FaultConfig:
    mode: str = FAULT_NONE
    delay_ms: int = 800
    exception_code: int = 4      # 03 非法数据值 / 04 从站故障 / 0B 网关目标失败
    function_code: int = 0       # 0 = 全部功能码
    remaining: int = 0           # -1 = 永久；>0 = 剩余生效次数

    def to_dict(self) -> dict:
        return {
            "mode": self.mode,
            "delay_ms": self.delay_ms,
            "exception_code": self.exception_code,
            "function_code": self.function_code,
            "remaining": self.remaining,
        }


@dataclass
class MeterProfile:
    """一台电表的寄存器映射（保持寄存器，零基）。"""
    slave: int
    name: str
    base_voltage: float = 220.0
    base_current: float = 12.0
    power_factor: float = 0.92
    energy: float = 1200.0
    running: int = 1


class SimState:
    """全局模拟状态。线程安全（粗粒度锁，模拟器性能足够）。"""

    COIL_COUNT = 2000
    REG_COUNT = 512

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self.faults: Dict[int, FaultConfig] = {}          # port -> fault
        self.meters: Dict[int, MeterProfile] = {
            1: MeterProfile(1, "一号线总电表", 220.0, 12.0, 0.92, 1200.0, 1),
            2: MeterProfile(2, "一号线分表-挤出机", 219.4, 8.6, 0.90, 860.0, 1),
            3: MeterProfile(3, "二号线总电表", 221.2, 15.4, 0.93, 2310.0, 1),
            4: MeterProfile(4, "二号线分表-空压机", 220.6, 5.2, 0.88, 640.0, 1),
            5: MeterProfile(5, "三号线总电表", 219.8, 10.8, 0.91, 1780.0, 1),
            6: MeterProfile(6, "车间照明回路", 221.0, 2.4, 0.97, 320.0, 1),
        }
        self.holding: Dict[int, List[int]] = {}
        self.input_regs: Dict[int, List[int]] = {}
        self.coils: Dict[int, List[int]] = {}
        self.discrete: Dict[int, List[int]] = {}
        for slave in self.meters:
            self.holding[slave] = [0] * self.REG_COUNT
            self.input_regs[slave] = [0] * self.REG_COUNT
            self.coils[slave] = [0] * self.COIL_COUNT
            self.discrete[slave] = [0] * self.COIL_COUNT
            # 线圈 0 = 运行指令，1 = 复位，2 = 急停
            self.coils[slave][0] = 1
            self.coils[slave][2] = 0
            for b in range(0, 40):
                self.discrete[slave][b] = 1 if b % 3 else 0

        # S7 数据块：DB1（S7-200 SMART / V 区）、DB2、DB3（1500 三条产线）、DB10（1200）
        self.s7_db: Dict[int, bytearray] = {n: bytearray(512) for n in (1, 2, 3, 10)}
        self.s7_m = bytearray(128)     # 位存储区
        self.s7_i = bytearray(64)      # 输入区
        self.s7_q = bytearray(64)      # 输出区

        self.started_at = time.time()
        self.tick_no = 0
        self._seed_s7()

    # ------------------------------------------------------------ S7 种子
    def _seed_s7(self) -> None:
        import struct
        # DB1: 0=INT16 温度(×10), 2=INT16 压力(×100), 4=FLOAT32 流量(ABCD), 8=DINT 计数
        struct.pack_into(">h", self.s7_db[1], 0, 655)
        struct.pack_into(">h", self.s7_db[1], 2, 1032)
        struct.pack_into(">f", self.s7_db[1], 4, 12.5)
        struct.pack_into(">i", self.s7_db[1], 8, 128000)
        self.s7_db[1][12] = 1                    # DB1.DBX12.0 运行标志

        for idx, db in enumerate((2, 3, 10), start=1):
            struct.pack_into(">h", self.s7_db[db], 0, 620 + idx * 7)     # 主轴转速
            struct.pack_into(">h", self.s7_db[db], 2, 480 + idx * 11)    # 电机电流 ×10
            struct.pack_into(">f", self.s7_db[db], 4, 78.4 + idx)        # 出口温度
            struct.pack_into(">f", self.s7_db[db], 8, 3.6 + idx * 0.25)  # 压力
            struct.pack_into(">i", self.s7_db[db], 12, 52000 + idx * 1200)  # 产量
            self.s7_db[db][16] = 1 if idx % 2 else 0                     # 运行状态位
            self.s7_db[db][17] = 0
            struct.pack_into(">h", self.s7_db[db], 20, 42 + idx)         # 良品率 ×10

        self.s7_m[0] = 0b0000_1010
        self.s7_m[10] = 0b0000_0100

    # ------------------------------------------------------------ 故障
    def set_fault(self, port: int, **kw) -> FaultConfig:
        with self._lock:
            cfg = self.faults.get(port) or FaultConfig()
            for k, v in kw.items():
                if hasattr(cfg, k) and v is not None:
                    setattr(cfg, k, v)
            self.faults[port] = cfg
            return cfg

    def clear_fault(self, port: Optional[int] = None) -> None:
        with self._lock:
            if port is None:
                self.faults.clear()
            else:
                self.faults.pop(port, None)

    def take_fault(self, port: int, function_code: int) -> FaultConfig:
        """取端口当前生效的故障（消耗一次计数）。"""
        with self._lock:
            cfg = self.faults.get(port)
            if not cfg or cfg.mode == FAULT_NONE:
                return FaultConfig()
            if cfg.function_code and cfg.function_code != function_code:
                return FaultConfig()
            if cfg.remaining > 0:
                cfg.remaining -= 1
                if cfg.remaining == 0:
                    snap = FaultConfig(**cfg.__dict__)
                    self.faults.pop(port, None)
                    return snap
            return FaultConfig(**cfg.__dict__)

    # ------------------------------------------------------------ 实时演化
    def tick(self, dt: float) -> None:
        with self._lock:
            self.tick_no += 1
            t = time.time() - self.started_at
            for slave, m in self.meters.items():
                hr = self.holding[slave]
                ir = self.input_regs[slave]
                running = self.coils[slave][0] and not self.coils[slave][2]
                m.running = 1 if running else 0

                wobble = math.sin(t / 7.0 + slave) * 0.02 + math.sin(t / 1.7 + slave * 3) * 0.004
                voltage = m.base_voltage * (1 + wobble)
                current = (m.base_current if running else 0.12) * (1 + wobble * 1.8)
                power = voltage * current * m.power_factor / 1000.0     # kW
                if running:
                    m.energy += power * dt / 3600.0

                self._put_f32(hr, 0, voltage, "CDAB")
                self._put_f32(hr, 2, current, "CDAB")
                self._put_f32(hr, 4, power, "CDAB")
                self._put_u32(hr, 6, int(m.energy * 100))
                hr[8] = (0b0000_0011 if running else 0b0000_0000) | (0b0100 if current > 100 else 0)
                hr[9] = int(power * 100) & 0xFFFF
                hr[10] = 50 + int(math.sin(t / 5.0) * 2)          # 频率 ×10
                self._put_f32(hr, 12, 3.8 + math.sin(t / 11.0) * 0.4 + slave * 0.1, "CDAB")  # 温度

                self._put_f32(ir, 0, voltage, "ABCD")
                self._put_f32(ir, 2, current, "ABCD")
                ir[4] = hr[10]
                ir[5] = 1 if running else 0
                self.discrete[slave][0] = 1 if running else 0
                self.discrete[slave][1] = self.coils[slave][2]

            # S7 实时量
            import struct
            for idx, db in enumerate((2, 3, 10), start=1):
                buf = self.s7_db[db]
                running = buf[16] & 0x01
                rpm = 600 + idx * 10 + int(math.sin(t / 6.0 + idx) * 25)
                amp = (470 + idx * 10) + int(math.sin(t / 3.0 + idx) * 30)
                temp = 78.4 + idx + math.sin(t / 9.0 + idx) * 2.5
                press = 3.6 + idx * 0.25 + math.sin(t / 13.0) * 0.15
                if running:
                    struct.pack_into(">h", buf, 0, rpm)
                    struct.pack_into(">h", buf, 2, amp if amp < 32767 else 32767)
                    struct.pack_into(">f", buf, 4, temp)
                    struct.pack_into(">f", buf, 8, press)
                    struct.pack_into(">i", buf, 12, struct.unpack_from(">i", buf, 12)[0] + 3)
                buf[17] = 1 if temp > 85 else 0
                struct.pack_into(">h", buf, 20, 980 + int(math.sin(t / 17.0) * 8))

            b1 = self.s7_db[1]
            struct.pack_into(">h", b1, 0, 655 + int(math.sin(t / 8.0) * 30))
            struct.pack_into(">h", b1, 2, 1032 + int(math.sin(t / 5.0) * 45))
            struct.pack_into(">f", b1, 4, 12.5 + math.sin(t / 12.0) * 1.4)
            struct.pack_into(">i", b1, 8, struct.unpack_from(">i", b1, 8)[0] + 1)
            self.s7_m[0] ^= 0b0000_0010 if int(t) % 3 == 0 else 0

    @staticmethod
    def _put_f32(regs: List[int], offset: int, value: float, order: str) -> None:
        import struct
        raw = struct.pack(">f", value)
        swap = SimState._order_to_regs(raw, order)
        regs[offset] = swap[0]
        regs[offset + 1] = swap[1]

    @staticmethod
    def _put_u32(regs: List[int], offset: int, value: int) -> None:
        regs[offset] = (value >> 16) & 0xFFFF
        regs[offset + 1] = value & 0xFFFF

    @staticmethod
    def _order_to_regs(raw: bytes, order: str) -> List[int]:
        """把规范大端 4 字节转成指定字节序下的两个寄存器值。"""
        b = list(raw)
        if order == "ABCD":
            seq = [b[0], b[1], b[2], b[3]]
        elif order == "CDAB":
            seq = [b[2], b[3], b[0], b[1]]
        elif order == "BADC":
            seq = [b[1], b[0], b[3], b[2]]
        else:  # DCBA
            seq = [b[3], b[2], b[1], b[0]]
        return [(seq[0] << 8) | seq[1], (seq[2] << 8) | seq[3]]

    # ------------------------------------------------------------ 控制面
    def set_register(self, slave: int, kind: str, address: int, value: int) -> bool:
        with self._lock:
            table = {
                "holding": self.holding, "input": self.input_regs,
                "coil": self.coils, "discrete": self.discrete,
            }.get(kind)
            if table is None or slave not in table:
                return False
            if not (0 <= address < len(table[slave])):
                return False
            table[slave][address] = value & 0xFFFF if kind in ("holding", "input") else (1 if value else 0)
            return True

    def set_s7(self, db: int, offset: int, raw_hex: str) -> bool:
        with self._lock:
            if db not in self.s7_db:
                return False
            data = bytes.fromhex(raw_hex)
            if offset + len(data) > len(self.s7_db[db]):
                return False
            self.s7_db[db][offset:offset + len(data)] = data
            return True

    def snapshot(self) -> dict:
        with self._lock:
            return {
                "uptime_s": round(time.time() - self.started_at, 1),
                "tick": self.tick_no,
                "faults": {str(k): v.to_dict() for k, v in self.faults.items()},
                "meters": [
                    {
                        "slave": m.slave, "name": m.name, "running": m.running,
                        "voltage": self.holding[m.slave][0] and round(
                            self._read_f32(self.holding[m.slave], 0, "CDAB"), 2),
                        "current": round(self._read_f32(self.holding[m.slave], 2, "CDAB"), 2),
                        "power": round(self._read_f32(self.holding[m.slave], 4, "CDAB"), 3),
                        "energy": round(self._read_u32(self.holding[m.slave], 6) / 100.0, 2),
                    }
                    for m in self.meters.values()
                ],
                "s7": {
                    "DB1": {"temp_x10": self._read_i16(self.s7_db[1], 0),
                            "press_x100": self._read_i16(self.s7_db[1], 2),
                            "flow": round(self._read_f32(self.s7_db[1], 4), 3)},
                    "DB2": self._s7_line(2), "DB3": self._s7_line(3), "DB10": self._s7_line(10),
                },
            }

    def _s7_line(self, db: int) -> dict:
        return {
            "rpm": self._read_i16(self.s7_db[db], 0),
            "amp_x10": self._read_i16(self.s7_db[db], 2),
            "temp": round(self._read_f32(self.s7_db[db], 4), 2),
            "press": round(self._read_f32(self.s7_db[db], 8), 3),
            "count": self._read_i32(self.s7_db[db], 12),
            "running": self.s7_db[db][16],
            "alarm": self.s7_db[db][17],
        }

    @staticmethod
    def _read_i16(buf, off):
        import struct
        return struct.unpack_from(">h", buf, off)[0]

    @staticmethod
    def _read_i32(buf, off):
        import struct
        return struct.unpack_from(">i", buf, off)[0]

    @staticmethod
    def _read_u32(regs, off):
        return ((regs[off] & 0xFFFF) << 16) | (regs[off + 1] & 0xFFFF)

    @staticmethod
    def _read_f32(regs, off, order="ABCD"):
        import struct
        hi, lo = regs[off] & 0xFFFF, regs[off + 1] & 0xFFFF
        be = bytes([hi >> 8, hi & 0xFF, lo >> 8, lo & 0xFF])
        if order == "ABCD":
            seq = be
        elif order == "CDAB":
            seq = be[2:4] + be[0:2]
        elif order == "BADC":
            seq = bytes([be[1], be[0], be[3], be[2]])
        else:
            seq = be[::-1]
        return struct.unpack(">f", seq)[0]


STATE = SimState()
