using System.Text.Json;

namespace MainCore.Services
{
    [RegisterSingleton<AttackAlertService>]
    public sealed class AttackAlertService(TwilioAttackAlertSettings settings, ITwilioCallClient callClient,
        IAttackAlertStateStore stateStore)
    {
        private int _warned;

        public async Task CheckAsync(AccountId accountId, IReadOnlyCollection<VillageDto> villages, CancellationToken cancellationToken = default)
        {
            if (!settings.Enabled || villages.Count == 0) return;
            var logger = Serilog.Log.ForContext<AttackAlertService>();
            if (!settings.IsConfigured)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0)
                    logger.Warning("Twilio calls are enabled but the credentials or phone numbers are not configured.");
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Persist BEFORE the request: restarts and ambiguous timeouts must not redial it.
                var newlyAttacked = stateStore.MarkNewAttacks(accountId, villages);
                if (newlyAttacked.Count == 0) return;
                var callSid = await callClient.CallAsync(newlyAttacked, cancellationToken);
                logger.Warning("New attack detected in {Count} village(s). Twilio accepted call {CallSid}.", newlyAttacked.Count, callSid);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or HttpRequestException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                logger.Warning("Attack call could not be confirmed ({ErrorType}). Check local state/settings and Twilio Voice logs. The same ongoing attack is not automatically redialed.", exception.GetType().Name);
            }
        }
    }
}
