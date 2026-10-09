using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 出站代理仓储实现。
/// </summary>
public class OutboundProxyRepository : Repository<OutboundProxy>, IOutboundProxyRepository
{
    public OutboundProxyRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<OutboundProxy>> ListAsync(
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(x => x.WarpProfile)
            .AsQueryable();

        if (!includeDisabled)
            query = query.Where(x => x.IsEnabled);

        return await query
            .OrderByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<OutboundProxy?> GetAsync(
        int id,
        bool includeAccounts = false,
        CancellationToken cancellationToken = default)
    {
        IQueryable<OutboundProxy> query = _dbSet.Include(x => x.WarpProfile);
        if (includeAccounts)
        {
            query = query.Include(x => x.Accounts);
        }

        return await query.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await SaveChangesWithSqliteLockRetryAsync(cancellationToken);
    }

    public Task<bool> AnyAccountUsesAsync(
        int proxyId,
        CancellationToken cancellationToken = default)
    {
        return _context.Accounts
            .AsNoTracking()
            .AnyAsync(x => x.ProxyId == proxyId, cancellationToken);
    }

    public async Task<int> BindAccountsAsync(
        IEnumerable<int> accountIds,
        int? proxyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);

        var ids = accountIds
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return 0;

        OutboundProxy? proxy = null;
        if (proxyId.HasValue)
        {
            proxy = await _dbSet.FirstOrDefaultAsync(
                x => x.Id == proxyId.Value,
                cancellationToken);
            if (proxy == null)
                throw new KeyNotFoundException($"代理 {proxyId.Value} 不存在");
            if (proxy.ManagedWgcfProfile != null)
                throw new InvalidOperationException("受管 WireGuard 绑定必须通过代理管理业务锁校验，禁止仓储捷径写入");
        }

        var accounts = await _context.Accounts
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var changedCount = 0;
        foreach (var account in accounts)
        {
            if (account.ProxyId == proxyId)
                continue;

            account.ProxyId = proxyId;
            changedCount++;
        }

        if (proxy != null && accounts.Count > 0 && proxy.FirstBoundAtUtc == null)
        {
            var now = DateTime.UtcNow;
            proxy.FirstBoundAtUtc = now;
            proxy.UpdatedAtUtc = now;
        }

        // 账号绑定与首次绑定时间必须在同一个 SaveChanges 中提交。
        await SaveChangesWithSqliteLockRetryAsync(cancellationToken);
        return changedCount;
    }

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(
        int proxyId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Accounts
            .AsNoTracking()
            .Include(x => x.Category)
            .Where(x => x.ProxyId == proxyId)
            .OrderByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
    }
}
