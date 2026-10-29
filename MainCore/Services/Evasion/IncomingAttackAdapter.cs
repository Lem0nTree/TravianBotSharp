using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MainCore.Services.Evasion;

public sealed record IncomingAttack(string Id, DateTimeOffset ImpactAt);
public sealed record RallyIssue(string Reason, string Classes, string[] MarkerIds, string[] Timers, int MarkerCount, int TimerCount);
public sealed record RallyReport(bool Complete, IncomingAttack[] Attacks, int Rows, int HostileRows, RallyIssue[] Issues, DateTimeOffset SampledAt);

public static partial class IncomingAttackAdapter
{
    // Only the displayed, current structured countdown is authoritative. The sample
    // time and values are captured in the same synchronous browser evaluation.
    public static async Task<RallyReport> ReadAsync(IPage page)
    {
        var start = DateTimeOffset.UtcNow;
        var raw = await page.EvaluateAsync<JsonElement>("""
            () => {
              const sampledMs = Date.now();
              const tables = Array.from(document.querySelectorAll('table.troop_details'));
              const rows = tables.map(table => {
                const own = selector => Array.from(table.querySelectorAll(selector)).filter(node => node.closest('table.troop_details') === table);
                const markers = own('[id^="markSymbol_"]');
                const timers = own('.infos .in span.timer[counting="down"]');
                return { classes: table.className || '', markerCount: markers.length,
                  markerIds: markers.slice(0, 8).map(n => n.id || ''), timerCount: timers.length,
                  timers: timers.slice(0, 8).map(n => n.textContent || '') };
              });
              const orphanCount = Array.from(document.querySelectorAll('[id^="markSymbol_"]')).filter(n => !n.closest('table.troop_details')).length;
              return { sampledMs, rows, orphanCount };
            }
            """);
        var end = DateTimeOffset.UtcNow;
        var data = JsonSerializer.Deserialize<RawPage>(raw.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (data?.Rows is null) return new(false, [], 0, 0, [new("unreadable-page", "", [], [], 0, 0)], end);
        DateTimeOffset sampled;
        try { sampled = DateTimeOffset.FromUnixTimeMilliseconds(data.SampledMs); }
        catch (ArgumentOutOfRangeException) { return new(false, [], data.Rows.Length, 0, [new("invalid-sample-time", "", [], [], 0, 0)], end); }
        var issues = new List<RallyIssue>();
        if (sampled < start.AddSeconds(-2) || sampled > end.AddSeconds(2))
            issues.Add(new("sample-clock-mismatch", "", [], [], 0, 0));
        if (data.OrphanCount > 0) issues.Add(new("orphan-marker", "", [], [], data.OrphanCount, 0));
        var attacks = new List<IncomingAttack>();
        var hostileCount = 0;
        foreach (var row in data.Rows)
        {
            var classes = (row.Classes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var incoming = classes.Where(c => c.StartsWith("in", StringComparison.OrdinalIgnoreCase)).ToArray();
            var outgoing = classes.Any(c => c.StartsWith("out", StringComparison.OrdinalIgnoreCase));
            var benign = incoming.Any(c => c.Equals("inReturn", StringComparison.OrdinalIgnoreCase) || c.Equals("inReinforcement", StringComparison.OrdinalIgnoreCase) || c.Equals("inReinf", StringComparison.OrdinalIgnoreCase) || c.Equals("inSupply", StringComparison.OrdinalIgnoreCase) || c.Equals("inSupplyOasis", StringComparison.OrdinalIgnoreCase) || c.Equals("inSupport", StringComparison.OrdinalIgnoreCase));
            var hostileClass = incoming.Any(c => c.Equals("inAttack", StringComparison.OrdinalIgnoreCase) || c.Equals("inRaid", StringComparison.OrdinalIgnoreCase));
            var unknownIncoming = incoming.Any(c => !c.Equals("inReturn", StringComparison.OrdinalIgnoreCase) && !c.Equals("inReinforcement", StringComparison.OrdinalIgnoreCase) && !c.Equals("inReinf", StringComparison.OrdinalIgnoreCase) && !c.Equals("inSupply", StringComparison.OrdinalIgnoreCase) && !c.Equals("inSupplyOasis", StringComparison.OrdinalIgnoreCase) && !c.Equals("inSupport", StringComparison.OrdinalIgnoreCase) && !c.Equals("inAttack", StringComparison.OrdinalIgnoreCase) && !c.Equals("inRaid", StringComparison.OrdinalIgnoreCase));
            if (incoming.Any(c => c.Equals("inAttack", StringComparison.OrdinalIgnoreCase)) && incoming.Any(c => c.Equals("inRaid", StringComparison.OrdinalIgnoreCase))) { hostileCount++; AddIssue("conflicting-hostile-classes", row); continue; }
            if ((outgoing || benign) && (row.MarkerCount > 0 || hostileClass || unknownIncoming)) { AddIssue("conflicting-classification", row); continue; }
            if (outgoing || benign) continue;
            if (unknownIncoming) { hostileCount++; AddIssue("unknown-incoming-class", row); continue; }
            if (row.MarkerCount > 0 && !hostileClass) { hostileCount++; AddIssue("unknown-marked-class", row); continue; }
            if (row.MarkerCount == 0 && !hostileClass) continue;
            hostileCount++;
            if (row.MarkerCount != 1 || row.MarkerIds is not { Length: 1 } || !IdRegex().IsMatch(row.MarkerIds[0] ?? "")) { AddIssue("invalid-marker", row); continue; }
            if (row.TimerCount != 1 || row.Timers is not { Length: 1 } || !TryTimer(row.Timers[0], out var seconds)) { AddIssue("invalid-countdown", row); continue; }
            var id = IdRegex().Match(row.MarkerIds[0]).Groups[1].Value;
            if (attacks.Any(a => a.Id == id)) { AddIssue("duplicate-marker", row); continue; }
            attacks.Add(new(id, sampled.AddSeconds(seconds)));
        }
        return new(issues.Count == 0, issues.Count == 0 ? [.. attacks] : [], data.Rows.Length, hostileCount, [.. issues.Take(8)], sampled);

        void AddIssue(string reason, RawRow row)
        {
            if (issues.Count >= 8) return;
            var safeClasses = string.Join(' ', (row.Classes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => ClassRegex().IsMatch(c)).Take(8));
            var markers = (row.MarkerIds ?? []).Take(8).Select(s => IdRegex().IsMatch(s ?? "") ? s! : "<invalid>").ToArray();
            var timers = (row.Timers ?? []).Take(8).Select(s => Clean(s).Length <= 20 && TimerRegex().IsMatch(Clean(s)) ? Clean(s) : "<invalid>").ToArray();
            issues.Add(new(reason, safeClasses, markers, timers, row.MarkerCount, row.TimerCount));
        }
    }

    private static bool TryTimer(string? text, out long seconds)
    {
        seconds = 0;
        var match = TimerRegex().Match(Clean(text));
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var hours) || hours > 10000 ||
            !int.TryParse(match.Groups[2].Value, out var minutes) || minutes > 59 ||
            !int.TryParse(match.Groups[3].Value, out var secs) || secs > 59) return false;
        seconds = hours * 3600 + minutes * 60 + secs;
        return seconds > 0;
    }
    private static string Clean(string? text) => new string((text ?? "").Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
    private sealed record RawPage(long SampledMs, RawRow[]? Rows, int OrphanCount);
    private sealed record RawRow(string? Classes, string[]? MarkerIds, string[]? Timers, int MarkerCount, int TimerCount);
    [GeneratedRegex(@"^markSymbol_(\d{1,20})$")] private static partial Regex IdRegex();
    [GeneratedRegex(@"^(\d+):(\d{2}):(\d{2})$")] private static partial Regex TimerRegex();
    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,40}$")] private static partial Regex ClassRegex();
}

