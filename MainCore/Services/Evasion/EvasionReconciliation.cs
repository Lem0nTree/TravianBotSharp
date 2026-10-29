namespace MainCore.Services.Evasion;

// An adapter must populate these values from the game, not from the intended order.
public sealed record ObservedMovement(string Id, string Kind, int OriginId, int DestinationId,
    long[] Amounts, DateTimeOffset SentAt, DateTimeOffset ArrivalAt);

public static class EvasionReconciliation
{
    // Only a unique newly observed movement with the exact identity and contents is safe
    // to associate with a submitted order. Otherwise the result remains uncertain.
    public static ObservedMovement? FindUniqueNewMovement(IEnumerable<string> previousIds,
        IEnumerable<ObservedMovement> observed, string kind, int origin, int destination,
        IReadOnlyList<long> expectedAmounts, DateTimeOffset earliestSend, DateTimeOffset latestSend)
    {
        if (kind is not ("merchant" or "troop") || origin <= 0 || destination <= 0 || origin == destination ||
            expectedAmounts.Count == 0 || expectedAmounts.Any(x => x < 0) || latestSend < earliestSend) return null;
        var previous = previousIds.ToHashSet(StringComparer.Ordinal);
        var candidates = observed.Where(x => !string.IsNullOrWhiteSpace(x.Id) && !previous.Contains(x.Id) &&
            x.Kind == kind && x.OriginId == origin && x.DestinationId == destination &&
            x.Amounts.SequenceEqual(expectedAmounts) && x.SentAt >= earliestSend && x.SentAt <= latestSend &&
            x.ArrivalAt > x.SentAt).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
