#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HAR 抓包一键导入工具 —— 智慧光明 / GuangmingDoorApi

直接吃 Reqable / Charles / Fiddler / Chrome 导出的 .har（或 .har.txt）文件，
自动完成：解析 -> 归类 -> 安全验活 -> 写入 appsettings.json -> 保存完整链路快照。

【重要】2026-11-07 晚间修正：现已支持 Mode=Login 自主登录，无需任何抓包。
  users/gm_login 返回的 token 是【有效的】，之前判定无效的根因是 device_guid 填错：
  必须是账号绑定的手机设备 ID（15621 端口 /oem/v5/user/login 的 Doordu-System 头里的
  device_guid），而不是门的 door_guid。
  因此本工具现在只用于【排错】或【切回 Replay 兜底模式】；日常开门请用 Login 模式。

实测协议结论（2026-11-07 完整 HAR + 登录实验）：
  1. 服务器按 Content-Type 决定是否解密请求体：
       text/plain          -> 走 AES 解密（App 原始密文）
       application/json    -> 直接接受明文 JSON（无需密钥）
     X-Enc-Level 只控制【响应】是否加密：0=明文 JSON，2=AES 密文。
  2. 全链路 = 15621 端口的 /oem/v5/user/login、/oem/v1/version/check
     + 15615 端口的 13 个 /api/index.php/v18/... 接口。
  3. users/gm_login 每次调用都会轮换服务端 token，使之前所有抓包密文
     立刻失效（426 / 10426 登录过期），并把手机 App 的会话顶下线。
     => 验活过程中不要调用它；Login 模式由网关统一管理登录与 token 缓存。
  4. 有效会话 token 可由程序自行登录获取；device_guid 必须与账号绑定一致。
  5. 因此验活必须用【无副作用】接口（user/auth_status / keypackage/list），
     不能用 doors/open（会真的把门打开）。

用法：
  python3 tools/import_har.py capture.har.txt               # 解析+验活+写配置
  python3 tools/import_har.py capture.har.txt --dry-run     # 只解析验活，不写
  python3 tools/import_har.py capture.har.txt --list        # 只列接口，不联网
  python3 tools/import_har.py --bundle capture-bundle.json  # 从已有快照恢复配置
  python3 tools/import_har.py --check-session               # 用当前配置验活（无副作用）
