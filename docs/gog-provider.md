# GOG Provider Implementation (Native, no gogdl)

Last updated: 2026-05-29

This document tracks the current state and target architecture of Retromind's native GOG integration.
It must be updated whenever implementation details, contracts, or security behavior change.

## Goal

- Remove runtime dependency on Heroic/gogdl for GOG integration.
- Build a native provider in incremental phases.
- Keep security strict: OAuth only (no custom credential fields in Retromind UI).

## Current status

Implemented (OAuth V1 core + library/node linking + install workflow with resume/detection fallbacks + update check/install baseline):

- Store abstraction interfaces
- GOG provider facade and service skeletons
- Secret-store abstraction with:
  - Secret Service integration on Linux (via `secret-tool` / libsecret)
  - In-memory session fallback
  - Composite selection logic
  - AppImage behavior:
    - host `secret-tool` is preferred
    - bundled `secret-tool` is used as fallback when host tool is missing
    - keyring access is always host-context (no DataRoot/portable keyring storage)
- Functional OAuth core:
  - callback handling:
    - loopback listener for loopback redirect URIs
    - embedded OAuth dialog for non-loopback redirect URIs via `WebAuthenticationBroker` (automatic callback capture)
    - fallback: system browser + manual callback URL input when embedded OAuth runtime is unavailable
  - `state` validation and PKCE challenge generation
  - token exchange + refresh against `https://auth.gog.com/token`
  - refresh-token persistence via `ISecretStore`
  - account probe via `https://embed.gog.com/userData.json`
  - loopback login timeout handling (prevents hanging command state when callback never arrives)
  - in-app OAuth hardening for non-loopback auth:
    - authorize URL host/path validation (`https://auth.gog.com/auth`)
    - redirect URI validation (absolute URI + allowlist policy for non-loopback)
    - callback URI must match expected redirect scheme/host/port/path (+ required static query params)
    - `WebAuthenticationBroker` with `NonPersistent = true`
- Read-only library fetch:
  - owned products from `https://embed.gog.com/account/getFilteredProducts` (paged)
- Initial UI wiring:
  - node context menu action `GOG-Medium hinzufügen`
    - normal node: opens scalable picker (search + filter + multi-select)
    - node declared as GOG node: syncs full owned library into that node (adds only missing titles)
    - newly created GOG entries now reuse existing metadata/assets from already linked items with the same `Store.GameId`
  - GOG-node declaration is done in Node Settings (not via separate create action)
    - node flag: `StoreProviderId = gog`
- Localization:
  - all newly introduced GOG UI texts/messages are backed by `Resources/Strings.resx` and `Resources/Strings.de.resx`
- UI dependency:
  - `Avalonia.Controls.WebView` is used for embedded OAuth authentication dialogs
  - Linux runtime prerequisite for embedded OAuth: `libwebkit2gtk` (WebKitGTK)
  - AppImage build does not bundle WebKitGTK runtime due stability/ABI issues across host environments; WebKitGTK is expected from the host when embedded OAuth is used.
  - current runtime policy: Linux startup forces X11 (`AVALONIA_PLATFORM=x11`), Wayland is intentionally disabled for now
  - local Linux development/debug runs still require system WebKitGTK (e.g. on Arch/CachyOS: `sudo pacman -S webkit2gtk-4.1`)
  - missing WebKitGTK now fails gracefully with a localized user message (no hard app crash)
- Performance/UX:
  - owned-games fetch is cached in-memory for a short TTL to avoid repeated full pagination on consecutive imports
  - long-running GOG import/picker preparation shows wait cursor feedback
  - background update sweep runs throttled (24h interval, per-item delays) with auth-state caching and in-flight guards
- Install/launch wiring (first functional step):
  - GOG-linked items without launch config are now treated as installable from the main Start action
  - Start button label switches to Install for those items
  - install dialog supports:
    - install path selection
    - platform selection (Linux/Windows installer)
    - Wine/Proton runner selection for Windows installer mode
  - installer metadata and download are resolved via native GOG API endpoints:
    - `https://api.gog.com/products/{id}?expand=downloads`
    - `https://api.gog.com/products/{id}/downlink/...`
  - installer download now supports retry reuse:
    - completed installer files are reused
    - partial downloads are persisted as `.part` files and resumed with HTTP Range when supported
    - fallback to full redownload if Range is not honored by the server
  - installer execution now opens a process log window (stdout/stderr + exit code)
  - Windows installer robustness hardening:
    - Windows installer execution currently uses system Wine resolution (`wine`/`wine64`) through `EmulatorResolverHelper`
    - execution path now evaluates multiple `.exe` installer candidates (architecture-aware preference + fallback order)
    - Inno argument handling now uses profile fallback (`/SP- /SILENT /NOGUI /SUPPRESSMSGBOXES /NORESTART /DIR=... /LOG=...`, with less-silent fallback profile)
    - Windows installer logs include diagnostics (runner env snapshot, prefix `dosdevices` mappings, installer candidate metadata, and per-attempt process outcome)
    - Windows package download now keeps all installer files (no heuristic file pruning) because some GOG installers require companion payload files next to the selected setup executable
  - launch mapping after install now uses layered detection:
    - parse local `goggame-*.info` `playTasks`
    - fallback to account `gameDetails` `playTasks` from `https://embed.gog.com/account/gameDetails/{id}.json`
    - fallback to filesystem heuristics (`start.sh`/script candidates on Linux, filtered `.exe` candidates on Windows)
    - final fallback: manual executable picker dialog
  - if mapping succeeds, item launch config is updated and the Start action switches back to launch behavior
  - if mapping still fails, install completes but manual launch config is required
  - installer fingerprint is persisted after install (`Store.InstalledVersion`, `Store.InstalledInstallerSignature`)
  - installer download and execution now support user cancellation:
    - process log window includes a Cancel button for aborting downloads and installer runs
    - cancellation propagates to HTTP downloads and terminates running installer processes (including child trees)
    - closing the log window during an operation triggers cancellation
    - staging data (including partial `.part` downloads) is preserved after cancellation for seamless resume
