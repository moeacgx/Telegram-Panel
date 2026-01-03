using System.Reflection;
using System.Runtime.Loader;

namespace TelegramPanel.Web.Modules;

public sealed class ModuleLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _baseDir;

    public ModuleLoadContext(string mainAssemblyPath) : base(isCollectible: false)
    {
        if (string.IsNullOrWhiteSpace(mainAssemblyPath))
            throw new ArgumentException("mainAssemblyPath 不能为空", nameof(mainAssemblyPath));

        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _baseDir = Path.GetDirectoryName(mainAssemblyPath) ?? "";
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = (assemblyName.Name ?? "").Trim();
        if (name.Length == 0)
            return null;

        // 1) 与宿主共享的“边界程序集”必须由 Default ALC 解析，避免同名程序集被模块再次加载导致类型身份不一致。
        //    典型问题：模块包携带 Microsoft.Extensions.DependencyInjection*.dll / Microsoft.AspNetCore.Components.dll，
        //    会让 IServiceCollection / EventCallback 等类型在不同 ALC 中变成“不同类型”，最终出现：
        //    - TypeLoadException（看起来像模块没有实现 ConfigureServices）
        //    - 组件参数绑定失败（表现为 DraftChanged 不生效）
        if (name.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.JSInterop", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("TelegramPanel.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "MudBlazor", StringComparison.OrdinalIgnoreCase)
            // TelegramPanel 宿主内置依赖：模块可直接复用宿主版本，避免重复携带导致包体积膨胀。
            || string.Equals(name, "WTelegramClient", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "PhoneNumbers", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "SixLabors.ImageSharp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase))
            return null;

        // 2) 其次：如果宿主已经加载过同名程序集，则同样交给 Default ALC（避免重复加载）。
        if (AssemblyLoadContext.Default.Assemblies.Any(a =>
                string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return LoadFromAssemblyPath(path);

        // fallback：如果模块未提供 deps.json，则在 lib 目录按名称寻找
        if (name.Length == 0 || string.IsNullOrWhiteSpace(_baseDir))
            return null;

        var candidate = Path.Combine(_baseDir, name + ".dll");
        if (File.Exists(candidate))
            return LoadFromAssemblyPath(candidate);

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return LoadUnmanagedDllFromPath(path);

        return base.LoadUnmanagedDll(unmanagedDllName);
    }
}
