# 智慧光明开门网关（C# WebAPI）

手机/浏览器点一下 → C# WebAPI → 自主登录取 token → 明文调用上游 → 开门。
**不需要抓包、不需要 Frida、不依赖手机 App 会话**；App 级协议常量内置通用。

```
手机（浏览器 / 快捷指令 / HTTP App）
   │  POST /api/door/open
   ▼
C# WebAPI（.NET 10 Minimal API）
   │  ① POST users/gm_login  {device_guid, mobile, password}  → token（缓存 30 分钟）
   │     登录响应自动带出 user_id；rooms 接口自动拉取门列表与开门全部参数
   │  ② POST doors/open      {device_guid, user_id, token, door_id, ...}
   │     统一 application/json + X-Enc-Level: 0（全程明文，无需 AES）
   │     登录失效（10426/401/426 等）→ 自动清缓存重登 → 用新 token 重发，最多 2 次
   ▼
https://zhgm.yrcid.com:15615/api/index.php/v18/doors/open  →  门禁机
```

## 功能

| 功能 | 说明 |
|---|---|
| 一键开门页 `index.html` | 一个按钮直接开门，下方显示最近 10 条记录与累计/成功/失败统计 |
| 设置页 `settings.html` | 主区只填**账号 + 密码**；「高级」里设备 ID 自动生成、可手动覆盖、可恢复自动 |
| 测试按钮 | 「保存并测试」分步验证 登录 → 设备校验 → 拉门列表（**无副作用不开门**）；「测试开门」二次确认后真实开一次门并留痕 |
| 完整记录页 `records.html` | 日期范围（默认最近 7 天，快捷 7/30 天/全部）、结果、关键词筛选；分页浏览，每页默认 20 条 |
| 登录失效自动重登 | 覆盖 HTTP 401/403/419/426、失效业务码、失效文案；成功响应绝不误判；网络异常不自动重发（防重复开门） |
| 开门记录持久化 | 落盘 `data/open-log.json`，重启不丢；明细最多 500 条，累计统计单独计数；不含任何凭据 |

## 快速开始（Windows）

```powershell
cd path\to\GuangmingDoorApi
.\run.ps1            # 监听 http://0.0.0.0:5080，首次启动后在设置页填账号密码
```

局域网内手机打开 `http://<电脑IP>:5080`；公网暴露建议前面加 HTTPS 反代 + 访问凭据。

## 快速开始（飞牛 fnOS / 树莓派，Docker）

镜像双架构（linux/amd64 + linux/arm64），**树莓派 4B 直接可用**。
已针对 4GB 内存优化：纯 chiseled 精简运行时（**约 124MB**，比完整 Debian 版省 51%）、
workstation GC + DATAS 自适应堆（实测稳定态约 26MB）、512MB 内存上限、非 root（UID 1654）运行。

**方式 A：拉 GHCR 镜像（推荐）**

```bash
docker pull ghcr.io/zeng-qh/openrooms:latest
docker run -d --name guangming-door --restart unless-stopped \
  -p 8080:8080 -e TZ=Asia/Shanghai \
  -v guangming-data:/app/data \
  ghcr.io/zeng-qh/openrooms:latest
```

**方式 B：docker compose（fnOS 的 Docker「项目」界面可直接粘贴 `docker-compose.yml`）**

已预填 GHCR 地址，`docker compose up -d` 即可；想在 NAS 本地构建就取消 `build: .` 注释。

**要点**

- `data/`（settings.json、open-log.json）通过 `/app/data` 卷持久化，升级镜像不丢
- **优先用命名卷**（自动继承目录权限）；绑定宿主机目录需先
  `mkdir -p ./data && sudo chown -R 1654:1654 ./data`，否则容器写不进、重启丢数据
- **GHCR 镜像不含任何用户凭据**：首次部署打开设置页填账号密码即可；
  设备 ID 默认自动生成，若开门报 `10426` 再到「高级」补填账号绑定的设备 ID
- 时区已内置北京时区，开门记录时间是 UTC+8
- 暴露公网务必经 fnOS 反代加 HTTPS + 访问凭据（本应用自身不做鉴权）

## API 端点

