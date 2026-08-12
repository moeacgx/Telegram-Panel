using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Web.Services;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class BatchProxyAccountImportTests
{
    private const string ApiHash = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task 纯Tdata缺少全局Api时不会检测或保存代理也不会连接Telegram()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        await using var zip = CreateTdataZip();

        var results = await fixture.ImportService.ImportFromZipStreamAsync(
            "tdata.zip",
            zip,
            perAccountProxyText: "http://tdata.proxy.test:8101");

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("未配置全局 Telegram API", result.Error);
        Assert.Equal(0, fixture.Probe.CallCount);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Equal(0, fixture.ClientPool.GetOrCreateCount);
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 账号代理数量不一致时整批零副作用()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        await using var zip = CreateAccountZip(
            ValidAccount("z/z.json", "8613800001002", 1002),
            ValidAccount("a/a.json", "8613800001001", 1001));

        var error = await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ImportService.ImportFromZipStreamAsync(
                "count-mismatch.zip",
                zip,
                perAccountProxyText: "http://only-one.proxy.test:8101"));

        Assert.Contains("账号与代理数量不一致", error.Message);
        Assert.Equal(0, fixture.Probe.CallCount);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Equal(0, fixture.ClientPool.GetOrCreateCount);
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 任一代理检测失败时不会连接Telegram或新增代理()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        fixture.Probe.ResultFactory = options =>
            options.Host == "failed.proxy.test"
                ? FailedProbe(
                    $"代理认证失败：{options.Username}/{options.Password}/"
                    + Uri.EscapeDataString(options.Password ?? string.Empty))
                : SuccessfulProbe(options);
        await using var zip = CreateAccountZip(
            ValidAccount("a/a.json", "8613800001101", 1101),
            ValidAccount("b/b.json", "8613800001102", 1102));

        var error = await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ImportService.ImportFromZipStreamAsync(
                "probe-failure.zip",
                zip,
                perAccountProxyText:
                    "http://user:good-secret@good.proxy.test:8111\n"
                    + "http://secret:secret%40password@failed.proxy.test:8112"));

        Assert.Contains("批量代理检测未全部通过", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("password", error.Message);
        Assert.DoesNotContain("%40", error.Message);
        Assert.Contains("***", error.Message);
        Assert.Equal(2, fixture.Probe.CallCount);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Equal(0, fixture.ClientPool.GetOrCreateCount);
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Zip写入乱序仍按规范相对路径一对一绑定代理()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        await using var zip = CreateAccountZip(
            ValidAccount("z/z.json", "8613800001202", 1202),
            ValidAccount("a/a.json", "8613800001201", 1201));

        var results = await fixture.ImportService.ImportFromZipStreamAsync(
            "unordered.zip",
            zip,
            perAccountProxyText:
                "http://first.proxy.test:8121\n"
                + "socks5://second.proxy.test:8122");

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(new[] { "a/a.json", "z/z.json" }, results.Select(x => x.SourceKey));
        Assert.Equal(new int?[] { 1, 2 }, results.Select(x => x.ProxyLine));
        Assert.Equal(ProbeIp(8121), results[0].ProxyEgressIp);
        Assert.Equal(ProbeIp(8122), results[1].ProxyEgressIp);

        var firstAccount = await fixture.Db.Accounts.AsNoTracking()
            .SingleAsync(x => x.Phone == "8613800001201");
        var secondAccount = await fixture.Db.Accounts.AsNoTracking()
            .SingleAsync(x => x.Phone == "8613800001202");
        var firstProxy = await fixture.Db.OutboundProxies.AsNoTracking()
            .SingleAsync(x => x.Id == firstAccount.ProxyId);
        var secondProxy = await fixture.Db.OutboundProxies.AsNoTracking()
            .SingleAsync(x => x.Id == secondAccount.ProxyId);
        Assert.Equal("first.proxy.test", firstProxy.Host);
        Assert.Equal(OutboundProxyProtocols.Http, firstProxy.Protocol);
        Assert.Equal("second.proxy.test", secondProxy.Host);
        Assert.Equal(OutboundProxyProtocols.Socks5, secondProxy.Protocol);
    }

    [Fact]
    public async Task Zip导入时写入选择的账号分类()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        var category = new AccountCategory
        {
            Name = "导入分类",
            Color = "#409eff",
            Description = "import-test"
        };
        fixture.Db.AccountCategories.Add(category);
        await fixture.Db.SaveChangesAsync();
        await using var zip = CreateAccountZip(
            ValidAccount("a/a.json", "8613800001251", 1251));

        var results = await fixture.ImportService.ImportFromZipStreamAsync(
            "categorized.zip",
            zip,
            categoryId: category.Id,
            perAccountProxyText: "http://category.proxy.test:8125");

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        var account = await fixture.Db.Accounts.AsNoTracking()
            .SingleAsync(x => x.Phone == "8613800001251");
        Assert.Equal(category.Id, account.CategoryId);
    }

    [Fact]
    public async Task 中间账号失败时后续账号仍使用原代理槽位()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        await using var zip = CreateAccountZip(
            ValidAccount("c/c.json", "8613800001303", 1303),
            new ZipAccount("b/b.json", "{\"phone\":\"+8613800001302\"}"),
            ValidAccount("a/a.json", "8613800001301", 1301));

        var results = await fixture.ImportService.ImportFromZipStreamAsync(
            "middle-failure.zip",
            zip,
            perAccountProxyText:
                "http://slot-one.proxy.test:8131\n"
                + "http://slot-two.proxy.test:8132\n"
                + "http://slot-three.proxy.test:8133");

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { "a/a.json", "b/b.json", "c/c.json" }, results.Select(x => x.SourceKey));
        Assert.Equal(new int?[] { 1, 2, 3 }, results.Select(x => x.ProxyLine));
        Assert.True(results[0].Success, results[0].Error);
        Assert.False(results[1].Success);
        Assert.True(results[2].Success, results[2].Error);
        Assert.Equal(3, await fixture.Db.OutboundProxies.CountAsync());

        var lastAccount = await fixture.Db.Accounts.AsNoTracking()
            .SingleAsync(x => x.Phone == "8613800001303");
        var lastProxy = await fixture.Db.OutboundProxies.AsNoTracking()
            .SingleAsync(x => x.Id == lastAccount.ProxyId);
        Assert.Equal("slot-three.proxy.test", lastProxy.Host);
        Assert.Equal(results[2].ProxyId, lastProxy.Id);
        Assert.False(await fixture.Db.Accounts.AsNoTracking()
            .AnyAsync(x => x.Phone == "8613800001302"));
    }

    [Fact]
    public async Task 重复代理保留两个槽位且只检测和保存一次()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        await using var zip = CreateAccountZip(
            ValidAccount("a/a.json", "8613800001401", 1401),
            ValidAccount("b/b.json", "8613800001402", 1402));
        const string proxy = "http://same-user:same-secret@shared.proxy.test:8141";

        var results = await fixture.ImportService.ImportFromZipStreamAsync(
            "duplicate-proxy.zip",
            zip,
            perAccountProxyText: $"{proxy}\n{proxy}");

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(new int?[] { 1, 2 }, results.Select(x => x.ProxyLine));
        Assert.Equal(results[0].ProxyId, results[1].ProxyId);
        Assert.Equal(1, fixture.Probe.CallCount);
        var savedProxy = Assert.Single(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
        Assert.Equal(savedProxy.Id, results[0].ProxyId);
        Assert.All(
            await fixture.Db.Accounts.AsNoTracking().ToListAsync(),
            account => Assert.Equal(savedProxy.Id, account.ProxyId));
    }

    [Fact]
    public async Task 已有启用代理会复用原记录并刷新检测信息()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        var existing = await fixture.AddProxyAsync(
            "reuse.proxy.test",
            8151,
            OutboundProxyProtocols.Http,
            "reuse-user",
            "reuse-secret");
        existing.Name = "http://reuse-user:reuse-secret@reuse.proxy.test:8151";
        await fixture.Db.SaveChangesAsync();

        var prepared = Assert.Single(await fixture.ProxyManagement
            .PreparePerAccountImportProxiesAsync(
                "http://reuse-user:reuse-secret@reuse.proxy.test:8151",
                1));

        Assert.Equal(existing.Id, prepared.ProxyId);
        Assert.Equal("http://reuse.proxy.test:8151", prepared.ProxyName);
        Assert.DoesNotContain("reuse-user", prepared.ProxyName);
        Assert.DoesNotContain("reuse-secret", prepared.ProxyName);
        Assert.Equal(ProbeIp(8151), prepared.EgressIp);
        Assert.Equal(1, fixture.Probe.CallCount);
        Assert.Equal(1, await fixture.Db.OutboundProxies.CountAsync());
        var reloaded = await fixture.Db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Equal("ok", reloaded.TestStatus);
        Assert.Equal(ProbeIp(8151), reloaded.EgressIp);
    }

    [Fact]
    public async Task 已有停用代理会安全拒绝且不会新增或泄露密码()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        await fixture.AddProxyAsync(
            "disabled.proxy.test",
            8161,
            OutboundProxyProtocols.Http,
            "disabled-user",
            "disabled-secret",
            isEnabled: false);

        var error = await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(
                "http://disabled-user:disabled-secret@disabled.proxy.test:8161",
                1));

        Assert.Contains("已停用", error.Message);
        Assert.DoesNotContain("disabled-secret", error.Message);
        Assert.Equal(1, fixture.Probe.CallCount);
        Assert.Equal(1, await fixture.Db.OutboundProxies.CountAsync());
        Assert.False(await fixture.Db.OutboundProxies.AsNoTracking()
            .Select(x => x.IsEnabled)
            .SingleAsync());
    }

    [Fact]
    public async Task 已有代理凭据冲突会安全拒绝且不会覆盖或泄密()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        await fixture.AddProxyAsync(
            "credential.proxy.test",
            8171,
            OutboundProxyProtocols.Http,
            "credential-user",
            "stored-secret");

        var error = await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(
                "http://credential-user:incoming-secret@credential.proxy.test:8171",
                1));

        Assert.Contains("认证信息不同", error.Message);
        Assert.DoesNotContain("stored-secret", error.Message);
        Assert.DoesNotContain("incoming-secret", error.Message);
        Assert.Equal(1, fixture.Probe.CallCount);
        var existing = await fixture.Db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Equal("stored-secret", existing.Password);
    }

    [Fact]
    public async Task 同批相同端点凭据冲突在检测前整体拒绝且不泄密()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();

        var error = await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(
                "http://same-user:first-secret@conflict.proxy.test:8181\n"
                + "http://same-user:second-secret@conflict.proxy.test:8181",
                2));

        Assert.Contains("认证信息不同", error.Message);
        Assert.DoesNotContain("first-secret", error.Message);
        Assert.DoesNotContain("second-secret", error.Message);
        Assert.Equal(0, fixture.Probe.CallCount);
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 代理认证字段包含控制字符时在检测前安全拒绝()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();

        var error = await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(
                "http://safe-user:line-one%0Aline-two@control.proxy.test:8182",
                1));

        Assert.Contains("控制字符", error.Message);
        Assert.DoesNotContain("line-one", error.Message);
        Assert.DoesNotContain("line-two", error.Message);
        Assert.Equal(0, fixture.Probe.CallCount);
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("http://http.proxy.test:8191", OutboundProxyProtocols.Http)]
    [InlineData("socks5://socks.proxy.test:8192", OutboundProxyProtocols.Socks5)]
    public async Task Http与Socks5代理检测成功后可以准备绑定(
        string proxyText,
        string expectedProtocol)
    {
        await using var fixture = await BatchImportFixture.CreateAsync();

        var prepared = Assert.Single(await fixture.ProxyManagement
            .PreparePerAccountImportProxiesAsync(proxyText, 1));

        Assert.Equal(expectedProtocol, prepared.ExpectedConnection.Protocol);
        Assert.Equal(1, fixture.Probe.CallCount);
        Assert.Equal(1, await fixture.Db.OutboundProxies.CountAsync());
    }

    [Theory]
    [InlineData("https://secret-user:secret-password@https.proxy.test:443")]
    [InlineData("mtproto://mt.proxy.test:443?secret=abcdef")]
    public async Task 不支持的代理协议在检测和保存前拒绝(string proxyText)
    {
        await using var fixture = await BatchImportFixture.CreateAsync();

        var error = await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(proxyText, 1));

        Assert.DoesNotContain("secret-password", error.Message);
        Assert.Equal(0, fixture.Probe.CallCount);
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 检测后代理连接变化会在首次Telegram请求前拒绝()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        var prepared = Assert.Single(await fixture.ProxyManagement
            .PreparePerAccountImportProxiesAsync(
                "http://snapshot-user:snapshot-secret@snapshot.proxy.test:8201",
                1));
        var proxy = await fixture.Db.OutboundProxies.SingleAsync();
        proxy.Port = 8202;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.ImportService.ImportFromStringSessionAsync(
            "session-data",
            12345,
            ApiHash,
            proxyBinding: new AccountProxyBindingInput(
                "existing",
                prepared.ProxyId,
                ExpectedConnection: prepared.ExpectedConnection));

        Assert.False(result.Success);
        Assert.Contains("检测后连接参数已变化", result.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Equal(0, fixture.ClientPool.GetOrCreateCount);
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 批量槽位的冻结代理会在每次Session首连时传给Importer且检测先于首连()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        var assignments = await fixture.ProxyManagement
            .PreparePerAccountImportProxiesAsync(
                "http://slot-a.proxy.test:8211\nhttp://slot-b.proxy.test:8212",
                2);
        fixture.Importer.ResultFactory = count => new ImportResult(
            true,
            $"86138000999{count}",
            99000 + count,
            $"slot-{count}",
            $"sessions/86138000999{count}.session");

        var first = await fixture.ImportService.ImportFromStringSessionAsync(
            "session-one",
            12345,
            ApiHash,
            proxyBinding: new AccountProxyBindingInput(
                "existing",
                assignments[0].ProxyId,
                ExpectedConnection: assignments[0].ExpectedConnection));
        var second = await fixture.ImportService.ImportFromStringSessionAsync(
            "session-two",
            12345,
            ApiHash,
            proxyBinding: new AccountProxyBindingInput(
                "existing",
                assignments[1].ProxyId,
                ExpectedConnection: assignments[1].ExpectedConnection));

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        var seen = fixture.Importer.SeenProxies.ToArray();
        Assert.Equal(2, seen.Length);
        Assert.Equal(assignments[0].ExpectedConnection, seen[0]);
        Assert.Equal(assignments[1].ExpectedConnection, seen[1]);

        var events = fixture.Events.ToArray();
        var firstImportEvent = Array.FindIndex(
            events,
            item => item.StartsWith("import:", StringComparison.Ordinal));
        Assert.Equal(2, firstImportEvent);
        Assert.Equal(2, events.Take(firstImportEvent)
            .Count(item => item.StartsWith("probe:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 逐账号批量代理严格执行一百条上限()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        var maximum = string.Join(
            '\n',
            Enumerable.Range(1, ProxyManagementService.MaxPerAccountProxyBatchSize)
                .Select(index => $"http://limit-{index}.proxy.test:{8300 + index}"));

        var prepared = await fixture.ProxyManagement
            .PreparePerAccountImportProxiesAsync(
                maximum,
                ProxyManagementService.MaxPerAccountProxyBatchSize);

        Assert.Equal(100, prepared.Count);
        Assert.Equal(100, fixture.Probe.CallCount);
        Assert.Equal(100, await fixture.Db.OutboundProxies.CountAsync());

        var overLimit = maximum + "\nhttp://limit-101.proxy.test:8401";
        await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(overLimit, 101));
        Assert.Equal(100, fixture.Probe.CallCount);
        Assert.Equal(100, await fixture.Db.OutboundProxies.CountAsync());
    }

    [Fact]
    public async Task 批量代理文本严格执行十万字符上限()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        const string proxy = "http://length.proxy.test:8402";
        var maximum = PadProxyText(proxy, ProxyManagementService.MaxPerAccountProxyTextLength);

        var prepared = await fixture.ProxyManagement
            .PreparePerAccountImportProxiesAsync(maximum, 1);

        Assert.Single(prepared);
        Assert.Equal(1, fixture.Probe.CallCount);
        Assert.Equal(1, await fixture.Db.OutboundProxies.CountAsync());

        var overLimit = maximum + "x";
        await Assert.ThrowsAsync<AccountImportProxyBatchException>(() =>
            fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(overLimit, 1));
        Assert.Equal(1, fixture.Probe.CallCount);
        Assert.Equal(1, await fixture.Db.OutboundProxies.CountAsync());
    }

    [Fact]
    public async Task 取消正在进行的代理检测时不会新增代理或连接Telegram()
    {
        await using var fixture = await BatchImportFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Probe.AsyncResultFactory = async (options, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessfulProbe(options);
        };
        using var cancellation = new CancellationTokenSource();

        var prepare = fixture.ProxyManagement.PreparePerAccountImportProxiesAsync(
            "http://cancel.proxy.test:8403",
            1,
            cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await prepare);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Equal(0, fixture.ClientPool.GetOrCreateCount);
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    private static ZipAccount ValidAccount(string jsonPath, string phone, long userId) =>
        new(
            jsonPath,
            $"{{\"api_id\":12345,\"api_hash\":\"{ApiHash}\","
            + $"\"phone\":\"+{phone}\",\"user_id\":{userId}}}");

    private static MemoryStream CreateAccountZip(params ZipAccount[] accounts)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var account in accounts)
            {
                WriteEntry(archive, account.JsonPath, account.Json);
                if (account.IncludeSession)
                {
                    var sessionPath = Path.ChangeExtension(account.JsonPath, ".session");
                    WriteEntry(archive, sessionPath, $"session-for-{account.JsonPath}");
                }
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateTdataZip()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            WriteEntry(archive, "account/tdata/key_data", "tdata-placeholder");
        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var output = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        output.Write(bytes);
    }

    private static EgressProbeResult SuccessfulProbe(ProxyConnectionOptions options) =>
        new(
            true,
            ProbeIp(options.Port),
            "US",
            "Test City",
            "Test ISP",
            "off",
            12,
            DateTime.UtcNow,
            null);

    private static EgressProbeResult FailedProbe(string error) =>
        new(false, null, null, null, null, null, null, DateTime.UtcNow, error);

    private static string ProbeIp(int port) => $"8.8.4.{port % 200 + 1}";

    private static string PadProxyText(string proxy, int length)
    {
        var prefix = proxy + "\n#";
        Assert.True(prefix.Length <= length);
        return prefix + new string('x', length - prefix.Length);
    }

    private sealed record ZipAccount(
        string JsonPath,
        string Json,
        bool IncludeSession = true);

    private sealed class BatchImportFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _sessionsPath;

        private BatchImportFixture(
            SqliteConnection connection,
            string sessionsPath,
            AppDbContext db,
            RecordingProbeService probe,
            RecordingSessionImporter importer,
            RecordingClientPool clientPool,
            ConcurrentQueue<string> events,
            ProxyManagementService proxyManagement,
            AccountImportService importService)
        {
            _connection = connection;
            _sessionsPath = sessionsPath;
            Db = db;
            Probe = probe;
            Importer = importer;
            ClientPool = clientPool;
            Events = events;
            ProxyManagement = proxyManagement;
            ImportService = importService;
        }

        public AppDbContext Db { get; }
        public RecordingProbeService Probe { get; }
        public RecordingSessionImporter Importer { get; }
        public RecordingClientPool ClientPool { get; }
        public ConcurrentQueue<string> Events { get; }
        public ProxyManagementService ProxyManagement { get; }
        public AccountImportService ImportService { get; }

        public static async Task<BatchImportFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var sessionsPath = Path.Combine(
                Path.GetTempPath(),
                $"telegram-panel-batch-proxy-tests-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Telegram:SessionsPath"] = sessionsPath
                })
                .Build();
            var events = new ConcurrentQueue<string>();
            var probe = new RecordingProbeService(events);
            var clientPool = new RecordingClientPool();
            var temporaryWarpClaims = new TemporaryWarpClaimStore();
            var warp = new WarpContainerManager(
                db,
                configuration,
                probe,
                NullLogger<WarpContainerManager>.Instance);
            var proxyManagement = new ProxyManagementService(
                db,
                clientPool,
                probe,
                warp,
                NullLogger<ProxyManagementService>.Instance,
                configuration,
                temporaryWarpClaims);
            var accountManagement = new AccountManagementService(
                new AccountRepository(db),
                new ChannelRepository(db),
                new GroupRepository(db),
                clientPool,
                configuration,
                NullLogger<AccountManagementService>.Instance,
                proxyManagement,
                new SessionPathResolver(configuration));
            var importer = new RecordingSessionImporter(events);
            var importService = new AccountImportService(
                importer,
                db,
                accountManagement,
                NullLogger<AccountImportService>.Instance,
                configuration,
                proxyManagement,
                temporaryWarpClaims);

            return new BatchImportFixture(
                connection,
                sessionsPath,
                db,
                probe,
                importer,
                clientPool,
                events,
                proxyManagement,
                importService);
        }

        public async Task<OutboundProxy> AddProxyAsync(
            string host,
            int port,
            string protocol,
            string? username,
            string? password,
            bool isEnabled = true)
        {
            var proxy = new OutboundProxy
            {
                Name = $"existing-{Guid.NewGuid():N}",
                Kind = OutboundProxyKinds.Manual,
                Protocol = protocol,
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                IsEnabled = isEnabled
            };
            Db.OutboundProxies.Add(proxy);
            await Db.SaveChangesAsync();
            return proxy;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            try
            {
                if (Directory.Exists(_sessionsPath))
                    Directory.Delete(_sessionsPath, recursive: true);
            }
            catch
            {
                // 临时 Session 目录清理失败不应掩盖测试断言。
            }
        }
    }

    private sealed class RecordingProbeService : IProxyEgressProbeService
    {
        private int _callCount;
        private readonly ConcurrentQueue<string> _events;

        public RecordingProbeService(ConcurrentQueue<string> events)
        {
            _events = events;
        }

        public Func<ProxyConnectionOptions, EgressProbeResult> ResultFactory { get; set; } =
            SuccessfulProbe;
        public Func<ProxyConnectionOptions, CancellationToken, Task<EgressProbeResult>>?
            AsyncResultFactory { get; set; }

        public ConcurrentQueue<ProxyConnectionOptions> Requests { get; } = new();
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<EgressProbeResult> ProbePanelAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SuccessfulProbe(new ProxyConnectionOptions(
                0,
                "panel",
                OutboundProxyKinds.Manual,
                OutboundProxyProtocols.Http,
                "panel.test",
                80,
                null,
                null,
                null)));

        public Task<EgressProbeResult> ProbeProxyAsync(
            OutboundProxy proxy,
            string stableAccountKey,
            CancellationToken cancellationToken = default) =>
            ProbeProxyAsync(
                new ProxyConnectionOptions(
                    proxy.Id,
                    proxy.Name,
                    proxy.Kind,
                    proxy.Protocol,
                    proxy.Host,
                    proxy.Port,
                    proxy.Username,
                    proxy.Password,
                    proxy.Secret),
                requireWarp: proxy.Kind == OutboundProxyKinds.Warp,
                cancellationToken);

        public Task<EgressProbeResult> ProbeProxyAsync(
            ProxyConnectionOptions options,
            bool requireWarp = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(options);
            Interlocked.Increment(ref _callCount);
            _events.Enqueue($"probe:{options.Host}");
            if (AsyncResultFactory != null)
                return AsyncResultFactory(options, cancellationToken);
            return Task.FromResult(ResultFactory(options));
        }
    }

    private sealed class RecordingSessionImporter : ISessionImporter
    {
        private int _importCount;
        private readonly ConcurrentQueue<string> _events;

        public RecordingSessionImporter(ConcurrentQueue<string> events)
        {
            _events = events;
        }

        public int ImportCount => Volatile.Read(ref _importCount);
        public Func<int, ImportResult>? ResultFactory { get; set; }
        public ConcurrentQueue<ProxyConnectionOptions?> SeenProxies { get; } = new();

        public Task<ImportResult> ImportFromSessionFileAsync(
            string filePath,
            int apiId,
            string apiHash,
            long? userId = null,
            string? phoneHint = null,
            string? sessionKey = null,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            ImportAsync(proxy);

        public async Task<List<ImportResult>> BatchImportSessionFilesAsync(
            string[] filePaths,
            int apiId,
            string apiHash,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            new() { await ImportAsync(proxy) };

        public Task<ImportResult> ImportFromStringSessionAsync(
            string sessionString,
            int apiId,
            string apiHash,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            ImportAsync(proxy);

        public Task<bool> ValidateSessionAsync(string sessionPath) => Task.FromResult(true);

        private Task<ImportResult> ImportAsync(ProxyConnectionOptions? proxy)
        {
            var count = Interlocked.Increment(ref _importCount);
            SeenProxies.Enqueue(proxy);
            _events.Enqueue($"import:{proxy?.Host ?? "direct"}");
            return Task.FromResult(ResultFactory?.Invoke(count) ?? new ImportResult(
                true,
                "8613800099999",
                99999,
                "imported",
                "sessions/8613800099999.session"));
        }
    }

    private sealed class RecordingClientPool : ITelegramClientPool
    {
        private int _getOrCreateCount;

        public int ActiveClientCount => 0;
        public int GetOrCreateCount => Volatile.Read(ref _getOrCreateCount);

        public Task<Client> GetOrCreateClientAsync(
            int accountId,
            int apiId,
            string apiHash,
            string sessionPath,
            string? sessionKey = null,
            string? phoneNumber = null,
            long? userId = null)
        {
            Interlocked.Increment(ref _getOrCreateCount);
            throw new NotSupportedException();
        }

        public Client? GetClient(int accountId) => null;
        public Task RemoveClientAsync(int accountId) => Task.CompletedTask;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }
}
