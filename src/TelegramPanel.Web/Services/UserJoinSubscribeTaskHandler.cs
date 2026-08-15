using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

public sealed class UserJoinSubscribeTaskHandler : IModuleTaskHandler
{
    public string TaskType => BatchTaskTypes.UserJoinSubscribe;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<UserJoinSubscribeTaskHandler>>();
        var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();
        var accountTools = host.Services.GetRequiredService<AccountTelegramToolsService>();

        var config = DeserializeConfig(host.Config);
        NormalizeConfig(config);

        var completed = 0;
        var failed = 0;
        var failures = new List<UserJoinSubscribeTaskFailure>();

        try
        {
            foreach (var accountId in config.AccountIds)
            {
                foreach (var link in config.Links)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await host.IsStillRunningAsync(cancellationToken))
                    {
                        config.Canceled = true;
                        await PersistConfigAsync(taskManagement, host.TaskId, config, failures, cancellationToken);
                        return;
                    }

                    try
                    {
                        var target = ParseChatMembershipTarget(link, config.TreatNoBotSuffixAsBot);
                        var result = await ExecuteTargetAsync(
                            accountTools,
                            accountId,
                            target,
                            config.Operation,
                            cancellationToken);

                        if (!result.Success)
                        {
                            failed++;
                            failures.Add(new UserJoinSubscribeTaskFailure
                            {
                                AccountId = accountId,
                                Target = link,
                                Reason = NormalizeReason(result.Error)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "User join/subscribe task item failed (taskId={TaskId}, accountId={AccountId}, target={Target})", host.TaskId, accountId, link);
                        failed++;
                        failures.Add(new UserJoinSubscribeTaskFailure
                        {
                            AccountId = accountId,
                            Target = link,
                            Reason = NormalizeReason(ex.Message)
                        });
                    }
                    finally
                    {
                        completed++;
                    }

                    await host.UpdateProgressAsync(completed, failed, cancellationToken);
                    await PersistConfigAsync(taskManagement, host.TaskId, config, failures, cancellationToken);

                    if (!await DelayAsync(host, config.DelayMs, cancellationToken))
                    {
                        config.Canceled = true;
                        await PersistConfigAsync(taskManagement, host.TaskId, config, failures, cancellationToken);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            config.Error = ex.Message;
            await PersistConfigAsync(taskManagement, host.TaskId, config, failures, cancellationToken);
            throw;
        }

        await PersistConfigAsync(taskManagement, host.TaskId, config, failures, cancellationToken);
    }

    private static async Task<(bool Success, string? Error)> ExecuteTargetAsync(
        AccountTelegramToolsService accountTools,
        int accountId,
        ChatMembershipTarget target,
        string operation,
        CancellationToken cancellationToken)
    {
        var join = string.Equals(operation, UserJoinSubscribeOperations.Join, StringComparison.OrdinalIgnoreCase);
        if (target.IsBot)
        {
            var (success, error, _) = join
                ? await accountTools.StartExternalBotAsync(
                    accountId,
                    target.Input,
                    startParameter: null,
                    cancellationToken: cancellationToken,
                    assumeBotUsername: target.AssumeBotUsername)
                : await accountTools.StopExternalBotAsync(
                    accountId,
                    target.Input,
                    cancellationToken: cancellationToken,
                    assumeBotUsername: target.AssumeBotUsername);
            return (success, error);
        }

        var membership = join
            ? await accountTools.JoinChatOrChannelAsync(accountId, target.Input, cancellationToken)
            : await accountTools.LeaveChatOrChannelAsync(accountId, target.Input, cancellationToken);
        return (membership.Success, membership.Error);
    }

    private static async Task<bool> DelayAsync(IModuleTaskExecutionHost host, int delayMs, CancellationToken cancellationToken)
    {
        delayMs = Math.Clamp(delayMs, 0, 60000);
        if (delayMs <= 0)
            return true;

        var remaining = delayMs + Random.Shared.Next(0, 350);
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(cancellationToken))
                return false;

            var chunk = Math.Min(remaining, 1000);
            await Task.Delay(chunk, cancellationToken);
            remaining -= chunk;
        }

        return true;
    }

