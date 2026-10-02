using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Modules;

public sealed class ModuleInstallerService
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly ModuleRegistry _registry;
    private readonly ModuleLayout _layout;
    private readonly ModuleStateStore _stateStore;
    private readonly BuiltIn.BuiltInModuleCatalog _builtInCatalog;
    private readonly string _hostVersion;

    private async Task<T> MutateAsync<T>(Func<Task<T>> action)
    {
        await _mutationGate.WaitAsync();
        try { return await action(); }
        finally { _mutationGate.Release(); }
    }

    public Task<InstallResult> InstallAsync(Stream stream, string fileName, bool enableAfterInstall = false) =>
        MutateAsync(() => InstallCoreAsync(stream, fileName, enableAfterInstall));
    public Task<OperationResult> EnableAsync(string id, string? version = null) => MutateAsync(() => EnableCoreAsync(id, version));
    public Task<OperationResult> DisableAsync(string id) => MutateAsync(() => DisableCoreAsync(id));
    public Task<OperationResult> SetActiveVersionAsync(string id, string version) => MutateAsync(() => SetActiveVersionCoreAsync(id, version));
    public Task<OperationResult> RemoveModuleAsync(string id) => MutateAsync(() => RemoveModuleCoreAsync(id));
    public Task<OperationResult> RemoveModuleVersionAsync(string id, string version) => MutateAsync(() => RemoveModuleVersionCoreAsync(id, version));
    public Task<OperationResult> PruneOldVersionsAsync(string id) => MutateAsync(() => PruneOldVersionsCoreAsync(id));

    public Task<InstallResult> InstallFromRepositoryAsync(Stream stream, string id, string version, bool activate) => MutateAsync(async () =>
    {
        var result = await InstallCoreAsync(stream, $"{id}-{version}.tpm", expectedId: id, expectedVersion: version);
        if (!result.Success) return result;
        if (activate)
        {
            // Enable 在校验兼容性与依赖成功后才保存活动版本，失败不破坏原版本。
            var enabled = await EnableCoreAsync(id, version);
            if (!enabled.Success) return result with { Message = $"模块已安装，但未启用：{enabled.Message}。原有启用状态保持不变。" };
        }
        return result with { Message = activate ? "模块已安装并启用，重启后加载" : "模块已安装，尚未切换启用版本" };
    });

    private HashSet<string> ProtectedVersions(ModuleStateItem item) => new(
        new[] { item.ActiveVersion, item.LastGoodVersion }
            .Concat(_registry.Modules.Where(m => string.Equals(m.Id, item.Id, StringComparison.OrdinalIgnoreCase)).Select(m => m.Version))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim().TrimEnd('.')), StringComparer.OrdinalIgnoreCase);

    internal static bool IsSafeVersion(string? version) => version != null
        && Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$") && SemVer.TryParse(version, out _);

    public Task<IReadOnlyList<ModulePrunePreview>> GetPrunePreviewAsync() => MutateAsync(async () =>
        (IReadOnlyList<ModulePrunePreview>)(await _stateStore.LoadAsync()).Modules.Where(m => !m.BuiltIn)
            .Select(m => new ModulePrunePreview(m.Id,
                m.InstalledVersions.Distinct(StringComparer.Ordinal).Where(v => !ProtectedVersions(m).Contains(v)).ToList(),
                ProtectedVersions(m).ToList())).Where(m => m.Versions.Count > 0).ToList());

    public Task<ModulePruneResult> PruneAllOldVersionsAsync() => MutateAsync(async () =>
    {
        var state = await _stateStore.LoadAsync();
        var results = new List<ModulePruneItemResult>();
        foreach (var item in state.Modules.Where(m => !m.BuiltIn))
        {
            foreach (var version in item.InstalledVersions.Distinct(StringComparer.Ordinal).Where(v => !ProtectedVersions(item).Contains(v)))
            {
                var result = await RemoveModuleVersionCoreAsync(item.Id, version);
                results.Add(new(item.Id, version, result.Success, result.Success ? "已删除" : result.Message));
            }
        }
        return new ModulePruneResult(results.All(r => r.Success), results.Count(r => r.Success), results);
    });

    public ModuleInstallerService(ModuleLayout layout, ModuleStateStore stateStore, BuiltIn.BuiltInModuleCatalog builtInCatalog, string hostVersion, ModuleRegistry? registry = null)
    {
        _registry = registry ?? new ModuleRegistry();
        _layout = layout;
        _stateStore = stateStore;
        _builtInCatalog = builtInCatalog;
        _hostVersion = hostVersion;
    }

    public async Task<IReadOnlyList<ModuleOverview>> GetOverviewAsync()
    {
        var state = await _stateStore.LoadAsync();
        var list = new List<ModuleOverview>();

        foreach (var item in state.Modules.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            var active = (item.ActiveVersion ?? "").Trim();
            ModuleManifest? manifest = null;
            string? manifestError = null;

            if (!string.IsNullOrWhiteSpace(active))
            {
                try
                {
                    if (item.BuiltIn)
                    {
                        if (_builtInCatalog.TryGetManifest(item.Id, out var m))
                            manifest = m;
                        else
                            manifestError = "内置模块未注册";
                    }
                    else
                    {
                        var manifestPath = Path.Combine(_layout.InstalledDir, item.Id, active, "manifest.json");
                        if (File.Exists(manifestPath))
                        {
                            var json = await File.ReadAllTextAsync(manifestPath);
                            manifest = JsonSerializer.Deserialize<ModuleManifest>(json);
                        }
                    }
                }
                catch (Exception ex)
                {
                    manifestError = ex.Message;
                }
            }

            list.Add(new ModuleOverview(
                Id: item.Id,
                Enabled: item.Enabled,
                ActiveVersion: item.ActiveVersion,
                LastGoodVersion: item.LastGoodVersion,
                InstalledVersions: item.InstalledVersions.OrderByDescending(x => x, StringComparer.Ordinal).ToList(),
                Manifest: manifest,
                ManifestError: manifestError,
                BuiltIn: item.BuiltIn));
        }

        return list;
    }

    private async Task<InstallResult> InstallCoreAsync(Stream packageStream, string fileName, bool enableAfterInstall = false, string? expectedId = null, string? expectedVersion = null)
    {
        if (packageStream == null)
            throw new ArgumentNullException(nameof(packageStream));

        fileName = (fileName ?? "").Trim();
        if (fileName.Length == 0)
            fileName = "module.tpm";

        var installId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(_layout.StagingDir, installId);
        Directory.CreateDirectory(staging);

        try
        {
            using var buffer = new MemoryStream();
            var copyBuffer = new byte[81920];
            int read;
            while ((read = await packageStream.ReadAsync(copyBuffer)) != 0)
            {
                if (buffer.Length + read > 50L * 1024 * 1024)
                    return InstallResult.Fail("模块包不能超过 50MB");
                buffer.Write(copyBuffer, 0, read);
            }
            buffer.Position = 0;

            // 1) 解压到 staging
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
            {
                var extractError = ExtractZipToDirectorySafe(zip, staging);
                if (!string.IsNullOrWhiteSpace(extractError))
                    return InstallResult.Fail(extractError);
            }

            // 2) 读取 manifest
            var manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                // 兼容用户把“文件夹整体压缩”的情况：<root>/<folder>/manifest.json
                TryPromoteSingleRootFolder(staging);
            }

            if (!File.Exists(manifestPath))
                return InstallResult.Fail("缺少 manifest.json");

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var manifest = JsonSerializer.Deserialize<ModuleManifest>(manifestJson);
            if (manifest == null)
                return InstallResult.Fail("manifest.json 格式无效");

            NormalizeManifest(manifest);
            var validateError = ValidateManifest(manifest);
            if (!string.IsNullOrWhiteSpace(validateError))
                return InstallResult.Fail(validateError);

            if ((expectedId != null && manifest.Id != expectedId) || (expectedVersion != null && manifest.Version != expectedVersion))
                return InstallResult.Fail("模块包的 ID 或版本与仓库目录不一致，未安装");
            if (_builtInCatalog.TryGetManifest(manifest.Id, out _))
                return InstallResult.Fail("不能覆盖内置模块");
            var existingState = await _stateStore.LoadAsync();
            if (existingState.Modules.Any(m => string.Equals(m.Id, manifest.Id, StringComparison.OrdinalIgnoreCase) && m.Id != manifest.Id))
                return InstallResult.Fail("模块 ID 与已安装模块存在大小写冲突");

            var hostCompatError = CheckHostCompatibility(manifest);
            if (!string.IsNullOrWhiteSpace(hostCompatError))
                return InstallResult.Fail(hostCompatError);

            // 3) 基础结构校验（entry assembly）
            var entryAssembly = (manifest.Entry?.Assembly ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(entryAssembly))
            {
                var entryPath = Path.Combine(staging, "lib", entryAssembly);
                if (!File.Exists(entryPath))
                    return InstallResult.Fail($"入口程序集不存在：lib/{entryAssembly}");
            }

            // 4) 写入 packages/<id>/<version>.tpm（便于回滚/留档）
            var packageDir = Path.Combine(_layout.PackagesDir, manifest.Id);
            Directory.CreateDirectory(packageDir);
            var packagePath = Path.Combine(packageDir, $"{manifest.Version}.tpm");
            if (!File.Exists(packagePath))
                await File.WriteAllBytesAsync(packagePath, buffer.ToArray());

            // 5) 安装目录：installed/<id>/<version>
            var targetDir = Path.Combine(_layout.InstalledDir, manifest.Id, manifest.Version);
            if (Directory.Exists(targetDir))
                return InstallResult.Fail("该版本已安装");

            Directory.CreateDirectory(Path.Combine(_layout.InstalledDir, manifest.Id));
            Directory.Move(staging, targetDir);

            // 6) 更新 state
            var state = await _stateStore.LoadAsync();
            var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, manifest.Id, StringComparison.Ordinal));
            if (item == null)
            {
                item = new ModuleStateItem { Id = manifest.Id, Enabled = false, BuiltIn = false };
                state.Modules.Add(item);
            }

            item.InstalledVersions ??= new List<string>();
            if (!item.InstalledVersions.Contains(manifest.Version, StringComparer.Ordinal))
                item.InstalledVersions.Add(manifest.Version);

            item.ActiveVersion ??= manifest.Version;
            if (enableAfterInstall)
                item.Enabled = true;

            await _stateStore.SaveAsync(state);

            return InstallResult.Ok(manifest.Id, manifest.Version);
        }
        catch (Exception ex)
        {
            return InstallResult.Fail(ex.Message);
        }
        finally
        {
            // 如果 staging 还在（未 move），清理
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<OperationResult> EnableCoreAsync(string id, string? version = null)
    {
        id = (id ?? "").Trim();
        version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        if (id.Length == 0)
            return OperationResult.Fail("id 不能为空");

        var state = await _stateStore.LoadAsync();
        var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        if (item == null)
            return OperationResult.Fail("模块未安装");

        if (item.BuiltIn)
        {
            // 内置模块版本跟随宿主，不从 installed/<id>/<ver>/manifest.json 读取
            item.ActiveVersion = _hostVersion;
            item.InstalledVersions ??= new List<string>();
            if (!item.InstalledVersions.Contains(_hostVersion, StringComparer.Ordinal))
                item.InstalledVersions.Add(_hostVersion);

            if (!_builtInCatalog.TryGetManifest(id, out var builtInManifest))
                return OperationResult.Fail("内置模块未注册");

            var builtInHostCompatError = CheckHostCompatibility(builtInManifest);
            if (!string.IsNullOrWhiteSpace(builtInHostCompatError))
                return OperationResult.Fail(builtInHostCompatError);

            var builtInDepError = await CheckDependenciesAsync(state, builtInManifest);
            if (!string.IsNullOrWhiteSpace(builtInDepError))
                return OperationResult.Fail(builtInDepError);

            item.Enabled = true;
            await _stateStore.SaveAsync(state);
            return OperationResult.Ok();
        }

        if (!string.IsNullOrWhiteSpace(version))
            item.ActiveVersion = version;

        if (string.IsNullOrWhiteSpace(item.ActiveVersion))
            return OperationResult.Fail("未选择启用的版本");

        var manifest = await TryLoadManifestAsync(id, item.ActiveVersion);
        if (manifest == null)
            return OperationResult.Fail("无法读取 manifest.json");

        var hostCompatError = CheckHostCompatibility(manifest);
        if (!string.IsNullOrWhiteSpace(hostCompatError))
            return OperationResult.Fail(hostCompatError);

        var depError = await CheckDependenciesAsync(state, manifest);
        if (!string.IsNullOrWhiteSpace(depError))
            return OperationResult.Fail(depError);

        item.Enabled = true;
        await _stateStore.SaveAsync(state);
        return OperationResult.Ok();
    }

    private async Task<OperationResult> DisableCoreAsync(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0)
            return OperationResult.Fail("id 不能为空");

        var state = await _stateStore.LoadAsync();
        var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        if (item == null)
            return OperationResult.Fail("模块不存在");

        item.Enabled = false;
        await _stateStore.SaveAsync(state);
        return OperationResult.Ok();
    }

    private async Task<OperationResult> SetActiveVersionCoreAsync(string id, string version)
    {
        id = (id ?? "").Trim();
        version = (version ?? "").Trim();
        if (!IsSafeId(id) || !IsSafeVersion(version))
            return OperationResult.Fail("参数无效");

        var state = await _stateStore.LoadAsync();
        var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        if (item == null)
            return OperationResult.Fail("模块不存在");

        if (item.BuiltIn)
            return OperationResult.Fail("内置模块版本随宿主，不支持切换");

        var dir = Path.Combine(_layout.InstalledDir, id, version);
        if (!item.InstalledVersions.Contains(version, StringComparer.Ordinal) || !Directory.Exists(dir))
            return OperationResult.Fail("该版本未安装");

        item.ActiveVersion = version;
        await _stateStore.SaveAsync(state);
        return OperationResult.Ok();
    }

    private async Task<OperationResult> RemoveModuleCoreAsync(string id)
    {
        id = (id ?? "").Trim();
        if (!IsSafeId(id))
            return OperationResult.Fail("模块 ID 无效");

        var state = await _stateStore.LoadAsync();
        var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        if (item == null)
            return OperationResult.Fail("模块不存在");

        if (item.BuiltIn)
            return OperationResult.Fail("内置模块不允许删除");

        // 先禁用，避免重启后继续加载（即使后续物理删除失败）
        item.Enabled = false;
        await _stateStore.SaveAsync(state);

        try
        {
            // 直接删除，不保留到 trash
            var moduleDir = Path.Combine(_layout.InstalledDir, id);
            if (Directory.Exists(moduleDir))
                Directory.Delete(moduleDir, recursive: true);

            var packageDir = Path.Combine(_layout.PackagesDir, id);
            if (Directory.Exists(packageDir))
                Directory.Delete(packageDir, recursive: true);

            state.Modules.RemoveAll(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            await _stateStore.SaveAsync(state);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"删除失败：{ex.Message}（建议先停用并重启后再删除）");
        }
    }

    private async Task<OperationResult> RemoveModuleVersionCoreAsync(string id, string version)
    {
        id = (id ?? "").Trim();
        version = (version ?? "").Trim();
        if (!IsSafeId(id) || !IsSafeVersion(version))
            return OperationResult.Fail("参数无效");

        var state = await _stateStore.LoadAsync();
        var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        if (item == null)
            return OperationResult.Fail("模块不存在");

        if (item.BuiltIn)
            return OperationResult.Fail("内置模块不支持删除版本");

        if (state.Modules.Count(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) > 1)
            return OperationResult.Fail("模块状态存在 ID 大小写冲突，请先备份并修复状态");

        if (!item.InstalledVersions.Contains(version, StringComparer.Ordinal))
            return OperationResult.Fail("该版本未安装");
        if (ProtectedVersions(item).Contains(version))
            return OperationResult.Fail("不能删除当前版本、最后可用版本或本进程仍加载的版本");

        try
        {
            // 直接删除，不保留到 trash
            var versionDir = Path.Combine(_layout.InstalledDir, id, version);
            if (Directory.Exists(versionDir))
                Directory.Delete(versionDir, recursive: true);

            var packageFile = Path.Combine(_layout.PackagesDir, id, $"{version}.tpm");
            if (File.Exists(packageFile))
                File.Delete(packageFile);

            item.InstalledVersions ??= new List<string>();
            item.InstalledVersions.RemoveAll(v => string.Equals(v, version, StringComparison.Ordinal));
            if (string.Equals((item.LastGoodVersion ?? "").Trim(), version, StringComparison.Ordinal))
                item.LastGoodVersion = null;

            // 如果删到一个版本都不剩，则等价于删除模块
            if (item.InstalledVersions.Count == 0)
                state.Modules.RemoveAll(m => string.Equals(m.Id, id, StringComparison.Ordinal));

            await _stateStore.SaveAsync(state);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"删除版本失败：{ex.Message}（建议先停用并重启后再删除）");
        }
    }

    private async Task<OperationResult> PruneOldVersionsCoreAsync(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0)
            return OperationResult.Fail("id 不能为空");

        var state = await _stateStore.LoadAsync();
        var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        if (item == null)
            return OperationResult.Fail("模块不存在");

        if (item.BuiltIn)
            return OperationResult.Fail("内置模块不支持清理版本");

        item.InstalledVersions ??= new List<string>();
        var keep = ProtectedVersions(item);

        var toRemove = item.InstalledVersions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .Where(v => !keep.Contains(v))
            .ToList();

        if (toRemove.Count == 0)
            return OperationResult.Ok();

        foreach (var v in toRemove)
        {
            var r = await RemoveModuleVersionCoreAsync(id, v);
            if (!r.Success)
                return r;
        }

        return OperationResult.Ok();
    }

    private async Task<ModuleManifest?> TryLoadManifestAsync(string id, string version)
    {
        try
        {
            var path = Path.Combine(_layout.InstalledDir, id, version, "manifest.json");
            if (!File.Exists(path))
                return null;
            var json = await File.ReadAllTextAsync(path);
            var manifest = JsonSerializer.Deserialize<ModuleManifest>(json);
            if (manifest == null)
                return null;
            NormalizeManifest(manifest);
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> CheckDependenciesAsync(ModuleState state, ModuleManifest manifest)
    {
        foreach (var dep in manifest.Dependencies ?? new List<ModuleDependency>())
        {
            var depId = (dep.Id ?? "").Trim();
            if (depId.Length == 0)
                return "依赖项缺少 id";

            var found = state.Modules.FirstOrDefault(m => string.Equals(m.Id, depId, StringComparison.Ordinal));
            if (found == null || string.IsNullOrWhiteSpace(found.ActiveVersion))
                return $"缺少依赖：{depId}";

            if (!found.Enabled)
                return $"依赖未启用：{depId}";

            if (!SemVer.TryParse(found.ActiveVersion, out var installed))
                return $"依赖版本无效：{depId} {found.ActiveVersion}";

            var rangeExpr = (dep.Range ?? "").Trim();
            if (!VersionRange.TryParse(rangeExpr, out var range, out var err))
                return $"依赖 range 无效：{depId} ({err})";

            if (!range.Contains(installed))
                return $"依赖不满足：{depId} 需要 {rangeExpr}，当前 {found.ActiveVersion}";
        }

        return null;
    }

    private static void TryPromoteSingleRootFolder(string stagingDir)
    {
        if (string.IsNullOrWhiteSpace(stagingDir))
            return;

        try
        {
            var manifest = Path.Combine(stagingDir, "manifest.json");
            if (File.Exists(manifest))
                return;

            var files = Directory.GetFiles(stagingDir)
                .Where(f => !string.Equals(Path.GetFileName(f), ".DS_Store", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var dirs = Directory.GetDirectories(stagingDir)
                .Where(d => !string.Equals(Path.GetFileName(d), "__MACOSX", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count != 0 || dirs.Count != 1)
                return;

            var root = dirs[0];
            var innerManifest = Path.Combine(root, "manifest.json");
            if (!File.Exists(innerManifest))
                return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var target = Path.Combine(stagingDir, name);
                if (Directory.Exists(entry))
                {
                    if (Directory.Exists(target))
                        Directory.Delete(target, recursive: true);
                    Directory.Move(entry, target);
                }
                else if (File.Exists(entry))
                {
                    if (File.Exists(target))
                        File.Delete(target);
                    File.Move(entry, target);
                }
            }

            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore：保持原行为（最终仍会报缺少 manifest.json）
        }
    }

    private string? CheckHostCompatibility(ModuleManifest manifest)
    {
        if (!SemVer.TryParse(_hostVersion, out var host))
            return null;

        var minOk = true;
        var maxOk = true;

        if (SemVer.TryParse(manifest.Host?.Min, out var min))
            minOk = host.CompareTo(min) >= 0;
        if (SemVer.TryParse(manifest.Host?.Max, out var max))
            maxOk = host.CompareTo(max) <= 0;

        if (!minOk || !maxOk)
            return $"宿主版本不兼容：当前 {_hostVersion}，要求 {manifest.Host?.Min ?? "-"} ~ {manifest.Host?.Max ?? "-"}";

        return null;
    }

    private static void NormalizeManifest(ModuleManifest manifest)
    {
        manifest.Id = (manifest.Id ?? "").Trim();
        manifest.Name = (manifest.Name ?? "").Trim();
        manifest.Version = (manifest.Version ?? "").Trim();
        manifest.Entry ??= new ModuleEntryPoint();
        manifest.Entry.Assembly = (manifest.Entry.Assembly ?? "").Trim();
        manifest.Entry.Type = (manifest.Entry.Type ?? "").Trim();
        manifest.Dependencies ??= new List<ModuleDependency>();
        manifest.Host ??= new HostCompatibility();
        manifest.Host.Min = (manifest.Host.Min ?? "").Trim();
        manifest.Host.Max = (manifest.Host.Max ?? "").Trim();
        foreach (var d in manifest.Dependencies)
        {
            d.Id = (d.Id ?? "").Trim();
            d.Range = (d.Range ?? "").Trim();
        }
    }

    private static string? ValidateManifest(ModuleManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
            return "模块 id 不能为空";
        if (manifest.Id.Length > 100)
            return "模块 id 过长";
        if (!IsSafeId(manifest.Id))
            return "模块 id 仅允许字母/数字/.-_";

        if (string.IsNullOrWhiteSpace(manifest.Name))
            return "模块名称不能为空";
        if (manifest.Name.Length > 100)
            return "模块名称过长";

        if (!IsSafeVersion(manifest.Version))
            return "模块版本必须是 x.y.z";

        if (string.IsNullOrWhiteSpace(manifest.Entry.Assembly) || string.IsNullOrWhiteSpace(manifest.Entry.Type))
            return "入口点缺失（entry.assembly / entry.type）";

        if (manifest.Entry.Assembly.IndexOfAny(['/', '\\', ':']) >= 0
            || !manifest.Entry.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || manifest.Entry.Assembly != Path.GetFileName(manifest.Entry.Assembly))
            return "入口程序集必须是 lib 下的 DLL 文件名";
        return null;
    }

    private static bool IsSafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.EndsWith('.') || id.Length > 100)
            return false;
        var stem = id.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return false;
        foreach (var ch in id)
        {
            if (char.IsLetterOrDigit(ch))
                continue;
            if (ch is '.' or '-' or '_' )
                continue;
            return false;
        }

        return true;
    }

    private static string? ExtractZipToDirectorySafe(ZipArchive zip, string destinationDir)
    {
        if (zip == null)
            throw new ArgumentNullException(nameof(zip));
        if (string.IsNullOrWhiteSpace(destinationDir))
            throw new ArgumentException("destinationDir 不能为空", nameof(destinationDir));

        var destFull = Path.GetFullPath(destinationDir);
        if (!destFull.EndsWith(Path.DirectorySeparatorChar))
            destFull += Path.DirectorySeparatorChar;

        if (zip.Entries.Count > 5000 || zip.Entries.Sum(x => (decimal)x.Length) > 200L * 1024 * 1024)
            return "模块解压内容超过限制（最多 5000 项、200MB）";
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            var entryPath = (entry.FullName ?? string.Empty).Replace('\\', '/');
            if (entryPath.Length == 0)
                continue;

            // 禁止绝对路径
            if (entryPath.StartsWith("/", StringComparison.Ordinal) || entryPath.StartsWith("\\", StringComparison.Ordinal))
                return "压缩包包含非法路径（绝对路径）";

            if (entryPath.Split('/').Any(part => part is "." or ".." || part.Contains(':') || part.EndsWith(' ') || part.EndsWith('.'))
                || !entries.Add(entryPath.TrimEnd('/')))
                return "压缩包包含非法或重复路径";
            var relative = entryPath.Replace('/', Path.DirectorySeparatorChar);
            var targetFull = Path.GetFullPath(Path.Combine(destFull, relative));
            if (!targetFull.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                return "压缩包包含非法路径（路径穿越）";

            // 目录条目（通常以 / 结尾，或 Name 为空）
            if (entryPath.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(entry.Name))
            {
                Directory.CreateDirectory(targetFull);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFull) ?? destFull);
            using var source = entry.Open();
            using var target = new FileStream(targetFull, FileMode.CreateNew, FileAccess.Write);
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                // ZIP 中央目录声明的长度可伪造，必须按实际解压字节计数。
                extractedBytes += read;
                if (extractedBytes > 200L * 1024 * 1024)
                    return "模块实际解压内容超过 200MB";
                target.Write(buffer, 0, read);
            }
        }

        return null;
    }
}

public sealed record ModuleOverview(
    string Id,
    bool Enabled,
    string? ActiveVersion,
    string? LastGoodVersion,
    IReadOnlyList<string> InstalledVersions,
    ModuleManifest? Manifest,
    string? ManifestError,
    bool BuiltIn);

public sealed record InstallResult(bool Success, string Message, string? ModuleId = null, string? Version = null)
{
    public static InstallResult Ok(string id, string version) => new(true, "ok", id, version);
    public static InstallResult Fail(string msg) => new(false, msg);
}

public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok() => new(true, "ok");
    public static OperationResult Fail(string msg) => new(false, msg);
}

public sealed record ModulePrunePreview(string Id, IReadOnlyList<string> Versions, IReadOnlyList<string> KeptVersions);
public sealed record ModulePruneItemResult(string Id, string Version, bool Success, string Message);
public sealed record ModulePruneResult(bool Success, int RemovedVersions, IReadOnlyList<ModulePruneItemResult> Results);
