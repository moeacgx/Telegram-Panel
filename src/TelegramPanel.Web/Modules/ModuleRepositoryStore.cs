using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace TelegramPanel.Web.Modules;

public sealed record ModuleRepositoryInput(string Name, string Kind, string Location, string? Ref = "main", string? Token = null, bool ClearToken = false);
public sealed record ModuleRepositoryInfo(string Id, string Name, string Kind, string Location, string Ref, bool HasToken);
public sealed record StoredModuleRepository(string Id, string Name, string Kind, string Location, string Ref, string? ProtectedToken)
{
    public ModuleRepositoryInfo ToInfo() => new(Id, Name, Kind, Location, Ref, !string.IsNullOrEmpty(ProtectedToken));
}

public sealed class ModuleRepositoryStore(ModuleLayout layout, IDataProtectionProvider protection)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDataProtector _protector = protection.CreateProtector("TelegramPanel.ModuleRepositories.v1");
    private readonly string _path = Path.Combine(layout.Root, "repositories.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<ModuleRepositoryInfo>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return (await ReadAsync(ct)).Select(x => x.ToInfo()).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<StoredModuleRepository> GetAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await ReadAsync(ct)).SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("仓库不存在"); }
        finally { _gate.Release(); }
    }

    public string? ReadToken(StoredModuleRepository repository)
    {
        if (string.IsNullOrEmpty(repository.ProtectedToken)) return null;
        try { return _protector.Unprotect(repository.ProtectedToken); }
        catch (System.Security.Cryptography.CryptographicException)
        { throw new InvalidOperationException("仓库令牌无法解密，请重新保存令牌，并检查 DataProtection 密钥持久化"); }
    }

    public async Task<ModuleRepositoryInfo> SaveAsync(string? id, ModuleRepositoryInput input, CancellationToken ct)
    {
        var name = (input.Name ?? "").Trim();
        var kind = (input.Kind ?? "").Trim();
        var location = (input.Location ?? "").Trim();
        var branch = string.IsNullOrWhiteSpace(input.Ref) ? "main" : input.Ref.Trim();
        if (name.Length is 0 or > 100) throw new InvalidOperationException("仓库名称须为 1～100 个字符");
        if (kind == "github")
        {
            if (!Regex.IsMatch(location, @"^[a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+$") || location.Split('/')[1] is "." or "..")
                throw new InvalidOperationException("GitHub 仓库请填写 owner/repo");
            if (branch.Length > 200 || branch.Any(char.IsControl)) throw new InvalidOperationException("分支或标签无效");
        }
        else if (kind == "https") ModuleRepositoryTransport.ValidateUrl(location);
        else throw new InvalidOperationException("仓库类型必须为 github 或 https");
        if (input.Token?.Length > 4096 || input.Token?.Any(char.IsControl) == true)
            throw new InvalidOperationException("仓库令牌格式无效");

        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadAsync(ct);
            var previous = id == null ? null : items.SingleOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("仓库不存在");
            if (id == null && items.Count >= 50) throw new InvalidOperationException("最多添加 50 个仓库");
            // 改变目标时不能将旧令牌转交给另一个仓库。
            var targetChanged = previous != null && (previous.Kind != kind || previous.Location != location);
            var secret = input.ClearToken || targetChanged ? null : previous?.ProtectedToken;
            if (!string.IsNullOrWhiteSpace(input.Token)) secret = _protector.Protect(input.Token.Trim());
            var item = new StoredModuleRepository(id ?? Guid.NewGuid().ToString("N"), name, kind, location, branch, secret);
            items.RemoveAll(x => x.Id == item.Id);
            items.Add(item);
            await WriteAsync(items, ct);
            return item.ToInfo();
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadAsync(ct);
            if (items.RemoveAll(x => x.Id == id) == 0) throw new InvalidOperationException("仓库不存在");
            await WriteAsync(items, ct);
        }
        finally { _gate.Release(); }
    }

    internal static Uri IndexUri(StoredModuleRepository item) => item.Kind == "github"
        ? new Uri($"https://api.github.com/repos/{item.Location}/contents/index.json?ref={Uri.EscapeDataString(item.Ref)}")
        : new Uri(item.Location);

    private async Task<List<StoredModuleRepository>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return [new("official", "官方模块仓库", "github", "moeacgx/Telegram-Panel-Modules", "main", null)];
        return JsonSerializer.Deserialize<List<StoredModuleRepository>>(await File.ReadAllTextAsync(_path, ct), Json)
            ?? throw new InvalidOperationException("仓库配置无效，请从备份恢复 repositories.json");
    }

    private async Task WriteAsync(List<StoredModuleRepository> items, CancellationToken ct)
    {
        Directory.CreateDirectory(layout.Root);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(items, Json), ct);
        File.Move(temporary, _path, overwrite: true);
    }
}
