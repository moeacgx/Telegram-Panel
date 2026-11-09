using System.Diagnostics;
using System.Globalization;

namespace TelegramPanel.Web.Services;

/// <summary>只传递显式覆盖值；Python 统一校验 Linux 的父级 rlimit。</summary>
internal sealed class WgcfWireproxyResources
{
    private readonly List<string> _arguments = new();
    public string? Error { get; }

    public WgcfWireproxyResources(IConfiguration configuration)
    {
        foreach (var (key, option) in new[]
        {
            ("GoMaxProcs", "gomaxprocs"), ("GoMemLimitMiB", "gomemlimit-mib"), ("NoFile", "nofile")
        })
        {
            var name = $"WgcfWarp:Wireproxy:{key}";
            var value = configuration[name];
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Length > 10 || value.Any(c => c < '0' || c > '9')
                || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                Error = $"{name} 必须为 1～2147483647 的十进制整数；请修正后重启面板";
                return;
            }
            _arguments.Add($"--wireproxy-{option}={parsed.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    public void Apply(ProcessStartInfo start)
    {
        if (Error != null) throw new InvalidOperationException(Error);
        foreach (var argument in _arguments) start.ArgumentList.Add(argument);
    }
}
