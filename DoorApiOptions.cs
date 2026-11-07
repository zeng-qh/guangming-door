using System.Collections.Generic;

namespace GuangmingDoorApi;

/// <summary>
/// 智慧光明开门网关配置，对应 appsettings.json 的 "DoorApi" 节点。
/// </summary>
public sealed class DoorApiOptions
{
    public const string SectionName = "DoorApi";

    /// <summary>上游服务器 API 根路径。</summary>
    public string BaseUrl { get; set; } = "https://zhgm.yrcid.com:15615/api/index.php/v18/";

    /// <summary>应用级 Basic 凭据（来自抓包 Authorization 头解码）。</summary>
    public string ClientId { get; set; } = "lajfnyqxrk6uhtp3sd4m0z7w195v2beo";
    public string ClientSecret { get; set; } = "8ycaquhd1x4f2z3ge6w90prl5mjotvni";

    public string PackageName { get; set; } = "com.guangmingzmt.oem";
    public string DoorDuSystem { get; set; } =
        "{\"app_version\":\"2.1.5.001\",\"system_version\":\"14\",\"system_models\":\"22101316UCP\",\"system_type\":2}";

    /// <summary>调用本 WebAPI 必须携带的密钥：Header X-Api-Key 或 query ?key=。</summary>
    public string ApiKey { get; set; } = "CHANGE-ME-PLEASE";

    /// <summary>
    /// 运行模式：
    ///   Login     —— 【推荐，默认】自主明文登录获取 token，无需抓包。
    ///   Replay    —— 重放抓包密文（旧方案，会话会过期，需反复抓包）。
    ///   Plaintext —— 使用手动填入的固定 Token 发明文请求（调试用）。
    /// </summary>
    public string Mode { get; set; } = "Login";

    // ---------- Login 模式 ----------

    /// <summary>
    /// 【关键】登录设备 guid，必须等于账号绑定的手机设备 ID。
    /// 取自 15621 端口 /oem/v5/user/login 请求头 Doordu-System 里的 device_guid。
    /// 用错（例如填成门的 door_guid）会导致所有业务接口 426 / 10426 登录过期。
    /// </summary>
    public string DeviceGuid { get; set; } = "";

    public string Mobile { get; set; } = "";
    public string Password { get; set; } = "";
    public long UserId { get; set; } = 0;

    /// <summary>登录接口路径。明文 gm_login 返回 data.token，即当前账号有效 token。</summary>
    public string LoginPath { get; set; } = "users/gm_login";

    /// <summary>
    /// token 缓存分钟数。缓存可减少对上游的登录次数、避免频繁顶掉手机 App 会话；
    /// 遇到 10426 会自动强制重新登录并重试一次。设为 0 表示每次开门都重新登录。
    /// </summary>
    public int TokenCacheMinutes { get; set; } = 30;

    /// <summary>无副作用验活接口（Login 模式下用明文 token 调用）。</summary>
    public string SessionCheckPath { get; set; } = "user/auth_status";

    // ---------- Replay 模式（兼容旧配置，兜底用） ----------

    /// <summary>Replay：doors/open 抓包密文（text/plain 请求体）。旧密文会返回 10426。</summary>
    public string? OpenDoorPayload { get; set; }

    /// <summary>Replay：keypackage/list 会话密文，用于列门。</summary>
    public string? KeyPackagePayload { get; set; }

    /// <summary>Replay：验活接口的会话密文。</summary>
    public string? SessionCheckPayload { get; set; }

    /// <summary>Plaintext：手动填入的固定 token。</summary>
    public string? Token { get; set; }

    /// <summary>15621 端口 OEM 接口根路径。仅记录，不主动调用。</summary>
    public string OemBaseUrl { get; set; } = "https://zhgm.yrcid.com:15621/oem/";

    /// <summary>最近一次导入抓包的来源与时间（仅 Replay 模式有意义）。</summary>
    public string? CaptureSource { get; set; }
    public string? CaptureImportedAt { get; set; }

    public List<DoorConfig> Doors { get; set; } = new List<DoorConfig>();
}

public sealed class DoorConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
    public string SipNo { get; set; } = "";
    public string RoomId { get; set; } = "";
    /// <summary>keypackage/list 下发的动态令牌（开门时可能用到）。</summary>
    public string DynamicToken { get; set; } = "";
    public string UnitId { get; set; } = "";
    public string BuildingId { get; set; } = "";
    public string DepId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    /// <summary>门类型：2 = 单元门口机。</summary>
    public string DoorType { get; set; } = "";
    /// <summary>是否支持动态令牌：1 = 支持。</summary>
    public string IsSupportToken { get; set; } = "";
}
