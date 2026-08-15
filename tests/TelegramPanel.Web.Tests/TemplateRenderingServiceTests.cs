using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class TemplateRenderingServiceTests
{
    [Theory]
    [InlineData("第一行\\n第二行", "第一行\n第二行")]
    [InlineData("第一行/n第二行", "第一行\n第二行")]
    [InlineData("不换行", "不换行")]
    public void 文本模板支持转义换行(string input, string expected)
    {
        Assert.Equal(expected, TemplateRenderingService.NormalizeEscapedNewlines(input));
    }
}
