using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

public class BotManagementService
{
    private readonly IBotRepository _botRepository;
    private readonly IBotChannelRepository _botChannelRepository;
    private readonly IBotChannelCategoryRepository _categoryRepository;
    private readonly ILogger<BotManagementService> _logger;

    public BotManagementService(
        IBotRepository botRepository,
        IBotChannelRepository botChannelRepository,
        IBotChannelCategoryRepository categoryRepository,
        ILogger<BotManagementService> logger)
    {
        _botRepository = botRepository;
        _botChannelRepository = botChannelRepository;
        _categoryRepository = categoryRepository;
        _logger = logger;
    }

    public async Task<IEnumerable<Bot>> GetAllBotsAsync()
    {
        return await _botRepository.GetAllWithStatsAsync();
    }

    public async Task<Bot?> GetBotAsync(int id)
    {
        return await _botRepository.GetByIdAsync(id);
    }

    public async Task<Bot> CreateBotAsync(string name, string token, string? username = null)
    {
        name = (name ?? string.Empty).Trim();
        token = (token ?? string.Empty).Trim();
        username = string.IsNullOrWhiteSpace(username) ? null : username.Trim().TrimStart('@');

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("机器人名称不能为空", nameof(name));
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("机器人 Token 不能为空", nameof(token));

        var existing = await _botRepository.GetByNameAsync(name);
        if (existing != null)
            throw new InvalidOperationException("机器人名称已存在");

        var bot = new Bot
        {
            Name = name,
            Token = token,
            Username = username,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        return await _botRepository.AddAsync(bot);
    }

    public async Task UpdateBotAsync(Bot bot)
    {
        bot.Name = (bot.Name ?? string.Empty).Trim();
        bot.Token = (bot.Token ?? string.Empty).Trim();
        bot.Username = string.IsNullOrWhiteSpace(bot.Username) ? null : bot.Username.Trim().TrimStart('@');

        await _botRepository.UpdateAsync(bot);
    }

    public async Task DeleteBotAsync(int id)
    {
        var bot = await _botRepository.GetByIdAsync(id);
        if (bot == null)
            return;

        try
        {
            await _botRepository.DeleteAsync(bot);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 视为已被删除（幂等）
        }
    }

    public async Task<IEnumerable<BotChannelCategory>> GetCategoriesAsync(int botId)
    {
        return await _categoryRepository.GetForBotAsync(botId);
    }

    public async Task<BotChannelCategory> CreateCategoryAsync(int botId, string name, string? description = null)
    {
        name = (name ?? string.Empty).Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (botId <= 0)
            throw new ArgumentException("botId 无效", nameof(botId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("分类名称不能为空", nameof(name));

        var existing = await _categoryRepository.GetByNameAsync(botId, name);
        if (existing != null)
            throw new InvalidOperationException("分类名称已存在");

        var cat = new BotChannelCategory
        {
            BotId = botId,
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        return await _categoryRepository.AddAsync(cat);
    }

    public async Task DeleteCategoryAsync(int categoryId)
    {
        var cat = await _categoryRepository.GetByIdAsync(categoryId);
        if (cat == null)
            return;

        await _categoryRepository.DeleteAsync(cat);
    }

    public async Task<IEnumerable<BotChannel>> GetChannelsAsync(int botId, int? categoryId = null)
    {
        // Bot 频道列表仅展示“频道”（不展示群组/超级群组）
        var list = await _botChannelRepository.GetForBotAsync(botId, categoryId);
        return list.Where(x => x.IsBroadcast);
    }

    public async Task<BotChannel> UpsertChannelAsync(BotChannel channel)
    {
        if (channel.BotId <= 0)
            throw new ArgumentException("BotId 无效", nameof(channel));
        if (channel.TelegramId == 0)
            throw new ArgumentException("TelegramId 无效", nameof(channel));

        var existing = await _botChannelRepository.GetByTelegramIdAsync(channel.BotId, channel.TelegramId);
        if (existing != null)
        {
            existing.Title = channel.Title;
            existing.Username = channel.Username;
            existing.IsBroadcast = channel.IsBroadcast;
            existing.MemberCount = channel.MemberCount;
            existing.About = channel.About;
            existing.AccessHash = channel.AccessHash;
            if (channel.CreatedAt.HasValue)
                existing.CreatedAt = channel.CreatedAt;
            existing.SyncedAt = DateTime.UtcNow;

            await _botChannelRepository.UpdateAsync(existing);
            return existing;
        }

        channel.SyncedAt = DateTime.UtcNow;
        return await _botChannelRepository.AddAsync(channel);
    }

    public async Task DeleteChannelByTelegramIdAsync(int botId, long telegramId)
    {
        var existing = await _botChannelRepository.GetByTelegramIdAsync(botId, telegramId);
        if (existing == null)
            return;

        await _botChannelRepository.DeleteAsync(existing);
    }

    public async Task SetChannelCategoryAsync(int botChannelId, int? categoryId)
    {
        var ch = await _botChannelRepository.GetByIdAsync(botChannelId);
        if (ch == null)
            return;

        ch.CategoryId = categoryId;
        await _botChannelRepository.UpdateAsync(ch);
    }
}
