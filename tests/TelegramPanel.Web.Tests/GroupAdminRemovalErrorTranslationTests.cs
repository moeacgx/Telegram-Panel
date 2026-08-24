using TelegramPanel.Core.Services.Telegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class GroupAdminRemovalErrorTranslationTests
{
    [Theory]
    [InlineData("400 CHAT_ADMIN_REQUIRED", "执行账号缺少取消管理员或封禁成员权限，请更换具备完整权限的群组管理员")]
    [InlineData("400 RIGHT_FORBIDDEN", "执行账号缺少取消管理员或封禁成员权限，请更换具备完整权限的群组管理员")]
    [InlineData("420 FLOOD_WAIT_17", "Telegram 风控限流，请等待约 17 秒后再试")]
    public void 管理员流程读取阶段的Rpc错误会转换为可操作中文提示(string error, string expected)
    {
        Assert.Equal(expected, GroupService.TranslateGroupAdminRemovalError(error));
    }
}
