"""TCP 故障代理：listen_port -> target_port，支持延时 / 丢弃 / 断链注入。

用途：
  1102 -> 1103   S7 端点走代理，可注入超时与断链，验证指数退避重连与质量戳
  1504 -> 1502   Modbus 端点走代理，同样可注入
"""
from __future__ import annotations

import socket
import threading
import time
from typing import Optional

from sim_state import FAULT_CLOSE, FAULT_DELAY, FAULT_DROP, STATE


class FaultProxy(threading.Thread):
    daemon = True

    def __init__(self, listen_port: int, target_port: int, host: str = "127.0.0.1") -> None:
        super().__init__(name=f"proxy-{listen_port}", daemon=True)
        self.listen_port = listen_port
        self.target_port = target_port
        self.host = host
        self._stop = threading.Event()
        self._sock: Optional[socket.socket] = None
        self.connections = 0
        self.dropped = 0

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
        srv.bind((self.host, self.listen_port))
        srv.listen(16)
        self._sock = srv
        print(f"[proxy] {self.listen_port} -> {self.target_port}", flush=True)
        while not self._stop.is_set():
            try:
                client, _ = srv.accept()
            except OSError:
                break
            self.connections += 1
            threading.Thread(target=self._pipe_pair, args=(client,), daemon=True).start()

    def _pipe_pair(self, client: socket.socket) -> None:
        try:
            upstream = socket.create_connection((self.host, self.target_port), timeout=3)
        except OSError:
            client.close()
            return
        client.settimeout(None)
        upstream.settimeout(None)
        t = threading.Thread(target=self._relay, args=(upstream, client), daemon=True)
        t.start()
        self._relay(client, upstream)

    def _relay(self, src: socket.socket, dst: socket.socket) -> None:
        try:
            while not self._stop.is_set():
                chunk = src.recv(4096)
                if not chunk:
                    break
                fault = STATE.take_fault(self.listen_port, 0)
                if fault.mode == FAULT_CLOSE:
                    break
                if fault.mode == FAULT_DROP:
                    self.dropped += 1
                    continue
                if fault.mode == FAULT_DELAY:
                    time.sleep(fault.delay_ms / 1000.0)
                dst.sendall(chunk)
        except (OSError, ConnectionResetError):
            pass
        finally:
            for s in (src, dst):
                try:
                    s.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                try:
                    s.close()
                except OSError:
                    pass
