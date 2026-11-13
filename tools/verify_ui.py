#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SFMS 前端页面验收（无头 Chrome + CDP）

对构建产物（由 API 在 5080 端口直接托管）逐页打开，检查：
  1. 页面是否渲染出主内容（非空白）
  2. 是否出现 console error / 未捕获异常 / Element Plus 错误提示
  3. 截图存到 .run/ui/*.png 供人工复核

用法：
  tools/sim/.venv/Scripts/python.exe tools/verify_ui.py
  # 可选：SFMS_UI_BASE（默认 http://127.0.0.1:5080）、SFMS_CHROME（浏览器路径）

退出码：0 = 全部通过；1 = 存在失败页。
"""

from __future__ import annotations

import base64
import json
import os
import re
import shutil
import subprocess
import sys
import time

import requests
import websocket

BASE = os.environ.get("SFMS_UI_BASE", "http://127.0.0.1:5080").rstrip("/")
ADMIN_USER = ("admin", "admin123")
OUT_DIR = os.path.abspath(".run/ui")
PROFILE_DIR = os.path.abspath(".run/chrome-profile")
PORT = 9222

CHROME_CANDIDATES = [
    os.environ.get("SFMS_CHROME", ""),
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "/usr/bin/google-chrome",
    "/usr/bin/chromium",
]

# (hash 路由, 页面名, 判定用文案片段)
PAGES = [
    ("#/login", "登录页", "SFMS"),
    ("#/overview", "厂区总览", "厂区"),
    ("#/screens", "画面管理", "画面"),
    ("#/screen/1", "画面运行态", ""),
    ("#/screens/1/edit", "组态编辑器", ""),
    ("#/trends", "历史趋势", "趋势"),
    ("#/alarms", "报警中心", "报警"),
    ("#/diagnostics", "通信诊断", "诊断"),
    ("#/import", "批量导入", "导入"),
    ("#/audit", "审计日志", "审计"),
    ("#/users", "用户管理", "用户"),
    ("#/config/endpoints", "端点管理", "端点"),
    ("#/config/devices", "设备管理", "设备"),
    ("#/config/tags", "点位管理", "点位"),
    ("#/config/groups", "采集分组", "分组"),
    ("#/config/rules", "报警规则", "规则"),
]


def find_chrome() -> str | None:
    for p in CHROME_CANDIDATES:
        if p and os.path.isfile(p):
            return p
    for name in ("chrome", "google-chrome", "chromium", "msedge"):
        p = shutil.which(name)
        if p:
            return p
    return None


class Cdp:
    def __init__(self, ws_url: str):
        self.ws = websocket.create_connection(ws_url, timeout=30, suppress_origin=True)
        self._id = 0
        self.events: list[dict] = []

    def send(self, method: str, **params) -> dict:
        self._id += 1
        mid = self._id
        self.ws.send(json.dumps({"id": mid, "method": method, "params": params}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == mid:
                return msg
            self.events.append(msg)

    def pump(self, seconds: float) -> None:
        end = time.time() + seconds
        while time.time() < end:
            try:
                self.ws.settimeout(max(0.05, end - time.time()))
                self.events.append(json.loads(self.ws.recv()))
            except Exception:  # noqa: BLE001
                break

    def eval(self, expr: str):
        r = self.send("Runtime.evaluate", expression=expr, returnByValue=True, awaitPromise=True)
        return (r.get("result", {}).get("result") or {}).get("value")

    def take_events(self) -> list[dict]:
        e, self.events = self.events, []
        return e


def collect_errors(events: list[dict]) -> list[str]:
    problems: list[str] = []
    for e in events:
        m = e.get("method")
        if m == "Runtime.exceptionThrown":
            d = e["params"].get("exceptionDetails", {})
            problems.append("异常: " + (d.get("exception", {}).get("description") or d.get("text") or "?"))
        elif m == "Log.entryAdded":
            entry = e["params"].get("entry", {})
            if entry.get("level") == "error":
                txt = entry.get("text") or ""
                if "favicon" in txt:      # 无 favicon 属噪音
                    continue
                problems.append(f"console.error: {txt[:160]}")
    return problems


def main() -> int:
    chrome = find_chrome()
    if not chrome:
        print("未找到 Chrome/Edge，跳过 UI 验收")
        return 0

    os.makedirs(OUT_DIR, exist_ok=True)
    if os.path.isdir(PROFILE_DIR):
        shutil.rmtree(PROFILE_DIR, ignore_errors=True)

    proc = subprocess.Popen([  # noqa: S603
        chrome, "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
        "--hide-scrollbars", "--force-device-scale-factor=1", "--window-size=1600,960",
        f"--user-data-dir={PROFILE_DIR}", f"--remote-debugging-port={PORT}", "about:blank",
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    try:
        ws_url = None
        for _ in range(60):
            try:
                pages = requests.get(f"http://127.0.0.1:{PORT}/json/list", timeout=2,
                                     proxies={"http": None, "https": None}).json()
                cand = [p for p in pages if p.get("type") == "page"]
                if cand:
                    ws_url = cand[0]["webSocketDebuggerUrl"]
                    break
            except Exception:  # noqa: BLE001
                pass
            time.sleep(0.5)
        if not ws_url:
            print("无法连接浏览器调试端口")
            return 1

        # 先取一次令牌，注入 localStorage，跳过手工登录
        lr = requests.post(f"{BASE}/api/auth/login", json={"username": ADMIN_USER[0], "password": ADMIN_USER[1]},
                           timeout=10, proxies={"http": None, "https": None})
        data = lr.json().get("data") or {}
        token = data.get("token", "")
        user = {k: data.get(k) for k in ("id", "username", "displayName", "role", "level")}
        if not token:
            print("登录失败，无法进行页面验收")
            return 1

        cdp = Cdp(ws_url)
        cdp.send("Page.enable")
        cdp.send("Runtime.enable")
        cdp.send("Log.enable")
        cdp.send("Emulation.setDeviceMetricsOverride", width=1600, height=960, deviceScaleFactor=1, mobile=False)
        cdp.send("Page.addScriptToEvaluateOnNewDocument", source=(
            "try{localStorage.setItem('sfms.token',%s);localStorage.setItem('sfms.user',%s);}catch(e){}"
            % (json.dumps(token), json.dumps(json.dumps(user, ensure_ascii=False)))))

        results: list[tuple[str, str, bool, str]] = []
        for route, name, expect in PAGES:
            cdp.take_events()
            url = f"{BASE}/{route}"
            if route == "#/login":  # 登录页需要未登录态
                cdp.send("Runtime.evaluate", expression="localStorage.clear()")
                cdp.send("Page.navigate", url="about:blank")
                cdp.pump(0.3)
                cdp.send("Runtime.evaluate", expression="localStorage.clear()")

            cdp.send("Page.navigate", url=url)
            cdp.pump(0.6)
            # 等 SPA 挂载与首屏接口返回
            deadline = time.time() + 12
            text = ""
            while time.time() < deadline:
                cdp.pump(0.4)
                text = cdp.eval("document.body.innerText || ''") or ""
                if len(text.strip()) > 40 and "SFMS" not in text[:6]:
                    break
            cdp.pump(1.2)

            errs = collect_errors(cdp.take_events())
            text = (cdp.eval("document.body.innerText || ''") or "").strip()
            err_banner = cdp.eval("document.querySelectorAll('.el-message--error').length") or 0

            ok = len(text) > 40 and not errs and not err_banner and (expect in text if expect else True)
            detail = f"{len(text)} 字符"
            if errs:
                detail = errs[0]
            elif err_banner:
                detail = f"{err_banner} 个错误提示"
            elif expect and expect not in text:
                detail = f"未匹配文案「{expect}」"
            results.append((name, route, ok, detail))

            shot = cdp.send("Page.captureScreenshot", format="png")
            b64 = shot.get("result", {}).get("data")
            if b64:
                safe = re.sub(r"[^0-9A-Za-z]+", "_", route).strip("_") or "root"
                with open(os.path.join(OUT_DIR, f"{safe}.png"), "wb") as f:
                    f.write(base64.b64decode(b64))

            print(f"[{'PASS' if ok else 'FAIL'}] {name:8s} {route:24s} — {detail}")

        # 登录页单独验证：清掉令牌后应落在登录表单
        cdp.send("Page.navigate", url=f"{BASE}/#/login")
        cdp.pump(2.0)
        has_login = cdp.eval("!!document.querySelector('input')")
        results.append(("登录页表单", "#/login", bool(has_login), "存在输入框" if has_login else "未渲染输入框"))
        print(f"[{'PASS' if has_login else 'FAIL'}] 登录页表单 #/login — {'存在输入框' if has_login else '未渲染输入框'}")

        failed = [r for r in results if not r[2]]
        print("-" * 70)
        print(f"页面合计 {len(results)}：通过 {len(results) - len(failed)}，失败 {len(failed)}")
        for name, route, _, detail in failed:
            print(f"  ✗ {name} {route}  {detail}")
        print(f"截图目录：{OUT_DIR}")
        return 0 if not failed else 1
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except Exception:  # noqa: BLE001
            proc.kill()


if __name__ == "__main__":
    sys.exit(main())
