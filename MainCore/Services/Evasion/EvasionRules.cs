namespace MainCore.Services.Evasion;

public static class EvasionRules
{
    public static long[] Allocate(IReadOnlyList<long> stock, long capacity)
    {
        if (stock.Count != 4 || capacity < 0 || stock.Any(x => x < 0))
            throw new ArgumentException("Expected four non-negative resource amounts and a non-negative capacity.");
        var result = new long[4];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Math.Min(stock[i], capacity);
            capacity -= result[i];
        }
        return result;
    }

    // Missed send windows are never replayed after a restart.
    public static bool IsSendWindow(DateTimeOffset now, DateTimeOffset impact, TimeSpan lead, TimeSpan tolerance)
    {
        if (lead <= TimeSpan.Zero || tolerance < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lead));
        var due = impact - lead;
        return now >= due && now <= due + tolerance && now < impact;
    }

    public static bool IsOwnedDestination(int source, int destination, IEnumerable<int> owned) =>
        source > 0 && destination > 0 && source != destination && owned.Contains(source) && owned.Contains(destination);

    public static bool CanSendTroops(bool? isCapital, int source, int destination, IEnumerable<int> owned) =>
        isCapital == false && IsOwnedDestination(source, destination, owned);

    // Remain away through every known hit, but cancel within 90 seconds and before arrival.
    public static DateTimeOffset? SafeCancelAt(DateTimeOffset sentAt, DateTimeOffset arrivalAt,
        IEnumerable<DateTimeOffset> impacts, TimeSpan impactMargin, TimeSpan cancellationMargin)
    {
        if (impactMargin < TimeSpan.Zero || cancellationMargin < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(impactMargin));
        var hits = impacts.Where(x => x >= sentAt).OrderBy(x => x).ToArray();
        if (hits.Length == 0) return null;
        var at = hits[0] + impactMargin;
        // A cancelled movement takes the elapsed outbound time to return home. A later
        // wave also needs protection if its next evasion preparation begins before then.
        foreach (var hit in hits.Skip(1))
        {
            if (hit.AddSeconds(-28) > at + (at - sentAt)) break;
            at = hit + impactMargin;
        }
        return at < sentAt.AddSeconds(90) - cancellationMargin && at < arrivalAt ? at : null;
    }
}
