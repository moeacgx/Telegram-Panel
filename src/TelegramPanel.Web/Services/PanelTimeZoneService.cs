using Microsoft.Extensions.Options;

namespace TelegramPanel.Web.Services;

public sealed class PanelTimeZoneService
{
    private readonly object _gate = new();
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;

    public PanelTimeZoneService(IOptionsMonitor<PanelTimeZoneOptions> optionsMonitor)
    {
        Apply(optionsMonitor.CurrentValue);
        optionsMonitor.OnChange(Apply);
    }

    public TimeZoneInfo Current
    {
        get
        {
            lock (_gate)
            {
                return _timeZone;
            }
        }
    }

    public string Format(DateTime? valueUtcOrUnspecified, string format = "yyyy-MM-dd HH:mm", string emptyText = "-")
    {
        if (valueUtcOrUnspecified == null)
            return emptyText;

        var converted = ConvertFromUtcOrUnspecified(valueUtcOrUnspecified.Value);
        return converted.ToString(format);
    }

    public DateTime ConvertFromUtcOrUnspecified(DateTime valueUtcOrUnspecified)
    {
        var utc = valueUtcOrUnspecified.Kind switch
        {
            DateTimeKind.Utc => valueUtcOrUnspecified,
            DateTimeKind.Local => valueUtcOrUnspecified.ToUniversalTime(),
            _ => DateTime.SpecifyKind(valueUtcOrUnspecified, DateTimeKind.Utc)
        };

        var tz = Current;
        return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
    }

    private void Apply(PanelTimeZoneOptions options)
    {
        var id = (options.TimeZoneId ?? string.Empty).Trim();

        var tz = Resolve(id);
        lock (_gate)
        {
            _timeZone = tz;
        }
    }

    private static TimeZoneInfo Resolve(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Utc;

        if (TryFind(timeZoneId, out var tz))
            return tz;

        // 常见跨平台兜底：IANA <-> Windows
        if (string.Equals(timeZoneId, "Asia/Shanghai", StringComparison.OrdinalIgnoreCase)
            && TryFind("China Standard Time", out tz))
            return tz;

        if (string.Equals(timeZoneId, "China Standard Time", StringComparison.OrdinalIgnoreCase)
            && TryFind("Asia/Shanghai", out tz))
            return tz;

        if (string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase) && TryFind("Etc/UTC", out tz))
            return tz;

        if (string.Equals(timeZoneId, "Etc/UTC", StringComparison.OrdinalIgnoreCase) && TryFind("UTC", out tz))
            return tz;

        return TimeZoneInfo.Utc;
    }

    private static bool TryFind(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}

