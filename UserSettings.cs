using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GuangmingDoorApi;

/// <summary>
/// 用户在设置页维护的凭据。只要求账号 + 密码；其余信息（user_id、门列表、门点参数）
/// 全部由程序登录后自动拉取。device_guid 默认按账号自动生成，失败时可手动覆盖。
/// 落盘在 data/settings.json，进程重启不丢。
/// </summary>
public sealed class UserSettings
{
    public string Mobile { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>用户在「高级」里手动补填的账号绑定设备 ID；null / 空 = 使用自动生成值。</summary>
    public string? DeviceGuidOverride { get; set; }

    /// <summary>按账号自动生成的稳定设备 ID（16 位 hex）。</summary>
    public string AutoDeviceGuid { get; set; } = "";

    /// <summary>登录后自动获得的 user_id（缓存，换账号会清空）。</summary>
    public long UserId { get; set; }

    /// <summary>登录后从 rooms 自动拉取的门点（缓存）。</summary>
    public List<DoorConfig> Doors { get; set; } = new();

    public DateTimeOffset UpdatedAt { get; set; }

    public string EffectiveDeviceGuid =>
        !string.IsNullOrWhiteSpace(DeviceGuidOverride) ? DeviceGuidOverride!.Trim() : AutoDeviceGuid;

    public string DeviceGuidSource =>
        string.IsNullOrWhiteSpace(DeviceGuidOverride) ? "auto" : "manual";

    public bool Configured =>
        !string.IsNullOrWhiteSpace(Mobile) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>
/// 设置存储：内存 + JSON 文件。持有一个可变的 DoorApiOptions 单例引用
/// （IOptions&lt;T&gt;.Value 本身是单例），保存后直接把值同步进去，
/// 这样 DoorGateway 无需改动即可实时读到新账号 / 新门列表。
/// </summary>
public sealed class SettingsStore
{
    private const string FileName = "settings.json";
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private readonly string _file;
    private readonly DoorApiOptions _opt;
    private UserSettings _data = new();

    public SettingsStore(IOptions<DoorApiOptions> options)
    {
        _opt = options.Value;
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        _file = Path.Combine(dir, FileName);
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(_file))
            {
                var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_file), JsonOpt);
                if (loaded != null)
                {
                    _data = loaded;
                    _data.Doors ??= new List<DoorConfig>();
                }
            }
            else
            {
                // 一次性迁移：把旧 appsettings.json 里写死的账号 / 设备 / 门点作为初始种子，
                // 之后一律以设置页保存的 data/settings.json 为准（通用方案不依赖这里）。
                _data = new UserSettings
                {
                    Mobile = _opt.Mobile,
                    Password = _opt.Password,
                    DeviceGuidOverride = string.IsNullOrWhiteSpace(_opt.DeviceGuid) ? null : _opt.DeviceGuid,
                    UserId = _opt.UserId,
                    Doors = new List<DoorConfig>(_opt.Doors),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _data.AutoDeviceGuid = GenerateDeviceGuid(_data.Mobile);
                Persist();
            }
            if (string.IsNullOrWhiteSpace(_data.AutoDeviceGuid))
                _data.AutoDeviceGuid = GenerateDeviceGuid(_data.Mobile);
        }
        catch
        {
            // 读取失败不阻塞服务启动
        }
        ApplyToOptions();
    }

    /// <summary>按账号生成稳定的 16 位 hex 设备 ID；不同账号不同、重启不变。</summary>
    public static string GenerateDeviceGuid(string mobile)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("gmdoor|" + (mobile ?? "")));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>把当前设置同步到网关实际读取的配置对象。</summary>
    private void ApplyToOptions()
    {
        _opt.Mobile = _data.Mobile;
        _opt.Password = _data.Password;
        _opt.DeviceGuid = _data.EffectiveDeviceGuid;
        if (_data.UserId > 0) _opt.UserId = _data.UserId;
        if (_data.Doors.Count > 0) _opt.Doors = new List<DoorConfig>(_data.Doors);
    }

    private void Persist()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_data, JsonOpt)); }
        catch { /* 写盘失败不影响运行 */ }
    }

    /// <summary>保存账号密码与可选的设备 ID。账号变化时重新生成自动设备 ID 并清空缓存。</summary>
    public UserSettings Save(string? mobile, string? password, string? deviceGuidOverride)
    {
        lock (_lock)
        {
            // 空 / 空白一律视为「未提供，沿用已保存值」，避免设置页留空把账号清掉
            var newMobile = string.IsNullOrWhiteSpace(mobile) ? _data.Mobile : mobile!.Trim();
            var oldMobile = _data.Mobile;
            var accountChanged = !string.Equals(oldMobile, newMobile, StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(password)) _data.Password = password!;
            _data.Mobile = newMobile;

            // null = 本次不改动设备 ID；空串 = 显式恢复自动生成；非空 = 手动覆盖
            if (deviceGuidOverride != null)
            {
                var v = deviceGuidOverride.Trim();
                _data.DeviceGuidOverride = string.IsNullOrEmpty(v) ? null : v;
            }

            if (accountChanged)
            {
                _data.AutoDeviceGuid = GenerateDeviceGuid(newMobile);
                _data.UserId = 0;
                _data.Doors = new List<DoorConfig>();
                _opt.UserId = 0;
                _opt.Doors = new List<DoorConfig>();
            }
            if (string.IsNullOrWhiteSpace(_data.AutoDeviceGuid))
                _data.AutoDeviceGuid = GenerateDeviceGuid(newMobile);

            _data.UpdatedAt = DateTimeOffset.UtcNow;
            Persist();
            ApplyToOptions();
            return _data;
        }
    }

    /// <summary>缓存自动拉取到的 user_id 与门列表。</summary>
    public void CacheDoors(long userId, List<DoorConfig> doors)
    {
        lock (_lock)
        {
            if (userId > 0) { _data.UserId = userId; _opt.UserId = userId; }
            if (doors.Count > 0)
            {
                _data.Doors = new List<DoorConfig>(doors);
                _opt.Doors = new List<DoorConfig>(doors);
            }
            _data.UpdatedAt = DateTimeOffset.UtcNow;
            Persist();
        }
    }

    public UserSettings Current
    {
        get { lock (_lock) { return _data; } }
    }

    public bool Configured
    {
        get { lock (_lock) { return _data.Configured; } }
    }

    /// <summary>给前端的脱敏视图：绝不返回密码明文。</summary>
    public object Snapshot()
    {
        lock (_lock)
        {
            var g = _data.EffectiveDeviceGuid;
            return new
            {
                configured = _data.Configured,
                mobile = MaskMobile(_data.Mobile),
                passwordSet = !string.IsNullOrEmpty(_data.Password),
                deviceGuid = string.IsNullOrEmpty(g) ? "" : g[..Math.Min(4, g.Length)] + "…" + g[^Math.Min(4, g.Length)..],
                deviceGuidSource = _data.DeviceGuidSource,
                autoDeviceGuid = _data.AutoDeviceGuid,
                hasManualDeviceGuid = !string.IsNullOrWhiteSpace(_data.DeviceGuidOverride),
                userId = _data.UserId,
                doorCount = _data.Doors.Count,
                doors = _data.Doors.ConvertAll(d => new { id = d.Id, name = d.Name }),
                updatedAt = _data.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
    }

    private static string MaskMobile(string m) =>
        m.Length >= 7 ? m[..3] + "****" + m[^4..] : (string.IsNullOrEmpty(m) ? "" : "****");
}
