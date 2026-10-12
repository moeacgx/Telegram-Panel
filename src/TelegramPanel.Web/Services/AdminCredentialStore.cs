using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TelegramPanel.Web.Services;

public sealed class AdminCredentialStore
{
    public sealed record AuthenticationSnapshot(string Username, string SecurityStamp);
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IOptionsMonitor<AdminAuthOptions> _options;
    private readonly ILogger<AdminCredentialStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private AdminCredentialFile? _cached;

    public AdminCredentialStore(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IOptionsMonitor<AdminAuthOptions> options,
        ILogger<AdminCredentialStore> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.CurrentValue.Enabled;

    public string Username => (_cached?.Username ?? _options.CurrentValue.InitialUsername).Trim();

    public bool MustChangePassword => _cached?.MustChangePassword == true;

    public string SecurityStamp => _cached?.SecurityStamp ?? string.Empty;

    public string CredentialsFilePath
    {
        get
        {
            var path = StoragePathResolver.ResolveWritablePath(
                _configuration, _environment, _options.CurrentValue.CredentialsPath, "admin_auth.json");
            var webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
                ? Path.Combine(_environment.ContentRootPath, "wwwroot")
                : _environment.WebRootPath;
            var physicalPath = StoragePathResolver.ResolvePhysicalPath(path);
            var physicalWebRoot = StoragePathResolver.ResolvePhysicalPath(webRoot);
            if (StoragePathResolver.IsPathWithin(path, webRoot)
                || StoragePathResolver.IsPathWithin(physicalPath, physicalWebRoot))
                throw new InvalidOperationException("后台凭据必须放在静态资源目录之外");
            return path;
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached != null)
                return;

            var path = CredentialsFilePath;
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var existing = JsonSerializer.Deserialize<AdminCredentialFile>(json) ?? throw new InvalidOperationException("admin_auth.json 解析失败");
                var changed = false;
                if (string.IsNullOrWhiteSpace(existing.SecurityStamp))
                {
                    existing.SecurityStamp = NewSecurityStamp();
                    changed = true;
                }
                if (existing.MustChangePassword && VerifyPassword(existing, "tgpanel123"))
                {
                    var bootstrap = await EnsureBootstrapSecretAsync(cancellationToken);
                    ApplyPassword(existing, bootstrap);
                    existing.SecurityStamp = NewSecurityStamp();
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    changed = true;
                }
                if (changed)
                    await SaveAsync(existing, cancellationToken);
                _cached = existing;
                if (!existing.MustChangePassword)
                    TryDeleteBootstrapSecret();
                return;
            }

            var opt = _options.CurrentValue;
            var initialUsername = (opt.InitialUsername ?? "tgpanel").Trim();
            if (string.IsNullOrWhiteSpace(initialUsername))
                throw new InvalidOperationException("AdminAuth 初始账号未配置");
            var configuredPassword = (opt.InitialPassword ?? string.Empty).Trim();
            var initialPassword = string.IsNullOrWhiteSpace(configuredPassword) || configuredPassword == "tgpanel123"
                ? await EnsureBootstrapSecretAsync(cancellationToken)
                : configuredPassword;

            var now = DateTime.UtcNow;
            var file = CreateCredentialFile(initialUsername, initialPassword, mustChangePassword: true, now);

            await SaveAsync(file, cancellationToken);
            _cached = file;

            _logger.LogWarning("后台登录已初始化：账号 {Username}，请使用配置的初始密码或持久化目录内的初始化密码文件登录并立即改密", initialUsername);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ValidateAsync(string? username, string? password, CancellationToken cancellationToken = default)
    {
        return await ValidateForAuthenticationAsync(username, password, cancellationToken) != null;
    }

    public async Task<AuthenticationSnapshot?> ValidateForAuthenticationAsync(
        string? username, string? password, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return new AuthenticationSnapshot((username ?? string.Empty).Trim(), string.Empty);

        await EnsureInitializedAsync(cancellationToken);

        username = (username ?? string.Empty).Trim();
        password = (password ?? string.Empty).Trim();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var file = _cached ?? throw new InvalidOperationException("凭据未初始化");
            // 不提前拒绝未知账号：全部请求执行同一 PBKDF2 校验，减少账号枚举时序差异。
            var matchesUser = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(username)),
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.Username)));
            var validPassword = VerifyPassword(file, password);
            return matchesUser && validPassword
                ? new AuthenticationSnapshot(file.Username, file.SecurityStamp)
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AuthenticationSnapshot> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("后台验证未启用");

        await EnsureInitializedAsync(cancellationToken);

        currentPassword = (currentPassword ?? string.Empty).Trim();
        newPassword = (newPassword ?? string.Empty).Trim();

        if (newPassword.Length < 12)
            throw new InvalidOperationException("新密码长度至少 12 位");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var file = _cached ?? throw new InvalidOperationException("凭据未初始化");
            if (!VerifyPassword(file, currentPassword))
                throw new InvalidOperationException("当前密码错误");
            if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
                throw new InvalidOperationException("新密码不能与当前密码相同");

            file = Clone(file);
            var now = DateTime.UtcNow;
            ApplyPassword(file, newPassword);
            file.MustChangePassword = false;
            file.SecurityStamp = NewSecurityStamp();
            file.UpdatedAtUtc = now;

            await SaveAsync(file, cancellationToken);
            _cached = file;
            TryDeleteBootstrapSecret();
            return new AuthenticationSnapshot(file.Username, file.SecurityStamp);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AuthenticationSnapshot> ChangeUsernameAsync(string currentPassword, string newUsername, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("后台验证未启用");

        await EnsureInitializedAsync(cancellationToken);

        currentPassword = (currentPassword ?? string.Empty).Trim();
        newUsername = NormalizeUsername(newUsername);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var file = _cached ?? throw new InvalidOperationException("凭据未初始化");
            if (!VerifyPassword(file, currentPassword))
                throw new InvalidOperationException("当前密码错误");

            file = Clone(file);
            file.Username = newUsername;
            file.UpdatedAtUtc = DateTime.UtcNow;
            file.SecurityStamp = NewSecurityStamp();

            await SaveAsync(file, cancellationToken);
            _cached = file;
            return new AuthenticationSnapshot(file.Username, file.SecurityStamp);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync(AdminCredentialFile file, CancellationToken cancellationToken)
    {
        var path = CredentialsFilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        await WriteSecretAtomicallyAsync(path, json, cancellationToken);
    }

    private async Task<string> EnsureBootstrapSecretAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetDirectoryName(CredentialsFilePath)!, "admin-bootstrap-password.txt");
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (File.Exists(path))
        {
            var existing = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            if (existing.Length >= 12)
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return existing;
            }
        }
        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await WriteSecretAtomicallyAsync(path, generated, cancellationToken);
        return generated;
    }

    private static async Task WriteSecretAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        // 先持久化临时秘密，再替换目标；失败时保留原凭据和可恢复的初始化密码。
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void TryDeleteBootstrapSecret()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(CredentialsFilePath)!, "admin-bootstrap-password.txt");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "清理后台初始化密码文件失败"); }
    }

    private static string NewSecurityStamp() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AdminCredentialFile Clone(AdminCredentialFile file) =>
        JsonSerializer.Deserialize<AdminCredentialFile>(JsonSerializer.Serialize(file))!;

    private static AdminCredentialFile CreateCredentialFile(string username, string password, bool mustChangePassword, DateTime nowUtc)
    {
        username = NormalizeUsername(username);
        var file = new AdminCredentialFile
        {
            Version = 1,
            Username = username,
            MustChangePassword = mustChangePassword,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            SecurityStamp = NewSecurityStamp()
        };
        ApplyPassword(file, password);
        return file;
    }

    private static void ApplyPassword(AdminCredentialFile file, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 150_000;
        file.SaltBase64 = Convert.ToBase64String(salt);
        file.HashBase64 = Convert.ToBase64String(HashPassword(password, salt, iterations));
        file.Iterations = iterations;
    }

    internal static bool TryNormalizeUsername(
        string? username,
        out string normalizedUsername,
        out string? error)
    {
        normalizedUsername = (username ?? string.Empty).Trim();
        if (normalizedUsername.Length < 4 || normalizedUsername.Length > 32)
        {
            error = "后台用户名长度应为 4-32 位";
            return false;
        }

        if (!normalizedUsername.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
        {
            error = "后台用户名只能包含字母、数字、下划线、短横线或点";
            return false;
        }

        if (string.Equals(normalizedUsername, "admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedUsername, "administrator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedUsername, "root", StringComparison.OrdinalIgnoreCase))
        {
            error = "请不要使用常见后台用户名";
            return false;
        }

        error = null;
        return true;
    }

    private static string NormalizeUsername(string? username)
    {
        if (!TryNormalizeUsername(username, out var normalizedUsername, out var error))
            throw new InvalidOperationException(error);

        return normalizedUsername;
    }

    private static byte[] HashPassword(string password, byte[] salt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static bool VerifyPassword(AdminCredentialFile file, string password)
    {
        var salt = Convert.FromBase64String(file.SaltBase64);
        var expected = Convert.FromBase64String(file.HashBase64);
        var actual = HashPassword(password, salt, file.Iterations);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private sealed class AdminCredentialFile
    {
        public int Version { get; set; }
        public string Username { get; set; } = "admin";
        public string SaltBase64 { get; set; } = "";
        public string HashBase64 { get; set; } = "";
        public int Iterations { get; set; } = 150_000;
        public bool MustChangePassword { get; set; } = true;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public string SecurityStamp { get; set; } = "";
    }
}

