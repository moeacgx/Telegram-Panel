using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;

namespace TelegramPanel.Web.Services;

internal readonly record struct UserChatActivePlannedSend(int AccountIndex, int MessageIndex);

internal static class UserChatActiveSendPlanner
{
    public static IReadOnlyList<UserChatActivePlannedSend> BuildFiniteRunPlan(
        int eligibleAccountCount,
        int requestedMessageCount,
        int dictionaryCount,
        string? accountMode,
        string? messageMode)
    {
        if (eligibleAccountCount <= 0 || requestedMessageCount <= 0 || dictionaryCount <= 0)
            return Array.Empty<UserChatActivePlannedSend>();

        var effectiveMessageCount = Math.Min(requestedMessageCount, eligibleAccountCount);
        var accountIndexes = BuildAccountIndexes(eligibleAccountCount, accountMode);
        var plannedSends = new List<UserChatActivePlannedSend>(effectiveMessageCount);
        var messageQueueIndex = 0;

        for (var i = 0; i < effectiveMessageCount; i++)
        {
            var messageIndex = SelectIndex(messageMode, dictionaryCount, ref messageQueueIndex);
            plannedSends.Add(new UserChatActivePlannedSend(accountIndexes[i], messageIndex));
        }

        return plannedSends;
    }

    public static int ResolveFiniteRunTotal(int completedMessageCount, int plannedSendCount)
    {
        return Math.Max(0, completedMessageCount) + Math.Max(0, plannedSendCount);
    }


    private static List<int> BuildAccountIndexes(int count, string? mode)
    {
        var indexes = Enumerable.Range(0, count).ToList();
        if (string.Equals(mode, UserChatActiveTaskModes.Queue, StringComparison.OrdinalIgnoreCase))
            return indexes;

        for (var i = indexes.Count - 1; i > 0; i--)
        {
            var swapIndex = Random.Shared.Next(i + 1);
            (indexes[i], indexes[swapIndex]) = (indexes[swapIndex], indexes[i]);
        }

        return indexes;
    }

    private static int SelectIndex(string? mode, int count, ref int queueIndex)
    {
        if (count <= 1)
            return 0;

        if (string.Equals(mode, UserChatActiveTaskModes.Queue, StringComparison.OrdinalIgnoreCase))
        {
            var idx = queueIndex % count;
            queueIndex = (queueIndex + 1) % int.MaxValue;
            return idx;
        }

        return Random.Shared.Next(0, count);
    }
}

public sealed class UserChatActiveTaskHandler : IModuleTaskHandler
{
    private const int MaxFailureLines = 100;
    private const int StartupRetryDelayMs = 30000;

    public string TaskType => BatchTaskTypes.UserChatActive;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<UserChatActiveTaskHandler>>();
        var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();
        var accountManagement = host.Services.GetRequiredService<AccountManagementService>();
        var accountTools = host.Services.GetRequiredService<AccountTelegramToolsService>();
        var templateRendering = host.Services.GetRequiredService<TemplateRenderingService>();
        var assetStorage = host.Services.GetRequiredService<ImageAssetStorageService>();
        var aiVerification = host.Services.GetRequiredService<UserChatActiveAiVerificationService>();
        var aiOptions = host.Services.GetRequiredService<IOptionsMonitor<AiOpenAiOptions>>();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var clientPool = host.Services.GetRequiredService<ITelegramClientPool>();
        var maxSendRetries = UserChatActiveSendRetryPolicy.NormalizeMaxRetries(
            configuration.GetValue("Telegram:MaxRetries", 0));

        var config = DeserializeConfig(host.Config);
        ValidateAndNormalizeConfig(config);
        if (IsGeneratedMessageMode(config))
        {
            foreach (var imageDictionaryToken in config.MessageRules
                         .Select(x => x.ImageDictionaryToken)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await templateRendering.ValidateImageTemplateAsync(imageDictionaryToken!, cancellationToken);
            }
        }

        config.Canceled = false;
        config.Error = null;
        var configGate = new SemaphoreSlim(1, 1);
        var progress = await LoadInitialProgressAsync(taskManagement, host.TaskId);
        var accountSlots = new List<AccountSlot>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var preparation = await PrepareAccountSlotsAsync(
                config,
                host,
                accountManagement,
                accountTools,
                templateRendering,
                aiOptions,
                cancellationToken);

            if (preparation.Canceled)
            {
                config.Canceled = true;
                await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
                return;
            }

            if (preparation.Success)
            {
                accountSlots = preparation.AccountSlots;
                config.Error = null;
                await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
                break;
            }

