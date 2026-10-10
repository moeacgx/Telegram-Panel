using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;

namespace TelegramPanel.Core;

/// <summary>
/// Core层服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramPanelCore(this IServiceCollection services)
    {
        services.AddSingleton<TelegramAccountUpdateHub>();
        services.AddSingleton<IAccountProxyResolver, AccountProxyResolver>();
        services.AddSingleton<ISessionPathResolver, SessionPathResolver>();
        services.AddSingleton<TelegramApiProfilePool>();
        services.AddSingleton<TemporaryWarpClaimStore>();

        // 注册 Telegram 客户端池（单例）
        services.AddSingleton<ITelegramClientPool, TelegramClientPool>();

        // 注册 Telegram 操作服务
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IChannelService, ChannelService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<ISessionImporter, SessionImporter>();
        services.AddScoped<AccountTelegramToolsService>();
        services.AddSingleton<AccountLoginEmailService>();
        services.AddScoped<BotTelegramService>();
        services.AddScoped<ProxyEgressProbeService>();
        services.AddScoped<IProxyEgressProbeService>(sp =>
            sp.GetRequiredService<ProxyEgressProbeService>());
        services.AddScoped<GlobalProxyResolver>();
        services.AddScoped<ProxyManagementService>();

        // Bot API updates（getUpdates）统一轮询与分发：避免 409 Conflict
        services.AddSingleton<BotUpdateHub>();

        // 注册账号导入协调服务
        services.AddScoped<AccountImportService>();

        // 注册数据管理服务
        services.AddScoped<AccountManagementService>();
        services.AddScoped<AccountCategoryManagementService>();
        services.AddScoped<ChannelManagementService>();
        services.AddScoped<ChannelGroupManagementService>();
        services.AddScoped<GroupManagementService>();
        services.AddScoped<GroupCategoryManagementService>();
        services.AddScoped<BatchTaskManagementService>();
        services.AddScoped<BotManagementService>();

        // 注册风控服务
        services.AddScoped<AccountRiskService>();

        return services;
    }
}
