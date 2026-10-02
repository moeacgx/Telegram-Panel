using TelegramPanel.Modules;

namespace TelegramPanel.Web.Modules.BuiltIn;

public sealed class BuiltInModuleCatalog
{
    private readonly List<ITelegramPanelModule> _modules;
    private readonly Dictionary<string, ModuleManifest> _manifestById;

    public BuiltInModuleCatalog(string hostVersion)
    {
        _modules = new List<ITelegramPanelModule>
        {
            new TaskCatalogModule(hostVersion),
        };

        _manifestById = _modules
            .Select(m => m.Manifest)
            .ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);
    }

    public IReadOnlyList<ITelegramPanelModule> CreateModules() => _modules;

    public bool TryGetManifest(string id, out ModuleManifest manifest)
    {
        id = (id ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            manifest = new ModuleManifest();
            return false;
        }

        return _manifestById.TryGetValue(id, out manifest!);
    }
}
