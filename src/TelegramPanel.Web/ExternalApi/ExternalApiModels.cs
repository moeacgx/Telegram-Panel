using System.Text.Json.Nodes;

namespace TelegramPanel.Web.ExternalApi;

public static class ExternalApiTypes
{
    public const string Kick = "kick";
}

public sealed class ExternalApiDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// 模块自定义配置（JSON object）。由具体 API 类型自行解释。
    /// </summary>
    public JsonObject Config { get; set; } = new();

    /// <summary>
    /// 踢人/封禁 API 的持久化配置；由可安装演示模块执行，保留已有配置和密钥合同。
    /// </summary>
    public KickApiDefinition? Kick { get; set; }
}

public sealed class KickApiDefinition
{
    public int BotId { get; set; } // 0=all bots
    public bool UseAllChats { get; set; } = true;
    public List<long> ChatIds { get; set; } = new();
    public bool PermanentBanDefault { get; set; }
}

