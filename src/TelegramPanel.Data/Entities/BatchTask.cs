namespace TelegramPanel.Data.Entities;

/// <summary>
/// 批量任务实体
/// </summary>
public class BatchTask
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string TaskType { get; set; } = null!; // invite/set_admin/create_channel等
    public string OwnerModuleId { get; set; } = "host.legacy";
    public string ExecutionKind { get; set; } = "batch";
    public string Status { get; set; } = "pending"; // initializing/updating/pending/running/pausing/paused/completed/failed/canceled
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public string? Config { get; set; } // JSON格式的任务配置
    public string? RuntimePhase { get; set; }
    public string? RuntimeMessage { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public bool RequiresAttention { get; set; }
    public DateTime? NextEligibleAtUtc { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
