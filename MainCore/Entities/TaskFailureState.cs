namespace MainCore.Entities;

public sealed class TaskFailureState
{
    public int AccountId { get; set; }
    public int VillageId { get; set; }
    public string TaskType { get; set; } = "";
    public int Attempts { get; set; }
    public bool Quarantined { get; set; }
    public string NextAttemptUtc { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Episode { get; set; } = "";
    public bool Alerted { get; set; }
}
