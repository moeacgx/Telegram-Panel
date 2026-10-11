using Microsoft.EntityFrameworkCore;
using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Proxy;

public sealed partial class ProxyManagementService
{
    public async Task RenameAsync(int id, string? name, CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80)
            throw new ArgumentException("代理名称不能为空且不能超过 80 个字符", nameof(name));

        // 名称属于展示元数据，与启停、绑定和删除共用变更锁，但不变更连接状态。
        await using var lease = await AcquireMutationLeaseAsync(cancellationToken);
        var proxy = await _db.OutboundProxies.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("代理不存在");
        proxy.Name = name;
        proxy.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public int? GetEnabledGlobalProxyId() =>
        _configuration == null
            ? null
            : GlobalTelegramProxyConfiguration.GetSelectedProxyId(_configuration);

    public bool IsEnabledGlobalProxy(int proxyId) =>
        proxyId > 0 && GetEnabledGlobalProxyId() == proxyId;

    public async Task<int> GetGlobalFallbackAccountCountAsync(
        CancellationToken cancellationToken = default)
    {
        if (!GetEnabledGlobalProxyId().HasValue)
            return 0;

        return await _db.Accounts
            .AsNoTracking()
            .CountAsync(x => x.ProxyId == null && x.UseGlobalProxy, cancellationToken);
    }

    public async Task<IReadOnlyList<ProxyCategory>> ListCategoriesAsync(
        CancellationToken cancellationToken = default) =>
        await _db.ProxyCategories
            .AsNoTracking()
            .Include(x => x.Proxies)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public async Task<ProxyCategory> CreateCategoryAsync(
        ProxyCategoryInput input,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCategoryInput(input);
        await MutationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureCategoryNameAvailableAsync(normalized.Name!, null, cancellationToken);
            var now = DateTime.UtcNow;
            var category = new ProxyCategory
            {
                Name = normalized.Name!,
                Color = normalized.Color,
                Description = normalized.Description,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.ProxyCategories.Add(category);
            await _db.SaveChangesAsync(cancellationToken);
            return category;
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task<ProxyCategory> UpdateCategoryAsync(
        int id,
        ProxyCategoryInput input,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCategoryInput(input);
        await MutationLock.WaitAsync(cancellationToken);
        try
        {
            var category = await _db.ProxyCategories
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("代理分类不存在");
            await EnsureCategoryNameAvailableAsync(normalized.Name!, id, cancellationToken);
            category.Name = normalized.Name!;
            category.Color = normalized.Color;
            category.Description = normalized.Description;
            category.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return category;
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task DeleteCategoryAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await MutationLock.WaitAsync(cancellationToken);
        try
        {
            var category = await _db.ProxyCategories
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("代理分类不存在");
            _db.ProxyCategories.Remove(category);
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            MutationLock.Release();
        }
    }

    public async Task<int> SetCategoriesAsync(
        IReadOnlyCollection<int> proxyIds,
        int? categoryId,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeProxyIds(proxyIds);
        categoryId = NormalizeCategoryId(categoryId);
        await MutationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureCategoryExistsAsync(categoryId, cancellationToken);
            var proxies = await _db.OutboundProxies
                .Where(x => ids.Contains(x.Id))
                .ToListAsync(cancellationToken);
            if (proxies.Count != ids.Length)
                throw new KeyNotFoundException("部分代理不存在，请刷新列表后重试");

            foreach (var proxy in proxies)
            {
                proxy.CategoryId = categoryId;
                proxy.UpdatedAtUtc = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(cancellationToken);
            return proxies.Count;
        }
        finally
        {
            MutationLock.Release();
        }
    }

    private async Task EnsureCategoryExistsAsync(
        int? categoryId,
        CancellationToken cancellationToken)
    {
        if (categoryId is not > 0)
            return;
        var exists = await _db.ProxyCategories
            .AsNoTracking()
            .AnyAsync(x => x.Id == categoryId.Value, cancellationToken);
        if (!exists)
            throw new KeyNotFoundException("代理分类不存在");
    }

    private static int? NormalizeCategoryId(int? categoryId) =>
        categoryId is > 0 ? categoryId : null;

    private async Task EnsureCategoryNameAvailableAsync(
        string name,
        int? exceptId,
        CancellationToken cancellationToken)
    {
        var exists = await _db.ProxyCategories
            .AsNoTracking()
            .AnyAsync(
                x => x.Id != exceptId && x.Name.ToLower() == name.ToLower(),
                cancellationToken);
        if (exists)
            throw new InvalidOperationException("代理分类名称已存在");
    }

    private static ProxyCategoryInput NormalizeCategoryInput(ProxyCategoryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 100)
            throw new ArgumentException("代理分类名称不能为空且不能超过 100 个字符");
        var color = NormalizeCatalogText(input.Color, 20, "分类颜色");
        var description = NormalizeCatalogText(input.Description, 500, "分类说明");
        return input with { Name = name, Color = color, Description = description };
    }

    private static string? NormalizeCatalogText(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (value.Length > maxLength)
            throw new ArgumentException($"{field}不能超过 {maxLength} 个字符");
        return value;
    }

    public static int[] NormalizeProxyIds(IReadOnlyCollection<int>? proxyIds)
    {
        var ids = (proxyIds ?? Array.Empty<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            throw new ArgumentException("请先选择代理");
        if (ids.Length > 500)
            throw new ArgumentException("单次最多处理 500 个代理");
        return ids;
    }
}
