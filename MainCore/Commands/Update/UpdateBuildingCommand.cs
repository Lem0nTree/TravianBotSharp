namespace MainCore.Commands.Update
{
    [Handler]
    public sealed partial class UpdateBuildingCommand(
        IChromeBrowser browser,
        IDbContextFactory<AppDbContext> contextFactory,
        RxQueue rxQueue)
    {
        public sealed record Command(VillageId VillageId) : IVillageCommand;

        private async ValueTask<Result> HandleAsync(Command command)
        {
            var villageId = command.VillageId;

            var dtoBuilding = await GetBuildings(browser.CurrentUrl, browser.CurrentPage);
            if (dtoBuilding.Count == 0) return Result.Ok();

            var dtoQueueBuilding = await BuildingLayoutParser.GetQueueBuilding(browser.CurrentPage);

            var tribe = await BuildingLayoutParser.GetTribe(browser.CurrentPage);

            var result = IsValidQueueBuilding(dtoQueueBuilding);
            if (result.IsFailed) return result;

            UpdateToDatabase(villageId, dtoBuilding, dtoQueueBuilding, tribe);
            Trigger(villageId);
            using (var context = contextFactory.CreateDbContext())
            {
                var account = context.Villages.Where(x => x.Id == villageId.Value).Select(x => (int?)x.AccountId).FirstOrDefault();
                if (account is int id)
                    rxQueue.Enqueue(new MainCore.Services.Telegram.TelegramVillageScan(new(id), villageId,
                        Buildings: dtoBuilding.Select(x => new MainCore.Services.Telegram.ObservedBuilding(x.Location, x.Type, Math.Max(0, x.Level))).ToArray()));
            }
            return Result.Ok();
        }

        private void Trigger(VillageId villageId)
        {
            using var context = contextFactory.CreateDbContext();
            var accountId = context.Villages
                .Where(x => x.Id == villageId.Value)
                .Select(x => new AccountId(x.AccountId))
                .FirstOrDefault();

            if (accountId != AccountId.Empty)
            {
                rxQueue.Enqueue(new BuildingsModified(accountId, villageId));
            }
        }

        private static async Task<List<BuildingDto>> GetBuildings(string url, IPage page)
        {
            if (url.Contains("dorf1"))
                return await BuildingLayoutParser.GetFields(page);

            if (url.Contains("dorf2"))
                return await BuildingLayoutParser.GetInfrastructures(page);

            return [];
        }

        private static Result IsValidQueueBuilding(List<QueueBuildingDto> dtos)
        {
            foreach (var dto in dtos)
            {
                if (!BuildingNameParser.TryParse(dto.Type, out var type))
                    return Stop.Error.WithError($"Cannot parse {dto.Type}. Is language English ?");

                dto.Type = type.ToString();
            }
            return Result.Ok();
        }

        private void UpdateToDatabase(VillageId villageId, List<BuildingDto> buildingDtos, List<QueueBuildingDto> queueBuildingDtos, TribeEnums tribe)
        {
            using var context = contextFactory.CreateDbContext();
            var dbBuildings = context.Buildings
                .Where(x => x.VillageId == villageId.Value)
                .ToList();

            foreach (var dto in buildingDtos)
            {
                var dbBuilding = dbBuildings
                    .Find(x => x.Location == dto.Location);

                if (dbBuilding is null)
                {
                    var building = dto.ToEntity(villageId);
                    dbBuildings.Add(building);
                    context.Add(building);
                }
                else
                {
                    dto.To(dbBuilding);
                }
            }

            var dbQueueBuildings = context.QueueBuildings
                .Where(x => x.VillageId == villageId.Value)
                .ToList();

            var completeQueueBuildings = dbQueueBuildings
                .Where(x => x.CompleteTime < DateTime.Now)
                // Identical rows have interchangeable timers: only the fresh layout
                // can establish which slot completed.
                .Where(x => dbQueueBuildings.Count(other => other.Type == x.Type && other.Level == x.Level) == 1)
                .OrderBy(x => x.Level)
                .ToList();

            if (completeQueueBuildings.Count > 0)
            {
                foreach (var completeQueueBuilding in completeQueueBuildings)
                {
                    if (completeQueueBuilding.Location == -1) continue;

                    var building = dbBuildings.Find(x => x.Location == completeQueueBuilding.Location);
                    if (building is null) continue;

                    building.Level = completeQueueBuilding.Level;
                    dbQueueBuildings.Remove(completeQueueBuilding);
                    context.Remove(completeQueueBuilding);
                }
            }

            // Reconcile all rows before inferring slots, consuming each saved row once.
            var unmatched = new List<QueueBuilding>(dbQueueBuildings);
            var observedQueue = new List<QueueBuilding>();
            foreach (var dto in queueBuildingDtos)
            {
                var type = BuildingNameParser.Parse(dto.Type);
                var row = unmatched.FirstOrDefault(x => x.Level == dto.Level && x.Type == type);
                if (row is null)
                {
                    row = dto.ToEntity(villageId);
                    context.Add(row);
                }
                else
                {
                    unmatched.Remove(row);
                    dto.To(row);
                }
                observedQueue.Add(row);
            }

            foreach (var stale in unmatched)
            {
                context.Remove(stale);
                // A disappearing duplicate does not prove which slot completed.
                if (observedQueue.Any(x => x.Type == stale.Type && x.Level == stale.Level))
                {
                    foreach (var remaining in observedQueue.Where(x => x.Type == stale.Type && x.Level == stale.Level))
                        remaining.Location = -1;
                    continue;
                }
                var building = dbBuildings.Find(x => x.Location == stale.Location);
                if (building is not null && buildingDtos.All(x => x.Location != building.Location))
                    building.Level = Math.Max(building.Level, stale.Level);
            }

            ResolveQueueLocations(observedQueue, dbBuildings.Where(x => buildingDtos.Any(dto => dto.Location == x.Location)).ToList());

            context.VillagesSetting
                .Where(x => x.VillageId == villageId.Value)
                .Where(x => x.Setting == VillageSettingEnums.Tribe)
                .ExecuteUpdate(x => x.SetProperty(x => x.Value, (int)tribe));

            context.SaveChanges();
        }
        private static void ResolveQueueLocations(List<QueueBuilding> queue, List<Building> buildings)
        {
            foreach (var group in queue.GroupBy(x => (x.Type, x.Level)))
            {
                var rows = group.ToList();
                var candidates = buildings.Where(x => x.IsUnderConstruction && x.Type == group.Key.Type
                    && (x.Level == group.Key.Level - 1 || x.Level == group.Key.Level - 2)).ToList();
                // Repair old duplicate slot assignments instead of treating them as proof.
                foreach (var duplicate in rows.Where(x => x.Location != -1).GroupBy(x => x.Location).Where(x => x.Count() > 1))
                    foreach (var row in duplicate) row.Location = -1;

                var occupied = rows.Where(x => x.Location != -1).Select(x => x.Location).ToHashSet();
                candidates.RemoveAll(x => occupied.Contains(x.Location));
                var missing = rows.Where(x => x.Location == -1).ToList();
                // Equal multiplicities prove all candidate slots have this target.
                // Otherwise keep the unknown slot for safe construction inspection.
                if (missing.Count != candidates.Count) continue;
                for (var index = 0; index < missing.Count; index++)
                    missing[index].Location = candidates[index].Location;
            }
        }
    }
}
