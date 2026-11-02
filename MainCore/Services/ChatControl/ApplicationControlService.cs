using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MainCore.Tasks.Base;
using TravianBotSharp.ControlProtocol;

namespace MainCore.Services.ChatControl;

public sealed class ApplicationControlService(IDbContextFactory<AppDbContext> factory, TaskManager tasks, ControlMutations mutations, ObservationTracker observations)
{
    private static object Bounded<T>(IEnumerable<T> source, int limit = 200)
    {
        var items = source.ToArray();
        return new { Total = items.Length, Limit = limit, Truncated = items.Length > limit, Items = items.Take(limit).ToArray() };
    }
    private static object? DecodeJob(string type, string content)
    {
        try { return type == nameof(JobTypeEnums.NormalBuild) ? JsonSerializer.Deserialize<NormalBuildPlan>(content) : type == nameof(JobTypeEnums.ResourceBuild) ? JsonSerializer.Deserialize<ResourceBuildPlan>(content) : null; }
        catch (JsonException) { return null; }
    }
    private static string Revision(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public Task<ControlResponse> ExecuteAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        if (method is "set_account_paused" or "update_village_settings" or "add_build_job" or "remove_build_job" or "move_build_job" or "request_village_refresh") return mutations.Execute(method, args, cancellationToken);
        try
        {
            using var db = factory.CreateDbContext();
            object result;
            if (method == "list_accounts")
            {
                var accounts = db.Accounts.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Username, x.Server }).ToArray();
                result = Bounded(accounts.Select(x => new { x.Id, x.Username, x.Server, Status = tasks.GetStatus(new AccountId(x.Id)).ToString() }));
            }
            else
            {
                int accountId = args.GetProperty("accountId").GetInt32();
                if (!db.Accounts.Any(x => x.Id == accountId)) throw new ControlError("account_not_found", "Account not found.");
                var aid = new AccountId(accountId);
                if (method == "get_account_status")
                {
                    var list = tasks.GetTaskList(aid);
                    result = new { Status = tasks.GetStatus(aid).ToString(), CurrentTask = tasks.GetCurrentTask(aid)?.Description, PendingCount = list.Count, HeldCount = list.Count(x => x.IsQuarantined), ApplicationDirectory = AppContext.BaseDirectory };
                }
                else if (method == "list_villages")
                    result = Bounded(db.Villages.AsNoTracking().Where(x => x.AccountId == accountId).OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.X, x.Y, x.IsUnderAttack }).ToArray());
                else if (method == "get_tasks")
                    result = Bounded(tasks.GetTaskList(aid).OrderBy(x => x.ExecuteAt).ThenBy(x => x.Key).Select(x => new { Kind = x.GetType().DeclaringType?.Name ?? x.GetType().Name, VillageId = (x as VillageTask)?.VillageId.Value, x.ExecuteAt, Stage = x.Stage.ToString(), x.IsQuarantined, x.FailureStatus }));
                else if (method == "get_task_failures")
                    result = Bounded(db.TaskFailureStates.AsNoTracking().Where(x => x.AccountId == accountId).OrderBy(x => x.VillageId).ThenBy(x => x.TaskType).Select(x => new { x.VillageId, x.TaskType, x.Attempts, x.Quarantined, x.NextAttemptUtc, Summary = x.Quarantined ? "Held for inspection" : "Deferred after failure" }).ToArray());
                else if (method == "get_account_settings")
                {
                    var overrides = db.AccountsSetting.AsNoTracking().Where(x => x.AccountId == accountId).ToArray();
                    result = AppDbContext.AccountDefaultSettings.OrderBy(x => x.Key).Select(x => new { Name = x.Key.ToString(), Value = overrides.FirstOrDefault(y => y.Setting == x.Key)?.Value ?? x.Value, Default = x.Value }).ToArray();
                }
                else
                {
                    int villageId = args.GetProperty("villageId").GetInt32();
                    if (!db.Villages.Any(x => x.Id == villageId && x.AccountId == accountId)) throw new ControlError("village_not_found", "Village not found for account.");
                    if (method == "get_village_snapshot")
                    {
                        var storage = db.Storages.AsNoTracking().Where(x => x.VillageId == villageId).Select(x => new { x.Wood, x.Clay, x.Iron, x.Crop, x.Warehouse, x.Granary, x.FreeCrop }).FirstOrDefault();
                        var buildings = db.Buildings.AsNoTracking().Where(x => x.VillageId == villageId).OrderBy(x => x.Location).ToArray().Select(x => new { x.Location, Type = x.Type.ToString(), x.Level, x.IsUnderConstruction }).ToArray();
                        var construction = db.QueueBuildings.AsNoTracking().Where(x => x.VillageId == villageId).OrderBy(x => x.CompleteTime).ToArray().Select(x => new { x.Location, Type = x.Type.ToString(), x.Level, x.CompleteTime }).ToArray();
                        result = new { Cached = true, ObservedAt = observations.Get(accountId, villageId), Storage = storage, Buildings = Bounded(buildings, 100), Construction = Bounded(construction, 100) };
                    }
                    else if (method == "get_build_queue")
                    {
                        var jobs = db.Jobs.AsNoTracking().Where(x => x.VillageId == villageId).OrderBy(x => x.Position).ThenBy(x => x.Id).ToArray().Select(x => new { x.Id, x.Position, Type = x.Type.ToString(), x.Content }).ToArray();
                        result = new { Revision = Revision(jobs), Total = jobs.Length, Limit = 500, Truncated = jobs.Length > 500, Jobs = jobs.Take(500).Select(x => new { x.Id, x.Position, x.Type, Target = DecodeJob(x.Type, x.Content) }).ToArray() };
                    }
                    else if (method == "get_village_settings")
                    {
                        var overrides = db.VillagesSetting.AsNoTracking().Where(x => x.VillageId == villageId).ToArray();
                        var settings = AppDbContext.VillageDefaultSettings.OrderBy(x => x.Key).Select(x => new { Name = x.Key.ToString(), Value = overrides.FirstOrDefault(y => y.Setting == x.Key)?.Value ?? x.Value, Default = x.Value }).ToArray();
                        result = new { Revision = Revision(settings), Settings = settings, EditableKeys = VillageControlPolicy.Editable.Select(x => x.ToString()).ToArray(), TrainingOptions = VillageControlPolicy.Options((TribeEnums)(overrides.FirstOrDefault(x => x.Setting == VillageSettingEnums.Tribe)?.Value ?? AppDbContext.VillageDefaultSettings[VillageSettingEnums.Tribe])) };
                    }
                    else throw new ControlError("unknown_method", "Unknown control method.");
                }
            }
            return Task.FromResult(new ControlResponse(PipeProtocol.Version, new { GeneratedAt = DateTimeOffset.UtcNow, Data = result }));
        }
        catch (ControlError ex) { return Task.FromResult(new ControlResponse(PipeProtocol.Version, null, ex.Code, ex.Message)); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return Task.FromResult(new ControlResponse(PipeProtocol.Version, null, "invalid_arguments", "Invalid or missing arguments.")); }
    }
    private sealed class ControlError(string code, string message) : Exception(message) { public string Code { get; } = code; }
}
