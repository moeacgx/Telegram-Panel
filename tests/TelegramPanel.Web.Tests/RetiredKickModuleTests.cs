using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Modules.BuiltIn;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class RetiredKickModuleTests
{
    [Fact]
    public void Catalog_retains_system_tasks_without_registering_kick_api()
    {
        var catalog = new BuiltInModuleCatalog("1.31.77");
        Assert.False(catalog.TryGetManifest("builtin.kick-api", out _));
        var tasks = Assert.IsType<TaskCatalogModule>(Assert.Single(catalog.CreateModules()));
        var definitions = tasks.GetTasks(new("1.31.77", Path.GetTempPath())).ToArray();
        Assert.Equal(10, definitions.Length);
        Assert.Contains(definitions, t => t.TaskType == BatchTaskTypes.ChannelInviteUsers);
        Assert.Contains(definitions, t => t.TaskType == BatchTaskTypes.AutoChangeLoginEmail);
        Assert.Equal("builtin.tasks", tasks.Manifest.Id);
    }

    [Fact]
    public void Startup_removes_only_retired_builtin_metadata_and_preserves_external_modules()
    {
        var external = new ModuleStateItem { Id = "demo.kick-api", Enabled = true, BuiltIn = false, ActiveVersion = "1.0.0", InstalledVersions = ["1.0.0"] };
        var state = new ModuleState { Modules = [
            new() { Id = "builtin.kick-api", BuiltIn = true, Enabled = true, ActiveVersion = "1.31.76", InstalledVersions = ["1.0.0", "1.31.76"] },
            new() { Id = "builtin.tasks", BuiltIn = true, Enabled = true }, external
        ] };
        var catalog = new BuiltInModuleCatalog("1.31.77");
        ModuleBootstrapper.EnsureBuiltInModules(state, catalog, "1.31.77");
        ModuleBootstrapper.EnsureBuiltInModules(state, catalog, "1.31.77");
        Assert.Equal(2, state.Modules.Count);
        Assert.DoesNotContain(state.Modules, m => m.Id == "builtin.kick-api");
        Assert.Same(external, state.Modules.Single(m => m.Id == "demo.kick-api"));
        Assert.False(external.BuiltIn);
        Assert.Equal("1.0.0", external.ActiveVersion);
        Assert.True(external.Enabled);
    }
}
