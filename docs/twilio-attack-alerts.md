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

The bot must be running, logged in and refreshing village data. It does not monitor attacks while closed, sleeping or paused. The evasion scanner also supplies sidebar observations. Observations enter a bounded in-memory queue; processing and the Twilio request do not block the browser. A queued clear suppresses an older queued attack for the same village, while a later attack can start a new episode. Observations older than two minutes update deduplication state without placing a call. If the queue is full or the app shuts down before an observation is processed, it can be skipped; a later refresh can observe an ongoing attack. Twilio accepting a request does not establish that the user answered; check Voice logs for delivery.

## Build and verification

This integration is ported from commit `7265c1cb27e4deb2299157eb626ffbbfb3f3bd95` into the combined bot and requires .NET 10. It preserves the combined village parser's `Active` and `UnderAttack` fields. Settings, attack state and the bot database resolve relative to the executable directory.

```powershell
dotnet restore --locked-mode
dotnet run --project MainCore.Test --no-restore
dotnet publish WPFUI/WPFUI.csproj --no-restore -c Release --self-contained true -p:PublishSingleFile=true
```

Tests cover repeated refreshes, restarts, grouped attacks, clear/rearm, partial pages, separate accounts, failures, corrupt state, concurrent refreshes, parser mapping, queue ordering and Windows-encrypted credentials. The HTTP client is exercised against a mock transport; tests never place phone calls.
