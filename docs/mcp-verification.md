# MCP implementation and verification

Verified on Windows on 2026-11-02, on branch `codex/mcp-chat-control`.
Baseline checkpoint: `4636d0d0a5477ceb853759480eaaa4aa85566132`, pushed to `origin/codex/remove-ai-planner` before the MCP branch was created.

## Delivered behavior

The standalone STDIO MCP server exposes 15 tools and connects over a current-user-only named pipe to the running desktop application's existing services. Queries cover accounts, villages, cached resources/buildings, construction and planned queues, scheduled tasks, failures, and non-secret settings. Controls cover explicit pause/resume, selected village settings, existing-building upgrade jobs, removal/reordering, and scheduled full-village refresh.

Desktop and MCP mutations share per-account coordination with task startup. Queue edits require an offline or fully paused account; recovery and outcome persistence remain part of execution. Ownership, queue/settings revisions, legal upgrade targets, request replay, bounded frames, protocol versions, and unknown outcomes are checked. Failure holds remain intact. Observation times are reported separately for fields, center, resources, and attack status; unknown times remain null.

## Acceptance results

| Check | Result |
| --- | --- |
| Baseline queue-tracking tests before checkpoint | 9/9 passed |
| Final Release solution build | Passed, 0 warnings, 0 errors |
| Focused MCP/control/transport/synchronization tests | 21/21 passed |
| Final complete Release test suite | 239/239 passed, 0 skipped |
| Independent STDIO process and real bridge | Initialized, discovered all 15 tools, queried and changed temporary database state |
| Ownership, stale revisions, request replay | Rejected foreign villages/stale edits; replay did not repeat a committed write |
| Protocol mismatch, timeout after dispatch, app unavailable | Explicit MCP errors with structured results and correct unknown-outcome handling |
| Post-commit scheduler failure | Reported an unknown outcome and retained a receipt preventing repeat submission |
| Real named-pipe malformed-frame and concurrent-client checks | Passed |
| Idle connection and bridge shutdown | Passed |
| Self-contained Windows publish | App and MCP console published successfully |
| Published-app smoke test | Standalone MCP queried the actual staged WPF host with a new empty database |

The independent probe used a temporary database and a separate bridge instance. The published-app smoke test used only the staged build, without accounts or game actions. Its generated empty database was removed afterward. Those initial staging checks did not change the installed application, credentials, account data, or live queues.

The expanded parallel suite initially exposed a Telegram delivery test's wall-clock deadline under contention. The same test passed alone, the untouched baseline passed 218/218, and the expanded suite passed when run serially. A narrow nonparallel collection now isolates the timing-sensitive Telegram delivery tests while retaining every deadline and assertion. The final normal suite passed all 239 tests; production Telegram behavior was not changed.

## Installed deployment verification

Following the user's installation request, version **2.13.0.16** was published and installed on 2026-11-02. Before replacement, the account was paused and the application closed. The complete prior installation, local application settings, and Codex configuration were backed up. Backup and installed-file hashes were checked: 1,496 existing files backed up and 897 release files installed. Existing data files were preserved.

The MCP executable was installed in the application's `Mcp` subdirectory and registered globally as `travianbot` through the Codex CLI. A fresh Codex app-server independently discovered all 15 tools with no tool errors. The already-running desktop Codex host had no reachable reload endpoint; its existing chat tool inventory requires a client refresh/restart.

All 15 tools were exercised through MCP against the installed desktop application. **38 live checks passed**, including all query types across the account's villages, settings patch/read-back/restore, job reorder/read-back/restore, temporary job add/remove, duplicate-request replay, stale-revision rejection, invalid-village rejection, online queue-edit rejection, pause/resume, and an actual scheduled refresh producing new field, center, resource, and attack observation times. Every village's queue and settings revisions matched the originals after the reversible tests, before normal automation resumed. The final account state was Online with no held tasks.

The complete Release test suite was run again for installation and passed **239/239**, with no skipped tests. Detailed installation manifests and live-data checks remain only in local backup/test records; they are not committed to the repository.

## Training control follow-up (2.13.0.18)

Village settings now expose troop training enablement and the five training-building selections, with tribe-specific legal choices. Training changes require a quiescent paused/offline account, validate the saved quantity/interval ranges and available buildings, preserve failure holds, and replace an unheld waiting task so previous building selections cannot leak into the next cycle. Existing batch sizes and intervals are retained.

Unavailable or unresearched troops are deferred without submitting a form or pausing the account. A partial batch is capped at the displayed maximum only when the existing low-resource training option permits it. Research remains outside the MCP tool set.

Release solution build passed with zero warnings/errors; the complete suite passed **259/259**, with no skipped tests. Added coverage includes training validation, task replacement/replay/holds, absent or ambiguous troop controls, malformed maximums, and partial-batch limits. The real-time observation acceptance tests now use the existing nonparallel bridge collection, retaining their five-second deadline and assertions after contention surfaced in the expanded suite.

The installed 2.13.0.18 application was restarted and the native MCP connection queried it successfully. Live settings read-back confirmed construction disabled, empty planned queues, and Spearman-only training in the two requested villages. The first available village submitted a batch of 15 Spearmen; the village awaiting research safely deferred training, retained its selection, and scheduled its next normal cycle. The account remained Online with no held tasks. Installation preserved user data and the connected MCP console; private village details and backups remain outside the repository.

## Repeatable commands

Run with the .NET 10 SDK on Windows:

```powershell
dotnet build TravBotSharp.sln -c Release
dotnet test --project MainCore.Test/MainCore.Test.csproj -c Release --no-build
./scripts/Publish-Mcp.ps1
```

The publish script stages the app, MCP executable, setup guide, and configuration templates under `artifacts/mcp-publish`. Installation and client registration are separate from publishing; see [the setup guide](mcp-setup.md).

## First-release limits

Game reads are cached. A refresh request is queued and requires a running online account to execute. Request receipts are retained only during the app run, up to 10,000 outcomes; clients must inspect state after an ambiguous failure or restart. Responses have explicit list limits and truncation metadata. Browser login/logout, new-building placement, direct troop dispatch, paid actions, bulk queue deletion, and clearing uncertain-action holds remain outside the tool set.
