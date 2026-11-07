# 智慧光明开门网关（C# WebAPI）

> **2026-11-08 重构｜通用方案：设置页只填账号 + 密码（当前架构，请以此为准）**
> `settings.html` 提供设置页，用户只需填写**账号和密码**，保存到
> `data/settings.json`（重启不丢）。系统登录后自动获取 `user_id`，并调用 `rooms`
> **自动拉取门列表及开门所需的全部参数**（door_id / door_guid / door_sip_no /
> room_id / unit_id / building_id / dep_id / door_type / dynamic_token），
> 无需抓包、无需填写任何门点信息。App 级 Basic 凭据与请求头内置，对所有账号通用。
> 换账号时自动清空缓存的 user_id 与门列表并重新生成设备 ID；开门前若门列表为空
> 会**自动补拉一次**，因此只填账号密码也能直接开门。
>
> **关于 device_guid（本次实测结论，重要）**：服务器硬性要求「账号绑定的设备 ID」。
> 4 轮真实上游探测确认：任意随机值 `gm_login` 能返回 token，但业务接口 / 开门
> 一律 `10426`；用正确 token 配随机设备 ID 仍 `10426`；`check_door_device` 无法
> 自助绑定；登录响应不含该值；也不是账号/密码/uid 的常见哈希。因此无法仅由账号密码推导。
> **采用「自动生成 + 失败再补」**：`UserSettings.cs` 按账号生成稳定 16 位 hex 设备 ID
> 并持久化；测试按钮若收到 `10426`，返回 `needDeviceGuid=true` 并提示到设置页
> 「高级」补填绑定设备 ID（可从 App 抓包 Doordu-System 头的 device_guid 取得）。
> 首次启动会从旧 `appsettings.json` 一次性迁移账号/设备/门点作为种子，保证老部署
> 开箱即用；此后一律以 `data/settings.json` 为准。
>
> 新增端点：`GET /api/settings`（脱敏，绝不返回密码明文）、`POST /api/settings`、
> `POST /api/test`。`GET /api/settings` 返回 `passwordSet` 等脱敏字段；
> `POST /api/test` 默认 `openDoor=false`，分步验证 **登录 → 设备校验 → 自动拉门列表**
> 且**不开门**；`openDoor=true` 时额外真实开门一次并写入开门记录（与 `/api/door/open`
> 口径一致）。设置页有「保存并测试」（无副作用）与「测试开门」（二次确认）两个动作。
> 主页面底部新增「⚙ 设置 / 开门记录」入口；未配置时开门会给出设置引导。
> 验证：模拟上游 29/29、真实上游 11/11（含真实开门与记录写入）、
> 三套回归 15/18/16 全部通过，编译 0 警告 0 错误。
>
> **2026-11-07 更新｜已切换为「一键开门」精简版**（凭据部分已被上方通用方案取代）
> 页面只剩一个大按钮，点一下就开门；前端零配置、零密钥。后端只暴露页面与必要的
> 开门/记录/设置/测试端点，登录/会话/转发/同步等调试端点已移除（对应方法仍保留在
> `DoorGateway.cs` 中，需要时可恢复）。线上版本部署在 `GuangmingDoorDeploy/`。
>
> **2026-11-08 加固｜登录失效自动重登开门（无需人工干预）**
> 开门时若上游明确告知登录 / token 失效，后台会：清 token 缓存 → 强制重新登录 →
> 用新 token **自动重发开门请求**，最多重试 2 次。首次登录失败也会自动重试一次。
> 失效识别覆盖：HTTP 401/403/419/426、失效业务码（10426 等）、失效文案
> （登录过期 / 登录已失效 / 未登录 / token expired / unauthorized 等）。
> 明确成功的响应优先排除，避免误判重复开门；网络异常（无法确认门是否已开）
> **不会**自动重发，需人工重试，防止重复开门。
> 响应含 `relogged` 与 `reloginAttempts`，页面在触发自动重登时会显示
> 「登录已失效，后台已自动重新登录并重试 N 次」。
> 模拟上游 7 个场景（含连续失效、超上限、首次登录失败）全部验证通过。
>
> **2026-11-08 新增｜开门记录**
> 每次开门（成功或失败，含自动重登）都会在后台留下一条记录：时间（UTC+8）、结果、
> 上游消息、门点、HTTP 状态、是否自动重登及次数、耗时。页面下方直接展示「累计 /
> 成功 / 失败」统计与最近 30 条明细。新增 `GET /api/door/history?take=30`（`take`
> 上限 500）。记录由 `OpenLog.cs` 落地到工作目录 `data/open-log.json`，内存 + 文件双写，
> **进程重启不丢**；最多保留 500 条明细，累计统计单独计数不受裁剪影响。
> 记录不含任何账号、密码、token、Basic 凭据；写盘失败不影响开门主流程。
> 模拟上游 15 个检查点全部通过（含重启持久化、字段脱敏、失败留痕、take 上限）。
>
> **2026-11-08 补充｜完整记录页（查询 + 分页）**
> 主页面 `index.html` 只显示**最近 10 条**，点「查看全部 ›」另开 `records.html`（访问凭据随链接带上）。
> 完整记录页把查询与分页集中在**一个页面**：日期范围、结果（全部/成功/失败）、关键词
> （匹配消息/门点/门 ID/时间）、每页条数（20/50/100，**默认 20**），以及首页/上一页/下一页/末页
> 与跳页。表格展示时间、结果、门点、消息、是否自动重登、耗时，窄屏可横向滚动。
> 新增 `GET /api/door/history/query?from=&to=&result=&keyword=&page=&pageSize=`，
> 由 `OpenLogStore.QueryPaged` 在后端筛选后分页，返回全局累计、命中数、页数与当页明细；
> 越界页码自动收敛到最后一页。模拟上游 18 个检查点 + 上一轮 15 个回归检查点全部通过。
>
> **默认日期范围为最近 7 天**（含今天，即「今天往前 6 天 ~ 今天」），并提供
> 「最近 7 天 / 最近 30 天 / 全部时间」快捷按钮；「重置」恢复默认 7 天，手动改日期标记
> 为自定义范围，结果条实时显示当前生效范围。此项仅改前端静态页面，线上即时生效、
> 无需重启服务。日期函数（`fmtDate` / `daysAgo`）经 Node 按北京时间校验，
> 另有 16 个跨日期检查点验证默认范围确实过滤 7 天前数据、边界当天包含、全部时间可查全。
>
> 下文保留的是完整版的技术说明与协议细节，端点清单以 `Program.cs` 实际代码为准。

