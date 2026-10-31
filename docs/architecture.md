# Retromind Architecture (high-level)

This document summarizes how Retromind is structured today and where core behavior lives.

## Tech stack
- UI: Avalonia (`12.x`) with MVVM (`CommunityToolkit.Mvvm`)
- Language/runtime: C# on `.NET 10` (`net10.0`)
- DI: `Microsoft.Extensions.DependencyInjection`
- Video preview pipeline: `LibVLCSharp` (BigMode)
- Audio/SFX helpers: external players (`ffplay`, `sidplayfp`) via services

## Runtime profile and startup
- `Program.Main` configures runtime behavior before Avalonia starts:
  - `--bigmode` startup mode
  - Linux defaults to X11/XWayland; `--avalonia-platform=wayland` opts into Avalonia's experimental native
    Wayland backend with X11 initialization fallback, while `x11` and `auto` retain the stable X11 path
  - AppImage portable HOME/XDG redirection via `PortableEnvironment.ApplyPortableXdgPaths()`
  - mandatory LibVLC initialization (`Core.Initialize()`)
- Release AppImages use checksum-pinned official `appimagetool` and static Type-2 runtime artifacts, removing
  the host `libfuse2` dependency. Their versioned filename, embedded GitHub Releases update information, and
  matching `.zsync` metadata are produced together and must not be renamed after the build. A legacy-named
  `.zsync` alias keeps update discovery working for AppImages released before the redundant `linux` filename
  segment was removed; it points to the single canonical AppImage asset.
- `App.OnFrameworkInitializationCompleted` then:
  - synchronizes portable themes (`AppPaths.EnsurePortableThemes()`)
  - bootstraps settings first
  - builds final DI container
  - creates `MainWindow` and triggers async `MainWindowViewModel.LoadData()`
  - falls back to the visible maximized desktop when direct BigMode startup cannot continue after a library-load failure
  - suppresses native Wayland server-side decoration negotiation before the first surface is created,
    then keeps the startup surface transparent until the compositor confirms the requested
    maximized/fullscreen state; together these prevent a brief decorated normal-window flash

## Repository map (logical)
- `Views/`: Avalonia views, including host-level behavior (`BigModeHostView`)
- `ViewModels/`: orchestration and state; large view models such as `MainWindowViewModel` and
  `SettingsViewModel` are split into partial files by concern
- `Models/`: persisted domain and settings (`MediaNode`, `MediaItem`, `AppSettings`, etc.)
- `Services/`: persistence, launcher, import/store import, scraping, audio, themes
- `Helpers/`: portability, path safety, converters, environment sanitizers, UI helpers
- `Extensions/`: `ThemeProperties` attached-property surface for runtime themes
- `Themes/`: shipped editable runtime themes
- `Resources/`: localized strings
- `tests/Retromind.Tests/`: xUnit tests for deterministic, high-risk application behavior

## Main UI architecture (desktop mode)
Main window is a layered shell:
1. Background/wallpaper layer
2. Three-column working layout:
   - left: tree (`RootItems`, `SelectedNode`)
   - center: active content VM (`SelectedNodeContent`, usually `MediaAreaViewModel`)
   - right: details for selected media item
3. Full-screen overlay slot (`FullScreenContent`) used by BigMode host

`MainWindowViewModel` is the core orchestrator:
- owns tree/content selection, command wiring, persistence triggers, and lifecycle cleanup
- rebuilds center content through a cancelable `UpdateContent()` pipeline
- keeps content updates race-safe (CTS + TCS), with background item collection and single UI-thread commit
- preserves and restores search/filter UI state when switching between node view and global search
- opens the library-statistics dialog and translates its navigation/filter requests back into the existing
  node-content or global-search workflows
- coordinates explicit item moves between nodes through a single-selection tree or tile-to-tree drag-and-drop;
  both paths share the same confirmation and move transaction, while inherited launch settings are resolved
  from the new parent without copying node defaults onto the item

## Persistence model
Persisted app/library data is portable under `AppPaths.DataRoot` (AppImage directory or app base directory).
Secrets that use the shared host credential abstraction (currently scraper credentials, the GOG refresh token,
and the RetroAchievements and Steam Web API keys) are stored through `ISecretStore`, not in `DataRoot`.

### Library (`retromind_tree.json`)
- service: `MediaDataService`
- atomic write strategy: temp -> backup -> replace
- IO is serialized with `SemaphoreSlim`
- `LibraryChangeTracker` owns versioned dirty tracking and debounced saves
- complete library-save pipelines are serialized so snapshot serialization cannot reorder writes
- bound collections are snapshotted on the UI thread and serialized in the background
- dirty state is cleared only after a successful write; save failures remain dirty and are surfaced to the user
- a valid empty primary library is authoritative; the backup is used only when the primary file is missing,
  unreadable, or invalid
- if persisted library files exist but neither primary nor backup can be loaded, startup keeps the library
  untracked and read-only so shutdown cannot replace the failed data with an empty library

### Settings (`app_settings.json`)
- service: `SettingsService`
- same atomic temp/backup strategy with serialized IO
- debounced saves carry cancellation through the write gate; once a write starts, its atomic
  temp/backup/replace transaction completes before a restore can proceed
- corrupt or structurally unusable settings are quarantined and fallback restore from `.bak` is attempted
- save failures propagate to the caller and are surfaced to the user
- scraper profile metadata remains portable, while runtime-only API keys, passwords, and client secrets are
  hydrated from the host keyring by `ScraperCredentialService`
- legacy `EncryptedApiKey`, `EncryptedPassword`, and `EncryptedClientSecret` settings fields are read only for
  migration and removed from both the primary settings file and `.bak` after the durable keyring write was verified;
  they remain intact, and may still enter metadata backups, while migration is pending because the keyring failed
- the settings dialog edits a detached working copy; **Save** commits it, while Cancel or closing the
  window discards it
- title sorting remains a live preview while the dialog is open and is restored if the dialog closes
  without saving

### Explicit save results and callers

`MainWindowViewModel.SaveData()` returns `true` only when the tracked library is clean after its save
and the current settings snapshot was written without an outstanding save failure or a newer settings
edit. A clean, tracked library needs no further write. `LibraryChangeTracker.SaveIfDirtyAsync()` itself
returns `Task`, not a persistence acknowledgement: it may skip clean/stale work or an unavailable snapshot.
The view model therefore also checks tracking and dirty state. A library-load failure, an untracked
library, or `SettingsService.HasLoadFailure` prevents a full-success result; the existing protections
against overwriting unreadable data remain in force. Healthy stores may still be saved independently.
Skipping a protected store does not fabricate a write exception or another dialog; startup already
explains the load failure.

This is not an atomic transaction across the two files. A library failure stops that explicit attempt
before settings are written; settings can fail after the library was successfully saved. Internally,
the settings-dialog completion also receives `SettingsSaved`, which confirms the actual settings write
independently of full success. Credential changes are completed when that write occurred, avoiding a
credential rollback that would contradict already-persisted settings. Existing credential rollback
handling remains in place when settings were not written.

Callers distinguish durable completion from applied in-memory or filesystem work:

- Media moves and category moves between parents return the save result. A failed save does not undo
  the completed asset move or category assignment; retry saves the changed library. Reordering within
  one parent retains its existing in-memory acceptance and debounced-save behavior.
- Imports, edits, scraping, statistics and bulk edits retain their applied changes on a failed save.
  They rely on the central persistence notice rather than claiming durable success or adding dialogs.
- Existing deletion and runner-removal gates still require confirmed persistence before subsequent
  destructive work. Runner-assignment replacement and achievement identification keep their existing
  explicit unsaved-change feedback.
- GOG/Epic installation, update and repair callbacks retain their physical-success boolean contract so
  the media editor reloads the actual launch configuration. They replace their ordinary completion text
  with a localized pending-save message when persistence fails. GOG installer staging is retained in that
  case. DLC batches stop further installations after such a failure, retain applied state and staging,
  and do not report an all-success result. GOG uninstall suppresses its success dialog on save failure
  while still acknowledging the completed physical uninstall. Epic uninstall retains its separate
  `StateSaved` result and existing deletion gate.

Library save failures retain dirty state and version; later explicit/debounced saves can retry. Settings
failures leave the live settings available for a later save. There is no automatic rollback of completed
library, file or installation work. Process crashes before a successful retry can still lose in-memory
metadata. There is no cross-file journal or new retry framework.

Shutdown deliberately keeps its separate safe-close contract: protected stores are skipped rather than
overwritten. A real final-write failure still cancels cleanup and returns control to the existing
exit-without-saving decision; the stricter full-save result does not trap users with unreadable startup
data in the application.

### Secret storage and cache boundaries
- `ISecretStore` is shared by store authentication and account integrations. On Linux the composite
  implementation prefers the host Secret Service and falls back to session-only memory when persistent
  storage is unavailable
- scraper credentials require the persistent `IPersistentSecretStore` boundary: edits fail visibly when the host
  keyring is unavailable instead of falling back to session-only memory. Moving the portable data to a different
  host preserves scraper profiles but requires the secrets to be entered again
- after legacy migration, scraper credentials join the GOG refresh token and RetroAchievements and Steam Web API
  keys in being excluded from `app_settings.json` and metadata backups; all of them are also excluded from
  `retromind_tree.json` and integration caches
- integration caches contain replaceable non-secret data. RetroAchievements catalog, progress, and badge caches,
  plus Steam and GOG progress and achievement-image caches, may follow portable-HOME migrations, while the
  corresponding credentials always remain in the host secret store or session memory

#### Secret-store outcomes and session fallback

The original `ISecretStore` availability/read/write/delete methods remain available. Additive
`SetWithStatusAsync(key, value, ct)` returns a per-write `SecretStorageStatus`: `Persistent` confirms a
successful durable-backend write, `SessionOnly` means only the current process retains the new value,
and `Failed` means the write could not be completed. Cancellation propagates instead of returning `Failed`.
`Unknown` makes no durability promise and also represents an absent/not-yet-observed value. Default interface
implementations preserve existing implementers: only `IPersistentSecretStore` implies persistence;
other legacy implementations return `Unknown`. `InMemorySecretStore` explicitly reports `SessionOnly`.
`GetStorageStatus(key)` is a snapshot of the last observed value/outcome, not a fresh backend probe or a
guarantee that an entry exists. The per-write result should be used for decisions about that write.
`IsAvailableAsync()` still means that some backend is usable; it never promises durable storage.

`CompositeSecretStore` serializes operations and tracks authoritative session overrides by `SecretKey`.
When a primary write fails or the primary is unavailable, the new value remains in the existing in-memory
fallback and is returned ahead of any older persisted value, even after the primary becomes available again.
Reads never migrate credentials. Only a later explicit write attempts persistence again; successful primary
writes invalidate the session override and clean up the stale fallback. A cleanup failure is diagnosed, but
the stale fallback is no longer eligible for reads. `sessionOnly: true` intentionally bypasses the primary
entirely and reports a diagnostic rather than a primary-store failure. This mode is an explicit constructor
policy, not a new UI setting; its deletes affect only session data and make no claim about the bypassed backend.
The fallback is owned by the composite and must not be mutated independently.