"""

import argparse
import base64
import json
import os
import re
import sys
from datetime import datetime

import requests
import urllib3

urllib3.disable_warnings()

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, ".."))
CFG_PATH = os.path.join(ROOT, "appsettings.json")
BUNDLE_PATH = os.path.join(ROOT, "capture-bundle.json")

BASE15 = "https://zhgm.yrcid.com:15615/api/index.php/v18/"
BASIC = "Basic bGFqZm55cXhyazZ1aHRwM3NkNG0wejd3MTk1djJiZW86OHljYXF1aGQxeDRmMnozZ2U2dzkwcHJsNW1qb3R2bmk="

COMMON_HEADERS = {
    "User-Agent": "okhttp/3.12.1",
    "Authorization": BASIC,
    "doordu-system": '{"app_version":"2.1.5.001","system_version":"14","system_models":"22101316UCP","system_type":2}',
    "Package-Name": "com.guangmingzmt.oem",
    "Accept-Language": "zh_CN",
    "X-Enc-Level": "0",                 # 强制上游返回明文 JSON
    "Content-Type": "text/plain; charset=utf-8",
}

# 需要重点提取的接口（按用途）
KEY_PATHS = {
    "doors/open": "开门",
    "keypackage/list": "门点列表",
    "user/auth_status": "验活（无副作用）",
    "user/rooms": "房间信息",
    "switch/config": "开关配置",
    "ad/system/config": "广告配置",
    "users/gm_login": "登录（禁止重放：会顶掉会话）",
}

# 绝不能用来验活 / 重放的接口
FORBIDDEN = {"users/gm_login", "user/login", "user/quit", "doors/open"}


def log(msg):
    print(msg, flush=True)


def load_har(path):
    with open(path, "r", encoding="utf-8-sig", errors="ignore") as f:
        raw = f.read()
    # 有些导出会在 JSON 前后带日志行，截取第一个 { 到最后一个 }
    try:
        return json.loads(raw)
    except Exception:
        i, j = raw.find("{"), raw.rfind("}")
        if i < 0 or j <= i:
            raise ValueError("文件不是合法 HAR/JSON")
        return json.loads(raw[i:j + 1])


def parse_entries(har):
    """提取所有智慧光明服务器请求，按时间正序返回结构化列表。"""
    out = []
    entries = har.get("log", {}).get("entries", [])
    for e in entries:
        req = e.get("request", {}) or {}
        url = req.get("url", "") or ""
        if "zhgm.yrcid.com" not in url:
            continue
        method = (req.get("method") or "").upper()
        if method == "CONNECT":
            continue
        pd = req.get("postData", {}) or {}
        payload = pd.get("text", "") or ""
        if not payload:
            continue
        hd = {}
        for h in req.get("headers", []) or []:
            hd[h.get("name", "")] = h.get("value", "")
        # 路径名
        if "/v18/" in url:
            name = url.split("/v18/", 1)[1].split("?", 1)[0]
        elif "/oem/" in url:
            name = "/oem/" + url.split("/oem/", 1)[1].split("?", 1)[0]
        else:
            name = url
        resp = e.get("response", {}) or {}
        out.append({
            "name": name,
            "url": url,
            "method": method,
            "payload": payload,
            "headers": hd,
            "status": resp.get("status"),
            "started": e.get("startedDateTime", ""),
            "time_ms": e.get("time"),
        })
    out.sort(key=lambda x: x["started"])
    return out


def human_time(s):
    try:
        dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
        return dt.astimezone().strftime("%Y-%m-%d %H:%M:%S %z")
    except Exception:
        return s


def replay(item, extra_headers=None, timeout=20):
    """重放单条密文，强制 X-Enc-Level:0 拿明文响应。"""
    h = dict(COMMON_HEADERS)
    # 保留抓包里的 doordu-system / Package-Name 等（若有）
    for k in ("doordu-system", "Package-Name", "Accept-Language", "Authorization"):
        if item["headers"].get(k):
            h[k] = item["headers"][k]
    if extra_headers:
        h.update(extra_headers)
    try:
        r = requests.post(item["url"], data=item["payload"].encode("utf-8"),
                          headers=h, verify=False, timeout=timeout)
        return r.status_code, r.text
    except Exception as ex:
        return 0, "请求异常: %s %s" % (type(ex).__name__, ex)


def parse_code(text):
    try:
        d = json.loads(text)
        return str(d.get("code")), d.get("message", "")
    except Exception:
        m = re.search(r'"code"\s*:\s*"?(\d+)"?', text or "")
        return (m.group(1) if m else None), (text or "")[:180]


def verify_session(entries):
    """用无副作用接口验活。返回 (bool, 说明, 用于验活的条目)。"""
    # 优先级：user/auth_status > keypackage/list > user/rooms
    candidates = [e for e in entries
                  if e["name"] in ("user/auth_status", "keypackage/list", "user/rooms")
                  and "/v18/" not in e["url"] or True]
    candidates = [e for e in entries if e["name"] in
                  ("user/auth_status", "keypackage/list", "user/rooms")]
    if not candidates:
        return False, "HAR 里没有可用于验活的无副作用接口（user/auth_status / keypackage/list / user/rooms）。", None
    # 逐个试，任一返回 200 即认为会话有效
    last = None
    for e in candidates:
        if e["name"] in FORBIDDEN:
            continue
        st, txt = replay(e)
        code, msg = parse_code(txt)
        log("    验活 %-20s -> HTTP %s, code=%s, message=%s"
            % (e["name"], st, code, str(msg)[:60]))
        last = (e, st, code, msg)
        if st == 200 and code == "200":
            return True, "会话有效（%s 返回 200）" % e["name"], e
        if code == "10426" or "登录过期" in str(msg) or st == 426:
            return False, "会话已过期（10426 登录过期）", e
    e, st, code, msg = last or (None, 0, None, "")
    return False, "无法确认会话状态：HTTP %s / %s" % (st, msg), e


def build_bundle(entries, source):
    """生成完整链路快照（不含鉴权之外的敏感信息，密文原样保留）。"""
    by_name = {}
    for e in entries:
        by_name.setdefault(e["name"], e)

    def payload_of(name):
        e = by_name.get(name)
        return e["payload"] if e else ""

    return {
        "source": source,
        "imported_at": datetime.now().astimezone().isoformat(),
        "server": {
            "v18": "https://zhgm.yrcid.com:15615/api/index.php/v18/",
            "oem": "https://zhgm.yrcid.com:15621/oem/",
        },
        "protocol": {
            "content_type_cipher": "text/plain; charset=utf-8",
            "content_type_plain": "application/json",
            "response_plain_header": {"X-Enc-Level": "0"},
            "auth": "Basic (client_id:client_secret)",
            "note": "X-Enc-Level 仅控制响应加密；gm_login 会轮换 token 并作废所有旧密文",
        },
        "chain": [
            {"order": i + 1, "name": e["name"], "url": e["url"],
             "method": e["method"], "status": e["status"],
             "started": e["started"], "time_ms": e["time_ms"],
             "payload_len": len(e["payload"]),
             "payload": e["payload"]}
            for i, e in enumerate(entries)
        ],
        "key_payloads": {
            "open_door": payload_of("doors/open"),
            "key_package": payload_of("keypackage/list"),
            "session_check": payload_of("user/auth_status"),
            "rooms": payload_of("user/rooms"),
        },
        "forbidden_replay": sorted(FORBIDDEN),
    }


def login_plain(d):
    """Login 模式：用配置里的账号密码自主登录，返回 token。"""
    body = {"device_guid": d.get("DeviceGuid", ""),
            "mobile": d.get("Mobile", ""),
            "password": d.get("Password", "")}
    item = {"name": "users/gm_login", "url": BASE15 + "users/gm_login",
            "payload": json.dumps(body, separators=(',', ':')), "headers": {}}
    st, txt = replay(item, extra_headers={"Content-Type": "application/json"})
    try:
        tok = json.loads(txt)["data"]["token"]
    except Exception:
        tok = None
    return tok, st, txt


def check_current_session():
    """用当前 appsettings.json 的配置做一次无副作用检查。
    Login 模式：自主登录取 token 后验活；Replay 模式：重放验活密文。
    """
    if not os.path.exists(CFG_PATH):
        log("[!] 找不到 %s" % CFG_PATH)
        return 2
    with open(CFG_PATH, encoding="utf-8") as f:
        d = json.load(f).get("DoorApi", {})

    mode = (d.get("Mode") or "Replay").strip().lower()
    path = d.get("SessionCheckPath") or "user/auth_status"
    log("[*] 验活接口：%s（无副作用，不会开门）" % path)

    if mode == "login":
        log("    当前为 Login 自主登录模式（无需抓包）")
        tok, st, txt = login_plain(d)
        if not tok:
            code, msg = parse_code(txt)
            log("    -> 登录失败 HTTP %s, code=%s, message=%s" % (st, code, str(msg)[:80]))
            log("[X] 请检查 appsettings.json 的 Mobile / Password / DeviceGuid。")
            return 1
        log("    登录成功，token=%s…" % tok[:8])
        body = {"device_guid": d.get("DeviceGuid", ""),
                "user_id": d.get("UserId", 0), "token": tok}
        item = {"name": path, "url": BASE15 + path.lstrip("/"),
                "payload": json.dumps(body, separators=(',', ':')), "headers": {}}
        st, txt = replay(item, extra_headers={"Content-Type": "application/json"})
        source = "Login 自主登录"
        imported = ""
    else:
        payload = d.get("SessionCheckPayload") or d.get("KeyPackagePayload")
        if not payload:
            log("[X] 未配置 SessionCheckPayload / KeyPackagePayload。")
            log("    建议在 appsettings.json 设 Mode=Login，或用本工具导入抓包。")
            return 1
        item = {"name": path, "url": BASE15 + path.lstrip("/"),
                "payload": payload, "headers": {}}
        st, txt = replay(item)
        source = d.get("CaptureSource", "未知")
        imported = d.get("CaptureImportedAt", "未知")
        log("    抓包来源：%s  导入时间：%s" % (source, imported))

    code, msg = parse_code(txt)
    log("    -> HTTP %s, code=%s, message=%s" % (st, code, str(msg)[:80]))
    if st == 200 and code == "200":
        log("[OK] 会话有效，可以开门。网关会在 token 失效时自动重新登录。")
        return 0
    if code == "10426" or "登录过期" in str(msg) or st == 426:
        log("[X] 会话已过期（10426 登录过期）。")
        if mode == "login":
            log("    请检查 DeviceGuid 是否为账号绑定的手机设备 ID（不是门 guid）。")
        else:
            log("    建议改为 Mode=Login 自主登录（无需抓包），或重新导入一份新 HAR。")
        return 1
    log("[X] 无法确认：HTTP %s / %s" % (st, msg))
    return 1


def write_config(bundle, dry_run=False):
    if not os.path.exists(CFG_PATH):
        log("[!] 找不到 %s" % CFG_PATH)
        return 2
    with open(CFG_PATH, encoding="utf-8") as f:
        cfg = json.load(f)
    d = cfg.setdefault("DoorApi", {})
    kp = bundle["key_payloads"]

    d["Mode"] = "Replay"
    if kp.get("open_door"):
        d["OpenDoorPayload"] = kp["open_door"]
    if kp.get("key_package"):
        d["KeyPackagePayload"] = kp["key_package"]
    if kp.get("session_check"):
        d["SessionCheckPayload"] = kp["session_check"]
    d["SessionCheckPath"] = "user/auth_status"
    d["OemBaseUrl"] = "https://zhgm.yrcid.com:15621/oem/"
    d["CaptureImportedAt"] = bundle["imported_at"]
    d["CaptureSource"] = bundle["source"]

    if dry_run:
        log("[dry-run] 未写入 appsettings.json；将写入的字段：")
        for k in ("Mode", "OpenDoorPayload", "KeyPackagePayload",
                  "SessionCheckPayload", "SessionCheckPath", "OemBaseUrl"):
            v = str(d.get(k, ""))
            log("    %-20s = %s%s" % (k, v[:60], "..." if len(v) > 60 else ""))
        return 0

    with open(CFG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    log("[+] 已写入 %s（Mode 已设为 Replay）" % CFG_PATH)
    log("    提示：日常开门推荐改回 Mode=Login 自主登录，无需抓包、不会过期。")
    return 0


def main():
    ap = argparse.ArgumentParser(description="智慧光明 HAR 抓包一键导入")
    ap.add_argument("har", nargs="?", help=".har / .har.txt 抓包文件")
    ap.add_argument("--bundle", help="从已有 capture-bundle.json 恢复配置")
    ap.add_argument("--dry-run", action="store_true", help="只解析验活，不写配置")
    ap.add_argument("--list", action="store_true", help="只列出接口，不联网")
    ap.add_argument("--force", action="store_true", help="会话过期也强行写入（不推荐）")
    ap.add_argument("--check-session", action="store_true",
                    help="用当前 appsettings.json 的验活密文检查会话（无副作用）")
    args = ap.parse_args()

    # ---------- 用当前配置验活 ----------
    if args.check_session:
        return check_current_session()

    if not args.har and not args.bundle:
        print(__doc__)
        return 2

    # ---------- 从已有快照恢复 ----------
    if args.bundle:
        with open(args.bundle, encoding="utf-8") as f:
            bundle = json.load(f)
        log("[*] 从快照恢复：%s（%d 条链路）" % (args.bundle, len(bundle.get("chain", []))))
        return write_config(bundle, args.dry_run)

    # ---------- 解析 HAR ----------
    if not os.path.exists(args.har):
        log("[!] 文件不存在：%s" % args.har)
        return 2
    try:
        har = load_har(args.har)
    except Exception as ex:
        log("[!] HAR 解析失败：%s" % ex)
        return 2
    entries = parse_entries(har)
    if not entries:
        log("[!] 没找到 zhgm.yrcid.com 的请求，请确认抓包文件正确")
        return 2

    log("[*] 解析到 %d 条智慧光明请求（按时间正序）：" % len(entries))
    log("    %-4s %-24s %-6s %-7s %s" % ("#", "接口", "状态", "字节", "时间"))
    for i, e in enumerate(entries, 1):
        tag = "  << %s" % KEY_PATHS[e["name"]] if e["name"] in KEY_PATHS else ""
        log("    %-4d %-24s %-6s %-7d %s%s"
            % (i, e["name"][:24], e["status"], len(e["payload"]),
               human_time(e["started"]), tag))

    if args.list:
        return 0

    bundle = build_bundle(entries, os.path.basename(args.har))

    # 保存快照（无论验活结果，便于排错与复用）
    with open(BUNDLE_PATH, "w", encoding="utf-8") as f:
        json.dump(bundle, f, ensure_ascii=False, indent=2)
    log("[+] 完整链路快照 -> %s" % BUNDLE_PATH)

    # 关键接口检查
    kp = bundle["key_payloads"]
    if not kp.get("open_door"):
        log("[!] 警告：没有抓到 doors/open。请确认抓包时真的点了一次「开门」。")
    if not kp.get("session_check"):
        log("[!] 警告：没有 user/auth_status，将退化为 keypackage/list / user/rooms 验活。")

    # ---------- 安全验活 ----------
    log("[*] 正在验活（只用无副作用接口，绝不调用 doors/open 与 gm_login）...")
    ok, why, _ = verify_session(entries)
    log("    => %s" % why)
    if not ok and not args.force:
        log("")
        log("[X] 未写入配置。会话失效的常见原因：")
        log("    1. 抓包后手机 App 重新登录 / 重启过（服务端 token 被轮换）；")
        log("    2. 期间有他人或脚本调用过 users/gm_login（会顶掉当前会话）；")
        log("    3. 抓包距今太久，服务端会话超时。")
        log("")
        log("    ★ 更推荐：直接改用 Mode=Login 自主登录，完全不需要抓包，也不会过期。")
        log("      在 appsettings.json 填好 Mobile / Password / DeviceGuid 即可。")
        log("    若确需 Replay：手机保持 App 登录 -> 点一次开门 -> 重新导出 HAR -> 再跑本工具。")
        return 1

    return write_config(bundle, args.dry_run)


if __name__ == "__main__":
    sys.exit(main())