手机发一个 HTTP 请求 → 本机 C# WebAPI → 自主登录取 token → 明文调用上游 → 执行开门。
**不再需要抓包、不再需要 Frida、不再依赖手机 App 会话。**

```
手机（浏览器 / 快捷指令 / HTTP App）
   │  POST /api/door/open  {"doorId":"<door_id>"}   Header: X-Api-Key
   ▼
C# WebAPI（.NET 10 Minimal API，本机 5080 端口）
   │  ① POST users/gm_login  {device_guid, mobile, password}  → 拿 token（缓存 10 分钟）
   │  ② POST doors/open      {device_guid, user_id, token, door_id, ...}
   │     统一 Content-Type: application/json + X-Enc-Level: 0（全程明文，无需 AES）
   ▼
https://zhgm.yrcid.com:15615/api/index.php/v18/doors/open  →  门禁机
```

自带手机端页面，打开 `http://<本机IP>:5080` 就能点按钮开门；也支持 iOS 快捷指令、Android HTTP 请求类 App、curl。

---

## 零、重大修正：可以通过登录拿 token ✅

之前判定「`gm_login` 返回的 token 是设备注册 token、调业务接口一律 10426，只能靠抓包重放」——
**这个结论是错的，根因是 `device_guid` 填错了。**

2026-11-07 晚间重新做了完整的登录协议实测：

