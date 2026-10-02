using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Modules.BuiltIn;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ModuleRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-modules-tests-" + Guid.NewGuid().ToString("N"));
    private ModuleLayout Layout => ModulePaths.GetLayout(_root);
    private ModuleInstallerService Installer(ModuleRegistry? registry = null) =>
        new(Layout, new ModuleStateStore(Layout), new BuiltInModuleCatalog("1.31.76"), "1.31.76", registry);

    [Fact]
    public async Task Repository_secrets_are_encrypted_redacted_preserved_and_cleared_on_target_change()
    {
        var store = new ModuleRepositoryStore(Layout, new EphemeralDataProtectionProvider());
        var created = await store.SaveAsync(null, new("个人", "github", "owner/modules", Token: "test-private-token"), default);
        Assert.True(created.HasToken);
        Assert.DoesNotContain("test-private-token", await File.ReadAllTextAsync(Path.Combine(_root, "repositories.json")));
        Assert.DoesNotContain("test-private-token", JsonSerializer.Serialize(await store.ListAsync()));
        Assert.Equal("test-private-token", store.ReadToken(await store.GetAsync(created.Id, default)));
        var retained = await store.SaveAsync(created.Id, new("改名", "github", "owner/modules"), default);
        Assert.True(retained.HasToken);
        var changed = await store.SaveAsync(created.Id, new("改目标", "github", "another/modules"), default);
        Assert.False(changed.HasToken);
        await store.DeleteAsync(created.Id, default);
        Assert.DoesNotContain(await store.ListAsync(), x => x.Id == created.Id);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void Repository_blocks_non_public_addresses(string ip) => Assert.False(ModuleRepositoryTransport.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://example.com/index.json")]
    [InlineData("https://user:password@example.com/index.json")]
    [InlineData("https://example.com:8443/index.json")]
    [InlineData("https://127.0.0.1/index.json")]
    public void Repository_rejects_unsafe_urls(string url) => Assert.Throws<InvalidOperationException>(() => ModuleRepositoryTransport.ValidateUrl(url));

    [Fact]
    public void Token_scope_stays_inside_selected_github_repository()
    {
        var repo = new StoredModuleRepository("x", "x", "github", "owner/modules", "feature/a", null);
        Assert.Contains("ref=feature%2Fa", ModuleRepositoryStore.IndexUri(repo).AbsoluteUri);
        Assert.True(ModuleRepositoryTransport.CanSendToken(new("https://api.github.com/repos/owner/modules/releases/assets/1"), repo));
        Assert.False(ModuleRepositoryTransport.CanSendToken(new("https://api.github.com/repos/another/modules/releases/assets/1"), repo));
        Assert.False(ModuleRepositoryTransport.CanSendToken(new("https://other.example.com/package.tpm"), repo));
        Assert.False(ModuleRepositoryTransport.CanSendToken(new("https://api.github.com/user"), repo));
    }

    [Fact]
    public void Index_rejects_duplicate_entries_and_invalid_hash()
    {
        var module = new RepositoryModule("test", "测试", "1.0.0", "https://example.com/package.tpm", new string('a', 64));
        byte[] Serialize(params RepositoryModule[] modules) => JsonSerializer.SerializeToUtf8Bytes(new ModuleRepositoryIndex(1, modules.ToList()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Single(ModuleRepositoryService.ParseIndex(Serialize(module)).Modules);
        Assert.Throws<InvalidOperationException>(() => ModuleRepositoryService.ParseIndex(Serialize(module, module)));
        Assert.Throws<InvalidOperationException>(() => ModuleRepositoryService.ParseIndex(Serialize(module with { Sha256 = "wrong" })));
    }

    [Fact]
    public async Task Mismatched_manifest_does_not_install_or_archive_package()
    {
        using var package = Package("actual", "1.0.0");
        var result = await Installer().InstallFromRepositoryAsync(package, "expected", "1.0.0", false);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(Layout.InstalledDir, "actual")));
        Assert.False(Directory.Exists(Path.Combine(Layout.PackagesDir, "actual")));
        Assert.Empty((await new ModuleStateStore(Layout).LoadAsync()).Modules);
    }

    [Theory]
    [InlineData(".", "entry.dll")]
    [InlineData("..", "entry.dll")]
    [InlineData("CON", "entry.dll")]
    [InlineData("test", "../outside.dll")]
    [InlineData("test", "/absolute.dll")]
    public async Task Installer_rejects_unsafe_manifest_paths(string id, string entry)
    {
        using var package = Package(id, "1.0.0", entry);
        Assert.False((await Installer().InstallAsync(package, "test.tpm")).Success);
        Assert.Empty((await new ModuleStateStore(Layout).LoadAsync()).Modules);
    }

    [Fact]
    public async Task Parallel_installs_preserve_state_and_reject_duplicate_version()
    {
        var installer = Installer();
        using var first = Package("first", "1.0.0");
        using var second = Package("second", "1.0.0");
        using var duplicate = Package("first", "1.0.0");
        var results = await Task.WhenAll(installer.InstallAsync(first, "a.tpm"), installer.InstallAsync(second, "b.tpm"), installer.InstallAsync(duplicate, "c.tpm"));
        Assert.Equal(2, results.Count(x => x.Success));
        Assert.Equal(2, (await new ModuleStateStore(Layout).LoadAsync()).Modules.Count);
    }

    [Fact]
    public async Task Installer_rejects_windows_aliases_and_protects_legacy_active_alias()
    {
        var installer = Installer();
        using var original = Package("test", "1.0.0");
        Assert.True((await installer.InstallAsync(original, "a.tpm")).Success);
        using var alias = Package("TEST", "1.0.1");
        Assert.False((await installer.InstallAsync(alias, "b.tpm")).Success);
        using var trailing = Package("test", "1.0.0.");
        Assert.False((await installer.InstallAsync(trailing, "c.tpm")).Success);
        var store = new ModuleStateStore(Layout);
        var state = await store.LoadAsync();
        state.Modules[0].ActiveVersion = "1.0.0.";
        await store.SaveAsync(state);
        Assert.Equal(0, (await installer.PruneAllOldVersionsAsync()).RemovedVersions);
    }

    [Fact]
    public async Task Redirects_strip_credentials_permanently_after_crossing_origin()
    {
        var repo = new StoredModuleRepository("x", "x", "github", "owner/modules", "main", null);
        var requests = new List<(string Url, string? Token)>();
        var transport = new ModuleRepositoryTransport(() => new FakeHandler(request =>
        {
            requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter));
            if (requests.Count == 1) return Redirect("https://api.github.com/repos/owner/modules/releases/assets/1");
            if (requests.Count == 2) return Redirect("https://cdn.example.com/asset");
            if (requests.Count == 3) return Redirect("https://api.github.com/repos/owner/modules/releases/assets/2");
            return new(HttpStatusCode.OK) { Content = new StringContent("package") };
        }));
        var bytes = await transport.DownloadAsync(ModuleRepositoryStore.IndexUri(repo), repo, "test-secret", 100, false, default);
        Assert.Equal("package", Encoding.UTF8.GetString(bytes));
        Assert.Equal(new string?[] { "test-secret", "test-secret", null, null }, requests.Select(x => x.Token));
    }

    [Fact]
    public async Task Transport_rejects_private_redirect_and_actual_oversize_stream()
    {
        var repo = new StoredModuleRepository("x", "x", "https", "https://example.com/index.json", "main", null);
        var calls = 0;
        var redirect = new ModuleRepositoryTransport(() => new FakeHandler(_ => { calls++; return Redirect("https://127.0.0.1/secret"); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => redirect.DownloadAsync(ModuleRepositoryStore.IndexUri(repo), repo, null, 100, true, default));
        Assert.Equal(1, calls);
        var oversize = new ModuleRepositoryTransport(() => new FakeHandler(_ =>
        {
            var content = new StreamContent(new MemoryStream(new byte[101]));
            content.Headers.ContentLength = 1;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => oversize.DownloadAsync(ModuleRepositoryStore.IndexUri(repo), repo, null, 100, true, default));
    }

    [Fact]
    public async Task Wrong_hash_download_never_reaches_installer()
    {
        var store = new ModuleRepositoryStore(Layout, new EphemeralDataProtectionProvider());
        var repo = await store.SaveAsync(null, new("测试", "https", "https://example.com/index.json"), default);
        var module = new RepositoryModule("test", "测试", "1.0.0", "https://example.com/package.tpm", new string('a', 64));
        var transport = new ModuleRepositoryTransport(() => new FakeHandler(request =>
            new(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith(".json")
                ? new StringContent(JsonSerializer.Serialize(new ModuleRepositoryIndex(1, [module]), new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                : new StringContent("wrong package") }));
        var service = new ModuleRepositoryService(store, transport, Installer());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(repo.Id, new("test", "1.0.0", module.Sha256, false, false), default));
        Assert.Empty((await new ModuleStateStore(Layout).LoadAsync()).Modules);
        Assert.Empty(Directory.GetFileSystemEntries(Layout.PackagesDir));
    }

    [Fact]
    public async Task Forged_zip_length_cannot_produce_unbounded_installed_output()
    {
        using var package = Package("test", "1.0.0");
        using (var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var file = archive.CreateEntry("lib/bomb.bin", CompressionLevel.SmallestSize).Open();
            var megabyte = new byte[1024 * 1024];
            for (var i = 0; i < 201; i++) file.Write(megabyte);
        }
        var bytes = package.ToArray();
        // 篡改中央目录，伪造最后一个条目的解压长度，模拟不可信 ZIP 元数据。
        for (var i = 0; i < bytes.Length - 46; i++)
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50 && Encoding.UTF8.GetString(bytes, i + 46, BitConverter.ToUInt16(bytes, i + 28)) == "lib/bomb.bin")
                BitConverter.GetBytes(1).CopyTo(bytes, i + 24);
        using var forged = new MemoryStream(bytes);
        var result = await Installer().InstallAsync(forged, "bomb.tpm");
        // .NET 8 会按声明长度截断；其它运行时可能继续解压，此时宿主必须拒绝。
        if (result.Success)
            Assert.InRange(new FileInfo(Path.Combine(Layout.InstalledDir, "test", "1.0.0", "lib", "bomb.bin")).Length, 0, 200L * 1024 * 1024);
        else
        {
            Assert.Contains("实际解压", result.Message);
            Assert.Empty((await new ModuleStateStore(Layout).LoadAsync()).Modules);
        }
        Assert.Empty(Directory.GetFileSystemEntries(Layout.StagingDir));
    }

    [Fact]
    public async Task Private_repository_install_checks_hash_and_identity_before_saving()
    {
        var store = new ModuleRepositoryStore(Layout, new EphemeralDataProtectionProvider());
        var repo = await store.SaveAsync(null, new("测试", "github", "owner/modules", Token: "fixture-read-token"), default);
        using var package = Package("test", "1.0.0");
        var bytes = package.ToArray();
        var module = new RepositoryModule("test", "测试", "1.0.0", "https://api.github.com/repos/owner/modules/releases/assets/1", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        var transport = new ModuleRepositoryTransport(() => new FakeHandler(request =>
        {
            Assert.Equal("fixture-read-token", request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith(".json")
                ? new StringContent(JsonSerializer.Serialize(new ModuleRepositoryIndex(1, [module]), new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                : new ByteArrayContent(bytes) };
        }));
        var service = new ModuleRepositoryService(store, transport, Installer());
        Assert.True((await service.InstallAsync(repo.Id, new("test", "1.0.0", module.Sha256, false, false), default)).Success);
        Assert.Equal("test", Assert.Single((await new ModuleStateStore(Layout).LoadAsync()).Modules).Id);
    }

    [Fact]
    public async Task Batch_prune_reports_partial_failure_and_continues_other_versions()
    {
        var installer = Installer();
        foreach (var version in new[] { "1.0.0", "1.0.1" })
        {
            using var package = Package("test", version);
            Assert.True((await installer.InstallAsync(package, "package.tpm")).Success);
        }
        var store = new ModuleStateStore(Layout);
        var state = await store.LoadAsync();
        state.Modules[0].InstalledVersions.Insert(0, "invalid-version");
        await store.SaveAsync(state);
        var result = await installer.PruneAllOldVersionsAsync();
        Assert.False(result.Success);
        Assert.Equal(1, result.RemovedVersions);
        Assert.Contains(result.Results, r => !r.Success && r.Version == "invalid-version");
        Assert.Contains(result.Results, r => r.Success && r.Version == "1.0.1");
        Assert.True(Directory.Exists(Path.Combine(Layout.InstalledDir, "test", "1.0.0")));
    }

    private static HttpResponseMessage Redirect(string url) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(url) } };
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }

    [Fact]
    public async Task Batch_prune_keeps_active_last_good_runtime_and_module_data()
    {
        var registry = new ModuleRegistry();
        registry.Add(new("test", "1.0.0", false, null!, new("1.31.76", _root), new(), null));
        var installer = Installer(registry);
        foreach (var version in new[] { "1.0.0", "1.0.1", "1.0.2", "1.0.3" })
        {
            using var package = Package("test", version);
            Assert.True((await installer.InstallAsync(package, "package.tpm")).Success);
        }
        var store = new ModuleStateStore(Layout);
        var state = await store.LoadAsync();
        state.Modules[0].ActiveVersion = "1.0.3";
        state.Modules[0].LastGoodVersion = "1.0.2";
        state.Modules.Add(new() { Id = "builtin.tasks", BuiltIn = true, InstalledVersions = ["0.9.0", "1.0.0"] });
        await store.SaveAsync(state);
        Directory.CreateDirectory(Path.Combine(_root, "data", "test"));
        await File.WriteAllTextAsync(Path.Combine(_root, "data", "test", "keep.txt"), "业务数据");
        Assert.Equal("1.0.1", Assert.Single(Assert.Single(await installer.GetPrunePreviewAsync()).Versions));
        var result = await installer.PruneAllOldVersionsAsync();
        Assert.True(result.Success);
        Assert.Equal(1, result.RemovedVersions);
        Assert.False(Directory.Exists(Path.Combine(Layout.InstalledDir, "test", "1.0.1")));
        Assert.False(File.Exists(Path.Combine(Layout.PackagesDir, "test", "1.0.1.tpm")));
        foreach (var version in new[] { "1.0.0", "1.0.2", "1.0.3" })
            Assert.True(Directory.Exists(Path.Combine(Layout.InstalledDir, "test", version)));
        Assert.Equal("业务数据", await File.ReadAllTextAsync(Path.Combine(_root, "data", "test", "keep.txt")));
        Assert.False((await installer.RemoveModuleVersionAsync("test", "../outside")).Success);
        Assert.False((await installer.RemoveModuleVersionAsync("test", "1.0.0")).Success);
        Assert.Equal(0, (await installer.PruneAllOldVersionsAsync()).RemovedVersions);
    }

    private static MemoryStream Package(string id, string version, string assembly = "entry.dll")
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(new { id, name = "测试模块", version, entry = new { assembly, type = "Test.Entry" } }));
            using var entry = zip.CreateEntry("lib/entry.dll").Open();
            entry.Write([1, 2, 3]);
        }
        stream.Position = 0;
        return stream;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
