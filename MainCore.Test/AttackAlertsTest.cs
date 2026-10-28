using MainCore.DTO;
using MainCore.Entities;
using MainCore.Parsers;
using MainCore.Services;
using Microsoft.Playwright;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MainCore.Test
{
    public sealed class AttackAlertsTest : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tbs-attack-tests-" + Guid.NewGuid().ToString("N"));
        private string StatePath => Path.Combine(_directory, "state.json");
        private readonly FakeCalls _calls = new();
        private readonly AccountId _account = new(1);

        public AttackAlertsTest() => Directory.CreateDirectory(_directory);

        private AttackAlertService Service(bool enabled = true, bool configured = true) => new(new()
        {
            Enabled = enabled, AccountSid = "AC" + new string('a', 32), AuthToken = configured ? "fake-token" : "",
            FromNumber = "+15005550006", ToNumber = "+393000000000",
        }, _calls, new AttackAlertStateStore(StatePath));

        private static VillageDto Village(int id, bool attacked = true) => new()
        {
            Id = new(id), Name = "Village " + id, IsUnderAttack = attacked,
        };

        [Fact]
        public async Task RefreshingTheSameAttackCallsOnlyOnce()
        {
            var service = Service();
            await service.CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            for (var i = 0; i < 30; i++) await service.CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(1);
        }

        [Fact]
        public async Task RestartingTheBotDoesNotRepeatAnOngoingAttack()
        {
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(1);
        }

        [Fact]
        public async Task NewAttackOnAnotherVillageTriggersANewCall()
        {
            var service = Service();
            await service.CheckAsync(_account, [Village(1), Village(2, false)], TestContext.Current.CancellationToken);
            await service.CheckAsync(_account, [Village(1), Village(2)], TestContext.Current.CancellationToken);
            await service.CheckAsync(_account, [Village(1), Village(2)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(2);
            _calls.Requests[1].Single().Id.ShouldBe(new VillageId(2));
        }

        [Fact]
        public async Task SeveralNewVillagesOnOneRefreshProduceOneGroupedCall()
        {
            await Service().CheckAsync(_account, [Village(1), Village(2), Village(3, false)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(1);
            _calls.Requests[0].Length.ShouldBe(2);
        }

        [Fact]
        public async Task ExplicitClearRearmsVillageAcrossRestart()
        {
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            await Service().CheckAsync(_account, [Village(1, false)], TestContext.Current.CancellationToken);
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(2);
        }

        [Fact]
        public async Task EmptyOrPartialPagesDoNotRearmAnAttack()
        {
            var service = Service();
            await service.CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            await service.CheckAsync(_account, [], TestContext.Current.CancellationToken);
            await service.CheckAsync(_account, [Village(2, false)], TestContext.Current.CancellationToken);
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(1);
        }

        [Fact]
        public async Task AccountsWithSameVillageIdRemainIndependent()
        {
            var service = Service();
            await service.CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            await service.CheckAsync(new AccountId(2), [Village(1)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(2);
        }

        [Fact]
        public async Task AmbiguousFailureDoesNotRedialOnRefreshOrRestart()
        {
            _calls.Fail = true;
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Fail = false;
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            await Service().CheckAsync(_account, [Village(1), Village(2)], TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(2);
            _calls.Requests[1].Single().Id.ShouldBe(new VillageId(2));
        }

        [Fact]
        public async Task CorruptStateFailsWithoutPlacingCalls()
        {
            File.WriteAllText(StatePath, "invalid state");
            await Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Requests.ShouldBeEmpty();
            File.ReadAllText(StatePath).ShouldBe("invalid state");
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public async Task DisabledOrUnconfiguredAlertsDoNotMarkStateOrCall(bool enabled, bool configured)
        {
            await Service(enabled, configured).CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Requests.ShouldBeEmpty();
            File.Exists(StatePath).ShouldBeFalse();
        }

        [Fact]
        public async Task ConcurrentRefreshesAreDeduplicatedBeforeCallCompletes()
        {
            _calls.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            var second = Service().CheckAsync(_account, [Village(1)], TestContext.Current.CancellationToken);
            _calls.Pending.SetResult("CA-test");
            await Task.WhenAll(first, second);
            _calls.Requests.Count.ShouldBe(1);
        }

        [Fact]
        public async Task RealParserPreservesAttackAndActiveFlagsFromBrowserJson()
        {
            var page = Substitute.For<IPage>();
            using var document = JsonDocument.Parse("[{\"IdStr\":\"123\",\"Name\":\"Home\",\"CoordinateX\":\"1\",\"CoordinateY\":\"2\",\"IsActive\":true,\"IsUnderAttack\":true}]");
            page.EvaluateAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>()).Returns(document.RootElement.Clone());
            var villages = await VillagePanelParser.Get(page);
            villages.Single().IsUnderAttack.ShouldBeTrue();
            villages.Single().IsActive.ShouldBeTrue();
            await Service().CheckAsync(_account, villages, TestContext.Current.CancellationToken);
            _calls.Requests.Count.ShouldBe(1);
        }

        [Fact]
        public void LoadsWindowsEncryptedTokenAndSavedPhoneSettings()
        {
            File.WriteAllText(Path.Combine(_directory, "twilio-alerts.local.json"),
                JsonSerializer.Serialize(new { Enabled = true, ToNumber = "+393000000000", AccountSid = "AC" + new string('a', 32), FromNumber = "+15005550006" }));
            var protectedToken = ProtectedData.Protect(Encoding.UTF8.GetBytes("fake-unit-test-token"),
                Encoding.UTF8.GetBytes("TravianBotSharp-Twilio-v1"), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(_directory, "twilio-auth.local.dpapi"), protectedToken);
            var settings = TwilioAttackAlertSettings.Load(_directory);
            settings.IsConfigured.ShouldBeTrue();
            settings.AuthToken.ShouldBe("fake-unit-test-token");
            settings.ToNumber.ShouldBe("+393000000000");
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        private sealed class FakeCalls : ITwilioCallClient
        {
            public List<VillageDto[]> Requests { get; } = [];
            public bool Fail { get; set; }
            public TaskCompletionSource<string>? Pending { get; set; }
            public Task<string> CallAsync(IReadOnlyCollection<VillageDto> villages, CancellationToken cancellationToken)
            {
                Requests.Add(villages.ToArray());
                return Fail ? Task.FromException<string>(new HttpRequestException("ambiguous timeout"))
                    : Pending?.Task ?? Task.FromResult("CA-test");
            }
        }
    }
}
