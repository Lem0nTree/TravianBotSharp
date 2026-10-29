namespace MainCore.Services;

public static class AttackAlertObservation
{
    // Shared by the evasion scanner, which bypasses normal account refresh behavior.
    public static async Task<bool> CaptureAsync(IPage page, AccountId accountId, AttackAlertWorker worker)
    {
        if (!worker.Enabled) return false;
        try
        {
            var villages = await VillagePanelParser.Get(page);
            return villages.Count > 0 && worker.Publish(accountId, villages);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("Twilio attack observation could not read the village sidebar ({ErrorType}).", ex.GetType().Name);
            return false;
        }
    }
}
