using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuangmingDoorApi;

public sealed class DoorResult
{
    public bool Ok { get; set; }
    public int HttpStatus { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Raw { get; set; }
    public string? DoorId { get; set; }
    /// <summary>诊断提示。</summary>
    public string? Hint { get; set; }
    /// <summary>本次是否真的向上游发出了开门请求。</summary>
    public bool Sent { get; set; }
    /// <summary>运行模式：Login / Replay / Plaintext。</summary>
    public string? Mode { get; set; }
    /// <summary>Login 模式下本次是否触发了重新登录。</summary>
    public bool Relogged { get; set; }
    /// <summary>Login 模式下本次自动重新登录 / 重试的次数。</summary>
    public int ReloginAttempts { get; set; }
}

public sealed class RelayResult
{
    public bool Ok { get; set; }
    public int HttpStatus { get; set; }
    public string? Response { get; set; }
}

/// <summary>自动拉取门列表的结果。</summary>
public sealed class FetchDoorsResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public long UserId { get; set; }
    public List<DoorConfig> Doors { get; set; } = new();
    public int HttpStatus { get; set; }
    public string? Code { get; set; }
    public bool Expired { get; set; }
}

/// <summary>设置页「测试」的分步结果。</summary>
public sealed class TestStep
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class TestResult
{
    public bool Ok { get; set; }
    public List<TestStep> Steps { get; set; } = new();
    public int DoorCount { get; set; }
    public string? Hint { get; set; }
    /// <summary>设备 ID 是否被上游接受（false 且 NeedDeviceGuid=true 时提示补填）。</summary>
    public bool DeviceAccepted { get; set; }
    public bool NeedDeviceGuid { get; set; }
    public bool OpenedDoor { get; set; }
    public string? DeviceGuidSource { get; set; }
    /// <summary>openDoor=true 且确实发出开门请求时的完整结果，供上层写开门记录。</summary>
    public DoorResult? OpenResult { get; set; }
}

public sealed class SessionResult
{
    public bool Valid { get; set; }
    public int HttpStatus { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Hint { get; set; }
    public string? CheckedPath { get; set; }
    public string? Raw { get; set; }
    public string? Mode { get; set; }
    public string? CaptureSource { get; set; }
    public string? CaptureImportedAt { get; set; }
    /// <summary>Login 模式：当前缓存的 token（脱敏）。</summary>
    public string? TokenMasked { get; set; }
}

public sealed class LoginResult
{
    public bool Ok { get; set; }
    public int HttpStatus { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Token { get; set; }
    public string? TokenMasked { get; set; }
    public long UserId { get; set; }
    public bool FromCache { get; set; }
    public string? Hint { get; set; }
}

/// <summary>
/// 手机 → 本服务 → 智慧光明服务器 的上行网关。
///
/// 协议结论（2026-11-07 完整 HAR + 登录协议实测）：
///  1. Content-Type: application/json + X-Enc-Level: 0 → 服务器接受【明文 JSON 请求体】，
///     无需 AES 密钥；text/plain 才走 AES 解密（服务器自动解密密文）。
///  2. X-Enc-Level 只控制【响应】是否加密：0 = 明文 JSON，2 = AES 密文。统一发 0。
///  3. 【2026-11-07 晚间修正】gm_login 返回的 token 是【有效的】，可以直接开门。
///     之前判定「无效」的根因是 device_guid 填错：必须是账号绑定的手机设备 ID
///     （15621 端口 /oem/v5/user/login 请求头 Doordu-System 里的 device_guid），
///     而不是门的 door_guid。字段名必须是 token，且放在 body 里（header 无效）。
///  4. device_guid 与账号绑定，换成任意其它值一律 426 / 10426 登录过期。
///  5. 每次 gm_login 都会轮换服务端 token；本网关缓存 token，开门时若上游明确告知
///     登录/token 失效（HTTP 401/403/419/426、10426 等失效码、失效文案），会自动清缓存、
///     强制重新登录并用新 token 重发开门请求，最多重试 2 次；首次登录失败也自动重试一次。
///     因此 Login 模式无需抓包、无需人工干预即可长期运行。
///     明确成功响应优先排除（防误判重复开门）；网络异常不自动重发（无法确认门是否已开）。
///  6. 验活用无副作用接口 user/auth_status（明文 token），绝不能用 doors/open 验活。
///  7. /api/relay 仍拦截登录/登出类接口，避免外部误调用顶掉 App 会话。
///  8. Replay 模式（旧方案）下门标识写在密文里，doorId 参数被忽略。
/// </summary>
public sealed class DoorGateway
{
    /// <summary>禁止通过 relay 转发的接口：会轮换 token / 顶掉 App 会话。</summary>
    private static readonly HashSet<string> ForbiddenPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "users/gm_login", "user/login", "user/quit", "user/logout",
        "v5/user/login", "oem/v5/user/login"
    };

