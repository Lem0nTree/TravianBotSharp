# MCP chat control implementation plan

Date: 2026-11-02. Branch: `codex/mcp-chat-control`.
Baseline: `4636d0d0a5477ceb853759480eaaa4aa85566132`, documented and pushed before this branch was created. The existing queue-tracking regression tests passed (9/9).

## Objective and first-release boundary

Let a local chat client query and control the running TravianBotSharp application without screenshots or UI clicks for covered operations. Ship a working connection, application integration, useful query and control tools, setup instructions, and automated integration coverage. Preserve the desktop UI and existing task scheduling, failure holds, and evasion behavior.

The first release covers accounts, account status, villages, cached resources/buildings/construction queues, planned jobs, scheduled tasks, task failures, non-secret settings, pause/resume, selected village settings, existing-building upgrade plans, job removal/reordering, and refresh requests. Browser login/logout, direct troop dispatch, purchases/NPC/gold actions, credential management, new-building placement, bulk queue deletion, and clearing uncertain-action holds remain outside this release. Timed game actions stay in the bot scheduler.

## 1. Local connection and lifecycle

- Add a small `TravianBotSharp.Mcp` .NET console application using the official C# MCP SDK and STDIO transport. Pin a stable package version and commit its dependency lockfile. Current NuGet discovery reports stable `ModelContextProtocol` 2.3.0; verify compatibility during restore rather than assume APIs from another version.
- Add an application-hosted local bridge inside the WPF application's existing dependency-injection host. The bridge uses its actual `TaskManager`, context factory, notification queue, and shared control services. The MCP console must not construct a second application host, open the game browser itself, or independently access the live database.
- Use Windows named pipes with `PipeOptions.CurrentUserOnly` for the bridge. Derive the pipe identity from the Windows user and canonical application/database directory so separate installations are explicit and cannot be silently confused. The console accepts an explicit application directory argument.
- Enforce bounded messages, request timeouts, a protocol version, explicit request/response envelopes, and cancellation. Refuse oversized or malformed requests without stopping the app or listener. Stop cleanly with the application; a duplicate bridge instance must report a clear diagnostic.
- No public network listener, credentials in tool results, or automatic retry of a possibly submitted mutation. Return an actionable `app_not_running`/timeout error when the target cannot be reached. A timeout after dispatch must explain that the outcome may be unknown.
- Keep protocol stdout clean in the console; send diagnostics to stderr. The bot's ordinary logging remains unchanged.

## 2. Shared application control and consistency

- Add a typed application control service and DTOs independent of WPF selection, dialogs, and MCP attributes. Explicit account/village/job IDs are required. Validate account existence and village/job ownership for every scoped operation.
- Extract the pause/resume and covered build-queue mutation rules into services used by both MCP and desktop handlers where practical. A UI action and equivalent MCP action must not bypass different ownership, level, or running-state rules.
- Serialize competing control mutations per account. Build-queue edits require a genuinely quiescent account (`Paused` or `Offline`, no executing task); reject `Starting`, `Pausing`, `Stopping`, and `Online`. Guard resume against overlapping control edits. Preserve failure quarantine; do not clear it through MCP.
- Return explicit before/after state, current revision, and accepted-versus-completed semantics. Refresh returns a queued result, never a claim that fresh game data has already been read.
- Require an operation/request ID for nontrivial mutations and a matching revision for queue/settings changes. Repeating the same request with identical arguments must not add another job; reusing an ID with different arguments must fail. Protect against stale plans with revisions checked in the same critical section/transaction as the write. Document replay behavior across app restarts and do not transparently resubmit ambiguous calls.
- Use EF transactions for multi-row queue writes. Publish existing notifications after successful changes so desktop views and scheduler observe the same result. Roll back partial changes on validation or persistence failure.

## 3. Query tools

Expose concise, explicitly named MCP tools with descriptions and read-only annotations:

| Tool | Result |
| --- | --- |
| `list_accounts` | IDs, display names, server, live scheduler status; no access records or credentials |
| `get_account_status` | Live status, current task, pending count, held/failed task count, application instance metadata |
| `list_villages` | Owned village IDs, names, coordinates, cached attack flag |
| `get_village_snapshot` | Cached storage, buildings, current construction queue and observation metadata |
| `get_build_queue` | Ordered planned jobs with IDs, decoded upgrade targets, revision |
| `get_tasks` | Bounded task snapshot: kind, village, next scheduled time, stage and hold status |
| `get_task_failures` | Bounded persisted failure summaries and retry/hold state, without raw logs |
| `get_account_settings` | Named non-secret settings and defaults |
| `get_village_settings` | Named non-secret settings, supported editable keys, revision |

