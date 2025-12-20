namespace TelegramPanel.Web.ExternalApi;

public static class ExternalApiTypes
{
    public const string Kick = "kick";
}

public sealed class ExternalApiDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Type { get; set; } = ExternalApiTypes.Kick;
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";

    public KickApiDefinition Kick { get; set; } = new();
}

public sealed class KickApiDefinition
{
    public int BotId { get; set; } // 0=all bots
    public bool UseAllChats { get; set; } = true;
    public List<long> ChatIds { get; set; } = new();
    public bool PermanentBanDefault { get; set; }
}

