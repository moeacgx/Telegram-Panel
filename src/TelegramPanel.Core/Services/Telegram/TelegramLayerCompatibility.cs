using TL;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 为当前 WTelegram Layer 注册已验证可兼容的相邻历史构造器。
/// </summary>
internal static class TelegramLayerCompatibility
{
    internal const uint LegacyUserConstructor = 0x31774388;
    internal const uint LegacyChannelConstructor = 0x1C32B11C;
    private static readonly object RegistrationLock = new();
    private static bool _registered;

    internal static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (_registered)
                return;

            // Layer 227 的两个负载与当前类型字段顺序一致，仅缺少尾部的可选字段。
            Layer.Table.TryAdd(LegacyUserConstructor, User.ReadTL);
            Layer.Table.TryAdd(LegacyChannelConstructor, Channel.ReadTL);
            _registered = true;
        }
    }
}