public sealed class RallyDiagnosticGate
{
    private readonly ConcurrentDictionary<string, State> _states = new();
    private sealed record State(string Fingerprint, DateTimeOffset LastLog, bool Failed);
    public string? Observe(string key, RallyReport report, DateTimeOffset now)
    {
        var fingerprint = string.Join("|", report.Issues.Select(i => $"{i.Reason}:{i.Classes}:{i.MarkerCount}:{i.TimerCount}"));
        lock (_states)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                _states[key] = new(fingerprint, now, !report.Complete);
                return report.Complete ? null : "first";
            }
            if (report.Complete)
            {
                _states[key] = new("", now, false);
                return state.Failed ? "recovered" : null;
            }
            if (!state.Failed || fingerprint != state.Fingerprint || now - state.LastLog >= TimeSpan.FromMinutes(2))
            {
                _states[key] = new(fingerprint, now, true);
                return !state.Failed ? "first" : fingerprint != state.Fingerprint ? "changed" : "repeat";
            }
            return null;
        }
    }
}

public static class RallyDiagnostics
{
    private static readonly RallyDiagnosticGate Gate = new();
    public static RallyReport RequireAttackWhenFlagged(RallyReport report, bool flagged) =>
        flagged && report.Complete && report.Attacks.Length == 0
            ? report with { Complete = false, Issues = [new("flag-without-hostile", "", [], [], 0, 0)] }
            : report;

    public static void Log(ILogger logger, int account, int village, string stage, RallyReport report)
    {
        var change = Gate.Observe($"{account}:{village}:{stage}", report, DateTimeOffset.UtcNow);
        if (change is null) return;
        if (report.Complete)
            logger.Information("Rally parser recovered account={AccountId} village={VillageId} stage={Stage} rows={Rows} hostile={Hostile}", account, village, stage, report.Rows, report.HostileRows);
        else
            logger.Warning("Rally parser {Change} account={AccountId} village={VillageId} stage={Stage} rows={Rows} hostile={Hostile} issues={@Issues}", change, account, village, stage, report.Rows, report.HostileRows, report.Issues);
    }
}
