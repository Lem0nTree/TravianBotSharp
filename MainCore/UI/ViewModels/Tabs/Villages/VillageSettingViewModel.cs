using System.Text.Json;

using MainCore.Infrasturecture.Extensions;
using MainCore.UI.Models.Input;
using MainCore.UI.ViewModels.Abstract;

namespace MainCore.UI.ViewModels.Tabs.Villages
{
    using FluentValidation;

    using ReactiveUI.Primitives;
    using ReactiveUI.Primitives.Signals;

    [RegisterSingleton<VillageSettingViewModel>]
    public partial class VillageSettingViewModel : VillageTabViewModelBase
    {
        public sealed record DestinationOption(int Id, string Name);
        [Reactive]
        private List<DestinationOption> _evasionDestinations = [];
        public VillageSettingInput VillageSettingInput { get; } = new();

        private readonly DialogService _dialogService;
        private readonly IDbContextFactory<AppDbContext> _contextFactory;
        private readonly IValidator<VillageSettingInput> _villageSettingInputValidator;
        private readonly TaskManager _taskManager;

        public VillageSettingViewModel(DialogService dialogService, IValidator<VillageSettingInput> villageSettingInputValidator, CustomServiceScopeFactory serviceScopeFactory, IDbContextFactory<AppDbContext> contextFactory, TaskManager taskManager)
        {
            _dialogService = dialogService;
            _villageSettingInputValidator = villageSettingInputValidator;
            _contextFactory = contextFactory;
            _taskManager = taskManager;

            LoadSettingCommand.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(VillageSettingInput.Set);
        }

        public async Task SettingRefresh(VillageId villageId)
        {
            if (!IsActive) return;
            if (villageId != VillageId) return;
            await LoadSettingCommand.Execute(villageId);
        }

        protected override async Task Load(AccountId accountId, VillageId villageId)
        {
            using var context = _contextFactory.CreateDbContext();
            var destinations = context.Villages.AsNoTracking().Where(x => x.AccountId == accountId.Value && x.Id != villageId.Value)
                .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.X, x.Y }).AsEnumerable()
                .Select(x => new DestinationOption(x.Id, $"{x.Name} ({x.X}|{x.Y})")).ToList();
            destinations.Insert(0, new DestinationOption(0, "Choose a destination"));
            await Signal.Start(() => EvasionDestinations = destinations, RxSchedulers.MainThreadScheduler);
            await LoadSettingCommand.Execute(villageId);
        }

        protected override async Task OnContextInvalidated(AccountId accountId)
        {
            await Signal.Start(() => VillageSettingInput.Set([]), RxSchedulers.MainThreadScheduler);
        }

        [ReactiveCommand(RunInBackground = true)]
        private async Task Save()
        {
            var result = await _villageSettingInputValidator.ValidateAsync(VillageSettingInput);
            if (!result.IsValid)
            {
                await _dialogService.SendMessage("Error", string.Join(Environment.NewLine, result.Errors.Select(x => x.ErrorMessage)));
                return;
            }

            using var context = _contextFactory.CreateDbContext();
            var settings = VillageSettingInput.Get();
            if (settings[VillageSettingEnums.ResourceEvasionEnable] == 1 || settings[VillageSettingEnums.TroopEvasionEnable] == 1)
            {
                var target = settings[VillageSettingEnums.EvasionDestinationId];
                if (target == VillageId.Value || !context.Villages.Any(x => x.AccountId == AccountId.Value && x.Id == target))
                {
                    await _dialogService.SendMessage("Error", "Choose another village belonging to this account as the evasion destination.");
                    return;
                }
                if (settings[VillageSettingEnums.TroopEvasionEnable] == 1 && settings[VillageSettingEnums.ConfirmedCapitalStatus] != 2)
                {
                    await _dialogService.SendMessage("Error", "Troop evasion requires this village to be explicitly confirmed as non-capital.");
                    return;
                }
            }
            context.SaveVillageSetting(VillageId, settings);
            context.TriggerTask(_taskManager, AccountId, VillageId, settings);

            await _dialogService.SendMessage("Information", "Settings saved.");
        }

        [ReactiveCommand(RunInBackground = true)]
        private async Task Import()
        {
            var path = await _dialogService.OpenFileDialog();
            Dictionary<VillageSettingEnums, int> settings;
            try
            {
                var jsonString = await File.ReadAllTextAsync(path);
                settings = JsonSerializer.Deserialize<Dictionary<VillageSettingEnums, int>>(jsonString)!;
            }
            catch
            {
                await _dialogService.SendMessage("Warning", "Invalid file.");
                return;
            }

            // Capital is a property of this village, never of a settings template.
            using (var current = _contextFactory.CreateDbContext())
                foreach (var key in new[] { VillageSettingEnums.ConfirmedCapitalStatus, VillageSettingEnums.ResourceEvasionEnable, VillageSettingEnums.TroopEvasionEnable, VillageSettingEnums.EvasionDestinationId })
                    settings[key] = current.VillagesSetting.Where(x => x.VillageId == VillageId.Value && x.Setting == key)
                        .Select(x => x.Value).FirstOrDefault();
            await Signal.Start(() => VillageSettingInput.Set(settings), RxSchedulers.MainThreadScheduler);
            var result = await _villageSettingInputValidator.ValidateAsync(VillageSettingInput);
            if (!result.IsValid)
            {
                await _dialogService.SendMessage("Error", string.Join(Environment.NewLine, result.Errors.Select(x => x.ErrorMessage)));
                return;
            }
            using var context = _contextFactory.CreateDbContext();
            settings.Remove(VillageSettingEnums.Tribe);
            settings.Remove(VillageSettingEnums.ConfirmedCapitalStatus);
            settings.Remove(VillageSettingEnums.ResourceEvasionEnable);
            settings.Remove(VillageSettingEnums.TroopEvasionEnable);
            settings.Remove(VillageSettingEnums.EvasionDestinationId);

            context.SaveVillageSetting(VillageId, settings);
            context.TriggerTask(_taskManager, AccountId, VillageId, settings);

            await _dialogService.SendMessage("Information", "Settings imported");
        }

        [ReactiveCommand(RunInBackground = true)]
        private async Task Export()
        {
            var path = await _dialogService.SaveFileDialog();
            if (string.IsNullOrEmpty(path)) return;

            using var context = _contextFactory.CreateDbContext();
            var settings = context.VillagesSetting
               .Where(x => x.VillageId == VillageId.Value)
               .ToDictionary(x => x.Setting, x => x.Value);
            var jsonString = JsonSerializer.Serialize(settings);
            await File.WriteAllTextAsync(path, jsonString);
            await _dialogService.SendMessage("Information", "Settings exported");
        }

        [ReactiveCommand(RunInBackground = true)]
        private Dictionary<VillageSettingEnums, int> LoadSetting(VillageId villageId)
        {
            using var context = _contextFactory.CreateDbContext();
            var settings = context.VillagesSetting
               .Where(x => x.VillageId == villageId.Value)
               .ToDictionary(x => x.Setting, x => x.Value);
            return settings;
        }
    }
}
