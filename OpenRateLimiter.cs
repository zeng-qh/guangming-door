using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace GuangmingDoorApi;

/// <summary>
/// 开门频率限制：滑动时间窗口内最多允许 N 次开门，防止误触 / 连点。
/// 状态保存在内存，进程重启自动清零；所有真实开门都经 DoorGateway.OpenAsync，故统一在此拦截。
/// </summary>
public sealed class OpenRateLimiter
{
    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _hits = new();
    private readonly int _max;
    private readonly TimeSpan _window;

    public OpenRateLimiter(IOptions<DoorApiOptions> options)
    {
        var o = options.Value;
        _max = Math.Max(1, o.RateLimitMax);
        _window = TimeSpan.FromSeconds(Math.Max(1, o.RateLimitWindowSeconds));
    }

    /// <summary>
    /// 尝试获取一次开门配额。
    /// </summary>
    /// <param name="retryAfterSeconds">被拒绝时距下次可开门的秒数。</param>
    /// <param name="used">当前窗口内已用次数。</param>
    /// <returns>true=允许；false=超过频率限制。</returns>
    public bool TryAcquire(out int retryAfterSeconds, out int used)
    {
        retryAfterSeconds = 0;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _hits.RemoveAll(t => now - t >= _window);
            used = _hits.Count;
            if (_hits.Count >= _max)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((_window - (now - _hits[0])).TotalSeconds));
                return false;
            }
            _hits.Add(now);
            used = _hits.Count;
            return true;
        }
    }
}
