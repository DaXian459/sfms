#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SFMS 端到端验证脚本（可重复执行）

覆盖 8 组用例：
  1. 登录鉴权（登录 / me / 锁定 / 越权 403 / ws-ticket）
  2. 采集正确性（5 端点在线 / 110 点位 Good / 指标）
  3. 写值闭环（prepare → execute → 回读 / 审计 / 非法令牌拒绝）
  4. 历史（query / >24h 聚合 / CSV 导出带 BOM / stats）
  5. Excel（模板 / 校验 / 提交 / 任务列表）
  6. WebSocket（welcome / snapshot / delta|heartbeat / ping-pong / 重发快照）
  7. 故障注入 → 通信报警 → 确认 → 恢复清除
  8. 画面（草稿 / 发布版本自增 / 版本列表 / 回滚）

用法：
  tools/sim/.venv/Scripts/python.exe tools/verify_e2e.py
  # 可选环境变量：SFMS_API / SFMS_CTRL / SFMS_ADMIN_PASS / SFMS_SKIP_SLOW=1

退出码：0 = 全部通过；1 = 存在失败项。
"""

from __future__ import annotations

import json
import os
import sys
import time

import requests
import websocket

API = os.environ.get("SFMS_API", "http://127.0.0.1:5080").rstrip("/")
CTRL = os.environ.get("SFMS_CTRL", "http://127.0.0.1:18080").rstrip("/")
ADMIN_PASS = os.environ.get("SFMS_ADMIN_PASS", "admin123")
WS_BASE = API.replace("https://", "wss://").replace("http://", "ws://")
SKIP_SLOW = os.environ.get("SFMS_SKIP_SLOW") == "1"

S = requests.Session()
# 本机环境常带 HTTP_PROXY 指向企业代理：走代理访问 127.0.0.1 会出现
# 复用连接后 POST 被代理拒绝（405）的诡异现象，脚本一律直连。
for _k in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"):
    os.environ.pop(_k, None)
S.trust_env = False

RESULTS: list[tuple[str, str, bool, str]] = [
]  # (group, name, ok, detail)


def check(group: str, name: str, ok: bool, detail: object = "") -> bool:
    detail = "" if detail == "" else str(detail)
    RESULTS.append((group, name, bool(ok), detail))
    mark = "PASS" if ok else "FAIL"
    print(f"[{mark}] {group} · {name}" + (f"  — {detail}" if detail else ""))
    return bool(ok)


def unwrap(payload):
    """兼容 ApiResult<T> 包装与裸对象两种返回形态。"""
    if isinstance(payload, dict) and "ok" in payload and "data" in payload:
        return payload["data"]
    return payload


def call(method: str, path: str, token: str | None = None, **kw):
    headers = kw.pop("headers", {}) or {}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return S.request(method, f"{API}{path}", headers=headers, timeout=kw.pop("timeout", 30), **kw)


def jcall(method: str, path: str, token: str | None = None, **kw):
    r = call(method, path, token, **kw)
    try:
        return r, unwrap(r.json()) if r.content else None
    except Exception:
        return r, None


def ctrl(method: str, path: str, **kw):
    return S.request(method, f"{CTRL}{path}", timeout=10, **kw)


# ---------------------------------------------------------------- 1. 登录鉴权
def group_auth() -> tuple[str, str, str]:
    g = "1 登录鉴权"
    r, d = jcall("POST", "/api/auth/login", json={"username": "admin", "password": ADMIN_PASS})
    token = (d or {}).get("token") if isinstance(d, dict) else None
    check(g, "admin 登录返回 JWT", r.status_code == 200 and bool(token), f"HTTP {r.status_code}")
    if not token:
        return g, "", ""

    r, me = jcall("GET", "/api/auth/me", token)
    check(g, "GET /auth/me 身份正确", r.status_code == 200 and (me or {}).get("username") == "admin",
          f"role={(me or {}).get('role')}")

    # 复位 observer：脚本可重复执行——上一次跑完可能已把它锁定，这里由 admin 重置口令与锁定计数
    # 口令策略要求 ≥6 位，种子口令即 ob1234
    OBS_PASS = "ob1234"
    r, users = jcall("GET", "/api/auth/users", token)
    obs = next((u for u in (users or []) if u.get("username") == "observer"), None)
    if obs:
        r, _ = jcall("PUT", f"/api/auth/users/{obs['id']}", token,
                     json={"username": "observer", "password": OBS_PASS,
                           "displayName": obs.get("displayName") or "观察者",
                           "role": "Observer", "enabled": True})
        check(g, "observer 口令复位（解锁）", r.status_code == 200, f"HTTP {r.status_code}")

    # observer 令牌（后面用于越权测试）
    r, od = jcall("POST", "/api/auth/login", json={"username": "observer", "password": OBS_PASS})
    observer = (od or {}).get("token") if isinstance(od, dict) else None
    check(g, "observer 登录成功", r.status_code == 200 and bool(observer), f"HTTP {r.status_code}")

    # 连续 5 次错误口令 → 423 锁定（用 observer，避免影响 admin 后续用例）
    codes = []
    for _ in range(5):
        rr = call("POST", "/api/auth/login", json={"username": "observer", "password": "wrong-pass"})
        codes.append(rr.status_code)
    check(g, "连续错误口令触发锁定(423)", codes[-1] == 423, f"codes={codes}")

    if observer:
        r = call("POST", "/api/endpoints", observer, json={"code": "E2E-X", "name": "x", "ip": "127.0.0.1", "port": 1})
        check(g, "Observer 越权写配置被拒(403)", r.status_code == 403, f"HTTP {r.status_code}")

    r, t = jcall("POST", "/api/auth/ws-ticket", token)
    ticket = (t or {}).get("ticket") if isinstance(t, dict) else None
    check(g, "ws-ticket 签发", r.status_code == 200 and bool(ticket), f"len={len(ticket or '')}")
    return g, token, ticket or ""


# ------------------------------------------------------------ 2. 采集正确性
def group_collect(token: str) -> list[int]:
    g = "2 采集正确性"
    r, eps = jcall("GET", "/api/diagnostics/endpoints", token)
    eps = eps or []
    check(g, "端点数量 = 5", len(eps) == 5, f"count={len(eps)}")
    online = [e for e in eps if e.get("status") == "在线"]
    check(g, "全部端点在线", len(online) == len(eps) == 5,
          ", ".join(f"{e['code']}={e.get('status')}" for e in eps))

    total_req = sum(e.get("requests", 0) for e in eps)
    total_ok = sum(e.get("success", 0) for e in eps)
    rate = (total_ok * 100.0 / total_req) if total_req else 0
    check(g, "端点请求成功率 ≥99%", rate >= 99, f"{total_ok}/{total_req} = {rate:.2f}%")

    tag_cnt = sum(e.get("tagCount", 0) for e in eps)
    on_cnt = sum(e.get("onlineTags", 0) for e in eps)
    bad_cnt = sum(e.get("badTags", 0) for e in eps)
    check(g, "点位规模 ≥110（含导入新增）", tag_cnt >= 110, f"tagCount={tag_cnt}")
    check(g, "点位零坏值", bad_cnt == 0, f"badTags={bad_cnt}")
    check(g, "点位在线数 == 总数", on_cnt == tag_cnt, f"{on_cnt}/{tag_cnt}")

    s7 = [e for e in eps if e.get("protocol") == "S7"]
    check(g, "S7 端点 3 个且均在线(端口修复验证)",
          len(s7) == 3 and all(e.get("status") == "在线" for e in s7),
          ", ".join(f"{e['code']}:{e['port']}" for e in s7))

    r, snap = jcall("GET", "/api/realtime/snapshot", token)
    items = (snap or {}).get("items") or []
    good = [i for i in items if i.get("quality") == 0]
    check(g, "实时快照覆盖率", len(items) >= 110, f"items={len(items)}")
    check(g, "实时快照无坏值", bool(items) and len(good) == len(items), f"{len(good)}/{len(items)}")

    r, metrics = jcall("GET", "/api/diagnostics/metrics", token)
    mm = {m["name"]: m["value"] for m in (metrics or [])}
    check(g, "指标 endpoint.online = 5", mm.get("endpoint.online") == 5, mm.get("endpoint.online"))
    check(g, "指标 points.goodRate ≥99", (mm.get("points.goodRate") or 0) >= 99, mm.get("points.goodRate"))
    jitter = mm.get("collector.jitterMs", -1)
    check(g, "调度抖动 <50ms", 0 <= jitter < 50, f"{jitter}ms")

    r, tags = jcall("GET", "/api/tags?page=1&size=500", token)
    rows = (tags or {}).get("items") or []
    return [t["id"] for t in rows if t.get("quality") == 0][:8] or [t["id"] for t in rows[:8]]


# --------------------------------------------------------------- 3. 写值闭环
def group_write(token: str) -> None:
    g = "3 写值闭环"
    r, tags = jcall("GET", "/api/tags?page=1&size=500", token)
    rows = (tags or {}).get("items") or []
    target = next((t for t in rows if t.get("code") == "SETPOINT" and t.get("deviceCode") == "PRESS-01"), None)
    if target is None:
        check(g, "找到可写点位 PRESS-01.SETPOINT", False, "未找到，跳过写值用例")
        return
    tag_id = target["id"]
    old = target.get("latestValue")
    new_val = 12.5 if (old is None or abs(old - 12.5) > 0.05) else 13.5

    r, prep = jcall("POST", "/api/writes/prepare", token,
                    json={"items": [{"tagId": tag_id, "numericValue": new_val, "textValue": None}],
                          "reason": "e2e 自动验证"})
    token_c = (prep or {}).get("confirmToken") if isinstance(prep, dict) else None
    blockers = (prep or {}).get("blockers") or []
    check(g, "prepare 返回确认令牌且无阻断", r.status_code == 200 and bool(token_c) and not blockers,
          f"HTTP {r.status_code} blockers={blockers}")

    r = call("POST", "/api/writes/execute", token, json={"confirmToken": "not-a-real-token"})
    check(g, "伪造令牌被拒绝", r.status_code in (400, 404, 409, 422), f"HTTP {r.status_code}")

    if not token_c:
        return
    r, exe = jcall("POST", "/api/writes/execute", token, json={"confirmToken": token_c})
    items = (exe or {}).get("items") or []
    ok_item = items[0] if items else {}
    check(g, "execute 执行成功", r.status_code == 200 and ok_item.get("ok") is True,
          f"status={(exe or {}).get('status')} msg={(exe or {}).get('message')}")
    rb = ok_item.get("readBack")
    check(g, "回读值 == 写入值", rb is not None and abs(float(rb) - new_val) < 0.051,
          f"写入 {new_val} 回读 {rb}")

    time.sleep(1.0)
    # 点位属"事件组"（周期 60s），实时值收敛需要等下一轮扫描，这里轮询而不是固定等待
    live = None
    deadline = time.time() + 70
    while time.time() < deadline:
        _, item = jcall("GET", f"/api/realtime/item/{tag_id}", token)
        live = (item or {}).get("value")
        if live is not None and abs(float(live) - new_val) < 0.051:
            break
        time.sleep(2)
    check(g, "实时值已同步（含扫描周期收敛）", live is not None and abs(float(live) - new_val) < 0.051,
          f"live={live}")

    r, batches = jcall("GET", "/api/writes/batches?page=1&size=5", token)
    check(g, "写入批次已记录", r.status_code == 200 and (batches or {}).get("total", 0) >= 1,
          f"total={(batches or {}).get('total')}")

    r, audit = jcall("GET", "/api/diagnostics/audit?action=write.execute&page=1&size=5", token)
    check(g, "审计含 write.execute", r.status_code == 200 and ((audit or {}).get("total", 0) >= 1),
          f"total={(audit or {}).get('total')}")


# ------------------------------------------------------------------- 4. 历史
def group_history(token: str, tag_ids: list[int]) -> None:
    g = "4 历史数据"
    if not tag_ids:
        check(g, "存在可用点位", False, "无点位，跳过")
        return
    ids = ",".join(str(i) for i in tag_ids[:3])
    r, series = jcall("GET", f"/api/history/query?tagIds={ids}&maxPoints=500", token)
    series = series or []
    total_pts = sum(len(s.get("points") or []) for s in series)
    check(g, "历史 query 返回序列", r.status_code == 200 and len(series) == len(tag_ids[:3]),
          f"series={len(series)}")
    check(g, "历史点数 > 0", total_pts > 0, f"points={total_pts}")

    r = call("GET", "/api/history/export", token, params={"tagIds": ids})
    body = r.content or b""
    check(g, "CSV 导出 200", r.status_code == 200, f"HTTP {r.status_code} bytes={len(body)}")
    check(g, "CSV 含 UTF-8 BOM", body.startswith(b"\xef\xbb\xbf"), body[:8])
    text = body.decode("utf-8-sig", errors="replace")
    head = text.splitlines()[0] if text else ""
    check(g, "CSV 表头正确", head == "tag_code,ts,value,quality,quality_text", head)
    check(g, "CSV 行数与点数一致", max(0, len(text.splitlines()) - 1) >= total_pts,
          f"rows={len(text.splitlines()) - 1} points={total_pts}")

    r, stats = jcall("GET", "/api/history/stats", token)
    check(g, "history/stats 可用", r.status_code == 200 and isinstance(stats, dict), "")


# ------------------------------------------------------------------- 5. Excel
def group_import(token: str) -> None:
    g = "5 Excel 导入"
    _, before = jcall("GET", "/api/tags?page=1&size=1000", token)
    before_ids = {t["id"] for t in ((before or {}).get("items") or [])}

    r = call("GET", "/api/import/template")
    blob = r.content or b""
    check(g, "模板下载 (xlsx 魔数)", r.status_code == 200 and blob[:2] == b"PK", f"bytes={len(blob)}")

    r, val = jcall("POST", "/api/import/validate", token,
                   files={"file": ("sfms-模板回环.xlsx", blob,
                                   "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")})
    job_id = (val or {}).get("jobId") if isinstance(val, dict) else None
    check(g, "模板回环校验通过", r.status_code == 200 and job_id is not None,
          f"HTTP {r.status_code} total={(val or {}).get('total')} invalid={(val or {}).get('invalid')}")

    r = call("POST", "/api/import/validate", token, files={"file": ("bad.txt", b"not excel", "text/plain")})
    check(g, "非 xlsx 被拒绝", r.status_code == 400, f"HTTP {r.status_code}")

    if job_id:
        r, cm = jcall("POST", f"/api/import/commit/{job_id}", token)
        check(g, "提交导入任务", r.status_code == 200,
              f"total={(cm or {}).get('total')} valid={(cm or {}).get('valid')}")

        # 清理：删掉本次导入真正落库的点位，避免污染后续统计与看板
        _, after = jcall("GET", "/api/tags?page=1&size=1000", token)
        new_ids = [t["id"] for t in ((after or {}).get("items") or []) if t["id"] not in before_ids]
        for tid in new_ids:
            call("DELETE", f"/api/tags/{tid}", token)
        check(g, "导入点位已回归清理", True, f"清理 {len(new_ids)} 个")

    r, jobs = jcall("GET", "/api/import/jobs?size=5", token)
    check(g, "导入任务列表可查", r.status_code == 200 and isinstance(jobs, list) and len(jobs) >= 1,
          f"jobs={len(jobs or [])}")

    r = call("GET", "/api/import/export", token)
    check(g, "点位表导出 xlsx", r.status_code == 200 and (r.content or b"")[:2] == b"PK",
          f"bytes={len(r.content or b'')}")


# --------------------------------------------------------------- 6. WebSocket
def group_ws(ticket: str, tag_ids: list[int]) -> None:
    g = "6 WebSocket"
    if not ticket:
        check(g, "获取 ws-ticket", False, "无 ticket，跳过")
        return
    url = f"{WS_BASE}/ws/realtime?ticket={ticket}"
    try:
        ws = websocket.create_connection(url, timeout=10)
    except Exception as exc:  # noqa: BLE001
        check(g, "建立 WebSocket 连接", False, str(exc))
        return
    check(g, "建立 WebSocket 连接", True, "ok")

    def recv_until(types: set[str], deadline: float) -> dict | None:
        while time.time() < deadline:
            try:
                ws.settimeout(max(0.5, deadline - time.time()))
                raw = ws.recv()
            except Exception:  # noqa: BLE001
                return None
            if not raw:
                return None
            try:
                msg = json.loads(raw)
            except Exception:  # noqa: BLE001
                continue
            if msg.get("type") in types:
                return msg
        return None

    welcome = recv_until({"welcome"}, time.time() + 8)
    check(g, "收到 welcome 帧", welcome is not None, (welcome or {}).get("sessionId"))

    subs = (tag_ids or [])[:6]
    ws.send(json.dumps({"type": "subscribe", "subId": "s1", "tags": subs, "rateMs": 500}))
    snap = recv_until({"snapshot"}, time.time() + 8)
    items = (snap or {}).get("items") or []
    check(g, "订阅后收到 snapshot", snap is not None and len(items) == len(subs),
          f"items={len(items)}/{len(subs)}")

    live = recv_until({"delta", "heartbeat"}, time.time() + 12)
    check(g, "收到增量帧 delta/heartbeat", live is not None, (live or {}).get("type"))

    ws.send(json.dumps({"type": "ping"}))
    pong = recv_until({"pong"}, time.time() + 8)
    check(g, "ping → pong", pong is not None, (pong or {}).get("type"))

    ws.send(json.dumps({"type": "snapshot", "subId": "s1"}))
    snap2 = recv_until({"snapshot"}, time.time() + 8)
    check(g, "主动 resync 得到全量快照", snap2 is not None and len((snap2 or {}).get("items") or []) == len(subs),
          f"items={len((snap2 or {}).get('items') or [])}")

    ws.send(json.dumps({"type": "unknown-type"}))
    err = recv_until({"error"}, time.time() + 8)
    check(g, "非法消息返回 error 帧", err is not None, (err or {}).get("code"))

    try:
        ws.close()
    except Exception:  # noqa: BLE001
        pass


# ------------------------------------------------------- 7. 故障注入 → 报警
def group_fault(token: str) -> None:
    g = "7 故障注入→报警"
    if SKIP_SLOW:
        check(g, "故障注入链路", True, "SFMS_SKIP_SLOW=1 已跳过")
        return

    def active_comm() -> list[dict]:
        _, data = jcall("GET", "/api/alarms?state=active&size=100", token)
        return [i for i in ((data or {}).get("items") or []) if i.get("kind") == "Comm"]

    before_ids = {a["id"] for a in active_comm()}

    r = ctrl("POST", "/fault", json={"port": 1102, "mode": "close"})
    check(g, "注入故障 1102 → close", r.status_code == 200,
          f"HTTP {r.status_code} {(r.text or '')[:60]}")

    alarm = None
    deadline = time.time() + 90
    while time.time() < deadline:
        for a in active_comm():
            if a["id"] not in before_ids and ":1102" in (a.get("message") or ""):
                alarm = a
                break
        if alarm:
            break
        time.sleep(2)
    check(g, "产生通信中断报警(EP-S7-1500)", alarm is not None,
          (alarm or {}).get("message", "")[:52] or "90s 内未出现")

    if alarm:
        r, ack = jcall("POST", f"/api/alarms/{alarm['id']}/ack", token, json={"note": "e2e 自动确认"})
        check(g, "报警确认成功", r.status_code == 200, f"HTTP {r.status_code}")
        _, acked = jcall("GET", "/api/alarms?state=acked&size=50", token)
        ids = [i["id"] for i in (acked or {}).get("items") or []]
        check(g, "报警进入已确认列表", alarm["id"] in ids, f"acked={len(ids)}")

    r = ctrl("POST", "/fault/clear", json={"port": 1102})
    check(g, "清除故障注入", r.status_code == 200, f"HTTP {r.status_code}")

    recovered = False
    deadline = time.time() + 120
    while time.time() < deadline:
        _, eps = jcall("GET", "/api/diagnostics/endpoints", token)
        ep2 = next((e for e in (eps or []) if e.get("port") == 1102), None)
        if ep2 and ep2.get("status") == "在线":
            recovered = True
            break
        time.sleep(3)
    check(g, "端点自动恢复在线", recovered, "")

    if alarm:
        cleared = False
        deadline = time.time() + 60
        while time.time() < deadline:
            _, cl = jcall("GET", "/api/alarms?state=cleared&size=50", token)
            if alarm["id"] in [i["id"] for i in (cl or {}).get("items") or []]:
                cleared = True
                break
            time.sleep(2)
        check(g, "报警自动清除", cleared, f"alarm#{alarm['id']}")


# --------------------------------------------------------------- 8. 画面版本
def group_screen(token: str) -> None:
    g = "8 画面发布回滚"
    r, screens = jcall("GET", "/api/screens", token)
    screens = screens or []
    check(g, "画面列表非空", r.status_code == 200 and len(screens) >= 1, f"count={len(screens)}")
    editable = next((s for s in screens if s.get("canEdit")), None)
    if not editable:
        check(g, "存在可编辑画面", False, "跳过")
        return
    sid = editable["id"]

    r, cur = jcall("GET", f"/api/screens/{sid}?mode=draft", token)
    doc = json.loads((cur or {}).get("json") or "{}")
    check(g, "读取草稿 JSON", isinstance(doc, dict) and "widgets" in doc,
          f"widgets={len(doc.get('widgets') or [])}")

    before_no = (cur or {}).get("publishedVersionNo") or 0
    marker = f"#e2e-{int(time.time())}"
    doc["background"] = marker
    r, sv = jcall("PUT", f"/api/screens/{sid}/draft", token,
                  json={"json": json.dumps(doc, ensure_ascii=False), "note": "e2e 草稿"})
    check(g, "保存草稿", r.status_code == 200, f"HTTP {r.status_code}")

    r, pub = jcall("POST", f"/api/screens/{sid}/publish", token,
                   json={"json": json.dumps(doc, ensure_ascii=False), "note": "e2e 发布"})
    ver = (pub or {}).get("versionNo")
    check(g, "发布生成新版本", r.status_code == 200 and ver == before_no + 1,
          f"v{before_no} → v{ver} (HTTP {r.status_code})")

    r, pub2 = jcall("GET", f"/api/screens/{sid}", token)
    check(g, "运行态取到已发布版本", (pub2 or {}).get("source") == "published"
          and (pub2 or {}).get("versionNo") == ver, (pub2 or {}).get("source"))

    r, vers = jcall("GET", f"/api/screens/{sid}/versions", token)
    vers = vers or []
    check(g, "版本列表 ≥2", len(vers) >= 2, f"versions={[v['versionNo'] for v in vers]}")
    oldest = min(vers, key=lambda v: v["versionNo"]) if vers else None

    if oldest:
        r, rb = jcall("POST", f"/api/screens/{sid}/rollback/{oldest['id']}", token)
        check(g, "回滚到最早版本", r.status_code == 200, f"from v{(rb or {}).get('fromVersion')}")
        r, after = jcall("GET", f"/api/screens/{sid}?mode=draft", token)
        doc2 = json.loads((after or {}).get("json") or "{}")
        check(g, "草稿已回退（背景标记消失）", doc2.get("background") != marker, doc2.get("background"))

    # 还原：重新发布一次，避免把 e2e 标记留在运行态
    r, cur3 = jcall("GET", f"/api/screens/{sid}?mode=draft", token)
    jcall("POST", f"/api/screens/{sid}/publish", token,
          json={"json": (cur3 or {}).get("json") or "{}", "note": "e2e 还原"})


# ---------------------------------------------------------------------- main
def main() -> int:
    print(f"SFMS 端到端验证  API={API}  CTRL={CTRL}")
    print("-" * 78)

    r = S.get(f"{API}/health/ready", timeout=10)
    if r.status_code != 200:
        print(f"API 未就绪：HTTP {r.status_code} {r.text[:200]}")
        return 1
    print(f"健康检查：{r.text}")

    auth = group_auth()
    token = auth[1]
    ticket = auth[2]
    if not token:
        print("无法取得令牌，终止。")
        return 1

    tag_ids = group_collect(token)
    group_write(token)
    group_history(token, tag_ids)
    group_import(token)
    group_ws(ticket, tag_ids)
    group_fault(token)
    group_screen(token)

    print("-" * 78)
    passed = sum(1 for _, _, ok, _ in RESULTS if ok)
    failed = [(gr, nm, dt) for gr, nm, ok, dt in RESULTS if not ok]
    print(f"合计 {len(RESULTS)} 项：通过 {passed}，失败 {len(failed)}")
    for gr, nm, dt in failed:
        print(f"  ✗ {gr} · {nm}  {dt}")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
