namespace TelegramPanel.Web.Services;

/// <summary>两个登录入口共享窗口，避免永久锁定管理员；全局窗口同时限制哈希计算量。</summary>
public sealed class AdminLoginLimiter(TimeProvider clock)
{
    private readonly object _sync = new();
    private readonly Queue<(DateTimeOffset At, string Address)> _attempts = new();

    public bool TryAcquire(string address, out int retryAfter)
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            while (_attempts.TryPeek(out var first) && now - first.At >= TimeSpan.FromMinutes(1))
                _attempts.Dequeue();
            var sameAddress = _attempts.Where(x => x.Address == address).ToArray();
            var blocked = _attempts.Count >= 30 || sameAddress.Length >= 5;
            var oldest = _attempts.Count >= 30 ? _attempts.Peek().At
                : sameAddress.Length > 0 ? sameAddress[0].At : now;
            retryAfter = blocked ? Math.Max(1, (int)Math.Ceiling((oldest.AddMinutes(1) - now).TotalSeconds)) : 0;
            if (blocked) return false;
            _attempts.Enqueue((now, address));
            return true;
        }
    }
}
