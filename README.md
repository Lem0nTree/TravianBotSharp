# TravianBotSharp (TBS)

[![alt text][image]][hyperlink]

[hyperlink]: https://discord.gg/mBa4f2K
[image]:https://discordapp.com/api/guilds/740829801446637601/widget.png?style=banner4

**TravianBotSharp** is a **free** Travian bot supports [Travian Official](https://www.travian.com).

## Quick start

1. Download TBS from **GitHub Releases**.
2. Extract files to a local folder.
3. Make sure you are on **Windows** and using the **latest Google Chrome**.
4. Run `TravianBotSharp.exe`.
5. Add your account and start setup.

## Main features

- Multi-account support
- Auto builder (support use resource from hero inventory, instant upgrade)
- Auto start adventures
- Auto start farm lists
- Auto claim daily quest/ village quest
- Auto NPC
- Auto train troop

Hero resource top-ups verify each named transfer row and its resource item identity, independently of inventory order. An unused resource may remain disabled or read-only when its verified transfer amount is zero, such as crop when the granary is full; a resource needed for the transfer must be editable. If production lowers the dialog's selected amount, the bot accepts the decrease only when current village stocks plus the selection still fund the building; it closes the dialog without transferring when production already covers everything. A genuine inventory shortage closes the dialog without transferring and logs `inventory_insufficient` followed by the normal `production_wait` time. Unreadable amounts are reported separately. After one confirmation the bot checks the same village/building and live stocks before upgrading; an uncertain transfer holds that village's build task for inspection instead of repeating it. Logs distinguish `confirmation_attempt`, `stocks_verified`, and `upgrade_attempt`; attempting a transfer alone does not mean construction has started.

## Telegram notifications

Construction, Auto Build and NPC failures defer only that village's affected task, with retries after 2, 5 and then 15 minutes. Other villages and tasks continue. A held build task is the village's ordered build-queue dispatcher, so later plans in that same queue also wait. Repeated failures (three attempts) raise a Telegram **Errors** alert when configured; task-table warnings and local logs remain visible regardless of notification delivery. Successful confirmed progress resets the failure count.

Task initialization and scheduling use a shared lock and stable queue snapshots so newly queued work cannot invalidate a running selection pass. Unexpected account-level failures include the execution phase and full exception in local logs; Telegram receives only the general recovery notice.

A queued building goal can require several upgrades: a goal of level 5 may first submit level 2, then level 3. The bot verifies the next step shown by the build button against the completed level and exact-slot queue, and confirms that submitted step on a fresh resource-field or village-center view. A read-only confirmation can recover an uncertain redirect without another upgrade click. The original queue goal remains unchanged; unresolved holds retain the submitted step in their warning and still require inspection.

An uncertain hero transfer, construction click or paid NPC exchange is held for inspection without automatic retries; saved holds survive app restarts. If saving the hold fails, the account pauses and retains an in-memory hold: inspect the game before restarting or resuming, because that unsaved hold cannot survive an app restart. Pause the account, check what happened in the game, then use **Retry failed tasks** in the task/log tab to clear the holds. Existing jobs and settings are preserved. User cancellation and an unusable browser or failed recovery still pause the account. Disabled or no-longer-eligible queued NPC tasks are removed before navigation, and abandoned NPC dialogs are closed with their cancel control or a read-only village overview request—never with a paid exchange.

Open **Account settings → Configure Telegram notifications**. Settings are separate for each account and local database location.

1. Create a Telegram bot with [@BotFather](https://t.me/BotFather), open your bot, and send `/start`. For a group, add the bot and send it a command there.
2. Paste the bot token. Use **Find chats**, explicitly select your destination, or enter its numeric chat ID. Discovery reads recent updates without acknowledging them; if the bot already uses a webhook or another updates consumer, enter the ID manually or use a dedicated bot.
3. Click **Send test** to send one message to that destination. Select **Enable Telegram notifications**, check the event types you want, and **Save**. Saving these settings is separate from the account settings Save button.

Available checkboxes:

| Notification | Trigger |
| --- | --- |
| Incoming attack | First observed attack flag; resets once a refresh sees no attack. It does not identify individual waves while the flag remains active. |
| Completed upgrades | An observed building level increases after an initial baseline; initial village loading sends no completion history. |
| Pending build queue dispatched or satisfied | The builder removes the last completed/already-started job. Active in-game construction can still be running. Manual queue deletion does not trigger this alert. |
| Storage nearly full | Any resource reaches 90% capacity; re-arms when all resources fall to 80% or less. |
| Low crop stocks | Crop falls to 10% of granary capacity or less; re-arms at 20%. This measures stocks, not starvation risk. |
| Account status | Account becomes online, paused, or offline; intermediate transitions and repeated states are suppressed. |
| Task failures and errors | A task stops after failure, or an Error/Fatal log is emitted. Only a summary is sent; repeated error summaries are limited to one per account per ten minutes. |

Notifications follow normal game-page refreshes while the bot is running; this feature does not add independent game polling and cannot monitor while the app is closed. Attack and resource alerts may fire on the first refresh after enabling or restarting. Paused accounts receive only events that are still observed or generated locally.

Tokens are stored in **Windows Credential Manager**, not account exports or JSON. Non-secret preferences are in `%LOCALAPPDATA%\TravianBotSharp\Telegram`. Messages include account name, server, relevant village and observation time. **Send silently** disables Telegram notification sound. **Disable / forget token** stops future queued alerts and removes the saved token; an already-sending message cannot be recalled.

Delivery uses a bounded in-memory queue, plain text, no paid broadcasts, and pacing of at least 3.1 seconds between attempts. Telegram rate-limit responses may be retried once using `retry_after` (up to five minutes), with settings checked again first. Authentication and ambiguous network failures are not retried automatically. Alerts older than ten minutes, over capacity, or from changed settings are skipped. Pending alerts do not survive app shutdown. Use **Delivery status** to see the latest delivery result; the **Send test** button verifies real delivery with your own token.

Protocol reference: [Telegram Bot API](https://core.telegram.org/bots/api#sendmessage), [Telegram message limits](https://core.telegram.org/bots/faq#my-bot-is-hitting-limits-how-do-i-avoid-this).

## Auto Build (experimental)

In a village's settings, enable **Automatically upgrade the lowest eligible building** and save. The bot refreshes fields and the village center approximately once per minute while the account is online. It selects one supported, existing building or resource field with the lowest level, breaking ties by slot, and places one upgrade in the regular build queue only when the observed construction queue and pending bot queue are empty. Normal upgrade settings still apply. The account must remain online; the timing is not guaranteed.

Existing normal jobs take priority over Auto Build. While those jobs remain, Auto Build skips its own village refreshes and ensures their build dispatcher exists, preserving any waiting time or inspection hold. Bounded status logs distinguish a waiting normal queue, a held dispatcher, active in-game construction and no eligible upgrade. A queued plan with no known completion wait that cannot progress is deferred through the normal task-failure policy rather than silently losing its dispatcher.

Set the three-state **Capital status** separately for each village: checked means capital, unchecked means non-capital, and the dash means unknown. Unknown status prevents capital-only upgrades and limits resource fields to level 10. This setting is not imported from another village's settings file. Incorrect capital status can make the choice invalid. Turn off Auto Build and save to stop adding new jobs; an already queued or started upgrade remains in place.

The current construction parser reads `.buildingList li`; two successive upgrades have been observed there, but master-builder pending orders and alternate game layouts have not been verified. Check those displays before enabling Auto Build.

## Evasion (experimental)

For troubleshooting, check the account log for `Rally parser` diagnostics and `Evasion` stage entries. Parser diagnostics contain only table classes, movement markers and countdown summaries, not page HTML or player titles. Incomplete or conflicting incoming rows stop scheduling and cancellation; the first failure, changes and recovery are logged, with repeated identical failures rate-limited. Operation entries show the village, attack marker, destination, stage, target time, actual time and reason. A click returning does not prove that a merchant delivery or troop movement succeeded; inspect the game when an outcome is reported as uncertain.

In each village's settings choose another owned village, set capital status, enable the desired evasion controls and save. Resource evasion attempts to send at 30 seconds before impact, using available merchant capacity in wood, clay, iron, crop order. Troop evasion is available only for a village explicitly marked non-capital; it attempts a reinforcement at 20 seconds before impact and cancels the uniquely identified movement after the relevant attack waves. The bot must be running and the account online. It checks for attacks independently of Auto Refresh, but browser or network delays, a late observation, multiple simultaneous actions, sleep, or downtime can cause a missed deadline.

Incoming-attack scans run every 120 seconds after a scan finishes and share one sidebar refresh per account, at most once every 120 seconds after the previous refresh attempt finishes. Idle villages reuse that sidebar without switching villages or opening rally points; an attacked village or an unresolved troop recovery is inspected separately. A failed refresh is not treated as fresh and is retried after the interval. Merchant sends remain scheduled 30 seconds before a detected attack, troop sends 20 seconds before it, and identified troop cancellations retain their existing schedule. Other enabled bot tasks may still navigate or refresh the browser for their own work.

The first send click is allowed within one second after its target; a recognized confirmation click is allowed up to three seconds after the target and never at or after impact. Actual click drift is logged. Troop matching requires a verified source map tile from the home-troops table or an existing outgoing movement and uses coordinates to calculate the destination's map tile; without that evidence troop evasion skips.

The observed English marketplace and rally forms inform the parsers, but the final confirmation and outgoing reinforcement layouts have only synthetic tests; no live send or cancellation has been tested. Unrecognized pages, destinations, capacities, times, or movement identities cause a skip. A submission whose outcome is uncertain is not retried blindly. It is recorded locally; inspect the in-game marketplace/rally point and the bot log for uncertain sends or cancellations. A troop send can require manual recovery if its exact movement cannot be identified, the cancellation window closes, or a previously unknown wave makes a safe return impossible. Resource deliveries are not automatically returned.

The scheduler reserves the browser near known deadlines and resumes saved, identified troop cancellations after a restart where time remains. It cannot interrupt every ordinary task or detect attacks while the application is offline. Do not rely on it as the sole defense for a time-critical attack. Existing in-game capital evasion is separate; the bot does not dispatch troops from villages marked capital or unknown.

## Twilio attack calls

Optional Twilio calls are disabled until configured. See [Twilio attack-call setup](docs/twilio-attack-alerts.md) for the included configuration helper, costs, and delivery limitations. Alert processing runs separately from the browser scheduler and does not place calls at startup.

## Platform

Currently, TBS only runs on Windows.

## FAQs

User guides are available in [FAQs/README.md](FAQs/README.md).

- Getting started
- Add account / proxy
- Account settings
- Village build queue
- NPC and troop training
- Farming setup