Deletion of the persistent entry must be confirmed: an unavailable or failing primary cannot silently return
success. Fallback deletion is still attempted, and a per-key in-memory tombstone prevents stale credentials
from reappearing in this instance, even if either deletion failed. The caller receives a sanitized exception
and can explicitly retry deletion or replace the value. Pre-canceled operations do not mutate state; expected
cancellation is neither diagnosed as a backend fault nor converted into fallback writes.

GOG login and token rotation use the per-write result; successful authentication is allowed with a session-only
refresh token, exposed separately through `GogAuthService.RefreshTokenStorageStatus`. Authentication/refresh
success does not imply persistence. Both-backend write failure is handled as a failed credential save.
GOG sign-out with `forgetPersistentToken: false` retains its existing meaning: clear the active OAuth session
while keeping the stored credential available; only `true` requests deletion of both token and cache identity.
Steam and RetroAchievements account services expose `ApiKeyStorageStatus`; their existing settings status
fields explicitly label session-only keys on save and reopen, in English and German, without new dialogs.
Read failures reuse the existing read-error status. Existing settings rollback continues to use the same
secret-store boundary. Scraper migration still requires the direct `IPersistentSecretStore` and verified
read-back before removing legacy credentials; no session fallback qualifies for that migration.

The composite receives `IErrorService` through DI and reports `SecretStore.PrimaryWriteFailed`,
`SecretStore.PrimaryReadFailed`, and `SecretStore.PrimaryAvailabilityFailed` (`Warning`),
`SecretStore.PrimaryDeleteFailed` and fallback availability/write/read/delete failures (`Error`), plus
`SecretStore.FallbackCleanupFailed` and deliberate `SecretStore.SessionOnly` (`Diagnostic`). Context is restricted
to operation, a fixed failure reason, and an exit code when available. Keys, service/account identifiers, values,
backend exception messages/stacks/types, arguments, stdin, stdout, and stderr are never sent to diagnostics.
Logging failures cannot alter credential outcomes. No notification callback is supplied.

`SecretServiceSecretStore` retains trusted host/bundled `secret-tool` discovery and environment sanitization.
Availability checks only locate the executable and do not unlock/probe a wallet. Failed reads and deletes now
throw when the command cannot start instead of pretending that an entry is absent/deleted. Command failures
expose only fixed messages and numeric exit metadata, without raw output or inner exceptions. Lookup/clear exit
code 1 with empty stderr retains the existing missing-entry meaning. Output pipes drain before stdin is written;
the ten-second timeout covers input, process waiting, and output reads. Cancellation/failure terminates the owned
process tree and observes pipe tasks, with a bounded final exit wait.

Limits: session overrides and deletion tombstones do not survive restart. After a failed persistent write an
older durable credential may still exist, and after a failed delete it may become readable on restart. No
automatic transfer, persistent reconciliation journal, or backend repair is performed. A canceled/timed-out
command might already have changed the keyring before termination; cancellation cannot roll back that external
commit. The component does not coordinate independent keyring writers or multiple application instances.
The existing settings rollback is value-based, not an atomic transaction across the keyring and memory.
If a previous durable value could not be read, a later rollback cannot reconstruct it; keyring availability
changes during a multi-key settings save therefore remain a reconciliation limitation.

#### GOG token activation boundary

`GogAuthService` stages OAuth responses locally. Refresh and interactive login accept a new active token only
after `StoreRefreshTokenAsync` confirms `Persistent` or `SessionOnly`; `Unknown` is rejected along with `Failed`.
Refresh returns `false` on a rejected write, while interactive login throws a fixed, credential-free exception.
The previous active token and cached account profile remain unchanged on failure or cancellation. If that
previous access token still has time remaining, `GetAuthStateAsync` may continue to report its original expiry;
the rejected candidate is never returned by a later access-token lookup. The existing 30-second refresh buffer
and account-info fallback remain in effect.

Optional gameplay-identity storage and, for login, account-info retrieval complete before activation. Cancellation
propagates and is checked again immediately before the commit. Nothing awaited/fallible follows token activation.
Login prepares the profile in a local variable and clears any previous profile when the new account-info lookup
falls back. Refresh invalidates the cached profile if the token's user ID changes. Best-effort identity-write
failure alone still does not reject an otherwise saved session.

A service-local semaphore serializes refresh, OAuth code exchange/activation, sign-out, and the full Comet
credential snapshot. The interactive browser/callback wait remains outside this gate. Concurrent refresh callers
recheck the active token after acquiring it and reuse a committed token instead of rotating the same refresh token
in parallel. Sign-out cannot be overwritten by a refresh that was already in progress. Gameplay credentials use
one immutable committed token snapshot. Comet checks that the stored refresh token matches the committed token
before combining it with the access token, user ID, and profile; a mismatched pair is unavailable rather than mixed.

`IErrorService` is injected into the auth service. `GogAuth.TokenStorageFailed` (`Error`) diagnoses a failed or
unconfirmed write by a store without its own reporting, including `Unknown`. Reports contain only the fixed
operation (`Refresh`/`SignIn`), storage status, and an OAuth-response-received flag. No exception, token, OAuth code,
client secret, user ID, or server response enters this report. When the registered `CompositeSecretStore` returns
`Failed`, its backend reports already describe that failure and no duplicate auth report is emitted. Diagnostic
output cannot change the result or activate a rejected token. Expected cancellation is not logged as a failure;
no dialogs are added.

This is an in-memory activation boundary, not an OAuth/keyring rollback transaction. An OAuth refresh may already
have invalidated the old refresh token server-side. Keeping the old local token does not guarantee another refresh
will work; interactive sign-in may be necessary. Likewise, storage can commit before cancellation or an account-info
request is canceled. Such a new refresh token may remain stored while the old session (or no session) remains active.
The service never overwrites it with an old refresh token or automatically retries the failed operation. A later
normal refresh can use whichever token the store actually retained. Neither server rotation nor independent
keyring writers are coordinated across multiple Retromind processes.

### Versioned metadata backups (`Backups/`)
- service: `MetadataBackupService`; UI: `MetadataBackupViewModel` / `MetadataBackupView`
- each ZIP contains serialized snapshots of `retromind_tree.json` and `app_settings.json` plus a manifest
  with format version, Retromind version, creation reason, and SHA-256 checksums
- archives are written to a temporary file, fully reopened and validated, and only then published
- automatic backups have a master switch and separate triggers for startup, accepted bulk metadata edits,
  bulk scraping, and restore; startup defaults off while the mutation-related triggers default on
- manual and pre-restore backups are retained until explicitly deleted; the ten newest valid startup,
  bulk-edit, and bulk-scrape backups are retained together
- listing and retention fully validate archive contents and checksums off the UI thread; invalid archives
  remain visible and are neither counted toward retention nor deleted automatically. Restore validates
  again independently of any earlier listing
- the manifest is limited to 64 KiB and each library/settings JSON entry retains a 512 MiB compatibility
  ceiling. Entry streams must end at their declared size and are rejected on the first trailing byte instead
  of being copied without a bound. Listing, retention, and post-write validation hash and parse entries without
  materializing JSON strings; restore materializes the two strings sequentially. The 512 MiB ceiling remains a
  deliberately generous compatibility bound, not a target size for normal libraries
- when its trigger is enabled, an accepted bulk edit or started bulk scrape requires a successful pre-change
  backup before any item is mutated
- restore validates both archive integrity and the actual library/settings serialization contracts, optionally
  creates a configured pre-restore safety backup when the current state is valid, and writes through the
  existing persistence gates
- a settings restore scope drains active writes and rejects all regular settings saves before capturing
  the pre-restore state, including direct runner-registration saves. Restore and rollback writes remain
  available; failure releases the block, while success retains it until the application restarts
- restore defaults to the library only; the optional full mode also restores `app_settings.json`, including
  emulator/runner configuration and the encrypted parental-control password, while item protection flags are
  always restored because they belong to the library model
- after restore, normal shutdown persistence is skipped because the in-memory models still contain the
  preceding state; Retromind closes and loads the restored files on its next start
- metadata backups intentionally exclude `Library/`, themes, portable HOME data, runners, games, media,
  external saves, and host-secret-store credentials; full portable-folder copies remain the disaster backup

## Central error output

`IErrorService` is singleton infrastructure for classification, durable diagnostics, and optional user
notifications. `ErrorService` has no dependency on store, scraper, launcher, or file-management services.
It never decides whether an operation should abort, retry, roll back, recover, or allow shutdown. Those
decisions and exception propagation remain with the calling service or UI orchestrator. There is no
global exception handler, automatic exception swallowing for business operations, or result hierarchy.

### Public contract and classification

- `IErrorService.Report(ErrorReport error, Func<Task>? notifyUser = null)` writes one diagnostic record
  and optionally schedules the supplied notification on the Avalonia UI thread through `UiThreadHelper`.
  Notifications are explicit; severity alone never opens a dialog. The callback owns its localized
  presentation, window selection, and any UI-specific reset behavior.
- `ErrorReport` is an output-only record with a stable `Code`, caller-selected `Severity`, originating
  `Component`, understandable/localizable `Message`, optional `Exception`, optional `CorrelationId`,
  optional string dictionary `Context`, and optional `SensitiveValues` for known credentials in free text.
  It does not change library/settings serialization contracts. Exception output includes inner exceptions
  and available stack traces; arbitrary `Exception.Data` objects are not serialized.
- `ErrorSeverity.Diagnostic`: technical information useful for investigation, without a user action.
- `ErrorSeverity.Warning`: a limitation or degraded behavior that may need attention.
- `ErrorSeverity.Error`: the requested operation failed.
- `ErrorSeverity.Critical`: a serious failure threatening continued application operation or data integrity.
  Even critical severity performs no automatic recovery or shutdown.

### Storage, retention, and sensitive data

DI resolves the directory with `Path.Combine(AppPaths.DataRoot, "Logs", "Exceptions")`, preserving the
existing AppImage/data-root rules. It is created lazily. `Logs/Launch/` and `LaunchLogService` remain
unchanged. Central records are UTF-8 JSON Lines in `Logs/Exceptions/errors.jsonl`, with UTC timestamps.
Writes and rotation are synchronous and serialized inside the singleton, with no background queue to drain
at shutdown. A nonempty file rotates before an append would exceed 1 MiB. The three newest archives are
`errors.jsonl.1` through `.3`, with `.1` the newest; older archives are replaced. Constructor parameters
allow a different byte threshold and archive count (including zero). A single record may exceed a custom
threshold smaller than the record; the next append rotates it. Individual records are limited to 64 KiB;
oversized reports retain shortened core fields, omit context, and include `Truncated: true`.
New Unix log files are created with owner-only read/write permissions.

Redaction happens before JSON serialization and before truncation, across the message, exception chain,
code, component, correlation ID, context keys, and context values. The existing `LaunchSecretRedactor`
sensitive-name policy and command-argument redaction are reused without changing launch logs. Additional
handling covers JSON/key-value credentials, authentication and cookie headers, Bearer/Basic credentials,
and URL userinfo. Sensitive context keys have their values replaced, and those original values plus
explicit `SensitiveValues` are also removed wherever they occur in free text. This helper is static
infrastructure, not a dependency on the launcher service.