    /// <summary>上游可能返回的登录 / token 失效业务码。</summary>
    private static readonly HashSet<string> ExpiredCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "10426", "10401", "10402", "10403", "10001", "10002", "10003",
        "401", "403", "419", "426", "20001", "20002", "20003", "30001"
    };

    /// <summary>上游可能返回的登录 / token 失效文案关键词。</summary>
    private static readonly string[] ExpiredKeywords =
    {
        "登录过期", "登录失效", "登录超时", "登录状态", "未登录", "请重新登录", "重新登录",
        "会话过期", "会话失效", "令牌失效", "令牌过期", "token失效", "token过期", "token无效",
        "invalid token", "token expired", "unauthorized", "unauthenticated", "login required",
        "not logged", "session expired"
    };

    private readonly DoorApiOptions _opt;
    private readonly HttpClient _http;
    private readonly ILogger<DoorGateway> _log;
    private readonly SettingsStore _store;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    // Login 模式的 token 缓存
    private string? _cachedToken;
    private DateTime _tokenAt = DateTime.MinValue;

    public DoorGateway(IOptions<DoorApiOptions> options, ILogger<DoorGateway> log, SettingsStore store)
    {
        _opt = options.Value;
        _log = log;
        _store = store;

        var handler = new HttpClientHandler
        {
            // 上游使用自签证书
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public IReadOnlyList<DoorConfig> Doors => _opt.Doors;

    private bool IsLogin => string.Equals(_opt.Mode, "Login", StringComparison.OrdinalIgnoreCase);
    private bool IsPlaintext => string.Equals(_opt.Mode, "Plaintext", StringComparison.OrdinalIgnoreCase);
    private string ModeName => IsLogin ? "Login" : (IsPlaintext ? "Plaintext" : "Replay");

    private void ApplyHeaders(HttpRequestMessage req)
    {
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(_opt.ClientId + ":" + _opt.ClientSecret));

        req.Headers.TryAddWithoutValidation("Authorization", "Basic " + basic);
        req.Headers.TryAddWithoutValidation("User-Agent", "okhttp/3.12.1");
        req.Headers.TryAddWithoutValidation("doordu-system", _opt.DoorDuSystem);
        req.Headers.TryAddWithoutValidation("Package-Name", _opt.PackageName);
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh_CN");
        req.Headers.TryAddWithoutValidation("Connection", "Keep-Alive");
        // 关键：让服务器返回明文 JSON
        req.Headers.TryAddWithoutValidation("X-Enc-Level", "0");
    }

    private string Url(string path) =>
        _opt.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static string Normalize(string path)
    {
        var p = (path ?? "").Trim().TrimStart('/');
        foreach (var prefix in new[] { "api/index.php/v18/", "oem/" })
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                p = p.Substring(prefix.Length);
        return p;
    }

    /// <summary>底层发送：精确控制 Content-Type，不经过 relay 的危险接口拦截。</summary>
    private async Task<RelayResult> SendAsync(
        string path, string content, string contentType, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url(path));
        ApplyHeaders(req);
        var sc = new StringContent(content, Encoding.UTF8);
        sc.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        req.Content = sc;

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var status = (int)resp.StatusCode;
        _log.LogInformation("上行 {Path} -> HTTP {Status} {Body}", path, status, Trim(text));
        return new RelayResult
        {
            Ok = status >= 200 && status < 300,
            HttpStatus = status,
            Response = text
        };
    }