            config.Error = preparation.Error ?? "常驻任务暂时无法启动";
            await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);

            if (!IsPersistent(config))
                throw new InvalidOperationException(config.Error);

            logger.LogWarning(
                "UserChatActive task waiting for retry (taskId={TaskId}): {Error}",
                host.TaskId,
                config.Error);

            await host.UpdateProgressAsync(progress.Completed, progress.Failed, cancellationToken);
            if (!await DelayWithPauseCheckAsync(host, StartupRetryDelayMs, cancellationToken))
            {
                config.Canceled = true;
                await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
                return;
            }
        }

        var verificationFailures = new ConcurrentQueue<VerificationFailure>();
        var verificationTasks = new ConcurrentDictionary<Guid, Task>();
        using var verificationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var accountQueueIndex = 0;
        var messageQueueIndex = 0;
        var targetQueueIndexByAccountId = new Dictionary<int, int>();
        var lastProgressPersistAt = DateTime.UtcNow;
        var useForwardSource = IsForwardSourceMode(config);
        var contentItemCount = ResolveContentItemCount(config);
        var replyToMessageId = useForwardSource ? null : ResolveReplyToMessageId(config);
        var finiteSendPlan = config.MaxMessages > 0
            ? UserChatActiveSendPlanner.BuildFiniteRunPlan(
                accountSlots.Count,
                Math.Max(0, config.MaxMessages - progress.Completed),
                contentItemCount,
                config.AccountMode,
                config.MessageMode)
            : null;
        if (finiteSendPlan is not null)
        {
            var finiteTotal = UserChatActiveSendPlanner.ResolveFiniteRunTotal(progress.Completed, finiteSendPlan.Count);
            if (finiteTotal != config.MaxMessages)
            {
                config.MaxMessages = finiteTotal;
                await taskManagement.UpdateTaskDraftAsync(host.TaskId, finiteTotal, SerializeIndented(config));
            }
        }
        var finiteSendPlanIndex = 0;

        try
        {
            async Task<bool> DelayUntilNextSendAsync(Stopwatch timer, int intervalMs)
            {
                if (intervalMs <= 0)
                    return true;

                var remaining = intervalMs - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0)
                    return true;

                return await DelayWithPauseCheckAsync(host, remaining, cancellationToken);
            }

            async Task<int> DrainVerificationFailuresAsync()
            {
                if (verificationFailures.IsEmpty)
                    return 0;

                var failures = new List<VerificationFailure>();
                while (verificationFailures.TryDequeue(out var item))
                    failures.Add(item);

                if (failures.Count == 0)
                    return 0;

                await AddFailuresAndPersistAsync(
                    taskManagement,
                    host.TaskId,
                    config,
                    failures,
                    configGate,
                    cancellationToken);

                return failures.Count;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    verificationTokenSource.Cancel();
                    break;
                }

                if (config.MaxMessages > 0 && progress.Completed >= config.MaxMessages)
                    break;

                UserChatActivePlannedSend plannedSend = default;
                if (finiteSendPlan is not null)
                {
                    if (finiteSendPlanIndex >= finiteSendPlan.Count)
                        break;

                    plannedSend = finiteSendPlan[finiteSendPlanIndex++];
                }

                var intervalMs = NextDelayMilliseconds(config.DelayMinMs, config.DelayMaxMs);
                var loopTimer = Stopwatch.StartNew();

                var accountIdx = finiteSendPlan is not null
                    ? plannedSend.AccountIndex
                    : SelectIndex(config.AccountMode, accountSlots.Count, ref accountQueueIndex);
                var accountSlot = accountSlots[accountIdx];

                if (!targetQueueIndexByAccountId.ContainsKey(accountSlot.Account.Id))
                    targetQueueIndexByAccountId[accountSlot.Account.Id] = 0;

                var targetQueueIndex = targetQueueIndexByAccountId[accountSlot.Account.Id];
                var targetIdx = SelectIndex(config.TargetMode, accountSlot.Targets.Count, ref targetQueueIndex);
                targetQueueIndexByAccountId[accountSlot.Account.Id] = targetQueueIndex;
                var targetSlot = accountSlot.Targets[targetIdx];

                var contentIdx = finiteSendPlan is not null
                    ? plannedSend.MessageIndex
                    : SelectIndex(config.MessageMode, contentItemCount, ref messageQueueIndex);

                if (!await host.IsStillRunningAsync(cancellationToken))
                {
                    config.Canceled = true;
                    verificationTokenSource.Cancel();
                    break;
                }

                var forwardSourceUrl = useForwardSource ? config.ForwardSourceUrls[contentIdx] : string.Empty;
                var messageRule = useForwardSource ? null : config.MessageRules[contentIdx];
                var textTemplate = messageRule?.Text ?? string.Empty;
                var text = string.Empty;
                var imageDictionaryToken = string.Empty;
                var hasImageDictionary = false;

                if (!useForwardSource)
                {
                    try
                    {
                        text = (await templateRendering.RenderTextTemplateAsync(textTemplate, cancellationToken)).Trim();
                    }
                    catch (Exception ex)
                    {
                        var completed = Interlocked.Increment(ref progress.Completed);
                        Interlocked.Increment(ref progress.Failed);
                        var hadTemplateFailure = true;
                        await AddFailureAndPersistAsync(
                            taskManagement,
                            host.TaskId,
                            config,
                            accountSlot.Account,
                            targetSlot.RawTarget,
                            $"消息规则模板解析失败：{ex.Message}",
                            configGate,
                            cancellationToken);

                        if (ShouldPersistProgress(completed, hadTemplateFailure, lastProgressPersistAt))
                        {
                            await host.UpdateProgressAsync(completed, progress.Failed, cancellationToken);
                            lastProgressPersistAt = DateTime.UtcNow;
                        }

                        if (config.MaxMessages > 0 && completed >= config.MaxMessages)
                            break;

                        if (!await DelayUntilNextSendAsync(loopTimer, intervalMs))
                        {
                            config.Canceled = true;
                            verificationTokenSource.Cancel();
                            break;
                        }

                        continue;
                    }

                    imageDictionaryToken = (messageRule?.ImageDictionaryToken ?? string.Empty).Trim();
                    hasImageDictionary = imageDictionaryToken.Length > 0;

                    if (text.Length == 0 && !hasImageDictionary)
                    {
                        var completed = Interlocked.Increment(ref progress.Completed);
                        Interlocked.Increment(ref progress.Failed);
                        var hadEmptyMessageFailure = true;
                        await AddFailureAndPersistAsync(
                            taskManagement,
                            host.TaskId,
                            config,
                            accountSlot.Account,
                            targetSlot.RawTarget,
                            "消息规则模板解析结果为空，无法发送",
                            configGate,
                            cancellationToken);

                        if (ShouldPersistProgress(completed, hadEmptyMessageFailure, lastProgressPersistAt))
                        {
                            await host.UpdateProgressAsync(completed, progress.Failed, cancellationToken);
                            lastProgressPersistAt = DateTime.UtcNow;
                        }

                        if (config.MaxMessages > 0 && completed >= config.MaxMessages)
                            break;

                        if (!await DelayUntilNextSendAsync(loopTimer, intervalMs))
                        {
                            config.Canceled = true;
                            verificationTokenSource.Cancel();
                            break;
                        }

                        continue;
                    }
                }

                async Task<(bool Success, string? Error, int? MessageId, bool SkippedByDedupe)> SendCurrentMessageAsync()
                {
                    if (config.SkipIfLastMessageFromSelf)
                    {
                        var latest = await accountTools.IsLatestMessageFromCurrentAccountAsync(
                            accountSlot.Account.Id,
                            accountSlot.Account.UserId,
                            targetSlot.Resolved,
                            cancellationToken);

                        if (!latest.Success)
                            return (false, $"发送前去重检查失败：{latest.Error ?? "无法读取目标最新消息"}", null, false);

                        if (latest.IsFromCurrentAccount)
                        {
                            logger.LogInformation(
                                "UserChatActive skipped send because latest message was from current account: taskId={TaskId}, accountId={AccountId}, target={Target}, latestMessageId={LatestMessageId}",
                                host.TaskId,
                                accountSlot.Account.Id,
                                targetSlot.RawTarget,
                                latest.MessageId);
                            return (true, null, null, true);
                        }
                    }

                    if (useForwardSource)
                    {
                        var result = await accountTools.ForwardMessageToResolvedChatAsync(
                            accountSlot.Account.Id,
                            forwardSourceUrl,
                            targetSlot.Resolved,
                            dropAuthor: string.Equals(config.ForwardMode, UserChatActiveForwardModes.HideAttribution, StringComparison.Ordinal),
                            cancellationToken: cancellationToken);
                        return (result.Success, result.Error, result.MessageId, false);
                    }

                    if (hasImageDictionary)
                    {
                        try
                        {
                            var asset = await templateRendering.ResolveImageTemplateAsync(imageDictionaryToken, cancellationToken);
                            await using var image = await assetStorage.OpenReadAsync(asset.AssetPath, cancellationToken);
                            var result = await accountTools.SendPhotoToResolvedChatAsync(
                                accountSlot.Account.Id,
                                targetSlot.Resolved,
                                image,
                                asset.FileName,
                                text,
                                replyToMessageId,
                                cancellationToken);
                            return (result.Success, result.Error, result.MessageId, false);
                        }
                        catch (Exception ex)
                        {
                            return (false, $"图片字典解析/发送准备失败：{ex.Message}", null, false);
                        }
                    }

                    var sendResult = await accountTools.SendMessageToResolvedChatAsync(
                        accountSlot.Account.Id,
                        targetSlot.Resolved,
                        text,
                        replyToMessageId,
                        cancellationToken);
                    return (sendResult.Success, sendResult.Error, sendResult.MessageId, false);
                }

                var send = await SendCurrentMessageAsync();
                var retryAttempts = 0;
                while (!send.Success
                       && retryAttempts < maxSendRetries
                       && UserChatActiveSendRetryPolicy.ShouldRetry(send.Error))
                {
                    if (cancellationToken.IsCancellationRequested
                        || !await host.IsStillRunningAsync(cancellationToken))
                    {
                        config.Canceled = true;
                        verificationTokenSource.Cancel();
                        break;
                    }

                    retryAttempts++;
                    logger.LogWarning(
                        "UserChatActive send retry {RetryAttempt}/{MaxRetries}: taskId={TaskId}, accountId={AccountId}, target={Target}, error={Error}",
                        retryAttempts,
                        maxSendRetries,
                        host.TaskId,
                        accountSlot.Account.Id,
                        targetSlot.RawTarget,
                        send.Error);

                    if (UserChatActiveSendRetryPolicy.ShouldResetClient(send.Error))
                        await clientPool.RemoveClientAsync(accountSlot.Account.Id);

                    var retryDelayMs = UserChatActiveSendRetryPolicy.GetDelayMilliseconds(retryAttempts);
                    if (!await DelayWithPauseCheckAsync(host, retryDelayMs, cancellationToken))
                    {
                        config.Canceled = true;
                        verificationTokenSource.Cancel();
                        break;
                    }

                    var refresh = await accountTools.ResolveChatTargetAsync(
                        accountSlot.Account.Id,
                        targetSlot.RawTarget,
                        cancellationToken);
                    if (refresh.Success && refresh.Target != null)
                    {
                        targetSlot.Resolved = refresh.Target;
                    }
                    else
                    {
                        logger.LogWarning(
                            "UserChatActive retry target refresh failed: taskId={TaskId}, accountId={AccountId}, target={Target}, error={Error}",
                            host.TaskId,
                            accountSlot.Account.Id,
                            targetSlot.RawTarget,
                            refresh.Error);
                    }

                    send = await SendCurrentMessageAsync();
                }

                if (config.Canceled)
                    break;

                if (send.Success && retryAttempts > 0)
                {
                    logger.LogInformation(
                        "UserChatActive send recovered after retry: taskId={TaskId}, accountId={AccountId}, target={Target}, retries={Retries}",
                        host.TaskId,
                        accountSlot.Account.Id,
                        targetSlot.RawTarget,
                        retryAttempts);
                }

                var sendCompleted = Interlocked.Increment(ref progress.Completed);
                var hadFailureThisRound = false;

                if (!send.Success)
                {
                    Interlocked.Increment(ref progress.Failed);
                    hadFailureThisRound = true;
                    await AddFailureAndPersistAsync(
                        taskManagement,
                        host.TaskId,
                        config,
                        accountSlot.Account,
                        targetSlot.RawTarget,
                        UserChatActiveSendRetryPolicy.DescribeFinalFailure(send.Error, retryAttempts),
                        configGate,
                        cancellationToken);

                    if (LooksLikePeerInvalid(send.Error))
                    {
                        var refresh = await accountTools.ResolveChatTargetAsync(
                            accountSlot.Account.Id,
                            targetSlot.RawTarget,
                            cancellationToken);

                        if (refresh.Success && refresh.Target != null)
                            targetSlot.Resolved = refresh.Target;
                    }
                }
                else if (!send.SkippedByDedupe && config.EnableAiVerification)
                {
                    if (!send.MessageId.HasValue || send.MessageId.Value <= 0)
                    {
                        Interlocked.Increment(ref progress.Failed);
                        hadFailureThisRound = true;
                        await AddFailureAndPersistAsync(
                            taskManagement,
                            host.TaskId,
                            config,
                            accountSlot.Account,
                            targetSlot.RawTarget,
                            "消息已发送，但未获取到消息 ID，无法执行 AI 验证",
                            configGate,
                            cancellationToken);
                    }
                    else
                    {
                        var verificationTaskId = Guid.NewGuid();
                        var verificationTask = RunVerificationAsync(
                            aiVerification,
                            accountSlot.Account,
                            targetSlot.Resolved,
                            targetSlot.RawTarget,
                            send.MessageId.Value,
                            config,
                            verificationFailures,
                            logger,
                            verificationTokenSource.Token);

                        verificationTasks[verificationTaskId] = verificationTask;
                        _ = verificationTask.ContinueWith(
                            _ => ((ICollection<KeyValuePair<Guid, Task>>)verificationTasks).Remove(new KeyValuePair<Guid, Task>(verificationTaskId, verificationTask)),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }

                if (ShouldPersistProgress(sendCompleted, hadFailureThisRound, lastProgressPersistAt))
                {
                    await host.UpdateProgressAsync(sendCompleted, progress.Failed, cancellationToken);
                    lastProgressPersistAt = DateTime.UtcNow;
                }

                var verificationFailureCount = await DrainVerificationFailuresAsync();
                if (verificationFailureCount > 0)
                {
                    var failed = Interlocked.Add(ref progress.Failed, verificationFailureCount);
                    await host.UpdateProgressAsync(progress.Completed, failed, cancellationToken);
                    lastProgressPersistAt = DateTime.UtcNow;
                }

                if (config.MaxMessages > 0 && sendCompleted >= config.MaxMessages)
                    break;

                if (!await DelayUntilNextSendAsync(loopTimer, intervalMs))
                {
                    config.Canceled = true;
                    verificationTokenSource.Cancel();
                    break;
                }
            }

            var pendingVerifications = verificationTasks.Values.ToArray();
            if (pendingVerifications.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pendingVerifications);
                }
                catch
                {
                    // 忽略验证任务中的异常，避免影响主流程。
                }
            }

            var finalFailures = await DrainVerificationFailuresAsync();
            if (finalFailures > 0)
            {
                var failed = Interlocked.Add(ref progress.Failed, finalFailures);
                await host.UpdateProgressAsync(progress.Completed, failed, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            verificationTokenSource.Cancel();
            var pendingVerifications = verificationTasks.Values.ToArray();
            if (pendingVerifications.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pendingVerifications);
                }
                catch
                {
                    // 忽略验证任务的二次异常，避免覆盖主异常。
                }
            }

            logger.LogWarning(ex, "UserChatActive task failed (taskId={TaskId})", host.TaskId);
            config.Error = ex.Message;
            await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
            throw;
        }

        await host.UpdateProgressAsync(progress.Completed, progress.Failed, cancellationToken);
        if (config.Canceled)
        {
            await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
            return;
        }

        config.Error = null;
        await PersistConfigAsync(taskManagement, host.TaskId, config, configGate, cancellationToken);
    }

    private static UserChatActiveTaskConfig DeserializeConfig(string? rawConfig)
    {
        var raw = (rawConfig ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new InvalidOperationException("任务缺少 Config");

        try
        {
            return JsonSerializer.Deserialize<UserChatActiveTaskConfig>(raw)
                   ?? throw new InvalidOperationException("任务 Config JSON 为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"任务 Config JSON 无效：{ex.Message}");
        }
    }

    private static void ValidateAndNormalizeConfig(UserChatActiveTaskConfig config)
    {
        config.CategoryIds = NormalizeSelectedCategoryIds(config);
        if (config.CategoryIds.Count == 0)
            throw new InvalidOperationException("任务缺少账号分类");

        config.CategoryId = config.CategoryIds[0];
        config.CategoryNames = NormalizeSelectedCategoryNames(config);
        config.CategoryName = config.CategoryNames.FirstOrDefault() ?? config.CategoryName;

        config.Targets = config.Targets
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.MessageActionMode = UserChatActiveMessageActionModes.Normalize(config.MessageActionMode);
        config.ForwardMode = UserChatActiveForwardModes.Normalize(config.ForwardMode);
        var isForwardSourceMode = IsForwardSourceMode(config);
        if (isForwardSourceMode)
        {
            config.ReplyToMessageUrl = null;
            config.ReplyToMessageId = null;
            config.ForwardSourceUrls = NormalizeForwardSourceUrls(config.ForwardSourceUrls);
            config.MessageRules = new List<UserChatActiveMessageRule>();
            config.Dictionary = new List<string>();
            config.ImageDictionaryToken = null;
            config.EnableAiVerification = false;
            config.AiModel = null;
        }
        else
        {
            config.ReplyToMessageUrl = NormalizeOptionalText(config.ReplyToMessageUrl);
            config.ReplyToMessageId = config.ReplyToMessageId is > 0 ? config.ReplyToMessageId : null;
            config.ForwardSourceUrls = new List<string>();
            config.MessageRules = UserChatActiveMessageRuleNormalizer.Normalize(config);
        }

        if (config.Targets.Count == 0)
            throw new InvalidOperationException("任务缺少目标群组/频道/Bot");

        if (isForwardSourceMode)
        {
            if (config.ForwardSourceUrls.Count == 0)
                throw new InvalidOperationException("转发模式缺少消息链接");

            foreach (var sourceUrl in config.ForwardSourceUrls)
            {
                if (!AccountTelegramToolsService.TryParseTelegramMessageReference(sourceUrl, out _, out var error))
                    throw new InvalidOperationException($"转发消息链接无效：{error ?? sourceUrl}");
            }
        }
        else
        {
            if (config.MessageRules.Count == 0)
                throw new InvalidOperationException("任务缺少消息规则");

            _ = ResolveReplyToMessageId(config);
        }

        if (config.DelayMinMs < 0) config.DelayMinMs = 0;
        if (config.DelayMaxMs < 0) config.DelayMaxMs = 0;
        if (config.DelayMinMs > 600000) config.DelayMinMs = 600000;
        if (config.DelayMaxMs > 600000) config.DelayMaxMs = 600000;
        if (config.DelayMaxMs < config.DelayMinMs) config.DelayMaxMs = config.DelayMinMs;

        if (config.MaxMessages < 0) config.MaxMessages = 0;
        if (config.VerificationTimeoutSeconds < 3) config.VerificationTimeoutSeconds = 15;
        if (config.VerificationTimeoutSeconds > 300) config.VerificationTimeoutSeconds = 300;

        config.AiModel = AiOpenAiSettingsSnapshot.NormalizeModel(config.AiModel);

        config.AccountMode = NormalizeMode(config.AccountMode);
        config.TargetMode = NormalizeMode(config.TargetMode);
        config.MessageMode = NormalizeMode(config.MessageMode);

        config.VerificationMatchMode = UserChatActiveAiVerificationMatchModes.Normalize(config.VerificationMatchMode);
        config.VerificationKeywords = NormalizeVerificationItems(config.VerificationKeywords);
        config.VerificationRegexes = NormalizeVerificationItems(config.VerificationRegexes);
        config.VerificationBotUsernames = NormalizeBotUsernames(config.VerificationBotUsernames);
        if (isForwardSourceMode)
        {
            config.VerificationTimeoutSeconds = 15;
            config.VerificationTimeoutAsFailure = false;
            config.VerificationMatchMode = UserChatActiveAiVerificationMatchModes.MentionOrReply;
            config.VerificationKeywords = new List<string>();
            config.VerificationRegexes = new List<string>();
            config.VerificationBotUsernameFilterEnabled = false;
            config.VerificationBotUsernames = new List<string>();
        }


        if (config.EnableAiVerification)
        {
            if (string.Equals(config.VerificationMatchMode, UserChatActiveAiVerificationMatchModes.Keyword, StringComparison.Ordinal)
                && config.VerificationKeywords.Count == 0)
            {
                throw new InvalidOperationException("AI 验证已启用，但未配置关键词匹配内容");
            }

            if (string.Equals(config.VerificationMatchMode, UserChatActiveAiVerificationMatchModes.Regex, StringComparison.Ordinal))
            {
                if (config.VerificationRegexes.Count == 0)
                    throw new InvalidOperationException("AI 验证已启用，但未配置正则匹配内容");

                foreach (var pattern in config.VerificationRegexes)
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

            if (config.VerificationBotUsernameFilterEnabled && config.VerificationBotUsernames.Count == 0)
                throw new InvalidOperationException("AI 验证已启用，但未配置允许的机器人用户名");
        }

        config.RecentFailures ??= new List<UserChatActiveTaskRuntimeFailure>();
    }


    private static List<int> NormalizeSelectedCategoryIds(UserChatActiveTaskConfig config)
    {
        var ids = (config.CategoryIds ?? new List<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0 && config.CategoryId > 0)
            ids.Add(config.CategoryId);

        return ids;
    }

    private static List<string> NormalizeSelectedCategoryNames(UserChatActiveTaskConfig config)
    {
        var names = (config.CategoryNames ?? new List<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fallbackName = (config.CategoryName ?? string.Empty).Trim();
        if (names.Count == 0 && fallbackName.Length > 0)
            names.Add(fallbackName);

        return names;
    }

    private static string NormalizeMode(string? mode)
    {
        return string.Equals((mode ?? string.Empty).Trim(), UserChatActiveTaskModes.Queue, StringComparison.OrdinalIgnoreCase)
            ? UserChatActiveTaskModes.Queue
            : UserChatActiveTaskModes.Random;
    }

    private static bool IsForwardSourceMode(UserChatActiveTaskConfig config) =>
        string.Equals(config.MessageActionMode, UserChatActiveMessageActionModes.ForwardUrl, StringComparison.Ordinal);

    private static bool IsGeneratedMessageMode(UserChatActiveTaskConfig config) => !IsForwardSourceMode(config);

    private static int ResolveContentItemCount(UserChatActiveTaskConfig config) =>
        IsForwardSourceMode(config) ? config.ForwardSourceUrls.Count : config.MessageRules.Count;

    private static string? NormalizeOptionalText(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    private static List<string> NormalizeForwardSourceUrls(IEnumerable<string>? urls)
    {
        return (urls ?? Array.Empty<string>())
            .SelectMany(SplitTargetValues)
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int? ResolveReplyToMessageId(UserChatActiveTaskConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ReplyToMessageUrl))
        {
            if (!AccountTelegramToolsService.TryParseTelegramMessageReference(config.ReplyToMessageUrl, out var reference, out var error) || reference == null)
                throw new InvalidOperationException($"回复消息链接无效：{error ?? config.ReplyToMessageUrl}");

            if (config.ReplyToMessageId is > 0 && config.ReplyToMessageId.Value != reference.MessageId)
                throw new InvalidOperationException("回复消息链接和消息 ID 不一致");

            config.ReplyToMessageId = reference.MessageId;
            config.ReplyToMessageUrl = reference.RawUrl;
        }

        return config.ReplyToMessageId is > 0 ? config.ReplyToMessageId : null;
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

    private static async Task<TaskProgressCounter> LoadInitialProgressAsync(
        BatchTaskManagementService taskManagement,
        int taskId)
    {
        var task = await taskManagement.GetTaskAsync(taskId);
        return new TaskProgressCounter
        {
            Completed = Math.Max(0, task?.Completed ?? 0),
            Failed = Math.Max(0, task?.Failed ?? 0)
        };
    }

    private static async Task<PrepareAccountSlotsResult> PrepareAccountSlotsAsync(
        UserChatActiveTaskConfig config,
        IModuleTaskExecutionHost host,
        AccountManagementService accountManagement,
        AccountTelegramToolsService accountTools,
        TemplateRenderingService templateRendering,
        IOptionsMonitor<AiOpenAiOptions> aiOptions,
        CancellationToken cancellationToken)
    {
        if (config.EnableAiVerification)
        {
            var settings = aiOptions.CurrentValue.ToSnapshot();
            if (!settings.TryValidateForTask(config.AiModel, out var aiError))
                return PrepareAccountSlotsResult.Failed($"AI 验证已启用，但全局 AI 配置无效：{aiError}");
        }

        var expandedTargetsResult = await ExpandTargetsAsync(config.Targets, templateRendering, cancellationToken);
        if (!expandedTargetsResult.Success)
            return PrepareAccountSlotsResult.Failed(expandedTargetsResult.Error ?? "目标群组/频道/Bot 配置无效");

        var targets = expandedTargetsResult.Targets;
        if (targets.Count == 0)
            return PrepareAccountSlotsResult.Failed("任务缺少目标群组/频道/Bot");

        var selectedCategoryIds = NormalizeSelectedCategoryIds(config).ToHashSet();
        var allAccounts = (await accountManagement.GetAllAccountsAsync())
            .Where(x => x.IsActive && x.UserId > 0 && x.Category?.ExcludeFromOperations != true)
            .Where(x => x.CategoryId.HasValue && selectedCategoryIds.Contains(x.CategoryId.Value))
            .OrderBy(x => x.Id)
            .ToList();

        if (allAccounts.Count == 0)
            return PrepareAccountSlotsResult.Failed("所选分类下没有可用执行账号");

        var accountSlots = new List<AccountSlot>();
        foreach (var account in allAccounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(cancellationToken))
                return PrepareAccountSlotsResult.CanceledResult();

            var slot = new AccountSlot(account);
            foreach (var rawTarget in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await host.IsStillRunningAsync(cancellationToken))
                    return PrepareAccountSlotsResult.CanceledResult();

                var resolved = await accountTools.ResolveChatTargetAsync(account.Id, rawTarget, cancellationToken);
                if (resolved.Success && resolved.Target != null)
                {
                    slot.Targets.Add(new TargetSlot(rawTarget, resolved.Target));
                    continue;
                }

                AddFailure(config, account, rawTarget, NormalizeReason(resolved.Error));
            }

            if (slot.Targets.Count > 0)
                accountSlots.Add(slot);
        }

        return accountSlots.Count == 0
            ? PrepareAccountSlotsResult.Failed("没有可用的账号-目标组合（请确认账号已加入目标群组/频道，或 Bot 用户名/链接正确）")
            : PrepareAccountSlotsResult.Ok(accountSlots);
    }

    private static async Task<ExpandTargetsResult> ExpandTargetsAsync(
        IEnumerable<string>? rawTargets,
        TemplateRenderingService templateRendering,
        CancellationToken cancellationToken)
    {
        var targets = new List<string>();
        foreach (var raw in rawTargets ?? Array.Empty<string>())
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0)
                continue;

            if (templateRendering.ExtractSingleTokenName(value) is { Length: > 0 })
            {
                IReadOnlyList<string> dictionaryValues;
                try
                {
                    dictionaryValues = await templateRendering.ResolveTextDictionaryValuesAsync(value, cancellationToken);
                }
                catch (Exception ex)
                {
                    return ExpandTargetsResult.Failed($"目标字典解析失败：{ex.Message}");
                }

                targets.AddRange(dictionaryValues.SelectMany(SplitTargetValues));
                continue;
            }

            targets.AddRange(SplitTargetValues(value));
        }

        targets = targets
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ExpandTargetsResult.Ok(targets);
    }

    private static IEnumerable<string> SplitTargetValues(string value)
    {
        return (value ?? string.Empty)
            .Split(new[] { "\r\n", "\n", "\r", ",", " " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsPersistent(UserChatActiveTaskConfig config) => config.MaxMessages <= 0;

    private static int SelectIndex(string mode, int count, ref int queueIndex)
    {
        if (count <= 1)
            return 0;

        if (string.Equals(mode, UserChatActiveTaskModes.Queue, StringComparison.OrdinalIgnoreCase))
        {
            var idx = queueIndex % count;
            queueIndex = (queueIndex + 1) % int.MaxValue;
            return idx;
        }

        return Random.Shared.Next(0, count);
    }

    private static int NextDelayMilliseconds(int minMs, int maxMs)
    {
        if (minMs <= 0 && maxMs <= 0)
            return 0;

        if (maxMs <= minMs)
            return minMs;

        return Random.Shared.Next(minMs, maxMs + 1);
    }

    private static async Task<bool> DelayWithPauseCheckAsync(
        IModuleTaskExecutionHost host,
        int delayMs,
        CancellationToken cancellationToken)
    {
        if (delayMs <= 0)
            return true;

        var remaining = delayMs;
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

    private static bool ShouldPersistProgress(int completed, bool hadFailureThisRound, DateTime lastPersistAt)
    {
        if (completed <= 1)
            return true;

        if (hadFailureThisRound)
            return true;

        if (completed % 5 == 0)
            return true;

        return (DateTime.UtcNow - lastPersistAt) >= TimeSpan.FromSeconds(10);
    }

    private static void AddFailure(UserChatActiveTaskConfig config, Account account, string rawTarget, string reason)
    {
        AddFailure(
            config,
            account.Id,
            BuildAccountDisplayName(account),
            rawTarget,
            reason);
    }

    private static void AddFailure(
        UserChatActiveTaskConfig config,
        int accountId,
        string accountDisplayName,
        string rawTarget,
        string reason)
    {
        config.RecentFailures ??= new List<UserChatActiveTaskRuntimeFailure>();
        config.RecentFailures.Add(new UserChatActiveTaskRuntimeFailure
        {
            TimeUtc = DateTime.UtcNow,
            AccountId = accountId,
            Account = accountDisplayName,
            Target = (rawTarget ?? string.Empty).Trim(),
            Reason = reason
        });

        if (config.RecentFailures.Count > MaxFailureLines)
            config.RecentFailures.RemoveRange(0, config.RecentFailures.Count - MaxFailureLines);
    }

    private static string BuildAccountDisplayName(Account account)
    {
        var nickname = string.IsNullOrWhiteSpace(account.Nickname) ? "" : $" ({account.Nickname.Trim()})";
        return $"{account.DisplayPhone}#{account.Id}{nickname}";
    }

    private static bool LooksLikePeerInvalid(string? error)
    {
        var text = (error ?? string.Empty).ToUpperInvariant();
        return text.Contains("PEER_ID_INVALID", StringComparison.Ordinal)
               || text.Contains("CHAT_ID_INVALID", StringComparison.Ordinal)
               || text.Contains("CHANNEL_INVALID", StringComparison.Ordinal)
               || text.Contains("USERNAME_INVALID", StringComparison.Ordinal)
               || text.Contains("USERNAME_NOT_OCCUPIED", StringComparison.Ordinal);
    }

    private static string NormalizeReason(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        return text.Length == 0 ? "失败" : text;
    }

    private static string SerializeIndented(UserChatActiveTaskConfig config)
    {
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task PersistConfigAsync(
        BatchTaskManagementService taskManagement,
        int taskId,
        UserChatActiveTaskConfig config,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await taskManagement.UpdateTaskConfigAsync(taskId, SerializeIndented(config));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task AddFailureAndPersistAsync(
        BatchTaskManagementService taskManagement,
        int taskId,
        UserChatActiveTaskConfig config,
        Account account,
        string rawTarget,
        string reason,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            AddFailure(config, account, rawTarget, reason);
            await taskManagement.UpdateTaskConfigAsync(taskId, SerializeIndented(config));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task AddFailuresAndPersistAsync(
        BatchTaskManagementService taskManagement,
        int taskId,
        UserChatActiveTaskConfig config,
        IReadOnlyList<VerificationFailure> failures,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        if (failures.Count == 0)
            return;

        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var failure in failures)
            {
                AddFailure(
                    config,
                    failure.AccountId,
                    failure.AccountDisplayName,
                    failure.RawTarget,
                    failure.Reason);
            }

            await taskManagement.UpdateTaskConfigAsync(taskId, SerializeIndented(config));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task RunVerificationAsync(
        UserChatActiveAiVerificationService aiVerification,
        Account account,
        AccountTelegramToolsService.ResolvedChatTarget target,
        string rawTarget,
        int sentMessageId,
        UserChatActiveTaskConfig config,
        ConcurrentQueue<VerificationFailure> verificationFailures,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var accountDisplayName = BuildAccountDisplayName(account);
        var timeoutSeconds = Math.Clamp(config.VerificationTimeoutSeconds, 3, 300);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 10));

        try
        {
            var verification = await aiVerification.TryHandleAsync(
                account,
                target,
                sentMessageId,
                config,
                timeoutCts.Token);

            if (!verification.Success)
            {
                if (!config.VerificationTimeoutAsFailure && IsVerificationTimeout(verification.Error))
                    return;

                verificationFailures.Enqueue(new VerificationFailure(
                    account.Id,
                    accountDisplayName,
                    rawTarget,
                    NormalizeReason(verification.Error)));
                return;
            }

            logger.LogInformation(
                "UserChatActive AI verification completed: accountId={AccountId}, target={Target}, action={Action}",
                account.Id,
                rawTarget,
                verification.ActionSummary ?? "(none)");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 任务被取消时忽略验证
        }
        catch (OperationCanceledException)
        {
            if (config.VerificationTimeoutAsFailure)
            {
                verificationFailures.Enqueue(new VerificationFailure(
                    account.Id,
                    accountDisplayName,
                    rawTarget,
                    "验证处理超时"));
            }
        }
        catch (Exception ex)
        {
            verificationFailures.Enqueue(new VerificationFailure(
                account.Id,
                accountDisplayName,
                rawTarget,
                $"验证处理异常：{ex.Message}"));
        }
    }

    private sealed class TaskProgressCounter
    {
        public int Completed;
        public int Failed;
    }

    private sealed record VerificationFailure(
        int AccountId,
        string AccountDisplayName,
        string RawTarget,
        string Reason);

    private sealed record PrepareAccountSlotsResult(
        bool Success,
        bool Canceled,
        string? Error,
        List<AccountSlot> AccountSlots)
    {
        public static PrepareAccountSlotsResult Ok(List<AccountSlot> accountSlots) =>
            new(true, false, null, accountSlots);

        public static PrepareAccountSlotsResult Failed(string error) =>
            new(false, false, error, new List<AccountSlot>());

        public static PrepareAccountSlotsResult CanceledResult() =>
            new(false, true, null, new List<AccountSlot>());
    }

    private sealed record ExpandTargetsResult(
        bool Success,
        string? Error,
        List<string> Targets)
    {
        public static ExpandTargetsResult Ok(List<string> targets) =>
            new(true, null, targets);

        public static ExpandTargetsResult Failed(string error) =>
            new(false, error, new List<string>());
    }

    private static bool IsVerificationTimeout(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        return error.Contains("等待验证消息超时", StringComparison.OrdinalIgnoreCase)
               || error.Contains("验证处理超时", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AccountSlot
    {
        public AccountSlot(Account account)
        {
            Account = account;
        }

        public Account Account { get; }
        public List<TargetSlot> Targets { get; } = new();
    }

    private sealed class TargetSlot
    {
        public TargetSlot(string rawTarget, AccountTelegramToolsService.ResolvedChatTarget resolved)
        {
            RawTarget = rawTarget;
            Resolved = resolved;
        }

        public string RawTarget { get; }
        public AccountTelegramToolsService.ResolvedChatTarget Resolved { get; set; }
    }
}
