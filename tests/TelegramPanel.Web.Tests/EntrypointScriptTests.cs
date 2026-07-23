using System.Diagnostics;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class EntrypointScriptTests
{
    [Fact]
    public void Entrypoint_RollsBackAttemptedUnconfirmedVersion()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            var previous = Path.Combine(data, "app-previous");
            Directory.CreateDirectory(imageApp);
            CreateRunnableVersion(current, "bad");
            CreateRunnableVersion(previous, "good");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-update-attempted"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath);

            Assert.Equal(Path.GetFullPath(current), File.ReadAllText(resultPath).Trim());
            Assert.Equal("good", File.ReadAllText(Path.Combine(current, "version.txt")));
            Assert.False(Directory.Exists(previous));

            var failed = Assert.Single(Directory.GetDirectories(data, "app-failed-*"));
            Assert.Equal("bad", File.ReadAllText(Path.Combine(failed, "version.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_MarksPendingVersionAttemptedBeforeLaunching()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            Directory.CreateDirectory(imageApp);
            CreateRunnableVersion(current, "new");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-update-pending"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath);

            Assert.Equal(Path.GetFullPath(current), File.ReadAllText(resultPath).Trim());
            Assert.False(File.Exists(Path.Combine(current, ".telegram-panel-update-pending")));
            Assert.True(File.Exists(Path.Combine(current, ".telegram-panel-update-attempted")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_FallsBackToImageVersionWhenNoBackupExists()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            CreateRunnableVersion(imageApp, "image");
            CreateRunnableVersion(current, "bad");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-update-attempted"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath);

            Assert.Equal(Path.GetFullPath(imageApp), File.ReadAllText(resultPath).Trim());
            Assert.False(Directory.Exists(current));
            var failed = Assert.Single(Directory.GetDirectories(data, "app-failed-*"));
            Assert.Equal("bad", File.ReadAllText(Path.Combine(failed, "version.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_MigratesLegacyMarkerForPackageWithNewEntrypoint()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            Directory.CreateDirectory(imageApp);
            CreateRunnableVersion(current, "new");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-self-update"), "{}");
            var packagedEntrypoint = Path.Combine(current, "self-update", "entrypoint.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(packagedEntrypoint)!);
            File.WriteAllText(packagedEntrypoint, "#!/usr/bin/env sh\n");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath);

            Assert.Equal(Path.GetFullPath(current), File.ReadAllText(resultPath).Trim());
            Assert.True(File.Exists(Path.Combine(current, ".telegram-panel-update-attempted")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_PrefersNewerImageOverConfirmedOlderSelfUpdate()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            Directory.CreateDirectory(imageApp);
            CreateRunnableVersion(imageApp, "1.31.38");
            File.WriteAllText(
                Path.Combine(imageApp, "version.txt"),
                "1.31.38",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            CreateRunnableVersion(current, "1.31.37");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-update-confirmed"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath);

            Assert.Equal(Path.GetFullPath(imageApp), File.ReadAllText(resultPath).Trim());
            Assert.False(Directory.Exists(current));
            var obsolete = Assert.Single(Directory.GetDirectories(data, "app-obsolete-*"));
            Assert.Equal("1.31.37", File.ReadAllText(Path.Combine(obsolete, "version.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_PrefersNewerImageOverLegacyOlderSelfUpdate()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            CreateRunnableVersion(imageApp, "1.31.38");
            CreateRunnableVersion(current, "1.31.37");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-self-update"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath);

            Assert.Equal(Path.GetFullPath(imageApp), File.ReadAllText(resultPath).Trim());
            Assert.False(Directory.Exists(current));
            var obsolete = Assert.Single(Directory.GetDirectories(data, "app-obsolete-*"));
            Assert.Equal("1.31.37", File.ReadAllText(Path.Combine(obsolete, "version.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_PrefersNewerConfirmedSelfUpdateOverOlderImage()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            Directory.CreateDirectory(imageApp);
            CreateRunnableVersion(imageApp, "1.31.37");
            CreateRunnableVersion(current, "1.31.38");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-update-confirmed"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath);

            Assert.Equal(Path.GetFullPath(current), File.ReadAllText(resultPath).Trim());
            Assert.True(Directory.Exists(current));
            Assert.Empty(Directory.GetDirectories(data, "app-obsolete-*"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_ImageModeAlwaysUsesImageDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            CreateRunnableVersion(imageApp, "1.31.37");
            CreateRunnableVersion(current, "1.31.38");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-update-confirmed"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath, "image");

            Assert.Equal(Path.GetFullPath(imageApp), File.ReadAllText(resultPath).Trim());
            Assert.True(Directory.Exists(current));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Entrypoint_BinaryModeAlwaysUsesConfirmedBinaryDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTempDirectory();
        try
        {
            var data = Path.Combine(root, "data");
            var imageApp = Path.Combine(root, "app");
            var current = Path.Combine(data, "app-current");
            CreateRunnableVersion(imageApp, "1.31.38");
            CreateRunnableVersion(current, "1.31.37");
            File.WriteAllText(Path.Combine(current, ".telegram-panel-update-confirmed"), "{}");

            var resultPath = Path.Combine(root, "started-from.txt");
            RunEntrypoint(data, imageApp, resultPath, "binary");

            Assert.Equal(Path.GetFullPath(current), File.ReadAllText(resultPath).Trim());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void RunEntrypoint(string data, string imageApp, string resultPath, string? updateMode = null)
    {
        var scriptPath = FindRepositoryFile(Path.Combine("docker", "entrypoint.sh"));
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["TELEGRAM_PANEL_DATA_DIR"] = data;
        startInfo.Environment["TELEGRAM_PANEL_DEFAULT_APP_DIR"] = imageApp;
        startInfo.Environment["TELEGRAM_PANEL_DOTNET_COMMAND"] = "/bin/sh";
        startInfo.Environment["TELEGRAM_PANEL_TEST_RESULT"] = resultPath;
        if (!string.IsNullOrWhiteSpace(updateMode))
            startInfo.Environment["TELEGRAM_PANEL_UPDATE_MODE"] = updateMode;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动入口脚本测试进程");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("入口脚本测试超过 10 秒");
        }

        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"入口脚本退出码：{process.ExitCode}\n{standardError}");
    }

    private static void CreateRunnableVersion(string directory, string version)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "version.txt"), version);
        File.WriteAllText(
            Path.Combine(directory, "TelegramPanel.Web.dll"),
            "printf '%s' \"$PWD\" > \"$TELEGRAM_PANEL_TEST_RESULT\"\n");
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"未找到仓库文件：{relativePath}");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "telegram-panel-entrypoint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖断言结果。
        }
    }
}
