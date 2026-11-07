using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GuangmingDoorApi;

/// <summary>一条开门记录。不含任何账号、密码、token、Basic 凭据。</summary>
public sealed class OpenLogEntry
{
    public string Id { get; set; } = "";
    /// <summary>本地时间（UTC+8），yyyy-MM-dd HH:mm:ss。</summary>
    public string Time { get; set; } = "";
    public bool Ok { get; set; }
    public string Result { get; set; } = "";
    public string? Message { get; set; }
    public string? DoorId { get; set; }
    public string? DoorName { get; set; }
    public int HttpStatus { get; set; }
    public bool Relogged { get; set; }
    public int ReloginAttempts { get; set; }
    public long ElapsedMs { get; set; }
    public bool Sent { get; set; }
    public string? Hint { get; set; }
}

/// <summary>持久化内容：累计统计 + 最近明细。</summary>
public sealed class OpenLogData
{
    public int TotalEver { get; set; }
    public int SuccessEver { get; set; }
    public int FailEver { get; set; }
    public List<OpenLogEntry> Items { get; set; } = new();
}

/// <summary>
/// 开门记录存储：内存 + JSON 文件持久化，进程重启不丢。
/// 文件位于工作目录下 data/open-log.json；读写失败不影响开门主流程。
/// </summary>
public sealed class OpenLogStore
{
    /// <summary>最多保留的明细条数，避免文件无限增长。</summary>
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private readonly string _file;
    private OpenLogData _data = new();

    public OpenLogStore()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        _file = Path.Combine(dir, "open-log.json");
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(_file))
            {
                var loaded = JsonSerializer.Deserialize<OpenLogData>(File.ReadAllText(_file), JsonOpt);
                if (loaded != null)
                {
                    _data = loaded;
                    _data.Items ??= new List<OpenLogEntry>();
                }
            }
        }
        catch
        {
            // 记录文件损坏或不可读：不影响服务启动与开门
        }
    }

    public OpenLogEntry Add(Action<OpenLogEntry> fill)
    {
        var entry = new OpenLogEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss")
        };
        fill(entry);

        lock (_lock)
        {
            _data.TotalEver++;
            if (entry.Ok) _data.SuccessEver++; else _data.FailEver++;

            _data.Items.Insert(0, entry);
            if (_data.Items.Count > MaxEntries)
                _data.Items.RemoveRange(MaxEntries, _data.Items.Count - MaxEntries);

            try
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(_data, JsonOpt));
            }
            catch
            {
                // 写盘失败（如目录只读）：内存记录仍有效，不阻断开门响应
            }
        }
        return entry;
    }

    public (int TotalEver, int SuccessEver, int FailEver, int Retained, string? LastTime, List<OpenLogEntry> Items) Query(int take)
    {
        lock (_lock)
        {
            var n = Math.Clamp(take, 1, MaxEntries);
            return (_data.TotalEver, _data.SuccessEver, _data.FailEver,
                    _data.Items.Count,
                    _data.Items.Count > 0 ? _data.Items[0].Time : null,
                    _data.Items.Take(n).ToList());
        }
    }

    private static bool Hit(string? text, string keyword) =>
        !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>完整记录页：按日期 / 结果 / 关键词筛选后分页。</summary>
    public (int TotalEver, int SuccessEver, int FailEver, int Retained,
            int TotalMatched, int MatchedSuccess, int MatchedFail,
            int Page, int PageSize, int PageCount, List<OpenLogEntry> Items)
        QueryPaged(string? from, string? to, string? result, string? keyword, int page, int pageSize)
    {
        lock (_lock)
        {
            IEnumerable<OpenLogEntry> q = _data.Items;

            if (!string.IsNullOrWhiteSpace(from))
            {
                var f = from.Trim();
                q = q.Where(x => string.CompareOrdinal(x.Time.Length >= 10 ? x.Time[..10] : "", f) >= 0);
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                var t = to.Trim();
                q = q.Where(x => string.CompareOrdinal(x.Time.Length >= 10 ? x.Time[..10] : "", t) <= 0);
            }
            if (result == "success") q = q.Where(x => x.Ok);
            else if (result == "fail") q = q.Where(x => !x.Ok);

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var k = keyword.Trim();
                q = q.Where(x => Hit(x.Message, k) || Hit(x.DoorName, k) || Hit(x.DoorId, k)
                                 || Hit(x.Result, k) || Hit(x.Hint, k) || Hit(x.Time, k));
            }

            var matched = q.ToList();
            var ps = Math.Clamp(pageSize, 1, 200);
            var totalMatched = matched.Count;
            var pageCount = Math.Max(1, (int)Math.Ceiling(totalMatched / (double)ps));
            var p = Math.Clamp(page, 1, pageCount);
            var items = matched.Skip((p - 1) * ps).Take(ps).ToList();

            return (_data.TotalEver, _data.SuccessEver, _data.FailEver, _data.Items.Count,
                    totalMatched, matched.Count(x => x.Ok), matched.Count(x => !x.Ok),
                    p, ps, pageCount, items);
        }
    }
}
