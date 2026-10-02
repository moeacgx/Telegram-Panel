using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Modules;

public sealed record RepositoryModule(string Id, string Name, string Version, string DownloadUrl, string Sha256, string? Description = null);
public sealed record ModuleRepositoryIndex(int SchemaVersion, List<RepositoryModule> Modules);
public sealed record RepositoryInstallRequest(string ModuleId, string Version, string Sha256, bool ActivateAndEnable, bool AutoRestart);

public sealed class ModuleRepositoryService(ModuleRepositoryStore store, ModuleRepositoryTransport transport, ModuleInstallerService installer)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ModuleRepositoryIndex> GetIndexAsync(string id, CancellationToken ct)
    {
        var repository = await store.GetAsync(id, ct);
        return await GetIndexAsync(repository, ct);
    }

    private async Task<ModuleRepositoryIndex> GetIndexAsync(StoredModuleRepository repository, CancellationToken ct)
    {
        var bytes = await transport.DownloadAsync(ModuleRepositoryStore.IndexUri(repository), repository,
            store.ReadToken(repository), 2 * 1024 * 1024, index: true, ct);
        return ParseIndex(bytes);
    }

    internal static ModuleRepositoryIndex ParseIndex(byte[] bytes)
    {
        var index = JsonSerializer.Deserialize<ModuleRepositoryIndex>(bytes, Json);
        if (index == null || index.SchemaVersion != 1 || index.Modules == null || index.Modules.Count > 1000)
            throw new InvalidOperationException("仓库目录格式无效：要求 schemaVersion=1，modules 最多 1000 项");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in index.Modules)
        {
            if (module == null || string.IsNullOrWhiteSpace(module.Id) || module.Id.Length > 100
                || !Regex.IsMatch(module.Id, @"^[a-zA-Z0-9][a-zA-Z0-9._-]*$")
                || !ModuleInstallerService.IsSafeVersion(module.Version) || string.IsNullOrWhiteSpace(module.Name)
                || module.Name.Length > 100 || module.Description?.Length > 2000
                || !Regex.IsMatch(module.Sha256 ?? "", "^[a-fA-F0-9]{64}$")
                || !seen.Add($"{module.Id}/{module.Version}"))
                throw new InvalidOperationException("仓库条目无效或重复：请检查 id、名称、版本和 SHA-256");
            ModuleRepositoryTransport.ValidateUrl(module.DownloadUrl ?? "");
        }
        return index;
    }

    public async Task<InstallResult> InstallAsync(string id, RepositoryInstallRequest request, CancellationToken ct)
    {
        var repository = await store.GetAsync(id, ct);
        var index = await GetIndexAsync(repository, ct);
        var module = index.Modules.SingleOrDefault(x => x.Id == request.ModuleId && x.Version == request.Version)
            ?? throw new InvalidOperationException("该模块版本已不在仓库中，请刷新目录");
        if (!string.Equals(module.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("仓库条目已变更，请刷新目录后重新确认安装");
        var bytes = await transport.DownloadAsync(new Uri(module.DownloadUrl), repository, store.ReadToken(repository),
            50 * 1024 * 1024, index: false, ct);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), module.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("模块包 SHA-256 校验失败，未安装");
        using var stream = new MemoryStream(bytes, writable: false);
        return await installer.InstallFromRepositoryAsync(stream, module.Id, module.Version, request.ActivateAndEnable);
    }
}
