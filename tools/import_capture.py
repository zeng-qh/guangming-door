#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
抓包导入工具（单条 Python requests 代码 / 单段密文）→ GuangmingDoorApi 配置。

【2026-11-07 晚间更新】现在首选 Mode=Login 自主登录（程序自行调 users/gm_login
取 token，明文开门），完全不需要抓包、不会过期。本脚本与 import_har.py 只用于
排错或切回 Replay 兜底模式。Login 模式要求 appsettings.json 里的 DeviceGuid 是
账号绑定的手机设备 ID（不是门 guid），填错会一律 10426 登录过期。

【重要】完整 HAR 抓包请优先用 tools/import_har.py，它能自动提取全链路密文，
并用 user/auth_status 等【无副作用】接口安全验活。

安全说明（2026-11-07 修订）：
  旧版本用 doors/open 重放来「验活」，这会把门真的打开一次！单条密文无法派生出
  无副作用的会话体验活密文，因此本脚本【默认不再联网验活】，只做静态检查后写入。
  WebAPI 的 /api/door/open 在开门前会自动调用 /api/session/check 验活（无副作用），
  所以配置里是过期密文也不会误开门，只会返回明确的「会话已过期」提示。
  若确实想通过开门来验活，需显式加 --verify-by-open 确认。

用法：
  python3 import_capture.py capture.py
  python3 import_capture.py capture.py --dry-run
  python3 import_capture.py --payload "AnDkQy5..."
  python3 import_capture.py --payload "..." --verify-by-open   # 会真的开门一次
  python3 import_capture.py --plaintext '{"device_guid":"...","token":"...","user_id":0}'
