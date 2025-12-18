# Contributing

Thanks for considering a contribution to Retromind!

## Development setup
- .NET SDK 10.0
- Rider recommended (Avalonia tooling)
- Optional: VLC installed (if you work on video playback)
- Optional: API keys (see README)

## Build
    dotnet restore dotnet build

## Run
    dotnet run --project Retromind.csproj

Start directly in BigMode:
    
    dotnet run --project Retromind.csproj -- --bigmode

## Secrets / API keys (Scrapers)
This repository does not include real API keys.

Use the template:
- `Helpers/ApiSecretsTemplate.cs` → copy/rename to `Helpers/ApiSecrets.cs`
- rename class `ApiSecretsTemplate` → `ApiSecrets`
- keep `ApiSecrets.cs` ignored by Git

## Repository hygiene (please do not commit)
- `Library/` (runtime library + assets)
- `app_settings.json`
- `retromind_tree.json` (+ `.bak` / `.tmp`)
- `ApiSecrets.cs`

Use `app_settings.sample.json` for examples instead.

## UI thread rule (important)
- Do **not** call `Dispatcher.UIThread.*` directly in the codebase.
- Use `Helpers/UiThreadHelper` instead (`UiThreadHelper.Post(...)`, `UiThreadHelper.InvokeAsync(...)`).
- Rationale: keeps Models UI-agnostic, centralizes UI-thread marshalling, and prevents hard-to-debug cross-thread issues.

## Pull requests
- Keep PRs small and focused
- Explain the motivation and scope
- Add screenshots for UI changes if possible