| 实验 | 结果 |
|---|---|
| 明文 `users/gm_login` 拿 token | `200 注册成功`，`data.token=E4AEB148…`（每次调用轮换） |
| token + **`device_guid=<你的设备ID>`**（手机设备 ID）→ `user/auth_status` | **`200 授权信息`** ✅ |
| 同一 token + 门 `door_guid=<door_guid>` / `device_id` / 随机值 | 全部 `426 / 10426 登录过期` |
| token 字段名改成 `access_token` / `session_token` 等，或放 header | `10400 参数：token未传入`（字段名必须是 `token`，且必须在 body） |
| **登录 → 明文 `doors/open`** | **`{"code":200,"message":"开门成功"}`** ✅ |
| 登录一次，token 连续复用 4 次验活 | 全部 `200`，可缓存 |
| 密码错误 | `400 / 40002 账号或密码不正确` |

**关键：`device_guid` 必须是账号绑定的手机设备 ID**（取自 15621 端口
`/oem/v5/user/login` 请求头 `Doordu-System` 里的 `device_guid`），
不是门的 `door_guid`。之前所有明文登录实验都误用了 `<door_guid>`，
所以每次都拿 `10426`，才误判成「token 无效」。

程序已据此改为 `Mode = "Login"`：自动登录、缓存 token、遇到 10426 自动强制重登并重试一次。
实测通过 C# WebAPI 端到端开门成功，**全程无需任何抓包密文**。

---

## 一、快速开始

```powershell
cd D:\path\to\GuangmingDoorApi
# 1) 改 appsettings.json 里的 DoorApi:ApiKey（必做，否则 /api/* 返回 503）
# 2) 运行
dotnet run
# 或
.\run.ps1
```

服务监听 `http://0.0.0.0:5080`。手机与电脑同 Wi-Fi 时浏览器打开
`http://<电脑局域网IP>:5080`；Windows 防火墙需放行 5080。
外网访问可用你熟悉的 `netsh portproxy` / 内网穿透映射 5080（建议前面套 HTTPS + 鉴权）。

手机点开门，或直接：

```bash
curl -X POST http://<电脑IP>:5080/api/door/open \
  -H "X-Api-Key: 你的key" -H "Content-Type: application/json" \
  -d '{"doorId":"<door_id>"}'
```

> 真实账号/设备/门点放在 **`appsettings.Local.json`**（git 与 Docker 均忽略）；仓库里的 `appsettings.json` 只有占位。本地开发复制一份 `appsettings.Local.json` 或直接在设置页填写。
> **换手机或换账号时必须同步更新 `DeviceGuid`**，否则一律 `10426`。

---

## Docker 部署（飞牛 fnOS / 树莓派，ARM64）

镜像多架构（`linux/amd64` + `linux/arm64`），树莓派 4B 直接可用。

**方式 A：GitHub Actions 自动构建（推荐）**

1. 把整个项目推到 GitHub 仓库（`.github/workflows/docker-publish.yml` 已就位）
2. 推送到 `main`/`master` 或打 `v*` 标签时，自动构建双架构镜像并发布到 GHCR：
   `ghcr.io/<你的用户名>/<仓库名>:latest`
3. NAS 上拉取运行：

```bash
docker pull ghcr.io/<你的用户名>/<仓库名>:latest
docker run -d --name guangming-door --restart unless-stopped \
  -p 8080:8080 -e TZ=Asia/Shanghai \
  -v guangming-data:/app/data \
  ghcr.io/<你的用户名>/<仓库名>:latest
```

**方式 B：docker compose（fnOS 的 Docker「项目」界面可直接粘贴）**

编辑 `docker-compose.yml` 里的 `image` 为你的 GHCR 地址后 `docker compose up -d`；
或取消 `build: .` 注释在 NAS 本地构建。

