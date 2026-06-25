# AGENTS.md

Guidance for coding agents working on **Retromind**.

## Project at a glance
- Desktop app: **C# + Avalonia**
- Target framework: **.NET 10** (`net10.0`)
- Primary platform: **Linux**
- State: early alpha (breaking data-format changes are acceptable when intentional)

## Architecture

Before making architectural decisions, read:

- docs/architecture.md

Treat this document as the authoritative source for:
- application architecture
- layer responsibilities
- dependency direction
- service boundaries
- MVVM rules

If architecture.md conflicts with implementation details found elsewhere,
follow architecture.md unless explicitly instructed otherwise.

## Domain-specific guidance

Retromind contains a complex launch pipeline.

Before modifying launch behavior, review:
- Services/LauncherService.cs
- docs/architecture.md

Important:
- Emulator launch configuration is inherited through
  Emulator -> Node -> MediaItem
- Relative path portability is intentional
- Wine/Proton/UMU handling has platform-specific behavior
- Wrapper chains are folded intentionally and must preserve execution order

## Repository map
- `Program.cs`, `App.axaml*`: app bootstrap and shell
- `ViewModels/`, `Views/`: MVVM UI layers
- `Services/`: business logic and external integrations
- `Models/`: domain/data models
- `Helpers/`, `Extensions/`: shared utilities and UI/behavior extensions
- `Themes/`: runtime themes and assets
- `Resources/`: localization and resources
- `build/`: packaging/migration scripts (AppImage + import tooling)
- `docs/`: project docs and changelog

## Working rules
- Keep changes focused and minimal; avoid broad refactors unless requested.
- Preserve architecture boundaries (MVVM, service separation, theme loading behavior).
- Prefer additive changes over behavior rewrites when touching import/migration logic.
- Follow current style conventions and nullable behavior (`<Nullable>enable</Nullable>`).
- Do not add new third-party dependencies unless explicitly requested.

## Portability and filesystem constraints
- Treat relative path behavior as critical (USB/AppImage portable usage).
- Do not silently convert portable-relative paths to absolute paths.
- Keep AppImage/portable env behavior intact (`APPIMAGE`, HOME/XDG overrides).
- Never commit runtime/user-generated data:
  - `Library/`
  - `app_settings.json`
  - `retromind_tree.json` and migration/backup variants
  - local logs or ad-hoc test artifacts

## Generated/managed files
- Do not hand-edit generated artifacts unless the task explicitly requires it.
- If generated files must change, modify the source and regenerate in a reproducible way.
- Keep migration scripts and docs in sync when CLI options or behavior change.

## High-impact areas (treat carefully)
- Launch/emulator path handling (absolute vs relative portability logic)
- AppImage-specific behavior and environment detection
- LibVLC integration and BigMode playback transitions
- Serialization contracts (`app_settings.json`, `retromind_tree.json`)
- Import/migration ordering and media matching rules

## Handoff expectations
- Summarize exactly what changed and why.
- List validation commands executed and their outcomes.
- Explicitly call out unverified paths, platform assumptions, and risks.

## Validation

Before completing a task:

- Build the project if code was modified.
- Fix compile errors introduced by the change.
- Do not claim success without validation.

Preferred validation:

```bash
dotnet build Retromind.csproj
```

If validation cannot be executed, explicitly state why.