Callers must avoid request payloads and credentials in diagnostic context. Pattern matching cannot
identify an arbitrary unlabeled secret: supply it through `SensitiveValues` when free text may contain it.
Those values are used only for redaction and are never serialized. Logging/redaction/rotation failures
and dispatcher/notification failures are contained strictly inside the output boundary, never recursively
reported or printed with potentially sensitive exception details, and do not replace the original failure
or prevent an independently requested notification.

### Persistence, audio, video, and Winetricks integration

`MainWindowViewModel.ReportPersistenceSaveFailure()` reports
`Persistence.SaveFailed` with severity `Error`, the existing localized `Persistence.SaveFailedFormat`
message, the original exception, and `Operation=Save` context. Its existing atomic notification gate
permits only one notice while a persistence failure remains unresolved. Library failures are diagnosed
by the tracker event, without a duplicate report from the explicit-save catch. Separate library/settings
failure state prevents a successful write to one store from clearing the other store's failure. Settings
completion also checks edit and failure generations so an older success cannot clear a newer failure.
The gate resets after both stores are healthy, tracked and clean. Each actual failed attempt is still
logged, including attempts whose notice is suppressed. Missing dialog owners and failed dialogs retain
their existing presentation-gate reset without clearing the underlying failure state. No extra
logging-failure dialog is shown.

During the final flush, existing suppression prevents persistence-error dialogs; failure still returns
`false`, skips cleanup, resumes launcher operation, and lets `MainWindow` own the existing
exit-without-saving decision. The final settings-save catch and other error paths have not been migrated.

`AudioService` receives `IErrorService` through constructor injection and reports audio failures only to
the central diagnostic log by default, without a notification callback or dialogs:

- `Audio.StartFailed` (`Warning`): the player could not be started, including a null process result.
- `Audio.ProcessFailed` (`Warning`): the current player exits unexpectedly with a nonzero exit code.
- `Audio.StopFailed` (`Diagnostic`): stopping, waiting during stop, or disposing the player fails.

All audio reports use component `AudioService` and include the operation and player type (`ffplay` or
`sidplayfp`), plus the file path or exit code where relevant. Failures captured under `_processLock` are
reported after releasing it so synchronous central log writes never run under the process lock. Exit
monitoring also begins outside the start lock, including for a process that has already exited. Cleanup
finishes before reporting, and even an error-service implementation that throws cannot interrupt cleanup.
Player selection, arguments, playback ordering, cancellation, and playback-ID guards remain owned by
`AudioService`; missing files retain their existing treatment, intentional stops and superseded playback
do not produce unexpected-exit reports, and failures never trigger automatic retries. A narrow internal
process adapter permits deterministic regression tests without starting audio players.

`BigModeViewModel` receives the same `IErrorService` from `MainWindowViewModel` when BigMode is created.
The existing preview and background players remain in the view model; no video service is introduced.
Video diagnostics use `Video.PlaybackFailed` (`Warning`), `Video.StopFailed` (`Diagnostic`), and
`Video.ResourceCleanupFailed` (`Diagnostic`). Reports include operation, surface index (0/1 for preview
A/B, 2 for the independent background channel, -1 for shared teardown), video path when available, and
the original exception. Native format failures also record the requested width and height. There are
no notification callbacks or dialogs. `Helpers/Video/VideoDiagnostics` only builds reports and dispatches
them to the central service on a worker; it adds no log sink, retries, playback policy, or service boundary.
Its guarded tasks keep synchronous log writes off VLC callbacks and the UI thread and out of buffer/media
locks. These best-effort output tasks are not awaited by playback or native teardown; abrupt application
exit can therefore interrupt a pending diagnostic write.

BigMode observes failed `Play()` returns and `EncounteredError` events as well as existing exception
paths. Diagnostic state tracks expected stops, current preview generation, player identity, and a single
playback-failure notice per attempt, independently of the existing crossfade state. Theme/view switches,
superseded starts, launch transitions, disposal, and cancellation do not become unexpected-playback reports.
Actual exceptions from stopping or releasing resources are still diagnostic failures, including at shutdown.
`LibVlcVideoSurface` reports native format/allocation and frame-listener failures; `VideoSurfaceControl`
reports bitmap/render failures through its LibVLC surface. The originating component is retained in the report.