"""

import argparse
import base64
import json
import os
import re
import sys

import requests
import urllib3

urllib3.disable_warnings()

BASE = "https://zhgm.yrcid.com:15615/api/index.php/v18/"
CFG_PATH = os.path.normpath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "appsettings.json")
)

HEADERS = {
    "Host": "zhgm.yrcid.com:15615",
    "User-Agent": "okhttp/3.12.1",
    "Connection": "Keep-Alive",
    "Accept-Encoding": "gzip",
    "Authorization": "Basic bGFqZm55cXhyazZ1aHRwM3NkNG0wejd3MTk1djJiZW86OHljYXF1aGQxeDRmMnozZ2U2dzkwcHJsNW1qb3R2bmk=",
    "doordu-system": '{"app_version":"2.1.5.001","system_version":"14","system_models":"22101316UCP","system_type":2}',
    "Package-Name": "com.guangmingzmt.oem",
    "Accept-Language": "zh_CN",
    "X-Enc-Level": "0",
    "Content-Type": "text/plain; charset=utf-8",
}


def post(path, body, content_type="text/plain; charset=utf-8"):
    h = dict(HEADERS)
    h["Content-Type"] = content_type
    try:
        r = requests.post(BASE + path, data=body, headers=h, verify=False, timeout=20)
        return r.status_code, r.text
    except Exception as e:
        return 0, "请求异常: %s %s" % (type(e).__name__, e)


def extract(code):
    """从 Python requests 抓包代码里提取 url 与 payload。"""
    url = re.search(r"url\s*=\s*[\"']([^\"']+)[\"']", code)
    payload = re.search(r"payload\s*=\s*[\"']([A-Za-z0-9+/=]+)[\"']", code)
    if not payload:
        # 也支持直接给一大段裸 base64
        m = re.search(r"[A-Za-z0-9+/]{100,}={0,2}", code)
        payload = m
    path = ""
    if url:
        m = re.search(r"/v18/(.+)$", url.group(1))
        path = m.group(1) if m else ""
    return path, (payload.group(1) if hasattr(payload, "group") else payload)


def parse_code(text):
    try:
        d = json.loads(text)
        return d.get("code"), d.get("message", "")
    except Exception:
        m = re.search(r'"code"\s*:\s*"?(\d+)"?', text)
        return (m.group(1) if m else None), text[:200]


def load_cfg():
    with open(CFG_PATH, encoding="utf-8") as f:
        return json.load(f)


def save_cfg(cfg):
    with open(CFG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    print("[+] 已写入 %s" % CFG_PATH)


def verify_cipher(path, payload):
    """【会真实开门】重放 doors/open 密文验活。仅在显式 --verify-by-open 时调用。"""
    status, text = post(path, payload)
    code, msg = parse_code(text)
    print("    重放 %s -> HTTP %s, code=%s, message=%s" % (path, status, code, msg))
    if code == "10426" or "登录过期" in (msg or ""):
        return False, "会话已过期（10426 登录过期）：这份抓包太旧，需要在 App 里重新点一次开门抓包。"
    if code == "11006" or "门禁机不存在" in (msg or ""):
        return False, "会话有效但请求体缺门标识（11006）。请确认抓的是 doors/open 而不是其它接口。"
    if status == 200 and code == "200":
        return True, "会话有效，响应正常（注意：这次验活已经把门打开了一次）。"
    return False, "未确认有效：HTTP %s / %s" % (status, msg)


def main():
    ap = argparse.ArgumentParser(description="智慧光明抓包导入 + 验活")
    ap.add_argument("capture", nargs="?", help="抓包代码文件路径；不传则从 stdin 读")
    ap.add_argument("--payload", help="直接指定 doors/open 密文")
    ap.add_argument("--plaintext", help="Frida 抓到的加密前明文 JSON")
    ap.add_argument("--plaintext-file", help="从文件读取加密前明文 JSON")
    ap.add_argument("--dry-run", action="store_true", help="只验证，不写配置")
    ap.add_argument("--verify-by-open", action="store_true",
                    help="通过重放 doors/open 验活（会真实开门一次，默认关闭）")
    args = ap.parse_args()

    if not os.path.exists(CFG_PATH):
        print("[!] 找不到 %s，请把本脚本放在 GuangmingDoorApi/tools/ 下" % CFG_PATH)
        return 2

    # ---------- 模式 B：Frida 明文 -> Plaintext ----------
    if args.plaintext or args.plaintext_file:
        raw = args.plaintext
        if args.plaintext_file:
            with open(args.plaintext_file, encoding="utf-8") as f:
                raw = f.read()
        m = re.search(r"\{[\s\S]*\}", raw or "")
        if not m:
            print("[!] 没找到 JSON 明文")
            return 2
        try:
            d = json.loads(m.group(0))
        except Exception as e:
            print("[!] 明文不是合法 JSON: %s" % e)
            return 2

        token = d.get("token")
        guid = d.get("device_guid", "")
        uid = d.get("user_id", 0)
        if not token:
            print("[!] 明文里没有 token 字段")
            return 2
        print("[*] 提取到 token=%s... device_guid=%s user_id=%s" % (str(token)[:8], guid, uid))

        body = {"device_guid": guid, "user_id": uid, "token": token}
        status, text = post("user/rooms", json.dumps(body, separators=(",", ":")),
                            "application/json")
        code, msg = parse_code(text)
        print("    用该 token 验证 user/rooms -> HTTP %s, code=%s, message=%s"
              % (status, code, msg))
        if code == "10426" or status == 426:
            print("[X] 该 token 无效（登录过期）。注意 gm_login 返回的 token 就是无效的，"
                  "必须抓 App 真实会话的明文。")
            return 1

        print("[OK] token 有效，将切换到 Plaintext 模式（之后可开任意门，不再依赖密文）。")
        if args.dry_run:
            print("[dry-run] 未写入。")
            return 0
        cfg = load_cfg()
        cfg["DoorApi"]["Mode"] = "Plaintext"
        cfg["DoorApi"]["Token"] = token
        cfg["DoorApi"]["DeviceGuid"] = guid
        cfg["DoorApi"]["UserId"] = uid
        if d.get("door_id"):
            cfg["DoorApi"].setdefault("Doors", [])
            if not any(str(x.get("Id")) == str(d["door_id"]) for x in cfg["DoorApi"]["Doors"]):
                cfg["DoorApi"]["Doors"].append({
                    "Id": str(d["door_id"]), "Name": "抓包门点",
                    "Guid": d.get("door_guid", ""), "SipNo": d.get("door_sip_no", ""),
                    "RoomId": str(d.get("room_id", ""))
                })
        save_cfg(cfg)
        print("[+] 重启 WebAPI 后即可开门。")
        return 0

    # ---------- 模式 A：抓包密文 -> Replay ----------
    if args.payload:
        code_text, path = args.payload, "doors/open"
        payload = args.payload
    else:
        code_text = ""
        if args.capture:
            with open(args.capture, encoding="utf-8", errors="ignore") as f:
                code_text = f.read()
        elif not sys.stdin.isatty():
            code_text = sys.stdin.read()
        if not code_text.strip():
            print(__doc__)
            return 2
        path, payload = extract(code_text)
        if not payload:
            print("[!] 没提取到 payload，请检查抓包代码格式")
            return 2

    try:
        n = len(base64.b64decode(payload + "=" * ((4 - len(payload) % 4) % 4)))
        print("[*] payload: %d 字符 / %d 字节 / %d 个 AES 块" % (len(payload), n, n // 16))
    except Exception:
        print("[!] payload 不是合法 base64")
        return 2

    if path and path != "doors/open":
        print("[!] 警告：抓包里的接口是 %s，不是 doors/open。仍按 doors/open 验证。" % path)

    if args.verify_by_open:
        print("[!] 你显式开启了 --verify-by-open：下面这次重放会【真的把门打开一次】。")
        print("[*] 正在通过 doors/open 验证会话（X-Enc-Level:0 取明文响应）...")
        ok, why = verify_cipher("doors/open", payload)
        print("    => %s" % why)
        if not ok:
            print("\n[X] 未写入配置。请重新抓包：")
            print("    1. 手机打开智慧光明 App，确认能正常开门；")
            print("    2. 抓包工具开启，点一次「开门」；")
            print("    3. 导出完整 HAR，改用 tools/import_har.py（可无副作用验活）。")
            return 1
    else:
        print("[*] 已跳过联网验活（避免误开门）。仅做静态格式检查。")
        print("    提示：完整 HAR 请用 tools/import_har.py，它会用 user/auth_status 安全验活。")
        print("    WebAPI 开门前会自动调用 /api/session/check，过期密文不会误开门。")

    if args.dry_run:
        print("[dry-run] 未写入。")
        return 0

    cfg = load_cfg()
    cfg["DoorApi"]["Mode"] = "Replay"
    cfg["DoorApi"]["OpenDoorPayload"] = payload
    cfg["DoorApi"]["SessionCheckPayload"] = cfg["DoorApi"].get("KeyPackagePayload", "")
    cfg["DoorApi"]["SessionCheckPath"] = "user/auth_status"
    save_cfg(cfg)
    print("[+] 完成：重启 WebAPI 后，先用 GET /api/session/check 验活，再开门。")
    print("    注意：验活调试期间不要调用 users/gm_login，也不要在手机上退出重登，")
    print("    否则服务端 token 轮换会让这份密文立刻失效。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
