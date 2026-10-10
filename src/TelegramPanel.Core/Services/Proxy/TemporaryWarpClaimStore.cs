using System.Collections.Concurrent;

namespace TelegramPanel.Core.Services.Proxy;

/// <summary>
/// 记录当前进程正在创建或使用的临时 WARP 请求，防止后台孤儿扫描误删活跃资源。
/// </summary>
public sealed class TemporaryWarpClaimStore
{
    private readonly ConcurrentDictionary<string, int> _requestClaims =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _managedClaims = new(StringComparer.Ordinal);

    public ManagedProfileClaim ClaimManagedProfile(string profile)
    {
        var token = Guid.NewGuid().ToString("N");
        if (!_managedClaims.TryAdd(profile, token))
            throw new InvalidOperationException("轻量 WARP 正被其它首次连接或绑定流程占用，请稍后重试");
        return new ManagedProfileClaim(this, profile, token);
    }

    public bool IsManagedProfileClaimed(string profile) => _managedClaims.ContainsKey(profile);

    public bool OwnsManagedProfile(string profile, string? token) => token != null
        && _managedClaims.TryGetValue(profile, out var current) && current == token;

    public sealed class ManagedProfileClaim : IDisposable
    {
        private TemporaryWarpClaimStore? _owner;
        private readonly string _profile;
        public string Token { get; }

        internal ManagedProfileClaim(TemporaryWarpClaimStore owner, string profile, string token)
        {
            _owner = owner;
            _profile = profile;
            Token = token;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null)
                ((ICollection<KeyValuePair<string, string>>)owner._managedClaims)
                    .Remove(new KeyValuePair<string, string>(_profile, Token));
        }
    }

    public IDisposable ClaimRequest(string requestId)
    {
        var key = Normalize(requestId);
        _requestClaims.AddOrUpdate(key, 1, static (_, count) => checked(count + 1));
        return new ClaimHandle(this, key);
    }

    public bool OwnsRequest(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        return _requestClaims.ContainsKey(requestId.Trim());
    }

    private void Release(string key)
    {
        while (_requestClaims.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                if (((ICollection<KeyValuePair<string, int>>)_requestClaims)
                    .Remove(new KeyValuePair<string, int>(key, count)))
                {
                    return;
                }

                continue;
            }

            if (_requestClaims.TryUpdate(key, count - 1, count))
                return;
        }
    }

    private static string Normalize(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("WARP 请求 ID 不能为空", nameof(requestId));

        return requestId.Trim();
    }

    private sealed class ClaimHandle : IDisposable
    {
        private TemporaryWarpClaimStore? _owner;
        private readonly string _key;

        public ClaimHandle(TemporaryWarpClaimStore owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_key);
        }
    }
}
