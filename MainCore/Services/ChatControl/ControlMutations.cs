using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MainCore.Services.BuildingAutomation;
using MainCore.UI.ViewModels.Tabs.Villages;
using TravianBotSharp.ControlProtocol;

namespace MainCore.Services.ChatControl;

public sealed class ControlMutations(IDbContextFactory<AppDbContext> factory, TaskManager tasks, RxQueue notifications)
{
    private readonly ConcurrentDictionary<string, (string Fingerprint, ControlResponse Response)> _replays = new();
    private readonly SemaphoreSlim _receiptCapacity = new(10000, 10000);
    private static string Canonical(JsonElement element) => element.ValueKind switch {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText()
    };
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static object Queue(AppDbContext db, int id) => db.Jobs.AsNoTracking().Where(x => x.VillageId == id).OrderBy(x => x.Position).ThenBy(x => x.Id).ToArray().Select(x => new { x.Id, x.Position, Type = x.Type.ToString(), x.Content }).ToArray();
    private static object Settings(AppDbContext db, int id)
    {
        var values = db.VillagesSetting.AsNoTracking().Where(x => x.VillageId == id).ToArray();
        return AppDbContext.VillageDefaultSettings.OrderBy(x => x.Key).Select(x => new { Name = x.Key.ToString(), Value = values.FirstOrDefault(y => y.Setting == x.Key)?.Value ?? x.Value, Default = x.Value }).ToArray();
    }
    public async Task<ControlResponse> Execute(string method, JsonElement args, CancellationToken ct)
    {
        bool reserved = false, stored = false, outcomeMayHaveChanged = false;
        string? replayKey = null, fingerprint = null;
        IDisposable? controlLease = null;
        try
        {
            var account = args.GetProperty("accountId").GetInt32();
            var key = args.GetProperty("requestId").GetString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 100) throw new ControlException("invalid_arguments", "A request ID of at most 100 characters is required.");
            fingerprint = Hash(method + "|" + Canonical(args));
            controlLease = await tasks.AcquireControlAsync(new AccountId(account), ct);
            replayKey = account + ":" + key;
            if (_replays.TryGetValue(replayKey, out var replay))
            {
                if (replay.Fingerprint != fingerprint) throw new ControlException("request_id_collision", "Request ID was already used with different arguments.");
                if (method == "set_account_paused" && tasks.GetStatus(new AccountId(account)) != (args.GetProperty("paused").GetBoolean() ? StatusEnums.Paused : StatusEnums.Online))
                    throw new ControlException("replay_state_changed", "The account state changed after this request; inspect it and use a new request ID.");
                return replay.Response;
            }
            if (!_receiptCapacity.Wait(0)) throw new ControlException("receipt_capacity", "The request receipt limit has been reached; restart the application after reviewing state.");
            reserved = true;
            using var db = factory.CreateDbContext();
            if (!db.Accounts.Any(x => x.Id == account)) throw new ControlException("account_not_found", "Account not found.");
            var aid = new AccountId(account);
            object result;
            if (method == "set_account_paused")
            {
                var desired = args.GetProperty("paused").GetBoolean();
                var before = tasks.GetStatus(aid);
                if (before == StatusEnums.Paused && tasks.IsExecuting(aid)) throw new ControlException("account_not_quiescent", "Wait for the current task to finish before changing pause state.");
                if (desired && before == StatusEnums.Online)
                {
                    outcomeMayHaveChanged = true;
                    tasks.SetStatus(aid, StatusEnums.Pausing);
                    await tasks.StopCurrentTask(aid, ct);
                }
                else if (!desired && before == StatusEnums.Paused)
                {
                    outcomeMayHaveChanged = true;
                    tasks.SetStatus(aid, StatusEnums.Online);
                }
                else if (!((desired && before == StatusEnums.Paused) || (!desired && before == StatusEnums.Online))) throw new ControlException("invalid_state", "The requested transition is not available from the current state.");
                result = new { Before = before.ToString(), After = tasks.GetStatus(aid).ToString(), Completed = true };
            }
            else
            {
                int village = args.GetProperty("villageId").GetInt32();
                if (!db.Villages.Any(x => x.Id == village && x.AccountId == account)) throw new ControlException("village_not_found", "Village not found for account.");
                var vid = new VillageId(village);
                if (method == "request_village_refresh")
                {
                    var existing = tasks.GetTaskList(aid).OfType<UpdateVillageTask.Task>().FirstOrDefault(x => x.VillageId == vid);
                    if (existing is null)
                    {
                        outcomeMayHaveChanged = true;
                        tasks.Add(new UpdateVillageTask.Task(aid, vid, oneShot: true));
                    }
                    else if (existing.IsQuarantined) throw new ControlException("task_held", "Refresh is held; inspect the task failure before requesting it again.");
                    else if (existing.Stage == StageEnums.Executing) throw new ControlException("refresh_in_progress", "A refresh is already in progress; inspect its results before requesting another.");
                    else
                    {
                        outcomeMayHaveChanged = true;
                        existing.ForceFullScan = true;
                        tasks.AddOrUpdate(new UpdateVillageTask.Task(aid, vid) { ExecuteAt = DateTime.Now });
                    }
                    result = new { Queued = true, AlreadyPending = existing is not null };
                }
                else if (method == "update_village_settings")
                {
                    using var transaction = db.Database.BeginTransaction();
                    var before = Settings(db, village);
                    if (Hash(before) != args.GetProperty("expectedRevision").GetString()) throw new ControlException("revision_conflict", "Village settings changed; read them again.");
                    var patch = args.GetProperty("patch");
                    var changes = new Dictionary<VillageSettingEnums, int>();
                    foreach (var property in patch.EnumerateObject())
                    {
                        if (!Enum.TryParse<VillageSettingEnums>(property.Name, false, out var setting) || setting is not (VillageSettingEnums.AutoBuildEnable or VillageSettingEnums.AutoRefreshEnable or VillageSettingEnums.AutoRefreshMin or VillageSettingEnums.AutoRefreshMax or VillageSettingEnums.AutoClaimQuestEnable)) throw new ControlException("invalid_settings", "The patch contains an unsupported key.");
                        var value = property.Value.GetInt32();
                        if ((setting is VillageSettingEnums.AutoRefreshMin or VillageSettingEnums.AutoRefreshMax) ? value < 1 || value > 1440 : value is not (0 or 1)) throw new ControlException("invalid_settings", "Boolean values must be 0 or 1; refresh intervals must be 1 to 1440 minutes.");
                        if (!changes.TryAdd(setting, value)) throw new ControlException("invalid_settings", "Duplicate setting.");
                    }
                    if (changes.Count == 0) throw new ControlException("invalid_settings", "The patch is empty.");
                    var current = AppDbContext.VillageDefaultSettings.ToDictionary(x => x.Key, x => x.Value);
                    foreach (var row in db.VillagesSetting.Where(x => x.VillageId == village)) current[row.Setting] = row.Value;
                    var previous = changes.Keys.ToDictionary(x => x, x => current[x]);
                    foreach (var change in changes) current[change.Key] = change.Value;
                    if (current[VillageSettingEnums.AutoRefreshMin] > current[VillageSettingEnums.AutoRefreshMax]) throw new ControlException("invalid_settings", "Minimum refresh interval exceeds maximum.");
                    foreach (var change in changes)
                    {
                        var row = db.VillagesSetting.FirstOrDefault(x => x.VillageId == village && x.Setting == change.Key);
                        if (row is null) db.VillagesSetting.Add(new VillageSetting { VillageId = village, Setting = change.Key, Value = change.Value });
                        else row.Value = change.Value;
                    }
                    db.SaveChanges();
                    outcomeMayHaveChanged = true;
                    transaction.Commit();
                    db.TriggerTask(tasks, aid, vid, changes);
                    var after = Settings(db, village);
                    result = new { BeforeRevision = Hash(before), AfterRevision = Hash(after), Revision = Hash(after), Changed = changes.ToDictionary(x => x.Key.ToString(), x => new { Before = previous[x.Key], After = x.Value }), Completed = true };
                }
                else
                {
                    var status = tasks.GetStatus(aid);
                    if (status is not (StatusEnums.Paused or StatusEnums.Offline) || tasks.IsExecuting(aid) || tasks.GetCurrentTask(aid) is not null) throw new ControlException("account_not_quiescent", "Pause the account and wait for its current task to finish.");
                    using var transaction = db.Database.BeginTransaction();
                    var before = Queue(db, village);
                    var beforeCount = db.Jobs.Count(x => x.VillageId == village);
                    var beforeRevision = Hash(before);
                    if (beforeRevision != args.GetProperty("expectedRevision").GetString()) throw new ControlException("revision_conflict", "Build queue changed; read it again.");
                    var jobs = db.Jobs.Where(x => x.VillageId == village).OrderBy(x => x.Position).ThenBy(x => x.Id).ToList();
                    if (method == "remove_build_job" || method == "move_build_job")
                    {
                        var jobId = args.GetProperty("jobId").GetInt32();
                        var job = jobs.FirstOrDefault(x => x.Id == jobId) ?? throw new ControlException("job_not_found", "Job not found in this village.");
                        jobs.Remove(job);
                        if (method == "remove_build_job") db.Jobs.Remove(job);
                        else
                        {
                            var position = args.GetProperty("position").GetInt32();
                            if (position < 0 || position > jobs.Count) throw new ControlException("invalid_position", "Position is outside the queue.");
                            jobs.Insert(position, job);
                        }
                    }
                    else if (method == "add_build_job")
                    {
                        var slot = args.GetProperty("slot").GetInt32();
                        var target = args.GetProperty("targetLevel").GetInt32();
                        var snapshot = BuildingSnapshotReader.Read(db, aid, vid);
                        var building = snapshot.Buildings.SingleOrDefault(x => x.Slot == slot) ?? throw new ControlException("invalid_building", "Slot is not observed.");
                        var capitalValue = db.VillagesSetting.Where(x => x.VillageId == village && x.Setting == VillageSettingEnums.ConfirmedCapitalStatus).Select(x => (int?)x.Value).FirstOrDefault() ?? 0;
                        bool? capital = capitalValue == 1 ? true : capitalValue == 2 ? false : null;
                        var queued = db.QueueBuildings.Where(x => x.VillageId == village && x.Location == slot).Select(x => (int?)x.Level).Max() ?? 0;
                        foreach (var job in jobs.Where(x => x.Type == JobTypeEnums.NormalBuild))
                        {
                            var plan = JsonSerializer.Deserialize<NormalBuildPlan>(job.Content);
                            if (plan?.Location == slot) queued = Math.Max(queued, plan.Level);
                        }
                        foreach (var job in jobs.Where(x => x.Type == JobTypeEnums.ResourceBuild && building.Type.IsResourceField()))
                        {
                            var plan = JsonSerializer.Deserialize<ResourceBuildPlan>(job.Content) ?? throw new ControlException("invalid_queue", "A resource plan cannot be read; inspect the existing queue.");
                            var applies = plan.Plan == ResourcePlanEnums.AllResources ||
                                plan.Plan == ResourcePlanEnums.ExcludeCrop && building.Type != BuildingEnums.Cropland ||
                                plan.Plan == ResourcePlanEnums.OnlyCrop && building.Type == BuildingEnums.Cropland;
                            if (!Enum.IsDefined(plan.Plan)) throw new ControlException("invalid_queue", "A resource plan has an unknown type.");
                            if (applies) queued = Math.Max(queued, plan.Level);
                        }
                        var initial = Math.Max(building.Level, queued);
                        if (target <= initial || target > (building.Type.IsResourceField() ? capital == true ? 20 : 10 : building.Type.GetMaxLevel())) throw new ControlException("invalid_level", "Target must exceed all current and planned levels and respect the legal maximum.");
                        if (building.Type.IsResourceField() && target > 10 && capital is null) throw new ControlException("invalid_level", "Capital status must be confirmed before planning fields above level 10.");
                        for (var level = initial; level < target; level++)
                            if (!BuildingRules.CanUpgrade(snapshot, building with { Level = level, UnderConstruction = false }, capital)) throw new ControlException("invalid_building", "Building prerequisites, tribe, capital, capacity, or cached data do not permit every requested level.");
                        jobs.Add(new Job { VillageId = village, Type = JobTypeEnums.NormalBuild, Content = JsonSerializer.Serialize(new NormalBuildPlan { Location = slot, Type = building.Type, Level = target }) });
                        db.Jobs.Add(jobs[^1]);
                    }
                    else throw new ControlException("unknown_method", "Unknown control method.");
                    for (var i = 0; i < jobs.Count; i++) jobs[i].Position = i;
                    db.SaveChanges();
                    outcomeMayHaveChanged = true;
                    transaction.Commit();
                    notifications.Enqueue(new JobsModified(aid, vid));
                    tasks.AddOrUpdate(new UpgradeBuildingTask.Task(aid, vid));
                    var after = Queue(db, village);
                    result = new { Before = new { Revision = beforeRevision, Count = beforeCount }, After = new { Revision = Hash(after), Count = jobs.Count }, Revision = Hash(after), Completed = true };
                }
            }
            var response = new ControlResponse(PipeProtocol.Version, new { GeneratedAt = DateTimeOffset.UtcNow, Data = result });
            _replays[replayKey] = (fingerprint, response);
            stored = true;
            return response;
        }
        catch (ControlException ex) { return new ControlResponse(PipeProtocol.Version, null, ex.Code, ex.Message); }
        catch (Exception) when (outcomeMayHaveChanged)
        {
            // Persistence or an account transition may already have completed even
            // when notification, scheduling, or cancellation subsequently fails.
            // Keep a receipt so replay cannot submit the mutation a second time.
            var response = new ControlResponse(PipeProtocol.Version, null, "mutation_outcome_unknown",
                "The request may have changed application state, but completion could not be confirmed. Read current state before making a new request.");
            if (replayKey is not null && fingerprint is not null)
            {
                _replays[replayKey] = (fingerprint, response);
                stored = true;
            }
            return response;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or JsonException) { return new ControlResponse(PipeProtocol.Version, null, "invalid_arguments", "Invalid or missing arguments or incomplete cached data."); }
        finally
        {
            if (reserved && !stored) _receiptCapacity.Release();
            controlLease?.Dispose();
        }
    }
    private sealed class ControlException(string code, string message) : Exception(message) { public string Code { get; } = code; }
}
