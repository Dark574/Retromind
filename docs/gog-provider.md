# GOG Provider Implementation (Native, no gogdl)

Last updated: 2026-05-08

This document tracks the current state and target architecture of Retromind's native GOG integration.
It must be updated whenever implementation details, contracts, or security behavior change.

## Goal

- Remove runtime dependency on Heroic/gogdl for GOG integration.
- Build a native provider in incremental phases.
- Keep security strict: browser OAuth only, no password input in Retromind UI.

## Current status

Implemented as scaffold only (no functional OAuth/API flow yet):

- Store abstraction interfaces
- GOG provider facade and service skeletons
- Secret-store abstraction with:
  - Secret Service placeholder (not implemented)
  - In-memory session fallback
  - Composite selection logic
- DI registration in app startup

## Implemented structure

### Models

- `Models/Stores/StoreAuthState.cs`
- `Models/Stores/StoreAccountInfo.cs`
- `Models/Stores/StoreGameRecord.cs`
- `Models/Stores/StoreInstallRecord.cs`

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
- `Services/Stores/Gog/GogLibraryService.cs` (returns empty)
- `Services/Stores/Gog/GogInstallDiscoveryService.cs` (returns empty)
- `Services/Stores/Gog/Auth/GogAuthService.cs` (not implemented yet)
- `Services/Stores/Gog/Auth/GogOAuthClient.cs` (not implemented yet)
- `Services/Stores/Gog/Auth/GogOAuthLoopbackListener.cs` (not implemented yet)
- `Services/Stores/Gog/Auth/GogPkceService.cs`
- `Services/Stores/Gog/Auth/GogTokenSet.cs`

### DI registration

- `App.axaml.cs` registers:
  - `ISecretStore` as `CompositeSecretStore(SecretServiceSecretStore, InMemorySecretStore)`
  - `GogProvider`
  - `IStoreAuthProvider`, `IStoreLibraryProvider`, `IStoreInstallDiscoveryProvider` mapped to `GogProvider`

## Security contract

Required behavior for native GOG auth:

- Use system browser for OAuth authorization.
- Do not implement username/password input fields for GOG credentials in Retromind.
- Validate `state` on callback.
- Use PKCE where supported.
- Never write OAuth tokens to `app_settings.json`, library JSON, or logs.
- Prefer Secret Service for persistent refresh token storage.
- If Secret Service is unavailable, use session-only in-memory storage.

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
- Implement OAuth loopback listener and callback parsing.
- Implement token exchange and refresh HTTP flows.
- Implement GOG API clients for library and install metadata.
- Integrate new provider contracts into `StoreImportService` orchestration.
- Add UI only after backend contracts are stable.

## Update policy for this file

Whenever GOG-related code changes, update at least:

- `Last updated` date
- `Current status`
- `Implemented structure` (new/removed files)
- `Security contract` if auth/token handling changed
- `Open items` and phase progress
