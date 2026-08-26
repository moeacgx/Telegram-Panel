using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules.BuiltIn;

namespace TelegramPanel.Web.Modules;

public static class ModuleBootstrapper
{
    public static void AddModuleSystem(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var hostVersion = ModulePaths.GetHostVersion();
        var root = ModulePaths.ResolveModulesRoot(configuration, environment);
        var layout = ModulePaths.GetLayout(root);
        var builtInCatalog = new BuiltInModuleCatalog(hostVersion);

        services.AddSingleton(layout);
        services.AddSingleton(new ModuleHostContext(hostVersion, layout.Root));
        services.AddSingleton<ModuleStateStore>();
        services.AddSingleton(builtInCatalog);
        services.AddSingleton(sp => new ModuleInstallerService(
            sp.GetRequiredService<ModuleLayout>(),
            sp.GetRequiredService<ModuleStateStore>(),
            sp.GetRequiredService<BuiltInModuleCatalog>(),
            hostVersion));

        // 在 DI 构建前加载模块清单并执行 ConfigureServices
        var registry = new ModuleRegistry();
        services.AddSingleton(registry);

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("ModuleBootstrapper");
        var stateStore = new ModuleStateStore(layout);

        var state = stateStore.LoadAsync().GetAwaiter().GetResult();
        EnsureBuiltInModules(state, builtInCatalog, hostVersion);
        stateStore.SaveAsync(state).GetAwaiter().GetResult();

        var context = new ModuleHostContext(hostVersion, layout.Root);

        // 1) 内置模块
        foreach (var builtIn in builtInCatalog.CreateModules())
        {
            var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, builtIn.Manifest.Id, StringComparison.Ordinal));
            var enabled = item?.Enabled ?? true;
            if (!enabled)
                continue;