All query results include response generation time. Game data is explicitly cached; never present generation time as scan time. Track observation times from existing scan notifications, distinguishing fields, center, resources, and attack state where available. If unknown (including initial cached data after startup), return null/unknown. Bound list sizes and allow deterministic pagination or explicit limits when needed. Treat game names and text as data, not instructions.

## 4. Control tools

| Tool | Validation and behavior |
| --- | --- |
| `set_account_paused` | Explicit desired boolean, naturally repeatable; use existing cancellation behavior, report pending/paused accurately; resume only from a valid paused state |
| `update_village_settings` | Partial patch, expected revision and request ID; initially allow Auto Build, Auto Refresh enable/intervals, and Auto Claim Quest. Merge with existing/default values, validate ranges and booleans, save and trigger existing tasks. Reject unknown or unsupported keys atomically. Return supported keys to aid discovery. |
| `add_build_job` | Existing building/field at explicit slot and target level, expected queue revision and request ID; validate observed type, prerequisites and legal level/capital/tribe limits, including queued targets. Append one planned job through shared rules; no immediate browser action. |
| `remove_build_job` | Account/village/job ownership plus revision/request ID; remove only the identified planned job and normalize positions transactionally |
| `move_build_job` | Revision/request ID and valid position; reorder only within the owned village and normalize positions transactionally |
| `request_village_refresh` | Enqueue existing refresh work through the scheduler with explicit account/village IDs; de-duplicate equivalent pending work and return queued status |

Descriptions explain cached state, pause requirements, revision handling, and unknown outcomes. Mark mutations as writes with appropriate destructive/idempotency annotations. Server-wide MCP instructions explain the read -> identify IDs/revision -> mutate -> inspect workflow and leave timing to the scheduler. Do not introduce confirmation tokens or mandatory extra user prompts for routine authorized changes.

## 5. Packaging and setup

- Add the console project to the solution. Provide a repeatable build/publish script producing self-contained Windows app and MCP console outputs in an ignored staging directory, without touching the running installation or its database.
- Add an MCP setup guide with exact STDIO configuration, explicit target application directory, supported tools and example chat requests, runtime/startup requirements, cache freshness, errors, replay semantics, and rollback/removal steps. Provide a configuration template without secrets or machine-specific committed absolute paths.
- Link the guide from README. Ignore newly generated WPF temporary project files and MCP build/staging artifacts without removing the pre-existing untracked temporary project.
- Do not silently replace/restart the user's currently running bot or mutate their real account/queue while verifying. The tested build can be installed and connected after review.

## 6. Validation and acceptance

1. Core tests using temporary/in-memory SQLite: account/village/job isolation; safe DTOs; cached/unknown observation times; invalid settings and queue levels; running/transitional-state rejection; legal paused edits; contiguous ordering; revision conflicts; identical replay and request-ID collision; unchanged quarantine.
2. Bridge tests: real named pipe request/response, malformed/oversized payloads, timeout/cancellation, app unavailable, concurrent clients and clean shutdown. Ensure handlers call the shared running service instance.
3. MCP protocol integration with an actual STDIO process and SDK client or independently framed JSON-RPC: initialize, tools/list, representative tools/call reads and writes through an isolated bridge/database, expected error behavior, stdout cleanliness, and process shutdown. Never use live game actions in tests.
4. Primary-agent review of all changed source and protocol/ownership/concurrency boundaries; fix material findings and add targeted regression coverage.
5. Run the complete existing test suite, Release solution build, and publish smoke check. Record actual results and any environmental limitations. If a pre-existing failure occurs, establish it against baseline and avoid disguising it as an MCP regression.
6. Leave work on the MCP branch with an updated implementation/verification record. The existing published baseline remains recoverable.

## Delegation and blocker handling

The primary agent owns this plan, final review, and acceptance testing. The user requested implementation by GPT-6.1 Terra with high reasoning. Any genuine blocker is assigned to GPT-6 Astra, with a bounded task and explicit write ownership. Implementation work is not complete merely because a delegate reports success; the primary checks and tests it independently.

## References

- OpenAI MCP connection/configuration: https://learn.chatgpt.com/docs/extend/mcp
- Official C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- STDIO hosting/transport documentation: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md

## Completion record

Implemented and independently verified on 2026-11-02. Release build: zero warnings/errors. Complete test suite: 239/239 passed. An independent MCP process exercised real bridge reads/writes, replay, ownership/revision checks, protocol mismatch, timeout, and app-unavailable errors. The self-contained published server also queried the actual staged desktop application with an empty temporary database. See [verification details](mcp-verification.md) and [setup instructions](mcp-setup.md).

Replay receipts are bounded in memory for this release, with restart semantics explicitly documented. List bounds are explicit in responses. Installation and registration have not been applied to the user's running bot.
