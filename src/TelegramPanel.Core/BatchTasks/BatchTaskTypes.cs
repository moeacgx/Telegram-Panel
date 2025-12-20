namespace TelegramPanel.Core.BatchTasks;

/// <summary>
/// 批量任务类型常量（数据库中 BatchTask.TaskType 的取值）。
/// </summary>
public static class BatchTaskTypes
{
    // Bot 任务（现有）
    public const string Invite = "invite";
    public const string SetAdmin = "set_admin";

    // User 任务（新增）
    public const string UserJoinSubscribe = "user_join_subscribe";
}

