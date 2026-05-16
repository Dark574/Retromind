# GOG Provider Implementation (Native, no gogdl)

Last updated: 2026-05-15

This document tracks the current state and target architecture of Retromind's native GOG integration.
It must be updated whenever implementation details, contracts, or security behavior change.

## Goal

- Remove runtime dependency on Heroic/gogdl for GOG integration.
- Build a native provider in incremental phases.
- Keep security strict: OAuth only (no custom credential fields in Retromind UI).

## Current status

Implemented (OAuth V1 core + read-only library/node linking) with install/discovery still in progress:

- Store abstraction interfaces
- GOG provider facade and service skeletons
- Secret-store abstraction with:
  - Secret Service placeholder (not implemented)
  - In-memory session fallback
  - Composite selection logic
- Functional OAuth core:
  - callback handling:
    - loopback listener for loopback redirect URIs
    - embedded OAuth dialog for non-loopback redirect URIs via `WebAuthenticationBroker` (automatic callback capture)
  - `state` validation and PKCE challenge generation
  - token exchange + refresh against `https://auth.gog.com/token`
  - refresh-token persistence via `ISecretStore`
  - account probe via `https://embed.gog.com/userData.json`
  - loopback login timeout handling (prevents hanging command state when callback never arrives)
  - in-app OAuth hardening for non-loopback auth:
    - authorize URL host/path validation (`https://auth.gog.com/auth`)
    - redirect URI validation (`https`, absolute URI)
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
  - AppImage build now bundles WebKitGTK runtime (`libwebkit2gtk`) into `usr/lib`, so AppImage users do not need to install it separately.
  - local Linux development/debug runs still require system WebKitGTK (e.g. on Arch/CachyOS: `sudo pacman -S webkit2gtk-4.1`)
  - missing WebKitGTK now fails gracefully with a localized user message (no hard app crash)
- Performance/UX:
  - owned-games fetch is cached in-memory for a short TTL to avoid repeated full pagination on consecutive imports
  - long-running GOG import/picker preparation shows wait cursor feedback
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
- `Services/Stores/Security/SecretServiceSecretStore.cs` (placeholder)
- `Services/Stores/Security/InMemorySecretStore.cs`
- `Services/Stores/Security/CompositeSecretStore.cs`

### GOG provider

- `Services/Stores/Gog/GogProvider.cs`
- `Services/Stores/Gog/GogLibraryService.cs` (owned-games fetch implemented)
- `Services/Stores/Gog/GogInstallDiscoveryService.cs` (returns empty)
- `Services/Stores/Gog/Auth/GogAuthService.cs` (interactive sign-in + refresh implemented)
- `Services/Stores/Gog/Auth/GogOAuthClient.cs` (authorize URL + token/account HTTP flows implemented)
- `Services/Stores/Gog/Auth/GogOAuthLoopbackListener.cs` (callback listener implemented)
- `Services/Stores/Gog/Auth/GogPkceService.cs`
- `Services/Stores/Gog/Auth/GogTokenSet.cs`

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
- `Views/GogPickerDialogView.axaml`
- `Views/GogPickerDialogView.axaml.cs`
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
- Validate authorize endpoint before opening auth UI (`https://auth.gog.com/auth`).
- Validate `state` on callback.
- Use PKCE where supported.
- Never write OAuth tokens to `app_settings.json`, library JSON, or logs.
- Prefer Secret Service for persistent refresh token storage.
- If Secret Service is unavailable, use session-only in-memory storage.
- OAuth runtime config is environment-variable overrideable:
  - `RETROMIND_GOG_CLIENT_ID`
  - `RETROMIND_GOG_CLIENT_SECRET`
  - `RETROMIND_GOG_REDIRECT_URI` (loopback HTTP uses local listener, non-loopback uses embedded OAuth callback capture)

## Planned phases

1. V1 read-only:
   - interactive browser OAuth
   - token refresh
   - owned games fetch
   - local install discovery
2. V2 install/update:
   - installer flow and path integration
   - checksums and resume strategy
3. V3 parity/comfort:
   - updates/patches
   - optional cloud/achievement features (if API feasibility is confirmed)

## Open items

- Implement `SecretServiceSecretStore` on Linux via D-Bus `org.freedesktop.secrets`.
- Extend library mapping (genres/artwork/store URLs) and stabilization around API edge-cases.
- Implement install discovery and launch integration for store-linked entries.
- Optional UX follow-up:
  - add explicit “remove titles no longer owned” sync mode for store-bound GOG nodes (current sync is additive only).
- Decide whether the fallback public OAuth client defaults remain or become explicit user configuration.

## Update policy for this file

Whenever GOG-related code changes, update at least:

- `Last updated` date
- `Current status`
- `Implemented structure` (new/removed files)
- `Security contract` if auth/token handling changed
- `Open items` and phase progress