**方式 C：本地构建（不依赖 GHCR）**

```bash
docker build -t guangming-door .
docker run -d --name guangming-door --restart unless-stopped \
  -p 8080:8080 -e TZ=Asia/Shanghai \
  -v guangming-data:/app/data \
  guangming-door
```

**要点**

- 容器内监听 `8080`，非 root 用户（UID 1654）运行；`data/`（settings.json、
  open-log.json）通过 `/app/data` 卷持久化，升级镜像不丢设置与记录
- 优先用命名卷（自动继承镜像内目录权限）；若绑定宿主机目录，先
  `mkdir -p ./data && sudo chown -R 1654:1654 ./data`
- 首次启动会从镜像内 `appsettings.json` 迁移种子配置；之后一律以设置页保存为准
- 访问 `http://<NAS-IP>:8080`；务必经 fnOS 反代加 HTTPS + 访问凭据后再暴露公网
- GHCR 包首次发布后默认 private，去 GitHub → Packages 把可见性改为 public 才能匿名拉取
  （或 `docker login ghcr.io` 后拉取）

---

## 二、三种模式

| Mode | 说明 | 是否需要抓包 | 推荐度 |
|---|---|---|---|
| **`Login`**（默认） | 程序用账号密码自主登录取 token，明文开门，可开任意已配置门 | **不需要** | ⭐ 推荐 |
| `Replay` | 重放抓包密文（旧方案），会话过期需重新抓包；门固定在密文里 | 需要 | 兜底 |
| `Plaintext` | 用手动填入的固定 `Token` 发明文请求 | 需要（Frida/抓包拿 token） | 调试用 |

切换只需改 `appsettings.json` 的 `Mode`，其余字段可保留。Replay 相关的密文与
`capture-bundle.json` 快照都还在，随时可切回。

Login 模式的 token 缓存由 `TokenCacheMinutes`（默认 10）控制：
缓存命中就不登录，减少对手机 App 会话的干扰；一旦遇到 `10426` 会清缓存、强制重新登录并重试一次。

---

## 三、协议结论（实测）

1. **服务器按 `Content-Type` 决定如何解析请求体**：
   - `application/json` + 明文 JSON → **直接接受，无需 AES 密钥** ✅
   - `text/plain; charset=utf-8` + 密文 → 服务器自动 AES 解密（App 原始方式）
2. **`X-Enc-Level` 只控制响应**：`0` = 明文 JSON，`2` = AES 密文。网关统一发 `0`。
3. **明文模式必填 `device_guid` + `token`**，字段名必须是 `token` 且放 body；
   `user_id` 与门标识按需补充。放 header 或改名一律 `10400`。
4. **`device_guid` 与账号绑定**，填错一律 `426 / 10426`。
5. **每次 `gm_login` 都会轮换服务端 token**，会把手机 App 的会话顶下线。
   网关因此缓存 token，并且不允许外部通过 `/api/relay` 调用登录/登出类接口（403）。
6. **验活用无副作用接口** `user/auth_status`（明文 token），
   绝不能用 `doors/open` 验活 —— 那会真的开门。
7. `keypackage/list` 明文调用返回 `10400 参数错误`（密文里含额外未知参数），
   所以 Login 模式的门列表直接来自 `appsettings.json`，不影响开门。
8. 15621 端口的 `/oem/...` 接口协议与 15615 不同：改 `X-Enc-Level` 会返回
   `400 / 40400 参数错误`，响应带 `\n` 前缀且每次不同。网关只记录、不调用。

---

## 四、完整启动链路（2026-11-07 18:12 HAR，15 条）

Reqable 3.0.38 导出，208,055 字节，用 `X-Enc-Level: 0` 重放全部拿到明文。