    private static string? ReadCode(string? text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text ?? string.Empty);
            if (doc.RootElement.TryGetProperty("code", out var c)) return c.ToString();
        }
        catch { }
        return null;
    }

    private static string? ReadMessage(string? text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text ?? string.Empty);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 判断上游响应是否属于「登录 / token 失效」。
    /// 明确成功的响应优先排除，避免误判触发重登并重复开门。
    /// 覆盖 HTTP 401/403/419/426、常见失效业务码、常见失效文案。
    /// </summary>
    private static bool IsExpired(RelayResult r)
    {
        var code = ReadCode(r.Response);

        // 明确成功 → 一律不算失效（防止误判导致重复开门）
        if (r.Ok && (code == "200" || code == "0")) return false;

        if (r.HttpStatus == 401 || r.HttpStatus == 403 ||
            r.HttpStatus == 419 || r.HttpStatus == 426) return true;

        if (!string.IsNullOrEmpty(code) && ExpiredCodes.Contains(code)) return true;

        var msg = ReadMessage(r.Response) ?? r.Response ?? "";
        foreach (var kw in ExpiredKeywords)
            if (msg.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    // ------------------------------------------------------------------
    // 自主登录（Login 模式）
    // ------------------------------------------------------------------

    /// <summary>清除 token 缓存，下次调用会强制重新登录。</summary>
    public void InvalidateToken()
    {
        _cachedToken = null;
        _tokenAt = DateTime.MinValue;
    }

    private static string Mask(string? t) =>
        string.IsNullOrEmpty(t) ? "" : (t.Length <= 8 ? "****" : t.Substring(0, 8) + "…");

    /// <summary>向上游明文登录，返回新 token（不写缓存，由 GetTokenAsync 统一处理）。</summary>
    private async Task<LoginResult> DoLoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.DeviceGuid) ||
            string.IsNullOrWhiteSpace(_opt.Mobile) ||
            string.IsNullOrWhiteSpace(_opt.Password))
        {
            return new LoginResult
            {
                Ok = false, Code = "CONFIG",
                Message = "Login 模式缺少 DeviceGuid / Mobile / Password 配置。",
                Hint = "DeviceGuid 必须是账号绑定的手机设备 ID（不是门 guid），可从 15621 登录抓包的 Doordu-System 头获取。"
            };
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["device_guid"] = _opt.DeviceGuid,
            ["mobile"] = _opt.Mobile,
            ["password"] = _opt.Password
        });
        var r = await SendAsync(Normalize(_opt.LoginPath), body, "application/json", ct);
        var res = new LoginResult
        {
            HttpStatus = r.HttpStatus,
            Code = ReadCode(r.Response),
            Message = ReadMessage(r.Response),
            UserId = _opt.UserId
        };

        try
        {
            using var doc = JsonDocument.Parse(r.Response ?? string.Empty);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("token", out var tk)) res.Token = tk.GetString();
                if (data.TryGetProperty("user_id", out var uid) && uid.TryGetInt64(out var v)) res.UserId = v;
            }
        }
        catch { }

        res.Ok = r.Ok && (res.Code == "200" || res.Code == "0") && !string.IsNullOrEmpty(res.Token);
        res.TokenMasked = Mask(res.Token);
        if (!res.Ok)
        {
            if (res.Code == "40002") res.Message = "账号或密码不正确";
            res.Hint = "请检查 appsettings.json 的 Mobile / Password / DeviceGuid。" +
                       "DeviceGuid 填错会导致后续接口一律 10426 登录过期。";
        }
        else
        {
            res.Hint = "登录成功，token 已缓存。此 token 可直接用于开门，无需抓包。";
        }
        return res;
    }

    /// <summary>
    /// 获取可用 token：命中缓存直接返回，否则登录。
    /// force=true 强制重新登录（用于 10426 自动重试）。
    /// </summary>
    public async Task<LoginResult> GetTokenAsync(bool force, CancellationToken ct)
    {
        var ttl = TimeSpan.FromMinutes(Math.Max(0, _opt.TokenCacheMinutes));
        if (!force && !string.IsNullOrEmpty(_cachedToken) && ttl > TimeSpan.Zero &&
            DateTime.UtcNow - _tokenAt < ttl)
        {
            return new LoginResult
            {
                Ok = true, FromCache = true, Token = _cachedToken,
                TokenMasked = Mask(_cachedToken), UserId = _opt.UserId,
                Message = "使用缓存 token", Hint = "token 有效期内复用，避免频繁登录顶掉 App 会话。"
            };
        }

        await _loginLock.WaitAsync(ct);
        try
        {
            // 双检：可能别的线程刚登录过
            if (!force && !string.IsNullOrEmpty(_cachedToken) && ttl > TimeSpan.Zero &&
                DateTime.UtcNow - _tokenAt < ttl)
            {
                return new LoginResult
                {
                    Ok = true, FromCache = true, Token = _cachedToken,
                    TokenMasked = Mask(_cachedToken), UserId = _opt.UserId,
                    Message = "使用缓存 token"
                };
            }
            var res = await DoLoginAsync(ct);
            if (res.Ok)
            {
                _cachedToken = res.Token;
                _tokenAt = DateTime.UtcNow;
                if (res.UserId > 0) _opt.UserId = res.UserId;
                _log.LogInformation("自主登录成功，token={Token}, user_id={UserId}", Mask(res.Token), res.UserId);
            }
            else
            {
                InvalidateToken();
                _log.LogWarning("自主登录失败：{Code} {Message}", res.Code, res.Message);
            }
            return res;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    // ------------------------------------------------------------------
    // 通用转发（外部 /api/relay）
    // ------------------------------------------------------------------

    /// <summary>
    /// 通用转发：payload 走 text/plain（密文重放），json 走 application/json（明文）。
    /// 会拦截登录/登出类接口，避免误触发导致会话被顶掉。
    /// </summary>
    public async Task<RelayResult> RelayAsync(
        string path, string? payload, string? json, CancellationToken ct)
    {
        var p = Normalize(path);
        if (ForbiddenPaths.Contains(p))
        {
            _log.LogWarning("拒绝转发危险接口 {Path}（会轮换 token、顶掉 App 会话）", p);
            return new RelayResult
            {
                Ok = false,
                HttpStatus = 403,
                Response = "{\"code\":\"FORBIDDEN\",\"message\":\"禁止转发 " + p +
                           "：该接口会轮换服务端 token。请使用 /api/login 由网关统一管理登录。\"}"
            };
        }
        if (!string.IsNullOrWhiteSpace(json))
            return await SendAsync(p, json, "application/json", ct);
        return await SendAsync(p, payload ?? "", "text/plain; charset=utf-8", ct);
    }

    // ------------------------------------------------------------------
    // 会话验活
    // ------------------------------------------------------------------

    /// <summary>
    /// 会话验活：只打无副作用接口，绝不触发开门。
    /// Login 模式用自动登录取到的 token 发明文请求；Replay 模式重放会话密文。
    /// </summary>
    public async Task<SessionResult> CheckSessionAsync(CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(_opt.SessionCheckPath)
            ? "user/auth_status" : _opt.SessionCheckPath;

        // ---------- Login 模式 ----------
        if (IsLogin)
        {
            var login = await GetTokenAsync(false, ct);
            var res = new SessionResult
            {
                Mode = "Login",
                CheckedPath = path,
                TokenMasked = login.TokenMasked,
                CaptureSource = _opt.CaptureSource,
                CaptureImportedAt = _opt.CaptureImportedAt
            };
            if (!login.Ok)
            {
                res.Valid = false;
                res.HttpStatus = login.HttpStatus;
                res.Code = login.Code ?? "LOGIN_FAILED";
                res.Message = login.Message ?? "自动登录失败";
                res.Hint = login.Hint;
                return res;
            }

            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["device_guid"] = _opt.DeviceGuid,
                ["user_id"] = _opt.UserId,
                ["token"] = login.Token
            });
            var r = await SendAsync(path, body, "application/json", ct);
            res.HttpStatus = r.HttpStatus;
            res.Raw = r.Response;
            res.Code = ReadCode(r.Response);
            res.Message = ReadMessage(r.Response);
            res.Valid = r.Ok && (res.Code == "200" || res.Code == "0");

            if (IsExpired(r))
            {
                InvalidateToken();
                res.Valid = false;
                res.Hint = "token 已失效，缓存已清除；下次开门会自动重新登录，无需人工抓包。";
            }
            else if (res.Valid)
            {
                res.Hint = "自主登录模式：token 由程序自动获取并缓存，无需抓包。" +
                           "注意 device_guid 必须保持为账号绑定的手机设备 ID。";
            }
            else
            {
                res.Hint = "上游返回异常：HTTP " + r.HttpStatus;
            }
            return res;
        }

        // ---------- Replay / Plaintext 模式（旧逻辑） ----------
        var payload = _opt.SessionCheckPayload;
        if (string.IsNullOrWhiteSpace(payload) && !string.IsNullOrWhiteSpace(_opt.KeyPackagePayload))
        {
            path = "keypackage/list";
            payload = _opt.KeyPackagePayload;
        }
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new SessionResult
            {
                Valid = false, Mode = ModeName, CheckedPath = path,
                Message = "未配置验活密文",
                Hint = "建议在 appsettings.json 设 Mode=Login 使用自主登录，或用 tools/import_har.py 导入抓包。",
                CaptureSource = _opt.CaptureSource,
                CaptureImportedAt = _opt.CaptureImportedAt
            };
        }

        var relay = await RelayAsync(path, payload, null, ct);
        var old = new SessionResult
        {
            Mode = ModeName,
            HttpStatus = relay.HttpStatus,
            CheckedPath = path,
            Raw = relay.Response,
            Code = ReadCode(relay.Response),
            Message = ReadMessage(relay.Response),
            CaptureSource = _opt.CaptureSource,
            CaptureImportedAt = _opt.CaptureImportedAt
        };
        old.Valid = relay.Ok && (old.Code == "200" || old.Code == "0" || old.Code == null);
        if (IsExpired(relay))
        {
            old.Valid = false;
            old.Hint = "抓包会话已过期。建议改为 Mode=Login 自主登录（无需抓包），或重新导入一份新 HAR。";
        }
        else if (old.Valid)
        {
            old.Hint = "会话有效，可以开门。注意不要调用任何登录接口，也不要在手机上退出重登。";
        }
        else
        {
            old.Hint = "上游返回异常：HTTP " + relay.HttpStatus;
        }
        return old;
    }

    // ------------------------------------------------------------------
    // 开门
    // ------------------------------------------------------------------

    private Dictionary<string, object?> BuildOpenBody(string doorId, DoorConfig? door, string token)
    {
        var body = new Dictionary<string, object?>
        {
            ["device_guid"] = _opt.DeviceGuid,
            ["user_id"] = _opt.UserId,
            ["token"] = token,
            ["door_id"] = doorId
        };
        if (door != null)
        {
            if (!string.IsNullOrEmpty(door.Guid)) body["door_guid"] = door.Guid;
            if (!string.IsNullOrEmpty(door.SipNo)) body["door_sip_no"] = door.SipNo;
            if (!string.IsNullOrEmpty(door.RoomId)) body["room_id"] = door.RoomId;
            if (!string.IsNullOrEmpty(door.DynamicToken)) body["dynamic_token"] = door.DynamicToken;
            body["open_type"] = 1;
        }
        return body;
    }

    /// <summary>执行开门。Login 模式会自动登录，登录失效时自动重登并重发，最多重试 2 次。</summary>
    public async Task<DoorResult> OpenAsync(string? doorId, CancellationToken ct,
        bool precheck = true)
    {
        // 通用方案兜底：换账号后门列表被清空时，先自动拉取一次（无副作用）。
        // 这样用户只在设置页填了账号密码、没点测试，也能直接开门。
        if (IsLogin && _opt.Doors.Count == 0)
        {
            var pre = await FetchDoorsAsync(ct);
            if (pre.Ok && pre.Doors.Count > 0)
            {
                _opt.Doors = new List<DoorConfig>(pre.Doors);
                // 持久化，避免每次冷启动都重新拉取
                _store.CacheDoors(pre.UserId, pre.Doors);
            }
        }

        var id = string.IsNullOrWhiteSpace(doorId)
            ? (_opt.Doors.Count > 0 ? _opt.Doors[0].Id : string.Empty)
            : doorId!;
        var door = _opt.Doors.Find(d => d.Id == id);

        // ---------- Login 模式：自主登录 + 明文开门（登录失效自动重登重试） ----------
        if (IsLogin)
        {
            // 失效响应意味着门没有被打开，重发开门请求是安全的；最多自动重登重试 2 次。
            const int MaxRelogin = 2;
            int attempts = 0;

            var login = await GetTokenAsync(false, ct);
            if (!login.Ok)
            {
                // 首次登录失败（网络抖动 / 上游 5xx）→ 强制重登再试一次
                attempts++;
                login = await GetTokenAsync(true, ct);
            }
            if (!login.Ok)
            {
                return new DoorResult
                {
                    Ok = false, Mode = "Login", DoorId = id,
                    Relogged = attempts > 0, ReloginAttempts = attempts,
                    HttpStatus = login.HttpStatus > 0 ? login.HttpStatus : 400,
                    Code = login.Code ?? "LOGIN_FAILED",
                    Message = login.Message ?? "自动登录失败（已重试）",
                    Hint = login.Hint
                };
            }

            var body = JsonSerializer.Serialize(BuildOpenBody(id, door, login.Token!));
            RelayResult relay;
            try
            {
                relay = await SendAsync("doors/open", body, "application/json", ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // 网络异常时无法确认门是否已开，不自动重发，避免重复开门。
                return new DoorResult
                {
                    Ok = false, Mode = "Login", DoorId = id,
                    Relogged = attempts > 0, ReloginAttempts = attempts,
                    Code = "NETWORK",
                    Message = "请求上游超时或网络异常，未确认门是否打开，请手动重试。",
                    Hint = "为避免重复开门，网络异常不会自动重发开门请求。"
                };
            }

            // 只要上游明确告知「登录失效」，就清缓存、强制重登、用新 token 重发开门
            while (IsExpired(relay) && attempts < MaxRelogin)
            {
                attempts++;
                InvalidateToken();
                var again = await GetTokenAsync(true, ct);
                if (!again.Ok)
                {
                    return new DoorResult
                    {
                        Ok = false, Mode = "Login", DoorId = id,
                        Relogged = true, ReloginAttempts = attempts,
                        HttpStatus = again.HttpStatus > 0 ? again.HttpStatus : 426,
                        Code = again.Code ?? "LOGIN_FAILED",
                        Message = again.Message ?? "重新登录失败",
                        Hint = again.Hint
                    };
                }
                body = JsonSerializer.Serialize(BuildOpenBody(id, door, again.Token!));
                try
                {
                    relay = await SendAsync("doors/open", body, "application/json", ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
                {
                    return new DoorResult
                    {
                        Ok = false, Mode = "Login", DoorId = id,
                        Relogged = true, ReloginAttempts = attempts,
                        Code = "NETWORK",
                        Message = "重登后请求上游超时或网络异常，请手动重试。",
                        Hint = "为避免重复开门，网络异常不会自动重发开门请求。"
                    };
                }
            }

            var result = ParseDoor(relay, id);
            result.Sent = true;
            result.Mode = "Login";
            result.Relogged = attempts > 0;
            result.ReloginAttempts = attempts;
            result.Hint = attempts > 0
                ? $"登录已失效，后台已自动重新登录并重试开门 {attempts} 次，无需人工干预。"
                : "Login 模式：程序自动登录获取 token 并发明文开门，无需抓包；可开任意已配置门点。";
            if (IsExpired(relay))
                result.Hint = $"已自动重新登录并重试 {attempts} 次仍报登录失效，请检查 DeviceGuid 是否为账号绑定的手机设备 ID。";
            return result;
        }

        // ---------- Replay 模式 ----------
        var replay = !IsPlaintext;
        if (replay && !string.IsNullOrWhiteSpace(doorId) && _opt.Doors.Count > 1)
        {
            return new DoorResult
            {
                Ok = false, Mode = "Replay", HttpStatus = 0, Code = "DOOR_FIXED", DoorId = id,
                Message = "Replay 模式下门由抓包密文决定，无法切换门点。",
                Hint = "建议改为 Mode=Login 自主登录，即可开任意门；或抓对应门的 doors/open 请求。"
            };
        }
        if (string.IsNullOrWhiteSpace(_opt.OpenDoorPayload) && replay)
        {
            return new DoorResult
            {
                Ok = false, Mode = "Replay", HttpStatus = 0, Code = "CONFIG", DoorId = id,
                Message = "未配置 DoorApi:OpenDoorPayload。",
                Hint = "建议改为 Mode=Login（无需抓包），或运行 python3 tools/import_har.py <抓包.har> 导入。"
            };
        }
        if (precheck && replay)
        {
            var sess = await CheckSessionAsync(ct);
            if (!sess.Valid)
            {
                return new DoorResult
                {
                    Ok = false, Mode = "Replay",
                    HttpStatus = sess.HttpStatus > 0 ? sess.HttpStatus : 426,
                    Code = sess.Code ?? "SESSION_EXPIRED",
                    DoorId = id, Message = sess.Message ?? "会话无效", Hint = sess.Hint
                };
            }
        }

        RelayResult relay2;
        if (IsPlaintext)
        {
            var body = BuildOpenBody(id, door, _opt.Token ?? "");
            relay2 = await RelayAsync("doors/open", null, JsonSerializer.Serialize(body), ct);
        }
        else
        {
            relay2 = await RelayAsync("doors/open", _opt.OpenDoorPayload, null, ct);
        }

        var r2 = ParseDoor(relay2, id);
        r2.Sent = true;
        r2.Mode = ModeName;
        if (replay) r2.Hint = "Replay 模式：门由抓包密文决定，doorId 参数被忽略。";
        if (r2.Code == "10426" || r2.Message?.Contains("登录过期") == true)
            r2.Hint = "抓包会话已过期。建议改为 Mode=Login 自主登录，或重新导入一份新 HAR。";
        return r2;
    }

    // ------------------------------------------------------------------
    // 通用方案：登录后自动拉取门列表 + 设置页分步测试
    // ------------------------------------------------------------------

    private static string? JsonStr(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    /// <summary>
    /// 登录（复用缓存 token）后调用 rooms，自动解析账号下的门点及开门所需参数。
    /// 无副作用，不触发开门。设备 ID 不被接受时返回 10426。
    /// </summary>
    public async Task<FetchDoorsResult> FetchDoorsAsync(CancellationToken ct)
    {
        var res = new FetchDoorsResult();
        var login = await GetTokenAsync(false, ct);
        if (!login.Ok)
        {
            res.Message = login.Message ?? "自动登录失败";
            res.HttpStatus = login.HttpStatus;
            res.Code = login.Code;
            return res;
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["device_guid"] = _opt.DeviceGuid,
            ["user_id"] = _opt.UserId,
            ["token"] = login.Token
        });
        var r = await SendAsync("rooms", body, "application/json", ct);
        res.HttpStatus = r.HttpStatus;
        res.Code = ReadCode(r.Response);
        res.UserId = _opt.UserId;
        res.Expired = IsExpired(r);

        if (!r.Ok || (res.Code != "200" && res.Code != "0" && res.Code != null))
        {
            res.Message = res.Expired
                ? "登录过期（10426）：设备 ID 未被账号接受"
                : (ReadMessage(r.Response) ?? "HTTP " + r.HttpStatus);
            return res;
        }

        try
        {
            using var doc = JsonDocument.Parse(r.Response ?? string.Empty);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("room_list", out var rooms) &&
                rooms.ValueKind == JsonValueKind.Array)
            {
                foreach (var room in rooms.EnumerateArray())
                {
                    var roomId = JsonStr(room, "room_id") ?? "";
                    var unitId = JsonStr(room, "unit_id") ?? "";
                    var buildingId = JsonStr(room, "building_id") ?? "";
                    var depId = JsonStr(room, "dep_id") ?? "";
                    if (!room.TryGetProperty("door_list", out var doorList) ||
                        doorList.ValueKind != JsonValueKind.Array) continue;

                    foreach (var d in doorList.EnumerateArray())
                    {
                        var id = JsonStr(d, "door_id");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var name = JsonStr(d, "door_name") ?? "";
                        var alias = JsonStr(d, "door_alias") ?? "";
                        if (!string.IsNullOrWhiteSpace(alias)) name = name + " / " + alias;
                        if (string.IsNullOrWhiteSpace(name)) name = id!;

                        res.Doors.Add(new DoorConfig
                        {
                            Id = id!,
                            Name = name,
                            Guid = JsonStr(d, "door_guid") ?? "",
                            SipNo = JsonStr(d, "door_sip_no") ?? "",
                            DoorType = JsonStr(d, "door_type") ?? "",
                            IsSupportToken = JsonStr(d, "is_support_token") ?? "",
                            DynamicToken = JsonStr(d, "dynamic_token") ?? "",
                            RoomId = roomId,
                            UnitId = unitId,
                            BuildingId = buildingId,
                            DepId = depId
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            res.Message = "解析门列表失败：" + ex.Message;
            return res;
        }

        res.Ok = res.Doors.Count > 0;
        res.Message = res.Ok ? "自动获取到 " + res.Doors.Count + " 个门点" : "账号下没有可用门点";
        return res;
    }

    /// <summary>
    /// 设置页「测试」：分步验证 登录 → 设备/会话校验 → 自动拉取门列表 →（可选）真实开门。
    /// openDoor=false 时全程无副作用，绝不触发开门。
    /// </summary>
    public async Task<TestResult> TestAsync(bool openDoor, SettingsStore store, CancellationToken ct)
    {
        var t = new TestResult { DeviceGuidSource = store.Current.DeviceGuidSource };

        // ① 登录（强制重新登录，真正验证账号密码）
        var login = await GetTokenAsync(true, ct);
        t.Steps.Add(new TestStep
        {
            Name = "登录",
            Ok = login.Ok,
            Detail = login.Ok
                ? "账号密码正确，user_id=" + login.UserId + "，token=" + login.TokenMasked
                : (login.Code ?? "") + " " + (login.Message ?? "登录失败")
        });
        if (!login.Ok)
        {
            t.Hint = "登录失败，请检查账号和密码。若密码留空表示沿用已保存的密码。";
            return t;
        }
        if (login.UserId > 0) store.CacheDoors(login.UserId, new List<DoorConfig>());

        // ② 设备 / 会话校验（无副作用接口）
        var session = await CheckSessionAsync(ct);
        t.DeviceAccepted = session.Valid;
        t.Steps.Add(new TestStep
        {
            Name = "设备校验",
            Ok = session.Valid,
            Detail = session.Valid
                ? "设备 ID 已被账号接受（" + session.Message + "）"
                : (session.Code ?? "") + " " + (session.Message ?? "校验失败")
        });
        if (!session.Valid)
        {
            t.NeedDeviceGuid = session.Code == "10426" ||
                               session.HttpStatus is 401 or 403 or 419 or 426;
            t.Hint = t.NeedDeviceGuid
                ? "服务器拒绝了当前设备 ID（10426）。自动生成的设备 ID 未被该账号绑定，"
                  + "请在「高级设置」里补填账号绑定的设备 ID（可从智慧光明 App 抓包的 "
                  + "Doordu-System 请求头里取 device_guid），保存后重新测试。"
                : "会话校验未通过：" + (session.Message ?? "") + "，请稍后重试。";
            return t;
        }

        // ③ 自动拉取门列表
        var doors = await FetchDoorsAsync(ct);
        t.DoorCount = doors.Doors.Count;
        t.Steps.Add(new TestStep
        {
            Name = "门列表",
            Ok = doors.Ok,
            Detail = doors.Ok
                ? doors.Message + "：" + string.Join("、", doors.Doors.ConvertAll(x => x.Name))
                : (doors.Code ?? "") + " " + doors.Message
        });
        if (doors.Ok) store.CacheDoors(doors.UserId, doors.Doors);
        if (!doors.Ok)
        {
            if (doors.Expired)
            {
                t.NeedDeviceGuid = true;
                t.Hint = "拉取门列表时返回 10426，请在「高级设置」补填账号绑定的设备 ID。";
            }
            else
            {
                t.Hint = "登录成功但未获取到门点，请确认该账号是否具备门禁权限。";
            }
            return t;
        }

        // ④ 可选：真实开门（明确由用户触发）
        if (openDoor)
        {
            var open = await OpenAsync(null, ct);
            t.OpenedDoor = open.Ok;
            t.OpenResult = open;
            t.Steps.Add(new TestStep
            {
                Name = "开门",
                Ok = open.Ok,
                Detail = (open.Code ?? "") + " " + (open.Message ?? "")
            });
            if (!open.Ok) t.Hint = "开门失败：" + (open.Message ?? "") +
                                   (string.IsNullOrEmpty(open.Hint) ? "" : "；" + open.Hint);
        }

        t.Ok = true;
        return t;
    }

    // ------------------------------------------------------------------
    // 门列表
    // ------------------------------------------------------------------

    /// <summary>拉取门列表，无副作用。Login 模式优先用 rooms 自动拉取，失败回退缓存。</summary>
    public async Task<RelayResult> GetDoorListAsync(CancellationToken ct)
    {
        if (IsLogin)
        {
            var fetched = await FetchDoorsAsync(ct);
            var src = fetched.Ok ? "auto" : "cache";
            var payload = JsonSerializer.Serialize(new
            {
                ok = true,
                source = src,
                message = fetched.Ok
                    ? fetched.Message
                    : "自动拉取未成功，显示已缓存的门点：" + fetched.Message,
                doors = fetched.Ok ? fetched.Doors : _opt.Doors
            });
            return new RelayResult { Ok = true, HttpStatus = 200, Response = payload };
        }

        if (IsPlaintext)
        {
            var body = new Dictionary<string, object?>
            {
                ["device_guid"] = _opt.DeviceGuid,
                ["user_id"] = _opt.UserId,
                ["token"] = _opt.Token
            };
            return await RelayAsync("keypackage/list", null, JsonSerializer.Serialize(body), ct);
        }

        if (string.IsNullOrWhiteSpace(_opt.KeyPackagePayload))
        {
            return new RelayResult
            {
                Ok = false, HttpStatus = 0,
                Response = "{\"code\":\"CONFIG\",\"message\":\"未配置 DoorApi:KeyPackagePayload；建议改为 Mode=Login\"}"
            };
        }
        return await RelayAsync("keypackage/list", _opt.KeyPackagePayload, null, ct);
    }

    private static DoorResult ParseDoor(RelayResult r, string doorId)
    {
        var res = new DoorResult
        {
            HttpStatus = r.HttpStatus,
            DoorId = doorId,
            Raw = r.Response
        };
        try
        {
            using var doc = JsonDocument.Parse(r.Response ?? string.Empty);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c)) res.Code = c.ToString();
            if (root.TryGetProperty("message", out var m)) res.Message = m.GetString();
            res.Ok = r.Ok && (res.Code == "200" || res.Code == "0" || res.Code == null);
        }
        catch
        {
            res.Ok = r.Ok;
            res.Message = r.Response;
        }
        return res;
    }

    private static string Trim(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= 300 ? s : s.Substring(0, 300));
}