本应用自身不做鉴权（访问控制交给部署层）。

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/status` | 门点名称与就绪标志（不暴露任何账号/凭据） |
| `GET` | `/api/settings` | 读取设置（脱敏，绝不返回密码明文） |
| `POST` | `/api/settings` | 保存设置，body `{mobile, password, deviceGuid?}`；deviceGuid 留空=不改动 |
| `POST` | `/api/test` | 分步测试，body `{openDoor:false}`；`true` 时额外真实开门一次并留痕 |
| `POST` | `/api/door/open` | **开门**；响应含 `sent`（是否真的发出）、`relogged`/`reloginAttempts`（自动重登） |
| `GET` | `/api/door/history?take=10` | 最近 N 条记录 + 累计统计（take 上限 500） |
| `GET` | `/api/door/history/query` | 筛选分页：`from`/`to`（YYYY-MM-DD）、`result=all|成功|失败`、`keyword`、`page`、`pageSize`（默认 20） |

开门成功响应：`{"ok":true,"message":"开门成功",...}`；失败带 `hint` 诊断信息；
`sent=false` 表示没有真的向上游发出开门请求（便于区分「门没开」和「请求没发」）。

## 配置与凭据安全模型

配置优先级（后者覆盖前者）：

| 来源 | 用途 | 是否入库/入镜像 |
|---|---|---|
| `data/settings.json` | **运行时唯一真源**，设置页保存即写 | 否（持久化卷） |
| `appsettings.Local.json` | 本地开发种子（真实账号/设备/门点） | **否**（git 与 Docker 均忽略） |
| `appsettings.json` | 结构与占位 + App 级协议常量 | 是（无任何用户凭据） |

- **仓库和镜像里没有任何用户级凭据**（手机号/密码/设备 ID/门点），历史提交已重写清除
- App 级常量（`ClientId`/`ClientSecret`/`PackageName`/`DoorDuSystem`/`BaseUrl`）来自 APK
  反编译，对该 App 所有用户相同，非个人凭据，内置以保证「只填账号密码」可用
- 换账号：设置页重新保存即可，程序自动清空缓存并重新生成设备 ID

## device_guid 实测结论（重要）

服务器硬性要求「账号绑定的设备 ID」，且**无法由账号密码推导**（4 轮真实上游探测确认：
随机值能登录但业务接口一律 `10426`；正确 token 配随机 ID 仍 `10426`；无自助绑定接口；
登录响应不含该值；也非账号/密码/uid 的常见哈希）。

因此采用「**自动生成 + 失败再补**」：按账号生成稳定 16 位 hex 设备 ID 并持久化；
测试按钮收到 `10426` 时返回 `needDeviceGuid=true`，提示到设置页「高级」补填绑定设备 ID
（从智慧光明 App 抓包 `Doordu-System` 请求头的 `device_guid` 获取）。

## 协议结论（实测）

1. **服务器按 `Content-Type` 决定如何解析请求体**：`application/json` + 明文 JSON 直接接受
   （无需 AES 密钥）；`text/plain` + 密文走 AES（App 原始方式）。网关统一明文。
2. **`X-Enc-Level` 只控制响应**：`0`=明文 JSON，`2`=AES 密文。网关统一发 `0`。
3. **明文模式必填 `device_guid` + `token`**，字段名必须是 `token` 且放 body；
   放 header 或改名一律 `10400`。
4. **`device_guid` 与账号绑定**，填错一律 `426 / 10426`。
5. **每次 `gm_login` 都会轮换服务端 token**，会把手机 App 会话顶下线；
   网关因此缓存 token 30 分钟（`TokenCacheMinutes` 可调，`0`=每次都重登）。
6. **验活用无副作用接口** `user/auth_status`，绝不能用 `doors/open` 验活（会真的开门）。
7. 门列表与开门参数由 `rooms` 接口自动拉取（`door_id`/`door_guid`/`door_sip_no`/`room_id`/
   `unit_id`/`building_id`/`dep_id`/`dynamic_token` 全套）；`keypackage/list` 明文返回
   `10400`（密文含额外未知参数），但不影响开门链路。
8. 15621 端口的 `/oem/...` 协议与 15615 不同，网关只记录、不调用。

## GitHub Actions

推送到 `main`/`master` 或打 `v*` 标签时，`.github/workflows/docker-publish.yml` 自动构建
**双架构镜像**发布到 GHCR（`ghcr.io/zeng-qh/openrooms:latest` + 语义化版本 + sha 标签），
并带 GHA 层缓存与并发取消（新推送自动取消旧构建）。构建用交叉编译 + 运行阶段零 `RUN`，
无需 QEMU 模拟目标架构。

## 抓包与工具（仅排错 / Replay 兜底用）

Login 模式不需要以下任何工具：

```powershell
python3 tools/import_har.py capture.har.txt --list      # 只列接口，不联网
python3 tools/import_har.py capture.har.txt             # 解析+安全验活+写配置（会设 Mode=Replay）
python3 tools/import_har.py --check-session             # 用当前配置验活
```

工具用无副作用接口验活，绝不触发开门、绝不碰登录接口。
`tools/import_capture.py`（单段抓包代码/密文导入）保留，早期用 `doors/open` 验活会真开门，
现已修复为默认不联网验活。

## 注意事项 / FAQ

- **安全**：不要把服务裸奔到公网，前面加 HTTPS 反代 + 访问控制；开门是真实物理动作。
  仓库务必保持 private（虽然已无凭据，但稳妥）。
- **Q：为什么报 `10426 登录过期`？**
  A：九成是设备 ID 不对。先在设置页「测试连接」看分步结果；`10426` 就到「高级」补填
  绑定设备 ID。程序遇 10426 会自动重登重试（最多 2 次），重登后仍失败才报错。
- **Q：会不会把手机 App 顶下线？**
  A：会。每次登录都轮换 token。程序缓存 30 分钟就是为了尽量少登录；
  App 和本服务交替登录会互相顶，建议以本服务为主要开门入口。
- **Q：能开多个门吗？**
  A：可以。`/api/door/open` 支持传 `doorId`，门列表自动拉取后可遍历；
  当前页面默认开第一个门。
- **上游自签证书**：网关已忽略证书校验；生产环境建议改成只信任该证书指纹。
- **Docker 无 shell**：chiseled 极简镜像不含 shell，排查问题用 `docker logs guangming-door`。
- 旧的命令行客户端工程在 `../GuangmingDoorClient/`，可用于单接口调试。

## 验证情况

| 测试 | 结果 |
|---|---|
| 通用方案（设置/脱敏/自动设备 ID/换账号/自动拉门列表/测试按钮/持久化） | 29/29 |
| 登录失效自动重登（连续失效、超上限、首次登录失败、防重复开门） | 7/7 |
| 开门记录（写入/脱敏/重启持久化/take 上限） | 15/15 |
| 记录页分页查询（默认 20 条/翻页/筛选/越界收敛） | 18/18 |
| 默认最近 7 天范围（跨日期过滤/边界/快捷切换） | 16/16 |
| Docker 最终镜像（不变全球化下中文/北京时间/开门链路/重启持久化） | 13/13 |
| 真实上游端到端（含真实开门与记录写入） | 通过 |
