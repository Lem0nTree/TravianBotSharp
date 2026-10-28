# Twilio attack calls

TBS can call a verified destination when its village sidebar detects a new attack. Configure a Twilio Voice-capable caller number and credentials before enabling alerts. Calls use the Twilio Calls API with a short spoken message, a 30-second ringing timeout and a 60-second call limit. Normal Twilio charges and trial restrictions apply. See [Twilio Voice setup](https://www.twilio.com/docs/usage/trials/try-out-voice) and [the Calls API](https://www.twilio.com/docs/voice/api/call-resource).

## Configure

On Windows, run the included helper with the directory containing the built executable:

```powershell
.\scripts\Configure-TwilioAlerts.ps1 -AppDirectory 'C:\path\to\TBS'
```

It prompts for Account SID, caller number, destination and the secret Auth Token. The token is hidden while typing and saved in `twilio-auth.local.dpapi`, protected with Windows DPAPI for the current user. It cannot be reused on another Windows account or PC. The helper places no test calls. Restart TBS after configuration.

`twilio-alerts.local.json` contains `Enabled`, `AccountSid`, `FromNumber` and `ToNumber`; phone numbers must include their international `+` prefix. Both local configuration and encrypted token are ignored by Git. Do not commit either file. The example JSON contains blank fields and starts disabled.

Alternatively supply `TWILIO_ACCOUNT_SID`, `TWILIO_FROM_NUMBER` and `TWILIO_AUTH_TOKEN` to the bot process; environment credentials take precedence. Keep the JSON file beside the executable with `Enabled` and `ToNumber` set. The Twilio documentation MCP does not authenticate or place these calls.

To disable:

```powershell
.\scripts\Configure-TwilioAlerts.ps1 -AppDirectory 'C:\path\to\TBS' -Disable
```

## When calls occur

- Routine page refreshes and restarting TBS do not repeat a call for a village that remains under attack.
- A different village becoming attacked triggers another call while existing attacks continue.
- Several newly attacked villages observed together produce one grouped call, naming up to five villages.
- A village rearms only after TBS explicitly observes its attack flag clear. Empty or partial sidebar snapshots do not rearm it.
- Detection uses the sidebar's boolean attack flag. Additional incoming attacks on the same village while that flag stays set cannot be distinguished.

The per-account state is stored in `Data/twilio-attack-state.json`, with a cross-process lock and atomic replacement. Preserve it during upgrades. Each attempt is durably recorded **before** requesting a call. Failures, ambiguous timeouts, busy responses and unanswered calls are not automatically redialed for the same episode. A crash after saving an attempt but before sending it can therefore mean no call for that episode. Invalid or unwritable state prevents calls and is logged instead of resetting deduplication.

The bot must be running, logged in and refreshing village data. It does not monitor attacks while closed, sleeping or paused. Twilio accepting a request does not establish that the user answered; check Voice logs for delivery.

## Build and verification

This integration is based on TBS 2.13.0 source commit `cca31dbcf37f3b6cc8bffb609a98630775780c2a` and requires .NET 10. It corrects the parser's `IsActive`/`IsUnderAttack` JSON mapping without changing the main SQLite schema. Startup uses the executable directory so other launchers do not accidentally select a different database.

```powershell
dotnet restore --locked-mode
dotnet test --project MainCore.Test/MainCore.Test.csproj --no-restore
dotnet publish WPFUI/WPFUI.csproj --no-restore -c Release --self-contained true -p:PublishSingleFile=true
```

The 15-test suite checks hosting, repeated refreshes, restarts, additional attacked villages, grouped attacks, clear/rearm, missing pages, separate accounts, failures, corrupt state, concurrent refreshes, real parser output mapping and Windows-encrypted credentials. Call clients are simulated; the tests never place phone calls.