- Update check/install wiring:
  - automatic non-interactive update checks on selection
  - automatic full-library sweep over all installed GOG titles every 24h
  - update availability flag persisted in custom fields (`Store.UpdateAvailable`, `Store.LastUpdateCheckUtc`, `Store.LastUpdateCheckStatus`)
  - search-card badge is shown when update is available
  - primary Start action now surfaces `Update` and runs the existing installer flow for update installs
- DI registration in app startup

## Implemented structure

### Models

- `Models/Stores/StoreAuthState.cs`
- `Models/Stores/StoreAccountInfo.cs`
- `Models/Stores/StoreGameRecord.cs`
- `Models/Stores/StoreInstallRecord.cs`
- `Models/MediaNode.cs` (`StoreProviderId` for store-bound nodes)

### Store abstractions

- `Services/Stores/Abstractions/StoreProviderCapabilities.cs`
- `Services/Stores/Abstractions/IStoreProvider.cs`
- `Services/Stores/Abstractions/IStoreAuthProvider.cs`
- `Services/Stores/Abstractions/IStoreLibraryProvider.cs`
- `Services/Stores/Abstractions/IStoreInstallDiscoveryProvider.cs`

### Security layer

- `Services/Stores/Security/SecretKey.cs`
- `Services/Stores/Security/ISecretStore.cs`
- `Services/Stores/Security/SecretServiceSecretStore.cs` (Linux Secret Service via `secret-tool`)
- `Services/Stores/Security/InMemorySecretStore.cs`
- `Services/Stores/Security/CompositeSecretStore.cs`

### GOG provider

- `Services/Stores/Gog/GogProvider.cs`
- `Services/Stores/Gog/GogLibraryService.cs` (owned-games fetch implemented)
- `Services/Stores/Gog/GogInstallDiscoveryService.cs` (returns empty)
- `Services/Stores/Gog/GogInstallService.cs` (installer metadata + downlink resolution + package download)
- `Services/Stores/Gog/Auth/GogAuthService.cs` (interactive sign-in + refresh implemented)
- `Services/Stores/Gog/Auth/GogOAuthClient.cs` (authorize URL + token/account HTTP flows implemented)
- `Services/Stores/Gog/Auth/GogOAuthLoopbackListener.cs` (callback listener implemented)
- `Services/Stores/Gog/Auth/GogPkceService.cs`
- `Services/Stores/Gog/Auth/PkceChallenge.cs`
- `Services/Stores/Gog/Auth/GogTokenSet.cs`
- `Services/Stores/Gog/Auth/OAuthCallbackResult.cs`

### GOG UI / binding

- `ViewModels/MainWindowViewModel.Import.cs`
  - `AddGogMediaAsync` (picker flow for normal nodes, full sync for GOG-declared nodes)
  - embedded in-app OAuth callback capture (`WebAuthenticationBroker`)
- `ViewModels/MainWindowViewModel.Command.cs`
  - localized menu label for `GOG-Medium hinzufügen`
- `Views/MainWindow.axaml`
  - context menu entry for localized `GOG-Medium hinzufügen`
- `ViewModels/NodeSettingsViewModel.cs`
  - `IsGogStoreNode` binding for store-node declaration (`StoreProviderId = gog`)
- `Views/NodeSettingsView.axaml`
  - checkbox to declare a node as GOG node
- `ViewModels/GogPickerDialogViewModel.cs`
- `ViewModels/GogInstallDialogViewModel.cs`
- `Views/GogPickerDialogView.axaml`
- `Views/GogPickerDialogView.axaml.cs`
- `Views/GogInstallDialogView.axaml`
- `Views/GogInstallDialogView.axaml.cs`
- `ViewModels/MainWindowViewModel.GogInstall.cs`
- `ViewModels/MainWindowViewModel.GogUpdates.cs`
- `ViewModels/ProcessLogViewModel.cs` (long-running process log, cancellation, and lifecycle management)
- `Views/ProcessLogView.axaml` (installer progress/log UI with Cancel and Close handling)
- `Views/ProcessLogView.axaml.cs`
- `Services/MediaDataService.cs`
  - cloning persistence updated for `MediaNode.StoreProviderId`

### DI registration

