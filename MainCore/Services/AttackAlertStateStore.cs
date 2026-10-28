using System.Text;
using System.Text.Json;

namespace MainCore.Services
{
    public interface IAttackAlertStateStore
    {
        IReadOnlyCollection<VillageDto> MarkNewAttacks(AccountId accountId, IReadOnlyCollection<VillageDto> villages);
    }

    public sealed class AttackAlertStateStore(string path) : IAttackAlertStateStore
    {
        private readonly object _sync = new();

        public IReadOnlyCollection<VillageDto> MarkNewAttacks(AccountId accountId, IReadOnlyCollection<VillageDto> villages)
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var states = File.Exists(path)
                    ? JsonSerializer.Deserialize<Dictionary<int, HashSet<int>>>(File.ReadAllText(path))
                        ?? throw new InvalidDataException("Attack state file is invalid.")
                    : [];
                var known = states.GetValueOrDefault(accountId.Value) ?? [];
                var previous = new HashSet<int>(known);
                // Only an explicit clear flag rearms a village; missing/partial pages do not.
                known.ExceptWith(villages.Where(x => !x.IsUnderAttack).Select(x => x.Id.Value));
                var newlyAttacked = villages.Where(x => x.IsUnderAttack && !known.Contains(x.Id.Value))
                    .DistinctBy(x => x.Id).ToList();
                known.UnionWith(newlyAttacked.Select(x => x.Id.Value));
                if (!previous.SetEquals(known) || !states.ContainsKey(accountId.Value))
                {
                    states[accountId.Value] = known;
                    Save(states);
                }
                return newlyAttacked;
            }
        }

        private void Save(Dictionary<int, HashSet<int>> states)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                    writer.Write(JsonSerializer.Serialize(states));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