Native setup validates dimensions before conversion or allocation: width and height must be positive and
at most 16,384 pixels, and tightly packed BGRA data must fit the 256 MiB per-frame budget. Byte arithmetic
uses unsigned 64-bit intermediates. Invalid dimensions, a null format pointer, and allocation failure return
0, with zero pitch/lines and no published frame; managed setup exceptions never cross the native callback.
Success returns one allocated picture buffer. Pixel-plane pointers are aligned to 32 bytes, while the
original allocation is retained for freeing. The tightly packed row layout is unchanged; padded pitches
and line counts are recommendations rather than requirements of the
[LibVLC 3 callback contract](https://videolan.videolan.me/vlc-3.0/group__libvlc__media__player.html).
Disposal and format setup check surface lifetime under the same lock. Normal native cleanup frees the
buffer without disposing the reusable surface. Bitmap sizing takes a width/height snapshot under that lock
so a concurrent format change cannot combine dimensions from different frames; renderer byte arithmetic
is checked as well. Native callback pointers are still trusted inputs from VLC;
arbitrary invalid non-null pointers or native decoder/driver crashes cannot be recovered by managed logging.

The previous pending-media map could lose a reference: queue media M1 for surface A, reuse A with M2 before
the delayed `Stopped` callback arrives, then queue M2. Assigning `_pendingDisposeMedia[0] = M2` replaced M1
without disposing it. A deterministic regression test reproduces these two queue operations without loading
VLC. Each surface now retains a list; the existing stop/idle disposal points drain every queued media, and
final teardown snapshots and clears the lists under the media lock before disposing their contents. This
changes ownership bookkeeping, not crossfade timing or stop policy. Actual decoder memory corruption from
the former unchecked size arithmetic, and hardware-specific consequences of unaligned buffers, have not
been reproduced with real playback; the invalid calculations and missing alignment guarantee are confirmed
by code inspection and callback/buffer tests.
The former lifetime check outside the buffer lock also allowed a format callback already waiting for that
lock to allocate after disposal; a coordinated callback/dispose regression test confirms the corrected
ordering without real playback. Native error events carry no media-generation ID: guards reject known stale
generations and replaced players, but attributing an event emitted only after reuse of the same player still
depends on VLC's event ordering. No such late-error misattribution has been reproduced.

Winetricks result categories, diagnostic codes, and cancellation behavior are described under
[Winetricks results and diagnostics](#winetricks-results-and-diagnostics).

Future callers should first perform their own business error handling, then report a stable code,
appropriate severity, component, understandable message, and useful non-secret operation/correlation data:

```csharp
errorService.Report(new ErrorReport(
    "Component.OperationFailed",
    ErrorSeverity.Error,
    nameof(MyService),
    "The requested operation could not be completed.",
    exception,
    CorrelationId: operationId));
```

UI orchestrators may supply `notifyUser` when user feedback is needed and own any duplicate-notification
policy. Business services can report without UI dependencies. Reporting does not consume an exception or
change the caller's return/throw/recovery policy; no additional services are migrated automatically.

## Path and portability contract
- persisted file paths are expected to be DataRoot-relative when possible
- with portable launch paths enabled, every emulator, node, and item save normalizes known single-path
  environment overrides inside DataRoot; item working-directory and XDG paths follow the same rule
- path resolution for general runtime assets/documents uses `AppPaths.ResolveDataPathInsideRootOrEmpty`;
  runtime themes use the stricter `ThemePathResolver` boundary described below
- escaping `DataRoot` is intentionally blocked (`TryResolveDataPathInsideRoot`)
- generic Linux filesystem identity and node asset-directory operations are case-sensitive
- this boundary is central for portable use and for avoiding accidental absolute-path drift
- provider-specific Windows metadata may use a case-insensitive, segment-by-segment fallback only after
  containment in the selected install root has been established
- GOG and Epic `Store.InstallPath` values inside DataRoot are stored DataRoot-relative when portable paths are
  enabled; the shared resolver also supports legacy and intentionally external absolute paths
- Wine/Proton prefix paths are resolved centrally against `LibraryRoot`; relative and absolute inputs are
  canonicalized before launcher, GOG, or Winetricks operations consume them
- destructive store operations have an additional ownership boundary: GOG install directories must not
  be dangerous roots, must not traverse symbolic links, and must contain a matching
  `.retromind-install.json` marker before recursive deletion
- the explicit legacy GOG linking workflow may establish that marker for an unmarked offline installation
  only when its `goggame-<id>.info` matches the selected owned game, the item's launch file is contained in
  that directory, and no other item references the same install root; linking itself preserves item assets
  and metadata
- GOG uninstall removes a separate prefix only when it is a non-shared subdirectory inside `LibraryRoot`
  without symbolic-link traversal; external prefixes and the shared `Library/Prefixes` or `Library/Games`
  roots are preserved together with their metadata for manual cleanup
- moving an item between nodes transfers its referenced assets through a staging transaction; failures roll
  files and the collection assignment back, while assets shared with another item or node are copied instead
  of removing the shared source. An incomplete filesystem rollback preserves conflicting or sole remaining
  files in place and surfaces their affected locations to the user
- media-editor and node-settings artwork changes remain detached until Save; imports are copied first and
  item deletions are quarantined until the model update succeeds, so Cancel and failed file operations leave
  the original files and assignments intact when rollback succeeds. Failed restoration retains the quarantined
  originals for recovery rather than discarding them

### Asset import and editor transactions

`FileManagementService` receives the singleton `IErrorService` through DI. Single imports and
`PrepareAssetTransactionAsync` share a local copy routine: create a unique `.retromind-asset-<guid>.tmp`
file with `CreateNew` in the destination directory, copy and flush all bytes, close both streams, then
publish with `File.Move(..., overwrite: false)`. An existing destination, including one created during the
copy, is preserved and the import fails. Only a temporary file actually created by that attempt is cleaned
up in `finally`, including partial-copy and publication failures. Temporary names do not match the asset
scanner's naming convention. Existing counters, title/ID and Manual/Music display prefixes, DataRoot-relative
references, single-import null results, and editor exception handling remain unchanged.
Renaming uses the existing best-effort image-cache invalidation helper so cache/render initialization failures
cannot prevent the underlying filesystem rename.

All mutation destinations use `AppPaths.TryResolveDataPathForMutation`; staging, publication, rollback, and
cleanup revalidate containment and symbolic-link/reparse-point components. Selected source files may remain
external and are read only, as before. These are managed path checks, not descriptor-relative filesystem
operations: a concurrent external replacement of path components between validation and IO remains a race.

`PreparedAssetTransaction` retains its quarantine-until-commit contract. Rollback removes imports before
restoring originals, never overwrites a conflicting original path, and removes only successful steps from
its pending work. If a step fails, `Rollback()` reports and throws an aggregate exception, keeps the transaction
unfinished and its remaining quarantine intact, and permits a later explicit `Rollback()` retry. `Commit()`
is rejected after rollback has started, preventing recursive cleanup from destroying recovery files.
`Dispose()` attempts rollback only if none was attempted yet; after a failed explicit/disposal rollback it
neither silently retries nor emits a duplicate report. Successfully completed steps are not repeated on retry.
Commit and successful rollback remain complete even if final quarantine-directory cleanup fails.

Diagnostic codes are `FileManagement.AssetImportFailed` and `FileManagement.AssetRollbackFailed` (`Error`),
`FileManagement.AssetRenameFailed` (`Warning`), and `FileManagement.AssetCleanupFailed` (`Diagnostic`). Reports
include the operation, asset type (or `Multiple` for a mixed transaction), relevant source/target or quarantine
paths, and the exception, using central redaction. There are no notification callbacks or new dialogs. The
failure is reported where handled; wrapper exceptions and `Dispose()` do not report it again, and failures
inside diagnostic output cannot affect file cleanup or the caller's result.

Recovery limits: filesystem permissions or device failures can also prevent temporary-file cleanup; the
remaining path is diagnosed. Quarantine remaining after a failed preparation or after application exit must
be recovered manually; there is no persistent transaction journal or automatic startup recovery. Atomic
publication does not promise recovery from power loss, and transactions remain single-owner objects rather
than a cross-process locking protocol. General item-move staging and rollback are unchanged.

## Theme subsystem
Themes are external runtime XAML loaded through `ThemeLoader`:
- resolves theme files against portable `ThemesRoot` and theme assets against their individual theme directory;
  rooted asset paths, traversal outside these roots, and symbolic-link/reparse-point components are rejected
- limits each runtime AXAML document to 1 MiB before parsing
- parses theme XAML at runtime and applies theme base path per view instance
- caches XAML text with LRU to reduce repeated file IO/parse overhead
- exposes theme metadata, visual tuning, selection effects, typography, video options, attract-mode options, etc. via `ThemeProperties` attached properties
- optionally derives a cached two-color accent palette from the active artwork; analysis is debounced during navigation and only themes that opt in through `DynamicAccentEnabled` receive host-updated colors
- user-visible runtime-theme text is exposed through localized `ThemeStrings` resource properties

Runtime AXAML is a trusted extension boundary rather than a sandboxed data format. Avalonia may resolve public
.NET types from loaded assemblies and invoke their constructors while materializing a theme. Retromind currently
ships all available themes itself and has no external theme distributor; if external themes are introduced, they
must be treated like executable extensions and obtained only from trusted sources unless the format is replaced
or constrained by a separately audited allowlist model.

### Portable theme sync/update at startup
`AppPaths.EnsurePortableThemes()` implements best-effort shipped-theme sync:
- first-time copy of missing top-level themes
- manifest-based update gate via `.retromind-theme.json`
- validated staging and same-filesystem directory swaps keep the active theme intact until a complete replacement is ready;
  an interrupted swap restores its backup before the next synchronization attempt
- manifest recovery when file is missing/corrupt but theme content still matches shipped version
- restoration of missing theme directories (any directory containing `theme.axaml`)
- local theme modifications are preserved (no forced overwrite when hashes differ)

## Image loading and cache

`AsyncImageHelper` owns asynchronous bitmap loading, downsampling, cancellation, and its shared LRU cache.
Cache entries are reference-counted while controls display them. The cache has a hard limit of 200 entries;
if every cached bitmap is currently in use, a newly loaded bitmap remains control-owned and uncached instead
of allowing the shared cache to grow beyond its limit. Desktop views use a small bounded parallel decode lane
so the visible grid can populate promptly, while the BigMode host opts its complete visual subtree into a
separate serial lane that keeps rapidly superseded carousel artwork from competing for CPU time.

## BigMode architecture
BigMode is an overlay workflow with clear host/VM split:

### Host (`BigModeHostView`)
- theme root attachment/swapping (`SetThemeContent`)
- shared, theme-independent Home presentation for every opted-in root theme
- shared video control attachment to theme-defined slots
- theme-independent system menu presentation, including the live controller-binding overview and application-exit confirmation
- system-host mode with per-system subtheme loading (`Themes/System/<id>/theme.axaml`)
- each subtheme instance receives a fixed `SystemPreviewNode`, keeping the outgoing
  theme's node-specific bindings stable throughout the crossfade
- subtheme cache with LRU
- shipped system themes currently include Default, C64, Amiga, PC, SNES, PS1, PS2, GBA, Mega Drive, and MAME variants
- theme guardrails/tuning for list behavior and selection visuals
- global cursor idle hide/show behavior (mouse-only)

### ViewModel (`BigModeViewModel`)
- navigation state (categories/items), selection memory, and robust restore from persisted settings
- receives semantic controller actions from `GamepadService`; action-button assignments and the two-button
  tracked-session stop gesture live in `AppSettings.ControllerBindings`, while D-pad/left-stick navigation stays fixed
- routes BigMode key events through semantic actions backed by `AppSettings.KeyboardBindings`; bindings are stored as
  readable Avalonia key names, use safe defaults for missing/invalid values, and intentionally exclude modifier chords
- page-navigation actions jump bounded chunks in library lists and the achievement grid, while Home reuses its
  native page model where available; reaching a list boundary precedes wrap-around so uneven final pages remain predictable
- owns system-menu state and selection; application shutdown is requested through an event so the main window retains
  its normal final-save and close-failure workflow
- the virtual library root uses `AppSettings.RootBigModeThemePath`; its selected top-level node remains
  preview content only, so the root theme is never inherited into that node
- every real library node resolves only its own `ThemePath`; missing or invalid values use the Default theme and
  never inherit a normal or System Host theme from a parent node. `SystemPreviewThemeId` follows the same local
  rule and independently falls back to its Default system subtheme
- node-aware artwork resolution and fallback overrides (logo/marquee etc.)
- wallpaper-aware screenshot fallback for themes that must avoid decoding two full-screen backgrounds for one selection
- dual preview surfaces with crossfade and defensive playback sequencing; presentation state avoids stale
  frames and unnecessary restarts when multiple items share the same node fallback video
- secondary background video channel support
- owns preview/background player sequencing and reports video failures to `IErrorService`; native frame
  allocation and callback safety remain in `LibVlcVideoSurface`, and bitmap presentation remains in
  `VideoSurfaceControl`. Error reporting never performs playback recovery or changes crossfade generations
- attract mode (theme-driven idle navigation)
- circular carousel windows retain overlapping item containers during one-step navigation instead of rebuilding every card
- root themes can opt into a non-persisted Home dashboard through `ThemeProperties.SupportsHome`; the user-facing
  `AppSettings.EnableBigModeHomeScreen` switch must also be enabled. Its recent,
  favorite, and library shortcuts retain their real source nodes for inherited artwork and launch behavior.
  The Library row keeps a transient mini-navigation path over the real node hierarchy without mutating the tree
  or the regular BigMode navigation stacks.
  Y/Triangle or the keyboard Home key toggles Home against an untouched library navigation snapshot, while direct
  BigMode startup opens Home and an explicit desktop item selection keeps the established library start position
- mirrors final BigMode selection back into core app settings on exit

## Search architecture
Global search uses a dedicated `SearchAreaViewModel`:
- debounced, cancelable background evaluation
- scope selection by node IDs
- parental-filter-aware visibility
- row grouping for large virtualized result grids
- shared filter state (text/favorites/status/year) coordinated by `MainWindowViewModel`
- saved search terms persist their associated favorites-only state as part of the saved filter behavior
- structured query matching includes `played:true` and `played:false` (alias: `started`); play evidence is
  defined centrally by `MediaPlayStateHelper` as a launch count, recorded play time, or last-played timestamp
- store-aware query fields expose normalized GOG, Steam, and Epic identities through `store:`, while
  `gogupdate:true` and its `has:gogupdate` shorthand match GOG items with either a main-game or DLC update
  without exposing internal `Store.*` custom fields in the filter builder
- media completeness queries use the existing `has:` / `missing:` syntax for every supported item asset type;
  presence is based on stored asset references and deliberately avoids filesystem access during filtering

## Library statistics
`LibraryStatisticsViewModel` calculates statistics on demand from the existing media tree; there is no
separate statistics database or persisted aggregate state.

- scope defaults to the entire visible library and can be changed to any selectable category including its
  descendants; categories are identified by their full tree path
- protected items remain part of every aggregate and ranking while parental filtering is active, but direct
  navigation from protected ranking entries is disabled until the lock is opened
- summary values cover item count, total play time, launches, favorites, and play status
- rankings show the ten most-played and ten most-recently played items and can navigate back to the real item
- distributions group the ten largest values by category, platform, genre, release year, or status
- clickable summary cards reuse the normal filters rather than maintaining a second filtering implementation;
  category scope stays in `MediaAreaViewModel`, while library-wide results use `SearchAreaViewModel`
- “In progress” and “Never started” combine the normal `Incomplete` status with `played:true` or
  `played:false`, using the same `MediaPlayStateHelper` definition as the displayed counts
- the media editor exposes launch count, total play time, and last-played time in a collapsed statistics
  section. Changes remain staged until the editor is saved; resetting all three values removes the same play
  evidence used by statistics and `played:` searches

## Library health check

`LibraryHealthService` performs an explicitly read-only, cancelable comparison between the loaded library tree
and the filesystem. It reports missing or invalid asset references, missing launch files, duplicate identities or
asset references, and unreferenced files in the immediate managed asset folders of existing nodes.

The orphan scan deliberately does not recurse indiscriminately through `Library/`: game installations, Wine/Proton
prefixes, GOG staging data, themes, caches, and backups are outside its scope. Shared asset paths remain live as long
as at least one node or item references them. The settings **Data** tab opens the report dialog; scanning and copying
the report never mutates JSON or deletes files.

## Import and metadata flow
- `ImportService`: recursive local file import with multi-disc grouping/labeling
- `MultiDiscFileNameHelper`: shared recognition for separated `Disk`, `Disc`, `CD`, `Side`, `Part`, and
  `Scen` filename forms; `LauncherService` can consume generated playlists for grouped media
- game-system assignments use stable provider-neutral identifiers, are inherited from node to item, and remain
  separate from free-text platform metadata and emulator configuration
- `GameFileFingerprintService` calculates CRC32, MD5, and SHA-1 in one cancelable sequential file read for
  integrations such as ScreenScraper; these raw file checksums are intentionally not treated as the
  system-specific hash required by RetroAchievements
- ScreenScraper keeps its numeric platform IDs in a provider-specific catalog instead of leaking them into the
  provider-neutral game-system model. Manual and bulk metadata searches first perform an exact ROM lookup when an
  item has both a supported effective game system and an existing primary file. A successful identification avoids
  the title-search request; an unknown ROM falls back to a title search constrained to the same system, while
  unsupported or incomplete items use the ordinary title search. ScreenScraper bulk jobs process one item at a
  time, and requests from one ScreenScraper profile are additionally serialized so concurrent dialogs also respect
  a one-thread account. Provider responses for exhausted concurrency, daily-request, or daily-no-match allowances
  stop the current bulk job instead of issuing further requests that cannot succeed
- bulk scraping derives a per-item enrichment request from the configured import fields, existing-data policy, and
  already stored artwork. Providers with selective enrichment use it to avoid optional metadata or artwork calls
  whose results would not be applied; manual scraping still enriches the selected result completely so every value
  remains available for explicit selection
- `RetroAchievementsHashService` maps provider-neutral game-system identifiers to the console IDs from the
  pinned rcheevos version and generates official system-specific hashes through the bundled
  `libretromind-rhash.so`; compressed CHD disc images are decoded by the statically bundled libchdr adapter,
  without requiring `chdman` or a system libchdr installation. Unsupported systems fail explicitly instead
  of falling back to a raw MD5
- `RetroAchievementsGameCatalogService` resolves those hashes against the system game list from the Web API;
  the large public responses are indexed in memory and cached for seven days. When AppImage portable HOME is
  enabled, the cache lives below `Home/.cache/retromind/RetroAchievements`; otherwise it remains below
  `Cache/RetroAchievements` in `DataRoot`. Successfully saving a change to the portable-HOME setting merges
  the cache into its new location, switches all running RetroAchievements cache services to that location, and
  retains the newer file when both locations contain the same system catalog.
  Stale-cache fallback and a refresh backoff cover temporary outages, and API keys are never written to this cache
- `RetroAchievementsGameIdentificationService` composes secret lookup, rcheevos hashing, and catalog resolution
  into one operation; a successfully calculated hash without a catalog entry is represented as a normal no-match
  result rather than an exception
- the media editor can stage this identification without mutating the library; saving persists the game ID,
  console ID, system ID, hash, and provider title on `MediaItem`, while canceling or changing the staged game
  system/launch file discards the staged assignment
- GOG- and Epic-linked items expose their supported installation and maintenance actions in dedicated media-editor
  sections, including the stored installed version. These store operations reuse the MainWindow workflows and
  persist immediately; after success, the editor reloads only the affected launch configuration so staged metadata
  remains intact and cannot overwrite store-generated launch settings on a later save
- the media editor can assign storeless items to an owned GOG or Epic Games title and switch an item between those
  providers. A switch replaces only internal `Store.*` state and preserves launch settings, media, metadata, and
  user-defined fields. Retromind-managed installations must be uninstalled first so their owned game directory,
  prefix, and backend registration cannot become associated with the wrong provider. Legacy Heroic/Epic launch
  entries can receive a structured Epic identity without replacing their existing external launch configuration;
  users may then install a separately managed Legendary copy from the same editor
- GOG main-game updates and explicit reinstalls snapshot the DLCs installed through Retromind before running the
  offline installer, then reapply those installed DLCs with their current same-platform packages in the shared
  cancelable progress flow. Updates are always in-place and cannot request directory cleanup; explicit reinstalls
  retain the clean-install option and its ownership/path-safety confirmation. Cleanup affects the contents of the
  managed game folder but not a separate Wine/Proton prefix. Clean reinstalls may restore a user-selected subset
  of the previously installed DLCs, while updates and in-place reinstalls always reapply all of them because omitted
  payloads would otherwise remain on disk. Each successful DLC is persisted independently, while unavailable or
  failed DLCs keep the aggregate update indicator active; owned but uninstalled DLCs are never added implicitly
- a node action can run the same identification pipeline sequentially across that node and its descendants, while
  local-folder ROM imports invoke it automatically for their new items when the target node has an effective game
  system. Already identified or incomplete items are skipped, individual failures do not stop a batch, and
  cancellation retains completed matches. A batch reads the Web API key once and reuses it for every candidate
- `RetroAchievementsApiClient` uses the stable account ULID with the official game-info-and-user-progress
  endpoint to retrieve typed achievement definitions, casual/hardcore unlock timestamps, and summary progress;
  these user-specific responses are kept separate from the long-lived public game-catalog cache
- `RetroAchievementsProgressService` prefers that stable ULID over the mutable username, coalesces concurrent
  requests through a fixed set of synchronization gates, and keeps up to 128 recently used progress snapshots in
  memory with a five-minute freshness window. Every successful response is also written atomically
  below `RetroAchievements/Progress/<SHA-256 user>/game-<id>.json`; neither the API key nor the plain account
  identifier is persisted there. The cache follows portable-HOME migrations. Callers may force a refresh after
  gameplay; when a refresh encounters a temporary API failure, the last memory or disk snapshot is returned with
  its original timestamp and an explicit fallback flag
- `RetroAchievementsProgressViewModel` owns the cancelable selection-driven loading state for the desktop media
  details. Desktop selections are briefly debounced so rapidly skipped games do not start short-lived HTTP requests;
  explicit refreshes remain immediate. Identified games show achievement and hardcore summaries plus the ordered
  achievement definitions with points, description, casual/hardcore state, and unlock time. The view exposes an
  explicit refresh action and labels persisted fallback data when the service is temporarily unavailable. After a
  regular tracked game session, the still-selected game's progress is refreshed automatically; test launches and
  untracked launcher handoffs do not trigger that refresh. Badge paths are loaded lazily when the detailed
  achievement list is first expanded, use bounded concurrency, and are cancelled when the selected game changes;
  unavailable images leave the remaining achievement card intact. The detailed achievement-card list is collapsed
  by default, retains its state across refreshes of the same game, and collapses again when the selected game changes
  - The shared `IsVisible` presentation contract requires an enabled, eligible provider and a loaded snapshot
    with a positive achievement count. Prism, Default, desktop details and existing theme bindings therefore
    hide the entire section until achievements are known, including valid zero-achievement cached results.
    Eligibility remains separate so invisible sections still load, in-flight and empty results are reused,
    and explicit/session refreshes remain possible. A selection change clears old data before activating the
    new provider; late responses cannot make another game's section appear. Refreshing the same known set
    retains its display until a new result arrives. Load failures retain their diagnostic status; without a
    previously known set the panel stays hidden, while a failed refresh of known data keeps its existing status UI.
- BigMode owns a separate `RetroAchievementsProgressViewModel` so its selection cannot overwrite desktop detail
  state. Identified selections are debounced before loading, stale progress is cleared immediately, and Attract
  Mode never starts progress requests. Themes consume the compact summary through
  `BigModeViewModel.RetroAchievementsProgress`; the host view owns the theme-independent achievement browser and
  routes controller/keyboard input to it while it is open. Badge images stay lazy until either that browser or the
  desktop achievement list is opened. A tracked game session refreshes the active BigMode instance instead of
  duplicating the desktop refresh
- `RetroAchievementsBadgeService` downloads the official unlocked and locked achievement PNGs on demand, validates
  their size and PNG signature, and publishes them atomically below `RetroAchievements/Badges`. Concurrent requests
  share a bounded set of synchronization gates, failures do not affect progress data, and the persistent badge cache
  follows the existing portable-HOME cache migration
- Steam achievement progress uses the store App ID already persisted by Steam imports. The Steam API client combines
  the localized game schema with account-specific unlock state while keeping the personal Web API key in the request
  header. Locked hidden achievements are stripped of title, description, and artwork before they reach presentation
  or cache state; only their remaining count is exposed. A bounded one-hour memory cache with up to 512 recently
  used games coalesces browsing requests. Localized public achievement schemas are cached separately for 30 days,
  so routine progress refreshes normally require only the account-specific endpoint. Every successful response is
  also written atomically to an account-separated JSON cache, allowing a
  valid stale response to be shown after a restart when Steam is unavailable. Imported Steam games on Linux are
  tracked automatically by their exact App ID unless a manual watch process is configured; after the detected game
  processes exit, the still-selected game's progress is force-refreshed
- GOG achievement progress uses the structured store game ID and the Galaxy `user_id` from the current OAuth token
  to query GOG's gameplay API. The access token is sent only in the authorization header, response bodies are
  limited to 4 MiB while streaming, and locked hidden achievements have their title, description, and artwork
  removed before reaching presentation state. A bounded one-hour account-specific memory cache with up to 512
  recently used games coalesces browsing requests. Every successful response is also written atomically to an
  account-separated JSON cache,
  allowing a valid stale response to be shown after a restart when GOG is unavailable. The non-secret Galaxy
  `user_id` is retained through the existing secret-store boundary so the correct account cache can be selected
  before an online token refresh. Progress is force-refreshed after a tracked game session and remains available
  only while the experimental Galaxy integration is enabled
- Steam and GOG progress caches live below `Cache/StoreAchievements` or the matching portable-HOME cache root.
  Account directory names contain SHA-256 hashes rather than Steam or Galaxy IDs, cache documents contain no
  credentials or plain account IDs, and cache files follow the existing link-safe portable-HOME migration.
  Achievement artwork is fetched lazily when its list is opened, limited to 2 MiB, checked for a supported image
  signature, and atomically retained in provider-specific image directories. Image filenames contain SHA-256 URL
  hashes rather than remote URL text; failed artwork downloads never affect the cached progress data. Directly
  cached images update their last-use timestamp at most weekly and are removed after 180 days without use
- the existing achievement presentation model is shared by RetroAchievements, Steam, and GOG. Provider-neutral progress,
  list expansion, refresh, and BigMode navigation remain common, while RetroAchievements adds points and Hardcore
  state, Steam adds hidden-achievement state, and GOG adds rarity. Historical RetroAchievements property/type names
  remain available so third-party runtime themes do not break
- `StoreImportService`: Steam import via `steamapps` manifest scan (`appmanifest_*.acf`). Epic imports load the
  owned library through the native Legendary provider. New imports persist the provider-neutral identity fields
  `Store.ProviderId` and `Store.GameId` on the `MediaItem`
- `StoreProviderBadgeHelper` resolves GOG, Steam, and Epic cover labels from that structured identity. For
  Steam/Heroic items created before the fields existed, it falls back to their command and launch URI without
  mutating library data. `AppSettings.ShowStoreBadges` controls presentation only; moving an item between
  ordinary nodes retains its store identity
- Native store-provider integration under `Services/Stores/` resolves providers through a case-insensitive
  `StoreProviderRegistry`. Every provider exposes a stable ID, display name, and capability flags; account and
  owned-library contracts extend that common identity so multiple providers can coexist without ambiguous DI
  registrations. GOG currently supplies those contracts through `GogProvider`, while its installer-specific services
  remain separate. Store identity and management state belong to each `MediaItem`, never to the containing node
- Epic's native provider boundary uses the Legendary CLI as an isolated backend. Executable discovery prefers the
  Retromind-managed copy before AppImage and host installations, arguments are passed without a shell, and each
  process receives a dedicated `LEGENDARY_CONFIG_PATH` below `Tools/Legendary/Data`. Legendary's account state,
  manifests, and temporary metadata therefore remain separate from Retromind settings and metadata backups.
  Installation streams Legendary progress into the cancelable process-log window, records the selected install
  root portably, and claims partial directories with an item-specific marker so interrupted downloads can resume
  without allowing unrelated non-empty directories. Manual update checks compare the version registered by
  Legendary with its freshly loaded Windows catalog version; available updates reuse the owned install root and
  the same cancelable progress pipeline. Epic installations can be uninstalled independently while retaining the
  library item, media, metadata, and store identity; the generated launch configuration and installation state are
  cleared as soon as Legendary confirms that the main game's registration is removed, independently of later
  filesystem cleanup. Deleting a managed item first persists that state and keeps the library item when cleanup
  is incomplete or persistence fails. Safely contained Wine/Proton prefixes are revalidated
  immediately before link-safe recursive removal, while prefixes shared by another item or rejected by the path-safety
  checks are preserved. Missing legacy install
  roots are unregistered without touching files when a later installation selects a new destination. Before each
  launch, Legendary supplies fresh Epic
  authentication arguments; Retromind then executes the installed game through its selected Wine/Proton runner so
  normal process tracking, playtime, wrappers, and launch logs remain active. Epic exchange codes are redacted from
  those logs, while the interactive authorization code is passed to Legendary through standard input rather than
  process arguments. Owned Epic DLCs and their installed versions remain authoritative in Legendary rather than
  being copied into the Retromind library model. The DLC manager installs and updates them into the main game's
  shared, marker-owned directory and removes only their Legendary-managed files; deleting the main game first
  unregisters its installed DLCs before removing that shared directory
  - `EpicInstallService.UninstallAsync` retains the existing `EpicUninstallResult` with `PrefixPreserved` and
    `PreservedInstallDirectory`, adding `CleanupFailed` and `CleanupCancelled`. Returning this result always means
    Legendary verified removal of the main game through `list-installed`; backend failures before that point
    still throw and retain the main game's local install/launch state. DLCs removed before a main-game failure
    remain removed in Legendary, whose catalog is still authoritative; the entire game is not marked uninstalled.
  - Immediately after confirmation, generated launch fields, files, runner assignment, and install/update metadata
    are cleared before any cleanup or cancellation check. `PrefixPath` is retained in its original LibraryRoot-relative
    or absolute form until deletion is confirmed. `Store.UninstallRemainingInstallPath` separately preserves the
    current operation's original DataRoot-relative or absolute install path, without implying an installed state.
    It remains when the directory cannot be inspected/removed or cleanup is canceled, and is removed once the
    directory is confirmed absent. This is recovery metadata for the current uninstall, not a complete history of
    earlier installations; explicit item deletion or later metadata changes can remove it.
  - Cleanup retains all ownership-marker, dangerous-root, containment, and symlink checks. Only Retromind's marker
    and an empty install directory may be removed; unrecognized files are preserved without recursive game-folder
    deletion. Prefix removal still uses `StoreDirectoryDeletionHelper` and additionally refuses a prefix that is
    the install directory or its ancestor, so it cannot erase retained game-folder contents indirectly. Failed
    cleanup stops further cleanup and returns the confirmed result; cancellation after confirmation does the same.
    A partially deleted prefix retains its path for manual recovery, with no automatic retry or aggressive sweep.
  - Both standalone uninstall and delete-item flows use `MainWindowViewModel.UninstallEpicAndPersistAsync`.
    It awaits the result and then always runs the normal persistence callback without the cleanup cancellation token.
    The callback marks the library dirty, calls `SaveData()`, and only then refreshes action availability. Standalone
    uninstall reports cleanup warnings/cancellation in its existing process log. Item deletion retains and saves
    the now-uninstalled item when cleanup is incomplete and reuses its existing information-dialog path. A successful
    uninstall is persisted before any later library-item removal, so failures in subsequent UI/deletion steps cannot
    leave the stored item falsely installed. A `false` save result retains the in-memory changes and existing dirty/error
    handling, stops item deletion, and never claims that uninstall was undone; no extra persistence-error dialog is added.
    When Legendary logs its specific nonempty-directory error but Retromind subsequently confirms removal of
    the game folder, successful cleanup and persistence, the process log adds a localized explanation next to
    the final success message. The original CLI output remains visible. This can happen because Retromind's
    ownership marker remains during Legendary's removal attempt. Retained files, cleanup failures, cancellation
    and failed saves do not receive the resolved-cleanup notice; no exit codes or uninstall outcomes are changed.
  - The injected `IErrorService` reports `Epic.UninstallFailed` (`Error`) for validation/backend failures and
    `Epic.UninstallCleanupFailed` (`Warning`) for post-confirmation cleanup failures. Expected cancellation is distinct:
    `Epic.UninstallInterrupted` (`Diagnostic`) records already removed DLCs before main-game confirmation, while
    `Epic.UninstallCleanupCancelled` (`Diagnostic`) records canceled cleanup after confirmation. Reports contain the
    operation, app identity, install/prefix paths, confirmed-main-game flag, removed-DLC count, and exception type.
    Exception messages/stacks and Legendary stdout/stderr are omitted because they can embed credentials. Each
    failure is reported at its handling boundary; callers retain presentation/persistence handling without logging
    the same backend failure again. Reporting failures cannot change cleanup or model outcomes; there are no
    automatic error-service notifications.
  - Remaining limits: cancellation/process failure between actual CLI removal and the verification query leaves
    the backend outcome unconfirmed; local state is retained rather than guessing, and may require manual reconciliation.
    A crash between backend confirmation and a durable library save is not an atomic cross-system transaction.
    Unwritable storage can prevent persistence despite the required save attempt. Native path checks also retain
    their existing race with external path replacement between inspection and IO. No startup repair, persistent
    uninstall journal, automatic DLC rollback, or deferred cleanup worker is introduced.
- the prepared GOG Galaxy sidecar boundary resolves Comet only from Retromind's packaged locations and never from
  `PATH`. It starts Comet without a shell, supplies the four transient account values through bounded JSON on
  standard input, and keeps its replaceable configuration, gameplay database, components, and cache below
  `Tools/Comet/Data` through private XDG paths. The returned session owns and cleans up the complete Comet process
  tree. Its credential provider combines a fresh access token and the Galaxy `user_id` from the same OAuth token
  response with the refresh token read through the existing secret-store boundary and adds no persistence. The
  separate account identity returned by `userData.json` is not valid for gameplay writes. An experimental,
  default-off setting starts
  this sidecar only for direct launches carrying a structured GOG identity; native Linux and compatibility-layer
  games use the same capability check. Comet startup failures do not block the game, delegated launcher handoffs are
  excluded only as a defensive legacy/external case because they have no reliable lifetime boundary, and the owned
  sidecar receives a 30-second normal-exit window for post-game synchronization before forced cleanup when the
  tracked game session ends. This cleanup runs outside the active-launch UI boundary; a subsequent GOG launch
  cancels and awaits pending cleanup before reclaiming Comet's fixed port. Application shutdown uses a shared,
  five-second maximum grace period for local GOG post-launch work and Comet completion, then forces cleanup. Comet error
  output is retained as a bounded, credential-redacted `0600` diagnostic
  log below `Tools/Comet/Data`; routine output is discarded. Current
  Retromind-managed GOG launches and Legendary-based Epic launches do not use that legacy handoff path
- `GogPlaytimeSyncService` submits completed, statistically eligible sessions for structured GOG items directly to
  GOG's gameplay service after Retromind observes the game exit. Durations are sent as whole minutes; sub-minute,
  canceled, delegated, and test launches are excluded. The launch path waits only for the bounded atomic queue write;
  a coalesced background worker performs network uploads without holding the queue lock, so GOG latency does not
  extend the active-launch UI state or prevent new sessions from being queued. Failed submissions remain queued and
  store only the numeric product ID, start time, duration, and a hash of the Galaxy account ID. Pending entries are
  retried only with credentials for the same account, while tokens and plain account IDs are never persisted.
  Retromind does not publish GOG Presence.
  - The schema-1 queue at `AppPaths.DataRoot/Cache/Gog/pending-playtime-sessions.json` is read under a local
    semaphore shared by enqueue and removal. Only a missing file/directory is an empty starting state. Existing
    files with read/access errors, invalid JSON/schema/session fields, missing `Sessions`, more than 256 entries,
    or more than 256 KiB are preserved without upload, replacement, deletion, or automatic repair. Existing valid
    schema-1 files, including UTF-8 BOMs, remain supported. Network requests run outside the queue semaphore;
    successful removal reloads and validates the latest queue to preserve concurrently enqueued sessions.
  - Writes still use a unique temporary file, flush, and replacement of the queue; unsuccessful writes preserve
    the previous queue and clean up only their own temporary file. Deleting the last uploaded session is also a
    checked persistence operation. `IGogPlaytimeSyncService.EnqueueSessionAsync` retains its `Task` signature;
    eligible sessions fault with `GogPlaytimeQueueException` (`Read`, `Write`, `Capacity`, or `AccountUnavailable`)
    when durable enqueue is impossible. The existing launcher catch preserves the launch result and does not
    request synchronization after a failed enqueue. A full queue rejects a new distinct session explicitly instead
    of evicting the oldest; updates to an already queued session remain possible.
  - Remote confirmation (HTTP 201) and the local queue commit are separate outcomes. After a confirmed upload,
    a failed reload/write/delete stops that synchronization run and reports `GogPlaytime.QueueUpdateAfterUploadFailed`.
    A bounded in-memory registry records confirmed sessions; later runs in the same service instance retry only
    their local removal, never the POST. Caller cancellation after confirmation retains this marker. Duplicate
    identical legacy rows upload once and are removed together. If a concurrent enqueue changes the duration for
    the same account/product/start time, removal preserves the updated session and reports
    `SessionChangedAfterUpload`; the instance continues to block its re-upload instead of inventing delta semantics.
    Reproduction: block the upload of a five-minute session, enqueue that same session with six minutes, then
    complete the upload. The six-minute entry remains pending. No automatic reconciliation is attempted.
  - This registry is intentionally not a new persistent acknowledgement protocol: restart/crash after remote
    confirmation but before local commit can cause a repeated upload. A timeout can also leave remote acceptance
    unknown. GOG deduplication/idempotence is not assumed. Queue locking covers one service instance, not external
    writers or multiple Retromind processes; exactly-once delivery across those boundaries is not guaranteed.
  - The injected `IErrorService` provides diagnostics only, without notification callbacks. Stable codes are
    `GogPlaytime.QueueReadFailed` and `GogPlaytime.QueueWriteFailed` (`Error`),
    `GogPlaytime.QueueCapacityExceeded` and `GogPlaytime.UploadFailed` (`Warning`), and
    `GogPlaytime.QueueUpdateAfterUploadFailed` (`Error`). Context includes the operation, product ID, last validated
    queue count where available, HTTP status, exception type, and confirmation/local-retry state. Neither account
    IDs nor their hashes, tokens, request payloads, or raw exception messages/stacks are passed to diagnostics;
    HTTP/auth exceptions can embed credentials and personal identifiers. Error-output failures cannot change
    queue outcomes. Transient HTTP/network diagnostics are limited to one report per operation per minute per
    instance using monotonic time; the next emitted report includes the number suppressed. Existing request
    timing, the 16-attempt batch bound, account separation, and per-session HTTP rejection behavior remain intact.
- `MetadataService`: scraper-provider factory + provider caching + connect gating
- scraper providers implement `IMetadataProvider` and are selected via configured scraper profile
- scraper search results keep their canonical `Title` separate from an optional UI-only `DisplayTitle`; provider
  details such as book authors, comic release years, or issue names must not weaken automatic bulk matching
- provider-owned request-rate gates space API calls independently of bulk-worker concurrency; IGDB, OpenLibrary,
  and TMDB therefore retain responsive parallel processing without exceeding their documented request cadence
- providers with expensive per-result calls expose optional preview/result enrichment
  capabilities; the manual dialog loads lightweight previews first and details only
  for the current selection, while bulk scraping enriches only the matched result
- providers can expose a cheaper bulk-search capability without weakening manual search. TheGamesDB limits automatic
  searches to its first probability-ranked result page, defers supplemental name and artwork requests until a safe
  match has been selected, and caches resolved platform, genre, developer, and publisher IDs for the provider
  lifetime. Its bulk jobs are sequential, track the allowance returned by the API, and stop the batch when access is
  exhausted instead of repeating rejected requests
- the manual metadata dialog presents changed fields individually; artwork imports are additive and never
  silently replace existing files
- `ScraperMatchEvaluator` gates automatic bulk imports by normalized title similarity, optional platform/year
  signals, a minimum confidence, and a lead over the runner-up. Platform bonuses require equal canonical names or
  explicit unambiguous aliases; sharing a vendor or family word is insufficient. Ambiguous or unsafe results are
  logged but not imported

For detailed GOG-native status and file map, see `docs/gog-provider.md`. The secure-credential Comet fork,
reproducible Docker workflow, runtime integration, and achievement-presentation handoff are recorded in
`docs/comet-fork.md`.

## Launch pipeline
`LauncherService` resolves and executes media launches:
- supports `Native`, `Emulator`, and `Command` media types
- media items may intentionally have no launch file and act as catalog placeholders; the editor can add,
  replace, or clear launch files later
- `LaunchPlaylistService` selects the primary media file and owns optional multi-disc playlist generation below
  the resolved library node folder, falling back to the primary file when a playlist cannot be created
- `LaunchPlanBuilder` owns the deterministic executable, argument-placeholder, and wrapper composition after the
  launch file has been resolved. Plan layering remains item launcher -> emulator config -> node/item wrapper chain;
  there is no application-global wrapper layer
- `LaunchEnvironmentService` owns inherited environment cleanup for AppImage and portable-home launches, applies
  resolved emulator/item variables in launch order, and enforces emulator/item XDG precedence
- `WinePrefixService` resolves existing portable or absolute prefix paths, selects the correct Wine, Proton, or UMU
  prefix layout, creates safe `dosdevices` mappings, and supplies the launch environment paths
- `ProtonPrefixRelocationService` rebases stale Proton-managed runtime links after a portable data-root move before
  a game launch or a later GOG installer accesses the existing prefix; reachable links are rewritten only when
  Proton's `tracked_files` manifest identifies them as runtime-owned
- `LaunchProcessService` owns operating-system process startup, bounded stdout/stderr capture, delegated-command
  observation, configured watch-process tracking, and the active tracked-session stop boundary. While Retromind
  is in the background, holding the configured stop combination (L1 + R1 by default) first requests an orderly
  window close; a second hold within the confirmation window
  may force-stop only the directly launched process tree or the exact process instances newly detected by a
  configured watch process or Steam App-ID tracking. Stored process start times prevent PID reuse from targeting
  an unrelated process. A same-named process that was already running is never adopted into the session
- `SteamProcessTrackingService` observes new Linux processes through `/proc`, matches Steam's exact App-ID
  environment markers, ignores known Steam/Proton/setup helpers, and requires a continuous group of likely game
  processes to remain active for five seconds before confirming a session, allowing legitimate handoffs between
  bootstrap, anti-cheat, and game processes. Pre-launch and active process identities combine PID with Linux start
  time so PID reuse cannot hide or replace a tracked process. Initial Steam activity has a 40-second bound;
  observed setup activity extends game discovery to five minutes so shader compilation and prerequisite installers
  do not create a false session boundary. Optional console diagnostics are enabled through
  `RETROMIND_STEAM_PROCESS_DIAGNOSTICS=1`
- `LauncherService` retains launch orchestration and business decisions: working-directory selection, runtime
  classification, launch-result evaluation, logging, and session-statistics updates
- explicit test launches use the same launch plan while suppressing play count, last-played, and playtime updates
- supports multi-file launch decisions (including playlist mode)
- supports merged environment overrides (node/emulator/item)
- handles Wine/Proton/UMU prefix setup and compatibility environment shaping
- prevents AppImage-injected library paths and the portable HOME/XDG environment from leaking into launched host processes
- reports launch-plan and process-start failures to the caller for visible user feedback
- waits up to 60 seconds for a configured `OverrideWatchProcess` and reports separately when the launcher starts
  but the expected game process never appears; already-running processes remain a neutral, untracked case
- automatically tracks imported Steam commands on Linux by their stored App ID when no `OverrideWatchProcess` is
  configured, records only the detected game-process lifetime as playtime, and exposes its exit as the session
  boundary used for achievement refreshes. Recognized Linux Steam commands receive Steam's `-silent` option so
  starting the client for a game does not normally place its main window in front of Retromind; a configured
  manual process remains authoritative
- treats other Steam-unidentified, Heroic, and URI commands as delegated handoffs: a successful handoff updates
  play count and last-played once, but does not record the launcher's lifetime as game playtime or expose it as a
  reliable game-exit boundary
- captures a bounded tail of stdout/stderr for direct process starts and includes it in missing-process or
  non-zero-exit feedback within ten seconds of process start; shell-based launches remain limited to
  operating-system start diagnostics
- writes one atomically replaced diagnostic log per media item below `DataRoot/Logs/Launch`; an initial record
  is persisted after process handoff and finalized with outcome, runtime, exit code, and captured output, while
  likely secret environment values and command arguments are redacted
- direct, explicitly watched, and automatically tracked Steam game sessions update playtime and play count after launch
- `GogLaunchDetectionService` owns installed-game launch detection. It prefers local or account `playTasks`
  metadata and preserves its executable, arguments, and working directory before falling back to filesystem
  heuristics; metadata-derived executable and working-directory paths are constrained to the selected install
  root. `MainWindowViewModel` retains only the API fallback orchestration, manual picker, and application of the
  detected configuration to the media item
- `GogInstallerProcessService` owns low-level installer process execution, cancellation with child-process
  termination, stdout/stderr draining, known MojoSetup/Wine error classification, the temporary Konsole
  compatibility shim, and Wine-prefix diagnostics. Temporary compatibility files use unique, owner-only OS
  temporary directories and are removed without following symbolic links. Installer output is reported through
  a callback so this infrastructure service remains independent of Avalonia and process-log view models
- `GogInstallerExecutionService` owns the platform installation workflow. Its Linux path selects compatibility
  argument profiles, falls back to extracted `startmojo.sh`, isolates shell-sensitive destination paths,
  creates extraction, destination, and alias directories as unique owner-only OS temporary directories, cleans
  extracted trees without following symbolic links, promotes temporary installs while preserving Unix modes,
  repairs relocated MojoSetup metadata, and validates that DLC installers actually changed the payload. Its
  Windows path owns prefix creation, installer architecture selection, silent/interactive Inno profiles, shortcut
  policy cleanup, Wine diagnostics, and target/prefix payload validation. Shared payload snapshots are provided by
  `GogInstallPayloadTracker`.
  Before its portable games-drive mapping creates `dosdevices`, the Windows path creates `drive_c` and supplies
  missing `C: -> ../drive_c` and `Z: -> /` mappings. Wine creates these defaults only when it creates `dosdevices`
  itself; creating just the games-drive mapping beforehand can otherwise leave Wine unable to find Windows
  system files. Existing mappings and prefix data are preserved. Missing mappings from an earlier interrupted
  attempt are filled on the next explicit installation attempt; invalid existing mappings prevent installer
  startup with an explanatory result rather than being overwritten. This is not general prefix repair.
  Host Wine installer processes use the existing AppImage/portable-HOME environment sanitizer while retaining
  the explicitly selected prefix and Wine settings.
  Process exit and redirected-output draining use cancellation-aware `WaitForExitAsync` rather than a synchronous
  final drain that can hang when a detached Wine child inherits the pipes. Cancel still kills the directly
  launched process tree. The Windows execution path additionally requests `wineserver -k` for the exact
  installer prefix, using only a sibling of the selected Wine executable and a five-second cleanup limit.
  This also reaches Wine setup processes that outlived their original launcher. No global name-based kill or
  fallback to an unrelated wineserver is used. Missing/failed/timed-out shutdown helpers are logged without
  changing cancellation into an installation failure. Prefix contents and downloaded installers remain intact.
  All Wine processes sharing the selected prefix belong to this shutdown scope; other prefixes are unaffected.
  There is no new overall timeout for an interactive installer, automatic prefix deletion, or repair of arbitrary
  existing prefix damage. Real Wine setup and host dependency failures still require runtime verification.
- `GogInstallerWorkflowService` coordinates the common base-game and DLC pipeline above those platform paths:
  resumable package download, throttled progress reporting, detailed file logging, clean-install preparation,
  execution, cancellation classification, and guarded staging cleanup. It reports plain results and log lines;
  `MainWindowViewModel` retains user confirmation, progress-window ownership, manual executable selection,
  media-item mutation, and persistence. Each installer-file response is limited while streaming to its checksum,
  HTTP, or product-catalog total when available and always to an absolute 64 GiB ceiling; a response that
  understates its length is stopped after the first excess byte and its partial file is removed. Checksum XML is
  likewise limited to 2 MiB while streaming before the existing DTD-disabled parser processes it

Winetricks follows the same application/infrastructure boundary. `EditMediaViewModel` resolves the effective
item/emulator environment and owns the process-log window, while `WinetricksService` owns prefix preparation,
portable C: and collision-free games-drive mappings, Proton/UMU fallback selection, command construction, process execution, and the
`winetricks.log` summary. The service receives plain request data and reports log lines through a callback; it has
no dependency on Avalonia views or view models.

### Winetricks results and diagnostics

`WinetricksService` receives the existing singleton `IErrorService` through DI. `RunAsync(request,
appendLog, cancellationToken)` returns the small, Winetricks-specific `WinetricksResult`: `Success`,
nullable `ExitCode`, `FailureCategory`, optional sanitized `ErrorMessage`, and derived `IsCancelled`.
Categories are `None`, `Preparation`, `ProcessStart`, `ProcessExecution`, and `Cancelled`; there is no
general operation-result framework. Exit code 0 is success; an actual nonzero exit is an execution failure.
Failure to obtain a started process is a start failure with no exit code, including missing/forbidden
executables and a null `Process.Start` result. Failures while observing an already-started process are
execution failures with an exit code only when it can be read reliably. Startup failures are reported
once at the execution boundary; the caller does not report the returned failure a second time.

Central diagnostic codes, all with component `WinetricksService`, are:

- `Winetricks.PreparationFailed` (`Error`): prefix/execution preparation failed.
- `Winetricks.StartFailed` (`Error`): process startup failed.
- `Winetricks.ProcessFailed` (`Warning`): a nonzero exit, process-monitoring failure, or failed process-handle cleanup.
- `Winetricks.PrefixMappingFailed` (`Warning`): optional drive preparation failed.
- `Winetricks.LogReadFailed` (`Diagnostic`): an existing `winetricks.log` could not be read.
- `Winetricks.LogWriteFailed` (`Diagnostic`): a process transcript could not be created, written, closed,
  or retained. This is an output failure and never changes the execution result.

Reports contain operation, effective runner type (`Wine`, `Proton`, or `UMU`), prefix path, and available exit
code. Process failures also include bounded sanitized verbs, a stderr tail, the relative transcript path,
transcript status, and its correlation ID. They never include a full environment or unredacted command-argument
context and request no dialogs.
The central writer remains responsible for persistent redaction. Sensitive environment values (including
inherited host credentials) and credential-option values are supplied as `SensitiveValues`. The text
callback and result messages are also sanitized using the existing redactor; trusted numeric exit metadata
keeps its existing `Exit code: ...` presentation. stdout/stderr still stream while the process runs, both
streams finish before normal result delivery, and a closed log observer cannot throw out of an output
callback and break monitoring. A failed redactor replaces failure details with a generic message; a failed
error service cannot change the result or prevent cleanup. Diagnostic logging never chooses retry or rollback.

Required prefix-directory failures return `Preparation`; the separate `PreparePrefix()` API diagnoses and
rethrows them so Generate Prefix retains its existing best-effort path-generation policy. Optional C:/games
drive failures are collected once per mapping preparation, including exceptions previously caught inside
`PrefixPathHelper`, without changing drive selection, preserving user mappings, or blocking execution.
The helper's optional failure callback leaves existing Wine/GOG callers and their fallback behavior unchanged.
The optional UMU `protonfixes` directory remains best-effort as before: an actual failure is diagnosed as
`PreparationFailed` with operation `EnsureUmuWinetricksDirectory`, but does not itself prevent execution.
Missing or empty summary logs are normal informational output. Reading an existing summary can fail without
turning a successful Winetricks execution into a failed result. Wine/Proton/UMU selection, portable paths,
prefix layout, fallback arguments, and host-environment sanitization are unchanged.

The shared `ProcessLogViewModel` has no failure status; the Winetricks UI therefore appends a clearly marked
`[ERROR]` completion line with category/exit code for failed results and adds no new dialog or shared status
contract. Success retains the existing presentation. The editor finalizes the log and resets both
`IsWinetricksRunning` and log `IsRunning` in `finally`, even after unexpected exceptions.

The existing process-log Cancel button is now enabled for Winetricks. The log token is captured before
showing the window, and both Cancel and the existing window-close `Dispose()` cancellation reach `RunAsync`.
A cancelled operation has a separate result, no start/execution-error report solely for cancellation, and a
`[CANCELLED]` completion line. Cancellation and monitoring failures attempt to kill the owned process tree,
reap it, and drain output; the cleanup wait is limited to five seconds. This is not an installer rollback:
changes already made to the prefix remain, interactive external windows may take time to disappear, and
detached/reparented Wine/UMU descendants or denied termination may survive. Cleanup problems are shown as a
warning in the text log. No new cancellation UI, global process tracking, automatic retry, or recovery
architecture is introduced. Folder-opening convenience behavior remains best-effort and unchanged.

### Winetricks process transcripts

Each prepared process attempt (including failed starts) gets a new GUID correlation ID and a UTF-8 JSON
Lines file at `AppPaths.DataRoot/Logs/Winetricks/winetricks-<id>.jsonl`. Pre-cancelled runs and preparation
failures that never reach a process attempt retain their existing result/report behavior without a transcript.
The portable root is resolved through `AppPaths`; central reports store the DataRoot-relative transcript
path, so the files can still be located after moving portable data. Start metadata records the correlation
ID, UTC time, effective runner, sanitized verbs, and prefix. Output records distinguish stdout/stderr;
the end record retains the actual exit code, success/failure/cancelled status, and truncation flag. The
prefix-owned `winetricks.log` and its existing UI summary are separate and remain unchanged.

`WinetricksProcessLog` is a local diagnostic writer used by `WinetricksService`, not another execution
service or logging dependency. stdout/stderr callbacks write complete records under a per-run lock and
continue delivering the existing live output independently of transcript failures or limits. The default
file budget is 1 MiB including metadata and a reserved end record; long individual output lines are limited
to 8 KiB of sanitized UTF-8 text before JSON encoding. Sanitized verbs are limited to 1 KiB and prefix
metadata to 2 KiB. Truncation is explicit, and output after the file budget is reached is omitted from the
file while live output continues. A normal completion closes and flushes the file before the failure report
links to it. A write/close failure can leave an incomplete transcript, which is identified in the central
context instead of being presented as a Winetricks execution failure.

At most 20 owned transcript files are retained. Creation and retention share a process-wide lock; oldest
inactive files (ordered by last-write time, with filename tie breaking) are deleted before a new file is
created. Active transcripts are never deleted. If all slots are active or retention deletion fails, the new
transcript is unavailable and execution continues; storage does not grow past the limit. Only filenames
matching Retromind's GUID transcript convention are managed; unrelated files are left alone. New Unix files
use owner-only read/write permissions.

For `Winetricks.ProcessFailed`, `StderrSummary` retains the last 16 nonblank stderr lines with a total
UTF-8 budget of 4 KiB, including the tail of a single oversized line. There is no `error` keyword filter:
Wine `err:`/`fixme:` output, warning formats, tracebacks, and otherwise unfamiliar stderr formats are all
eligible. It is a bounded tail, not a claim to infer the root cause; noisy trailing output can displace an
earlier message. The tail continues to update even after the file fills or becomes unwritable. `Verbs`,
`ProcessLogPath`, and `ProcessLogStatus` (`Written`, `Truncated`, `Incomplete`, or `Unavailable`) accompany
the summary. The same correlation ID appears in the central report, transcript filename, and start/end
records. Central records contain this summary, never the full stdout/stderr transcript.

Every process-output line and all metadata pass through the existing redactor **before** truncation,
serialization, or any file write. Known credential values from inherited/overridden environments and
credential arguments are removed even in free text; known multiline values also contribute individual
line fragments. Existing handling covers labeled URL credentials, URL userinfo, authentication headers,
JSON/key-value secrets, and command-line credentials. Redaction failure omits the content with a fixed
placeholder, never a raw fallback. Full environments and secret lookup values are never serialized.
Automatic redaction cannot recognize arbitrary unknown, unlabeled credentials, unrecognized credential
names, or every encoded/transformed or split secret. Such data should not be emitted by the invoked tool;
Retromind does not claim that arbitrary external output can be made secret-free by pattern matching alone.

Transcript creation/write/retention failures produce only `Winetricks.LogWriteFailed` diagnostics. Exit
code, cancellation, typed results, runner/prefix configuration, and UI notification policy are independent
of this writer. Correlation and the in-memory stderr tail remain available even if the linked file cannot
be written; abrupt process/application termination can still leave only a partial transcript.

## Runner management

Runner definitions are stored in `AppSettings` and selected through inheritance from emulator to media item.
`RunnerVersionService` owns external GE-Proton discovery, managed downloads, archive extraction, validation,
runner-kind inspection, and the safe removal of managed runner directories. `SettingsViewModel.Runners` owns the
editable UI state, registration, usage/replacement, confirmation, and persistence workflow. A completed managed
download is registered immediately, managed files require confirmation before physical removal, and display order
uses `MediaSortHelper.NaturalStringComparer` so numeric release segments sort naturally (for example, GE-Proton9
before GE-Proton10). Managed downloads are extracted into uniquely named staging directories and atomically renamed
into place; they are registered only after their Proton entry point and UMU tool manifest have been verified. An
explicit reinstall validates the replacement first and preserves the previous runner until the final atomic swap.
Installation, publication, cleanup, and removal accept only direct runner directories beneath
`Emulators/ProtonVersions`; every existing path component below the data root must be free of symbolic links so a
managed operation cannot be redirected outside that boundary. Symbolic links contained inside an otherwise valid
runner are removed as links and are not followed during recursive deletion. Before a selected runner is launched,
its persisted definition and files are checked again so removed, moved, or incomplete runners fail with visible
guidance rather than silently falling back to another runtime. GE-Proton downloads are streamed with a 1 GiB hard
limit even when the server omits or understates `Content-Length`. Before extraction, the TAR is inspected without
writing files and rejected if its declared payload exceeds 4 GiB or 100,000 entries; the validated archive is then
written by the framework TAR extractor rather than a custom extraction implementation. Release metadata is accepted
only from the fixed `api.github.com` repository endpoint. Each listed archive must have a matching `.sha512sum`
asset in the same official GitHub release; both asset URLs must match their tag and filenames exactly. Downloads use
manual, bounded redirects that accept only the original GitHub release path and GitHub's release-asset CDN, and the
archive must pass SHA-512 verification before it is parsed or extracted. Because the checksum and archive share the
same upstream release trust boundary, this detects corruption or inconsistent content but does not protect against
a compromise of the upstream publisher or GitHub release itself.

Emulator executable values without path separators remain command tokens resolved through the effective `PATH` at
launch time; explicit and portable-relative paths bypass that lookup. The emulator settings use the same resolver
to show the current concrete executable and whether it is PATH-dependent. Wine entry points additionally expose an
explicit, timeout-bounded version check instead of starting arbitrary emulator binaries automatically.

## Parental control as cross-cutting concern
Parental behavior is not isolated to one screen:
- tree visibility recalculation (`IsVisibleInTree`) on lock/protection changes
- current content refresh and fallback node selection when active node becomes hidden
- search view respects parental filtering
- node/item protection state propagation and auto-protect recalculation is debounced
- the parental lock is a local content-filtering and UX boundary, not a security boundary against users with
  access to the application files or persisted settings. Its password uses `SecurityHelper` with a fixed key by
  design so portable profiles continue to work across machines; this prevents casual plaintext inspection but
  does not prevent a determined local user from recovering the password or modifying the stored lock state

## Media model notes
- `MediaNode` and `MediaItem` both support asset collections and active-asset overrides
- node-level fallback toggles control whether node artwork participates in item display resolution
- `MediaItem.MediaType` models launch strategy (`Native`, `Emulator`, `Command`), not content taxonomy
- internal `Store.*` custom fields carry provider identity and installation state but are excluded from the
  user-facing custom-field list

## Automated tests

`Retromind.sln` includes the `tests/Retromind.Tests` xUnit project. The main SDK project explicitly excludes
`tests/**/*.cs` from its own default compile items, and selected internal contracts are exposed to the test
assembly through `InternalsVisibleTo` rather than widening the production API.

The suite is risk-focused rather than UI-complete. It covers persistence recovery and restore behavior,
portable-path and prefix contracts, Winetricks prefix/command preparation, launch planning, import and multi-disc
rules, metadata matching, search, statistics, managed runner publication/removal, GOG authentication/install
safety, and the RetroAchievements account, hashing, catalog, progress,
badge, cache-migration, and bulk-identification services, plus Steam account discovery, API parsing, App-ID
resolution, progress caching, and privacy-safe achievement presentation, plus GOG gameplay API parsing, progress
caching, and shared presentation. Destructive filesystem tests use unique
`/tmp/retromind-tests-<guid>` roots, validate the exact target before cleanup, and avoid following symbolic
links. This includes dangerous system/application roots, ownership markers, symbolic links, Linux case
sensitivity, path containment, and persisted-library relocation.

Future coverage should stay risk-based and favor deterministic logic with low maintenance cost. The next
useful candidates should follow newly introduced high-risk boundaries rather than duplicate behavior already
covered below the view layer. UI tests should be added only where behavior cannot be verified through models,
helpers, services, or view models.

## Extending the app safely
When adding features, preserve these invariants:
- keep persisted paths portable and inside `DataRoot`
- keep library/settings writes atomic and non-concurrent
- never clear dirty state or report success after a failed persistence write
- never restore a non-empty backup over a valid empty primary library
- require explicit ownership evidence and symlink-safe containment before recursive deletion
- avoid UI-thread blocking in import/scrape/search/rebuild paths
- keep BigMode host/VM responsibilities separated
- do not bypass launcher/environment sanitization for host helper processes
- add focused regression coverage when changing destructive operations or other deterministic high-risk rules