    private static UserJoinSubscribeTaskConfig DeserializeConfig(string? rawConfig)
    {
        var raw = (rawConfig ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("任务缺少 Config");

        try
        {
            return JsonSerializer.Deserialize<UserJoinSubscribeTaskConfig>(raw)
                   ?? throw new InvalidOperationException("任务 Config JSON 为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"任务 Config JSON 无效：{ex.Message}");
        }
    }

    private static void NormalizeConfig(UserJoinSubscribeTaskConfig config)
    {
        if (config.LegacyAccountIds is { Count: > 0 })
            config.AccountIds.AddRange(config.LegacyAccountIds);
        config.LegacyAccountIds = null;

        if (config.LegacyLinks is { Count: > 0 })
            config.Links.AddRange(config.LegacyLinks);
        config.LegacyLinks = null;

        if (config.LegacyDelayMs.HasValue)
            config.DelayMs = config.LegacyDelayMs.Value;
        config.LegacyDelayMs = null;

        config.AccountIds = config.AccountIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        config.Links = config.Links
            .SelectMany(x => (x ?? string.Empty).Split(new[] { "\r\n", "\n", "\r", ",", " " }, StringSplitOptions.RemoveEmptyEntries))
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.Operation = string.Equals(config.Operation, UserJoinSubscribeOperations.Leave, StringComparison.OrdinalIgnoreCase)
            ? UserJoinSubscribeOperations.Leave
            : UserJoinSubscribeOperations.Join;
        config.DelayMs = Math.Clamp(config.DelayMs, 0, 60000);

        if (config.AccountIds.Count == 0)
            throw new InvalidOperationException("任务缺少账号列表");
        if (config.Links.Count == 0)
            throw new InvalidOperationException("任务缺少目标列表");
    }

    private static ChatMembershipTarget ParseChatMembershipTarget(string raw, bool assumePlainUsernameIsBot)
    {
        var input = (raw ?? string.Empty).Trim();
        var isBot = ShouldTreatAsBot(input, assumePlainUsernameIsBot);
        return new ChatMembershipTarget(input, isBot, assumePlainUsernameIsBot && isBot);
    }

    private static bool ShouldTreatAsBot(string raw, bool assumePlainUsernameIsBot)
    {
        if (raw.StartsWith("tg://resolve", StringComparison.OrdinalIgnoreCase))
            return true;

        if (raw.Contains("?start=", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("&start=", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ExtractUsernameCandidate(raw).EndsWith("bot", StringComparison.OrdinalIgnoreCase))
            return true;

        return assumePlainUsernameIsBot && LooksLikeUsernameTarget(raw);
    }

    private static bool LooksLikeUsernameTarget(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        if (value.StartsWith("tg://join", StringComparison.OrdinalIgnoreCase)
            || value.Contains("joinchat/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/+", StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = ExtractUsernameCandidate(value);
        return !candidate.StartsWith("+", StringComparison.Ordinal)
               && candidate.Length is >= 5 and <= 64
               && candidate.All(x => char.IsLetterOrDigit(x) || x == '_');
    }

    private static string ExtractUsernameCandidate(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        try
        {
            if (value.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value);
                value = GetQueryValue(uri.Query, "domain") ?? value;
            }
            else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                     || value.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase)
                     || value.StartsWith("telegram.me/", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value.Contains("://", StringComparison.Ordinal) ? value : $"https://{value}");
                value = uri.AbsolutePath.Trim('/').Split('/')[0];
            }
            else
            {
                value = value.TrimStart('@');
            }
        }
        catch
        {
            value = value.TrimStart('@');
        }

        return value.Split('?')[0].Split('/')[0].Trim();
    }

    private static string? GetQueryValue(string query, string key)
    {
        var trimmed = (query ?? string.Empty).TrimStart('?');
        if (trimmed.Length == 0)
            return null;

        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 0 || !string.Equals(Uri.UnescapeDataString(pieces[0]), key, StringComparison.OrdinalIgnoreCase))
                continue;

            return pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }

        return null;
    }

    private static async Task PersistConfigAsync(
        BatchTaskManagementService taskManagement,
        int taskId,
        UserJoinSubscribeTaskConfig config,
        List<UserJoinSubscribeTaskFailure> failures,
        CancellationToken cancellationToken)
    {
        config.Failures = failures.TakeLast(200).ToList();
        await taskManagement.UpdateTaskConfigAsync(taskId, JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string NormalizeReason(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        return text.Length == 0 ? "失败" : text;
    }

    private sealed record ChatMembershipTarget(string Input, bool IsBot, bool AssumeBotUsername);
}
