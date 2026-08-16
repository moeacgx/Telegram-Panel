using System.Text.Json;
using System.Text.RegularExpressions;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class UserChatActiveTaskRerunBuilder : IModuleTaskRerunBuilder
{
    public string TaskType => BatchTaskTypes.UserChatActive;

    public ModuleTaskCreateRequest Build(ModuleTaskSnapshot task)
    {
        var rerunConfig = BuildRerunConfig(task.Config);
        var total = rerunConfig.MaxMessages > 0 ? rerunConfig.MaxMessages : 0;
        var configJson = JsonSerializer.Serialize(rerunConfig, new JsonSerializerOptions { WriteIndented = true });

        return new ModuleTaskCreateRequest
        {
            TaskType = BatchTaskTypes.UserChatActive,
            Total = total,
            Config = configJson
        };
    }

    private static UserChatActiveTaskConfig BuildRerunConfig(string? rawConfig)
    {
        var raw = (rawConfig ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("任务配置为空，无法重新运行");

        UserChatActiveTaskConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<UserChatActiveTaskConfig>(raw)
                  ?? throw new InvalidOperationException("任务配置解析结果为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"任务配置 JSON 无效：{ex.Message}");
        }

        cfg.CategoryIds = NormalizeCategoryIdsForRerun(cfg.CategoryIds, cfg.CategoryId);
        if (cfg.CategoryIds.Count == 0)
            throw new InvalidOperationException("任务缺少账号分类，无法重新运行");

        cfg.CategoryId = cfg.CategoryIds[0];
        cfg.CategoryNames = NormalizeCategoryNamesForRerun(cfg.CategoryNames, cfg.CategoryName);
        cfg.CategoryName = cfg.CategoryNames.FirstOrDefault() ?? cfg.CategoryName;

        cfg.Targets = (cfg.Targets ?? new List<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        cfg.MessageActionMode = UserChatActiveMessageActionModes.Normalize(cfg.MessageActionMode);
        cfg.ForwardMode = UserChatActiveForwardModes.Normalize(cfg.ForwardMode);
        var isForwardSourceMode = string.Equals(cfg.MessageActionMode, UserChatActiveMessageActionModes.ForwardUrl, StringComparison.Ordinal);
        if (isForwardSourceMode)
        {
            cfg.ReplyToMessageUrl = null;
            cfg.ReplyToMessageId = null;
            cfg.ForwardSourceUrls = NormalizeForwardSourceUrls(cfg.ForwardSourceUrls);
            cfg.MessageRules = new List<UserChatActiveMessageRule>();
            cfg.Dictionary = new List<string>();
            cfg.ImageDictionaryToken = null;
            cfg.EnableAiVerification = false;
            cfg.AiModel = null;
        }
        else
        {
            cfg.ReplyToMessageUrl = NormalizeOptionalText(cfg.ReplyToMessageUrl);
            cfg.ReplyToMessageId = cfg.ReplyToMessageId is > 0 ? cfg.ReplyToMessageId : null;
            cfg.ForwardSourceUrls = new List<string>();
            cfg.MessageRules = UserChatActiveMessageRuleNormalizer.Normalize(cfg);
        }

        if (cfg.Targets.Count == 0)
            throw new InvalidOperationException("任务缺少目标群组/频道/Bot，无法重新运行");

        if (isForwardSourceMode)
        {
            if (cfg.ForwardSourceUrls.Count == 0)
                throw new InvalidOperationException("转发模式缺少消息链接，无法重新运行");

            foreach (var sourceUrl in cfg.ForwardSourceUrls)
            {
                if (!AccountTelegramToolsService.TryParseTelegramMessageReference(sourceUrl, out _, out var error))
                    throw new InvalidOperationException($"转发消息链接无效：{error ?? sourceUrl}");
            }
        }
        else
        {
            if (cfg.MessageRules.Count == 0)
                throw new InvalidOperationException("任务缺少消息规则，无法重新运行");

            if (!string.IsNullOrWhiteSpace(cfg.ReplyToMessageUrl))
            {
                if (!AccountTelegramToolsService.TryParseTelegramMessageReference(cfg.ReplyToMessageUrl, out var reference, out var error) || reference == null)
                    throw new InvalidOperationException($"回复消息链接无效：{error ?? cfg.ReplyToMessageUrl}");

                if (cfg.ReplyToMessageId is > 0 && cfg.ReplyToMessageId.Value != reference.MessageId)
                    throw new InvalidOperationException("回复消息链接和消息 ID 不一致");

                cfg.ReplyToMessageId = reference.MessageId;
                cfg.ReplyToMessageUrl = reference.RawUrl;
            }
        }

        if (cfg.DelayMinMs < 0) cfg.DelayMinMs = 0;
        if (cfg.DelayMaxMs < 0) cfg.DelayMaxMs = 0;
        if (cfg.DelayMinMs > 600000) cfg.DelayMinMs = 600000;
        if (cfg.DelayMaxMs > 600000) cfg.DelayMaxMs = 600000;
        if (cfg.DelayMaxMs < cfg.DelayMinMs) cfg.DelayMaxMs = cfg.DelayMinMs;
        if (cfg.MaxMessages < 0) cfg.MaxMessages = 0;
        if (cfg.VerificationTimeoutSeconds < 3) cfg.VerificationTimeoutSeconds = 15;
        if (cfg.VerificationTimeoutSeconds > 300) cfg.VerificationTimeoutSeconds = 300;

        cfg.AiModel = AiOpenAiSettingsSnapshot.NormalizeModel(cfg.AiModel);

        cfg.AccountMode = NormalizeModeValue(cfg.AccountMode);
        cfg.TargetMode = NormalizeModeValue(cfg.TargetMode);
        cfg.MessageMode = NormalizeModeValue(cfg.MessageMode);

        cfg.VerificationMatchMode = UserChatActiveAiVerificationMatchModes.Normalize(cfg.VerificationMatchMode);
        cfg.VerificationKeywords = NormalizeVerificationItems(cfg.VerificationKeywords);
        cfg.VerificationRegexes = NormalizeVerificationItems(cfg.VerificationRegexes);
        cfg.VerificationBotUsernames = NormalizeBotUsernames(cfg.VerificationBotUsernames);
        if (isForwardSourceMode)
        {
            cfg.VerificationTimeoutSeconds = 15;
            cfg.VerificationTimeoutAsFailure = false;
            cfg.VerificationMatchMode = UserChatActiveAiVerificationMatchModes.MentionOrReply;
            cfg.VerificationKeywords = new List<string>();
            cfg.VerificationRegexes = new List<string>();
            cfg.VerificationBotUsernameFilterEnabled = false;
            cfg.VerificationBotUsernames = new List<string>();
        }


        if (cfg.EnableAiVerification)
        {
            if (string.Equals(cfg.VerificationMatchMode, UserChatActiveAiVerificationMatchModes.Keyword, StringComparison.Ordinal)
                && cfg.VerificationKeywords.Count == 0)
            {
                throw new InvalidOperationException("AI 验证已启用，但未配置关键词匹配内容");
            }

            if (cfg.VerificationBotUsernameFilterEnabled && cfg.VerificationBotUsernames.Count == 0)
                throw new InvalidOperationException("AI 验证已启用，但未配置允许的机器人用户名");

            if (string.Equals(cfg.VerificationMatchMode, UserChatActiveAiVerificationMatchModes.Regex, StringComparison.Ordinal))
            {
                if (cfg.VerificationRegexes.Count == 0)
                    throw new InvalidOperationException("AI 验证已启用，但未配置正则匹配内容");

                foreach (var pattern in cfg.VerificationRegexes)
                {
                    try
                    {
                        _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"AI 验证正则无效：{ex.Message}");
                    }
                }
            }
        }

        cfg.Canceled = false;
        cfg.Error = null;
        cfg.RecentFailures = new List<UserChatActiveTaskRuntimeFailure>();

        return cfg;
    }


    private static List<int> NormalizeCategoryIdsForRerun(IEnumerable<int>? values, int fallback)
    {
        var ids = (values ?? Array.Empty<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0 && fallback > 0)
            ids.Add(fallback);

        return ids;
    }

    private static List<string> NormalizeCategoryNamesForRerun(IEnumerable<string>? values, string? fallback)
    {
        var names = (values ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fallbackName = (fallback ?? string.Empty).Trim();
        if (names.Count == 0 && fallbackName.Length > 0)
            names.Add(fallbackName);

        return names;
    }

    private static string NormalizeModeValue(string? mode)
    {
        return string.Equals((mode ?? string.Empty).Trim(), UserChatActiveTaskModes.Queue, StringComparison.OrdinalIgnoreCase)
            ? UserChatActiveTaskModes.Queue
            : UserChatActiveTaskModes.Random;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static List<string> NormalizeForwardSourceUrls(IEnumerable<string>? urls)
    {
        return (urls ?? Array.Empty<string>())
            .SelectMany(x => (x ?? string.Empty).Split(new[] { "\r\n", "\n", "\r", ",", " " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> NormalizeVerificationItems(IEnumerable<string>? items)
    {
        return (items ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    private static List<string> NormalizeBotUsernames(IEnumerable<string>? items)
    {
        return (items ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim().TrimStart('@'))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