| # | 接口 | 端口 | 状态 | 说明 |
|---|---|---|---|---|
| 1 | `/oem/v5/user/login` | **15621** | 200 | 真正登录入口，`Doordu-System` 里带 `device_guid` |
| 2 | `users/gm_login` | 15615 | 200 | 返回可用 token（会轮换） |
| 3 | `user/rooms` | 15615 | 200 | 房间信息 |
| 4 | `ad/config` | 15615 | 200 | 广告配置 |
| 5 | `user/certificate/info` | 15615 | 200 | 证件信息 |
| 6 | `lottery/list` | 15615 | 200 | 活动列表（163KB 响应） |
| 7 | `keypackage/list` | 15615 | 200 | **门点列表** |
| 8 | `disturb/info` | 15615 | 200 | 免打扰 |
| 9 | `switch/config` | 15615 | 200 | 开关配置（含个人信息） |
| 10 | `/oem/v1/version/check` | **15621** | 400 | 版本检查 |
| 11 | `ad/system/config` | 15615 | 200 | 广告配置 |
| 12 | `user/auth_status` | 15615 | 200 | **验活（无副作用）** |
| 13-15 | `doors/open` ×2 等 | 15615 | 200 | **开门成功** |

`user/auth_status`、`keypackage/list`、`switch/config` 共用**同一份 192 字符会话体密文**
（实测逐字节相同）；`doors/open` 与之公共前缀 85 字符。这是 Replay 模式验活可用无副作用接口的原因。

---

## 五、已确认的真实数据

| 项 | 值 |
|---|---|
| 登录设备 ID | **`device_guid=<你的设备ID>`**（关键，非门 guid） |
| 账号 | `mobile=138****0000（示例，真实值见 appsettings.Local.json）`（密码见 appsettings.Local.json（不入库）） |
| 用户 | `user_id=<user_id>`、`device_id=<device_id>` |
| 门 | `door_id=<door_id>`、<门点名称> |
| 门标识 | `door_guid=<door_guid>`、`door_sip_no=<door_sip_no>` |
| 动态令牌 | `dynamic_token=<开门时从上游动态获取>`、`is_support_token=1` |
| 房间 | `room_id=<room_id>`、`unit_id=<unit_id>`、`building_id=<building_id>`、`dep_id=168` |
| 开门成功响应 | `{"code":200,"message":"开门成功","data":[]}` |
| Basic 凭据 | `lajfnyqxrk6uhtp3sd4m0z7w195v2beo : 8ycaquhd1x4f2z3ge6w90prl5mjotvni` |

---

## 六、API 端点

所有 `/api/*` 需要 `X-Api-Key` 头（或 `?key=`）；静态页面与 `/` 不需要。

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/health` | 连通性、模式、`loginReady`、token 缓存时长、抓包来源 |
| `POST` | `/api/login` | **手动登录 / 强制刷新 token**，body `{"force":true}` 忽略缓存 |
| `GET` | `/api/session/check` | **会话验活**（无副作用，不会开门；Login 模式自动登录取 token） |
| `GET` | `/api/door/list` | 配置里的门点列表 |
| `GET` | `/api/door/sync` | 门列表（Login 模式返回配置来源；Replay 模式重放上游密文） |
| `POST` | `/api/door/open` | **开门**，body `{"doorId":"<door_id>"}` |
| `POST` | `/api/relay` | 通用转发；**已拦截登录/登出类接口**（403） |

`relay` 示例（密文重放）：

```bash
curl -X POST http://localhost:5080/api/relay -H "X-Api-Key: 你的key" \
  -H "Content-Type: application/json" \
  -d '{"path":"keypackage/list","payload":"<base64密文>"}'