- `App.axaml.cs` registers:
  - `ISecretStore` as `CompositeSecretStore(SecretServiceSecretStore, InMemorySecretStore)`
  - `GogProvider`
  - `IStoreAuthProvider`, `IStoreLibraryProvider`, `IStoreInstallDiscoveryProvider` mapped to `GogProvider`

## Security contract

Required behavior for native GOG auth:

- Use OAuth authorization flow only.
- Do not implement username/password input fields for GOG credentials in Retromind forms.
- For non-loopback redirect URIs, use embedded OAuth via `WebAuthenticationBroker` with non-persistent session mode.
- If embedded OAuth runtime is unavailable, fall back to system browser flow with manual callback URL capture.
- Validate authorize endpoint before opening auth UI (`https://auth.gog.com/auth`).
- Validate `state` on callback.
- Use PKCE where supported.
- Never write OAuth tokens to `app_settings.json`, library JSON, or logs.
- Prefer Secret Service for persistent refresh token storage.
- Secret Service execution path is constrained to trusted host executable locations (and bundled fallback in AppImage).
- Portable mode must not store OAuth secrets in `DataRoot`; Secret Service access remains host-based.
- If Secret Service is unavailable, use session-only in-memory storage.
- OAuth runtime config is environment-variable overrideable:
  - `RETROMIND_GOG_CLIENT_ID`
  - `RETROMIND_GOG_CLIENT_SECRET`
  - `RETROMIND_GOG_REDIRECT_URI` (accepted values are loopback HTTP, or `https://embed.gog.com/on_login_success...`; loopback uses local listener, non-loopback uses embedded OAuth callback capture)

## Planned phases

1. V1 read-only:
   - interactive browser OAuth
   - token refresh
   - owned games fetch
   - local install discovery
2. V2 install/update:
   - installer flow and path integration
   - checksums, resume strategy, and user cancellation support
3. V3 parity/comfort:
   - updates/patches
   - optional cloud/achievement features (if API feasibility is confirmed)

## Open items

- Extend library mapping (genres/artwork/store URLs) and stabilization around API edge-cases.
- Implement install discovery sync for store-linked entries (launch mapping is already integrated in install/update flow).
- Optional UX follow-up:
- add explicit “remove titles no longer owned” sync mode for store-bound GOG nodes (current sync is additive only).
- broaden install robustness:
  - optional explicit install discovery sync back into already-linked items
  - finer-grained installer log output buffering/throttling
- broaden update flow ergonomics:
  - progress/cancel UI dedicated to update checks
  - richer update state model (`unknown` vs `available` vs `up_to_date`) in UI
- Decide whether the fallback public OAuth client defaults remain or become explicit user configuration.

## Update function design (implemented baseline)

Goal: for installed GOG-linked items, show `Update available` only when we can confidently detect a newer installer package.

### 1) Persist install fingerprint after successful install

Install metadata is now persisted on the media item (custom fields):

- `Store.InstalledVersion` (installer `version` from `downloads.installers[]`)
- `Store.InstalledInstallerSignature` (stable hash of installer file identities: filename + size + downlink path token)
- `Store.LastUpdateCheckUtc` / `Store.LastUpdateCheckStatus`

This avoids relying on local executable file versions (often unreliable for Wine/Linux wrappers).

### 2) Build a remote installer fingerprint per title/platform

For each installed GOG item:

1. fetch `GET https://api.gog.com/products/{id}?expand=downloads`
2. select installer candidate with the same platform policy currently used for install (Linux/Windows)
3. compute remote fingerprint:
   - installer `version`
   - optional build id / generation fields if present
   - normalized file signature from `files[]`

### 3) Update detection rule

Current implementation marks `UpdateAvailable = true` when:

- installed fingerprint exists, and remote fingerprint exists, and
- either changed:
  - `InstalledVersion != RemoteVersion`
  - `InstalledInstallerSignature != RemoteInstallerSignature`

If we cannot build a trustworthy comparison (missing local baseline), status is stored as `unknown`.

### 4) Update execution flow

Update execution reuses the existing install pipeline:

1. resolve current remote installer package
2. download into existing staging root (`.retromind-gog-installers/...`) with resume support
3. execute installer with same silent flags / runner policy
4. rerun launch mapping detection
5. persist new install fingerprint

Operationally this remains a reinstall-over-existing-target flow, surfaced as `Update` in the main action button.

### 5) UX/API shape

- Provider capability flags for updates are not added yet.
- Main action button states:
  - `Install` (not installed)
  - `Play` (installed, no update)
  - `Update` (installed, update available)
- Automatic checks run on item selection and via background full sweep (24h interval).

### 6) Edge cases to handle

- Delisted title / no installers for selected platform anymore.
- Language-priority change selects different installer without true version bump.
- Installer metadata changes without visible version string change (signature fallback catches this).
- User switched platform preference (Windows <-> Linux): treat as platform migration, not normal patch update.

## Update policy for this file

Whenever GOG-related code changes, update at least:

- `Last updated` date
- `Current status`
- `Implemented structure` (new/removed files)
- `Security contract` if auth/token handling changed
- `Open items` and phase progress
