using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class AdminAuthSecurityTests
{
    [Theory]
    [InlineData("GET", "/login", true)]
    [InlineData("GET", "/api/panel/auth/me", true)]
    [InlineData("GET", "/ui/admin/password", true)]
    [InlineData("POST", "/login", true)]
    [InlineData("POST", "/api/panel/auth/login", true)]
    [InlineData("POST", "/api/panel/settings/password", true)]
    [InlineData("POST", "/api/panel/settings/username", true)]
    [InlineData("GET", "/api/panel/accounts", false)]
    [InlineData("POST", "/api/panel/accounts/batch/delete", false)]
    [InlineData("GET", "/uploads/private.zip", false)]
    public void SetupAllowlistIsExplicit(string method, string path, bool expected)
    {
        Assert.Equal(expected, AdminAuthSecurity.IsSetupPath(path, method));
    }
}
