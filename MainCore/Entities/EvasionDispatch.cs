namespace MainCore.Entities;

// A confirmation must be marked Submitting before the click; an uncertain result is
// reconciled against the game and must never be retried blindly.
public sealed class EvasionDispatch
{
    public int AccountId { get; set; }
    public int VillageId { get; set; }
    public string AttackId { get; set; } = "";
    public string Kind { get; set; } = "";
    public int DestinationId { get; set; }
    public string ImpactUtc { get; set; } = "";
    public string State { get; set; } = "";
    public string? MovementId { get; set; }
    public string? SentUtc { get; set; }
    public string? ArrivalUtc { get; set; }
    public string Payload { get; set; } = "";
}
