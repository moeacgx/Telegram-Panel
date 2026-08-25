using System.Text;
using TelegramPanel.Core.Services.Telegram;
using TL;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramLayerCompatibilityTests
{
    [Fact]
    public void 历史构造器映射到当前兼容类型且不覆盖当前Layer构造器()
    {
        TelegramLayerCompatibility.EnsureRegistered();

        Assert.True(Layer.Table.ContainsKey(TelegramLayerCompatibility.LegacyUserConstructor));
        Assert.True(Layer.Table.ContainsKey(TelegramLayerCompatibility.LegacyChannelConstructor));
        Assert.True(Layer.Table.ContainsKey(0xB1B8CC83));
        Assert.True(Layer.Table.ContainsKey(0xD49F34C6));
    }

    [Fact]
    public void 历史用户最小负载可以按当前布局读取()
    {
        TelegramLayerCompatibility.EnsureRegistered();

        const long userId = 31774388;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(userId);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var user = Assert.IsType<User>(Layer.Table[TelegramLayerCompatibility.LegacyUserConstructor](reader));

        Assert.Equal(userId, user.id);
    }

    [Fact]
    public void 历史频道最小负载可以按当前布局读取()
    {
        TelegramLayerCompatibility.EnsureRegistered();

        const long channelId = 162;
        const string title = "legacy channel";
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(channelId);
            WriteTlString(writer, title);
            writer.Write(0x37C1011Cu);
            writer.Write(0);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var channel = Assert.IsType<Channel>(Layer.Table[TelegramLayerCompatibility.LegacyChannelConstructor](reader));

        Assert.Equal(channelId, channel.id);
        Assert.Equal(title, channel.title);
    }

    private static void WriteTlString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Assert.InRange(bytes.Length, 0, 253);

        writer.Write((byte)bytes.Length);
        writer.Write(bytes);

        var padding = (4 - ((bytes.Length + 1) % 4)) % 4;
        for (var index = 0; index < padding; index++)
            writer.Write((byte)0);
    }
}