# 危险接口会被拒绝，避免外部误调用登录接口顶掉 App 会话
# {"path":"users/gm_login",...} => 403 {"code":"FORBIDDEN",...}
```

开门响应带 `hint` 说明诊断信息；`sent=false` 表示**没有真的发出开门请求**
（例如登录失败），便于区分「门没开」和「请求没发」。`mode` / `relogged` 字段标明
运行模式与本次是否触发了自动重登。

---

## 七、配置项（appsettings.json → `DoorApi`）

| 键 | 说明 |
|---|---|
| `ApiKey` | **必改**，调用本服务的密钥 |
| `Mode` | `Login`（默认）/ `Replay` / `Plaintext` |
| **`DeviceGuid`** | **Login 模式关键**：账号绑定的手机设备 ID，填错一律 10426 |
| `Mobile` / `Password` | 登录账号密码 |
| `UserId` | 用户 ID（登录成功后会自动用响应值更新） |
| `LoginPath` | 登录接口，默认 `users/gm_login` |
| `TokenCacheMinutes` | token 缓存分钟数，默认 10；`0` = 每次开门都重新登录 |
| `SessionCheckPath` | 无副作用验活接口，默认 `user/auth_status` |
| `OpenDoorPayload` / `KeyPackagePayload` / `SessionCheckPayload` | Replay 模式密文（保留作兜底） |
| `Token` | Plaintext 模式的固定 token |
| `OemBaseUrl` | 15621 端口 OEM 接口根路径（仅记录） |
| `CaptureSource` / `CaptureImportedAt` | 最近一次抓包来源与时间 |
| `Doors` | 门点清单，已填全 `<door_id>` 的 guid/sip/room/unit/device/dynamic_token |
| `BaseUrl` / `ClientId` / `ClientSecret` / `DoorDuSystem` / `PackageName` | 上游协议常量 |

---

## 八、抓包与工具（现在只用于排错 / Replay 兜底）

Login 模式**不需要**下面这些。仅当想切回 Replay 模式或排查上游协议时才用：

```powershell
python3 tools/import_har.py capture.har.txt --list      # 只列接口，不联网
python3 tools/import_har.py capture.har.txt             # 解析+安全验活+写配置（会设 Mode=Replay）
python3 tools/import_har.py capture.har.txt --dry-run   # 只验活不写
python3 tools/import_har.py --check-session            # 用当前配置验活
python3 tools/import_har.py --bundle capture-bundle.json  # 从快照恢复
```

工具会用 `user/auth_status` 等**无副作用**接口验活，绝不触发开门、绝不碰登录接口；
会话过期则拒绝写入。`capture-bundle.json` 是 15 条链路的完整密文快照。

> `tools/import_capture.py`（单条 Python 抓包代码 / 单段密文）仍保留，
> 早期版本用 `doors/open` 验活会**真的开一次门**，现已修复为默认不联网验活。

---

## 九、注意事项 / FAQ

- **安全**：`ApiKey` 一定要改；不要把服务裸奔到公网，前面加 HTTPS 反代 + 访问控制。
  `appsettings.json` 含账号密码，注意文件权限。开门是真实物理动作，页面已做「二次点击确认」。
- **Q：为什么又报 `10426 登录过期`？**
  A：先查 `DeviceGuid` 是否正确（真实值见 `appsettings.Local.json`）。程序遇 10426 会自动重登重试一次；
  若重登后仍失败，多半是设备 ID 或账号密码不对。
- **Q：会不会把手机 App 顶下线？**
  A：会。每次登录都会轮换 token。程序缓存 token 10 分钟就是为了尽量少登录；
  如果 App 和本服务交替登录，会互相顶。以本服务为主要开门入口即可。
- **Q：能开多个门吗？**
  A：Login 模式可以，把每个门的 `door_id` 等字段加进 `Doors` 配置即可，
  `doorId` 参数真正生效（Replay 模式门是固定的）。
- **上游自签证书**：网关已忽略证书校验；生产环境建议改成只信任该证书指纹。
- **进程清理**：`dotnet run` 用 Ctrl+C 结束；任务结束后确认 5080 端口已释放。
- 旧的命令行客户端工程在 `../GuangmingDoorClient/`，可继续用于单接口调试。
