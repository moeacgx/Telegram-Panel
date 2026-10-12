using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class AdminCredentialSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tgpanel-auth-tests", Guid.NewGuid().ToString("N"));
    private string Credentials => Path.Combine(_root, "admin_auth.json");
    private string Bootstrap => Path.Combine(_root, "admin-bootstrap-password.txt");

    [Theory]
    [InlineData("")]
    [InlineData("tgpanel123")]
    public async Task Initialization_GeneratesRecoverablePassword_AndRestartPreservesIt(string configuredPassword)
    {
        var store = CreateStore(configuredPassword);
        await store.EnsureInitializedAsync();
        Assert.True(File.Exists(Bootstrap));
        var password = (await File.ReadAllTextAsync(Bootstrap)).Trim();
        Assert.True(password.Length >= 32);
        Assert.NotEqual("tgpanel123", password);
        Assert.True(await store.ValidateAsync("tgpanel", password));
        Assert.False(await store.ValidateAsync("unknown", password));
        Assert.False(await store.ValidateAsync("tgpanel", "tgpanel123"));
        var credential = await File.ReadAllTextAsync(Credentials);
        var restarted = CreateStore(configuredPassword);
        await restarted.EnsureInitializedAsync();
        Assert.Equal(credential, await File.ReadAllTextAsync(Credentials));
        Assert.Equal(password, (await File.ReadAllTextAsync(Bootstrap)).Trim());
        Assert.True(await restarted.ValidateAsync("tgpanel", password));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Bootstrap));
    }

    [Fact]
    public async Task ExistingCredentials_ArePreserved_AndReceiveStamp()
    {
        WriteLegacy("admin", "existing-secret", false);
        var store = CreateStore();
        await store.EnsureInitializedAsync();
        Assert.True(await store.ValidateAsync("admin", "existing-secret"));
        Assert.False(store.MustChangePassword);
        Assert.False(File.Exists(Bootstrap));
        var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Credentials));
        Assert.False(string.IsNullOrWhiteSpace(saved.RootElement.GetProperty("SecurityStamp").GetString()));
    }

    [Fact]
    public async Task LegacyUnchangedDefault_RotatesPassword_AndPreservesAdminUsername()
    {
        WriteLegacy("admin", "tgpanel123", true);
        var store = CreateStore();
        await store.EnsureInitializedAsync();
        Assert.Equal("admin", store.Username);
        Assert.False(await store.ValidateAsync("admin", "tgpanel123"));
        Assert.True(await store.ValidateAsync("admin", (await File.ReadAllTextAsync(Bootstrap)).Trim()));
        Assert.True(store.MustChangePassword);
    }

    [Fact]
    public async Task UsernameChange_DoesNotClearRequiredPasswordChange()
    {
        var store = CreateStore("configured-secret");
        await store.EnsureInitializedAsync();
        await store.ChangeUsernameAsync("configured-secret", "manager");
        Assert.True(store.MustChangePassword);
        Assert.True(await store.ValidateAsync("manager", "configured-secret"));
    }

    [Fact]
    public async Task PasswordChange_RejectsShortOrIdenticalPassword_ThenRemovesBootstrapAndRotatesStamp()
    {
        var store = CreateStore();
        await store.EnsureInitializedAsync();
        var password = (await File.ReadAllTextAsync(Bootstrap)).Trim();
        var previous = JsonDocument.Parse(await File.ReadAllTextAsync(Credentials));
        var stamp = previous.RootElement.GetProperty("SecurityStamp").GetString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ChangePasswordAsync(password, password));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ChangePasswordAsync(password, "short"));
        Assert.True(store.MustChangePassword);
        Assert.True(File.Exists(Bootstrap));
        await store.ChangePasswordAsync(password, "new-password-long-enough");
        Assert.False(store.MustChangePassword);
        Assert.False(File.Exists(Bootstrap));
        Assert.True(await store.ValidateAsync("tgpanel", "new-password-long-enough"));
        Assert.False(await store.ValidateAsync("tgpanel", password));
        var updated = JsonDocument.Parse(await File.ReadAllTextAsync(Credentials));
        Assert.NotEqual(stamp, updated.RootElement.GetProperty("SecurityStamp").GetString());
    }

    [Fact]
    public async Task InterruptedInitialization_ReusesPreparedBootstrapSecret()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Bootstrap, "prepared-random-password-long-enough");
        var store = CreateStore();
        await store.EnsureInitializedAsync();
        Assert.True(await store.ValidateAsync("tgpanel", "prepared-random-password-long-enough"));
    }

    [Fact]
    public async Task FailedCredentialWrite_RetainsBootstrap_AndCanRetrySameStore()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Credentials);
        var error = await Record.ExceptionAsync(() => store.EnsureInitializedAsync());
        Assert.True(error is IOException or UnauthorizedAccessException);
        var password = await File.ReadAllTextAsync(Bootstrap);
        Directory.Delete(Credentials);
        await store.EnsureInitializedAsync();
        Assert.True(await store.ValidateAsync("tgpanel", password));
        Assert.Equal(password, await File.ReadAllTextAsync(Bootstrap));
    }

    [Theory]
    [InlineData("custom-existing-secret", true)]
    [InlineData("tgpanel123", false)]
    public async Task ExistingCredentials_OutsideLegacyDefaultInitialization_AreNotRotated(string password, bool mustChange)
    {
        WriteLegacy("admin", password, mustChange);
        var store = CreateStore();
        await store.EnsureInitializedAsync();
        Assert.True(await store.ValidateAsync("admin", password));
        Assert.Equal(mustChange, store.MustChangePassword);
        Assert.False(File.Exists(Bootstrap));
    }

    [Fact]
    public async Task ExplicitInitialPassword_IsHonored_WithoutPlaintextFile()
    {
        var store = CreateStore("configured-secret");
        await store.EnsureInitializedAsync();
        Assert.True(await store.ValidateAsync("tgpanel", "configured-secret"));
        Assert.False(File.Exists(Bootstrap));
        Assert.True(store.MustChangePassword);
    }

    [Fact]
    public async Task CredentialPath_UnderWebRoot_IsRejectedBeforeWritingSecrets()
    {
        var store = CreateStore(credentialsPath: "wwwroot/uploads/admin_auth.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureInitializedAsync());
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CredentialPath_ThroughLinkIntoWebRoot_IsRejected()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "linked-data"), Path.Combine(_root, "wwwroot"));
        var store = CreateStore(credentialsPath: "linked-data/admin_auth.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureInitializedAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "wwwroot")));
    }

    [Fact]
    public async Task FailedPasswordSave_DoesNotMutateCachedCredentialOrDeleteBootstrap()
    {
        var store = CreateStore();
        await store.EnsureInitializedAsync();
        var password = await File.ReadAllTextAsync(Bootstrap);
        var stamp = store.SecurityStamp;
        var backup = Credentials + ".backup";
        File.Move(Credentials, backup);
        Directory.CreateDirectory(Credentials);
        try
        {
            var error = await Record.ExceptionAsync(() => store.ChangePasswordAsync(password, "new-password-long-enough"));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(store.MustChangePassword);
            Assert.Equal(stamp, store.SecurityStamp);
            Assert.True(await store.ValidateAsync("tgpanel", password));
            Assert.True(File.Exists(Bootstrap));
        }
        finally
        {
            Directory.Delete(Credentials);
            File.Move(backup, Credentials);
        }
    }

    private AdminCredentialStore CreateStore(string initialPassword = "", string credentialsPath = "admin_auth.json")
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = _root
        }).Build();
        return new AdminCredentialStore(config, new TestEnvironment { ContentRootPath = _root },
            new TestOptions(new AdminAuthOptions { Enabled = true, InitialPassword = initialPassword, CredentialsPath = credentialsPath }),
            NullLogger<AdminCredentialStore>.Instance);
    }

    private void WriteLegacy(string username, string password, bool mustChange)
    {
        Directory.CreateDirectory(_root);
        var salt = RandomNumberGenerator.GetBytes(16);
        File.WriteAllText(Credentials, JsonSerializer.Serialize(new
        {
            Version = 1, Username = username, SaltBase64 = Convert.ToBase64String(salt),
            HashBase64 = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32)),
            Iterations = 150_000, MustChangePassword = mustChange, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        }));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class TestOptions(AdminAuthOptions value) : IOptionsMonitor<AdminAuthOptions>
    {
        public AdminAuthOptions CurrentValue => value;
        public AdminAuthOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AdminAuthOptions, string?> listener) => null;
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
