using System.Text.Json.Serialization;

namespace TelegramPanel.Web.Services;

public static class UserChatActiveTaskModes
{
    public const string Random = "random";
    public const string Queue = "queue";
}

public static class UserChatActiveAiVerificationMatchModes
{
    public const string MentionOrReply = "mention_or_reply";
    public const string Keyword = "keyword";
    public const string Regex = "regex";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Keyword, StringComparison.OrdinalIgnoreCase))
            return Keyword;

        if (string.Equals(value, Regex, StringComparison.OrdinalIgnoreCase))
            return Regex;

        return MentionOrReply;
    }
}

public sealed class UserChatActiveTaskConfig
{
    [JsonPropertyName("category_id")]
    public int CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("category_ids")]
    public List<int> CategoryIds { get; set; } = new();

    [JsonPropertyName("category_names")]
    public List<string> CategoryNames { get; set; } = new();

    [JsonPropertyName("targets")]
    public List<string> Targets { get; set; } = new();

    [JsonPropertyName("dictionary")]
    public List<string> Dictionary { get; set; } = new();

    [JsonPropertyName("image_dictionary_token")]
    public string? ImageDictionaryToken { get; set; }

    [JsonPropertyName("message_rules")]
    public List<UserChatActiveMessageRule> MessageRules { get; set; } = new();

    [JsonPropertyName("delay_min_ms")]
    public int DelayMinMs { get; set; } = 15000;

    [JsonPropertyName("delay_max_ms")]
    public int DelayMaxMs { get; set; } = 45000;

    [JsonPropertyName("account_mode")]
    public string AccountMode { get; set; } = UserChatActiveTaskModes.Random;

    [JsonPropertyName("message_mode")]
    public string MessageMode { get; set; } = UserChatActiveTaskModes.Random;

    [JsonPropertyName("target_mode")]
    public string TargetMode { get; set; } = UserChatActiveTaskModes.Queue;

    [JsonPropertyName("max_messages")]
    public int MaxMessages { get; set; }

    [JsonPropertyName("enable_ai_verification")]
    public bool EnableAiVerification { get; set; }

    [JsonPropertyName("ai_model")]
    public string? AiModel { get; set; }

    [JsonPropertyName("verification_timeout_seconds")]
    public int VerificationTimeoutSeconds { get; set; } = 15;

    [JsonPropertyName("verification_timeout_as_failure")]
    public bool VerificationTimeoutAsFailure { get; set; }

    [JsonPropertyName("verification_match_mode")]
    public string VerificationMatchMode { get; set; } = UserChatActiveAiVerificationMatchModes.MentionOrReply;

    [JsonPropertyName("verification_keywords")]
    public List<string> VerificationKeywords { get; set; } = new();

    [JsonPropertyName("verification_regexes")]
    public List<string> VerificationRegexes { get; set; } = new();

    [JsonPropertyName("verification_bot_username_filter")]
    public bool VerificationBotUsernameFilterEnabled { get; set; }

    [JsonPropertyName("verification_bot_usernames")]
    public List<string> VerificationBotUsernames { get; set; } = new();

    [JsonPropertyName("canceled")]
    public bool Canceled { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("recent_failures")]
    public List<UserChatActiveTaskRuntimeFailure> RecentFailures { get; set; } = new();
}

public sealed class UserChatActiveMessageRule
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_dictionary_token")]
    public string? ImageDictionaryToken { get; set; }
}

internal static class UserChatActiveMessageRuleNormalizer
{
    public static List<UserChatActiveMessageRule> Normalize(UserChatActiveTaskConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var legacyDictionary = (config.Dictionary ?? new List<string>())
            .Select(NormalizeMultilineText)
            .Where(x => x.Length > 0)
            .ToList();
        var legacyImageDictionaryToken = NormalizeOptionalTemplateToken(config.ImageDictionaryToken);
        var rules = (config.MessageRules ?? new List<UserChatActiveMessageRule>())
            .Select(NormalizeRule)
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        if (rules.Count == 0)
        {
            rules.AddRange(legacyDictionary.Select(text => new UserChatActiveMessageRule
            {
                Text = text,
                ImageDictionaryToken = legacyImageDictionaryToken
            }));

            if (rules.Count == 0 && legacyImageDictionaryToken != null)
            {
                rules.Add(new UserChatActiveMessageRule
                {
                    Text = string.Empty,
                    ImageDictionaryToken = legacyImageDictionaryToken
                });
            }
        }

        config.Dictionary = rules
            .Select(x => x.Text ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToList();

        var imageTokens = rules
            .Select(x => (x.ImageDictionaryToken ?? string.Empty).Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.ImageDictionaryToken = imageTokens.Count == 1 && imageTokens[0].Length > 0
            ? imageTokens[0]
            : null;
        config.MessageRules = rules;
        return rules;
    }

    private static UserChatActiveMessageRule? NormalizeRule(UserChatActiveMessageRule? rule)
    {
        if (rule == null)
            return null;

        var text = NormalizeMultilineText(rule.Text);
        var imageDictionaryToken = NormalizeOptionalTemplateToken(rule.ImageDictionaryToken);
        if (text.Length == 0 && imageDictionaryToken == null)
            return null;

        return new UserChatActiveMessageRule
        {
            Text = text,
            ImageDictionaryToken = imageDictionaryToken
        };
    }

    private static string NormalizeMultilineText(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string? NormalizeOptionalTemplateToken(string? value)
    {
        var token = (value ?? string.Empty).Trim();
        return token.Length == 0 ? null : token;
    }
}



public sealed class UserChatActiveTaskRuntimeFailure
{
    [JsonPropertyName("time_utc")]
    public DateTime TimeUtc { get; set; }

    [JsonPropertyName("account_id")]
    public int AccountId { get; set; }

    [JsonPropertyName("account")]
    public string Account { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}