            TryConfigureAndRegister(logger, registry, services, builtIn.Manifest.Id, builtIn.Manifest.Version, builtIn, context, builtIn.Manifest, builtIn: true, moduleRootPath: null);
        }

        // 2) 外部安装模块（仅加载启用且有 ActiveVersion 的）
        foreach (var item in state.Modules.Where(m => !m.BuiltIn && m.Enabled && !string.IsNullOrWhiteSpace(m.ActiveVersion)).ToList())
        {
            var id = item.Id;
            var version = item.ActiveVersion!.Trim();
            var moduleRoot = Path.Combine(layout.InstalledDir, id, version);
            var manifestPath = Path.Combine(moduleRoot, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                logger.LogWarning("Module manifest missing: {Id} {Version}", id, version);
                item.Enabled = false;
                continue;
            }

            ModuleManifest? manifest;
            try
            {
                var json = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<ModuleManifest>(json);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse module manifest: {Id} {Version}", id, version);
                item.Enabled = false;
                continue;
            }

            if (manifest == null)
            {
                item.Enabled = false;
                continue;
            }

            NormalizeManifest(manifest);

            try
            {
                var module = LoadExternalModule(moduleRoot, manifest);
                TryConfigureAndRegister(logger, registry, services, id, version, module, context, manifest, builtIn: false, moduleRootPath: moduleRoot);

                item.LastGoodVersion = version;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Module load failed: {Id} {Version}", id, version);

                // 自动回滚：尝试切回 lastGood
                var fallback = (item.LastGoodVersion ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(fallback) && !string.Equals(fallback, version, StringComparison.Ordinal))
                {
                    logger.LogWarning("Module rollback to last good: {Id} {Version}", id, fallback);
                    item.ActiveVersion = fallback;

                    try
                    {
                        var fallbackRoot = Path.Combine(layout.InstalledDir, id, fallback);
                        var fallbackManifestPath = Path.Combine(fallbackRoot, "manifest.json");
                        if (File.Exists(fallbackManifestPath))
                        {
                            var json = File.ReadAllText(fallbackManifestPath);
                            var fallbackManifest = JsonSerializer.Deserialize<ModuleManifest>(json);
                            if (fallbackManifest != null)
                            {
                                NormalizeManifest(fallbackManifest);
                                var fallbackModule = LoadExternalModule(fallbackRoot, fallbackManifest);
                                TryConfigureAndRegister(logger, registry, services, id, fallback, fallbackModule, context, fallbackManifest, builtIn: false, moduleRootPath: fallbackRoot);
                            }
                        }
                    }
                    catch (Exception ex2)
                    {
                        logger.LogWarning(ex2, "Module rollback load failed: {Id} {Version}", id, fallback);
                        item.Enabled = false;
                    }
                }
                else
                {
                    item.Enabled = false;
                }
            }
        }

        stateStore.SaveAsync(state).GetAwaiter().GetResult();

        // 贡献注册表（任务/API/UI 元数据）：启动时固化一次，启用/停用通常需要重启
        services.AddSingleton(sp => new ModuleContributionRegistry(
            sp.GetRequiredService<ModuleRegistry>(),
            sp.GetRequiredService<ILogger<ModuleContributionRegistry>>(),
            sp));
    }

    private static void TryConfigureAndRegister(
        ILogger logger,
        ModuleRegistry registry,
        IServiceCollection services,
        string id,
        string version,
        ITelegramPanelModule module,
        ModuleHostContext context,
        ModuleManifest manifest,
        bool builtIn,
        string? moduleRootPath)
    {
        try
        {
            ConfigureServicesAtomically(module, services, context);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Module ConfigureServices failed: {Id} {Version}", id, version);
            throw;
        }

        registry.Add(new LoadedModule(id, version, builtIn, module, context, manifest, moduleRootPath));
    }

    internal static void ConfigureServicesAtomically(
        ITelegramPanelModule module,
        IServiceCollection services,
        ModuleHostContext context)
    {
        var serviceSnapshot = services.ToArray();
        try
        {
            module.ConfigureServices(services, context);
        }
        catch
        {
            services.Clear();
            foreach (var descriptor in serviceSnapshot)
                services.Add(descriptor);
            throw;
        }
    }

    private static void EnsureBuiltInModules(ModuleState state, BuiltInModuleCatalog builtInCatalog, string hostVersion)
    {
        state.Modules ??= new List<ModuleStateItem>();

        foreach (var manifest in builtInCatalog.CreateModules().Select(m => m.Manifest))
        {
            var id = manifest.Id;
            var item = state.Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            if (item == null)
            {
                item = new ModuleStateItem
                {
                    Id = id,
                    BuiltIn = true,
                    Enabled = true,
                    ActiveVersion = hostVersion,
                    LastGoodVersion = hostVersion,
                    InstalledVersions = new List<string> { hostVersion }
                };
                state.Modules.Add(item);
                continue;
            }

            item.BuiltIn = true;
            item.ActiveVersion = hostVersion;
            item.LastGoodVersion ??= hostVersion;
            item.InstalledVersions ??= new List<string>();
            if (!item.InstalledVersions.Contains(hostVersion, StringComparer.Ordinal))
                item.InstalledVersions.Add(hostVersion);
        }
    }

    private static ITelegramPanelModule LoadExternalModule(string moduleRoot, ModuleManifest manifest)
    {
        var libDir = Path.Combine(moduleRoot, "lib");
        var assemblyFile = Path.Combine(libDir, manifest.Entry.Assembly);
        if (!File.Exists(assemblyFile))
            throw new FileNotFoundException($"入口程序集不存在：{assemblyFile}");

        var alc = new ModuleLoadContext(assemblyFile);
        var asm = alc.LoadFromAssemblyPath(assemblyFile);
        var typeName = manifest.Entry.Type;
        var t = asm.GetType(typeName, throwOnError: true, ignoreCase: false);
        if (t == null)
            throw new TypeLoadException($"入口类型不存在：{typeName}");

        if (!typeof(ITelegramPanelModule).IsAssignableFrom(t))
            throw new InvalidOperationException($"入口类型未实现 ITelegramPanelModule：{typeName}");

        return (ITelegramPanelModule)Activator.CreateInstance(t)!;
    }

    private static void NormalizeManifest(ModuleManifest manifest)
    {
        manifest.Id = (manifest.Id ?? "").Trim();
        manifest.Name = (manifest.Name ?? "").Trim();
        manifest.Version = (manifest.Version ?? "").Trim();
        manifest.Entry ??= new ModuleEntryPoint();
        manifest.Entry.Assembly = (manifest.Entry.Assembly ?? "").Trim();
        manifest.Entry.Type = (manifest.Entry.Type ?? "").Trim();
        manifest.Host ??= new HostCompatibility();
        manifest.Host.Min = (manifest.Host.Min ?? "").Trim();
        manifest.Host.Max = (manifest.Host.Max ?? "").Trim();
        manifest.Dependencies ??= new List<ModuleDependency>();
        foreach (var d in manifest.Dependencies)
        {
            d.Id = (d.Id ?? "").Trim();
            d.Range = (d.Range ?? "").Trim();
        }
    }
}
