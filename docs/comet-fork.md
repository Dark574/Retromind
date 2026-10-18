# Comet fork and Retromind integration handoff

Last updated: 2026-10-18

This document records the complete state of the Retromind-specific Comet work, the Git/GitHub workflow,
the reproducible Docker validation and build commands, and the remaining Retromind integration work. It is
intended to let a later maintainer or coding-agent instance continue without reconstructing decisions from
conversation history.

## Goal and security boundary

Comet implements parts of GOG Galaxy's communication service and can provide Galaxy features such as
achievements to games launched outside the official Galaxy client. Retromind must provide Comet with the
current GOG access token, refresh token, Galaxy user ID, and user name.

Passing those values as command-line arguments is not acceptable for the Retromind integration because
arguments may be exposed through process listings, diagnostics, crash reports, or launch logs. The fork adds a
`--credentials-stdin` input mode so Retromind can write one bounded JSON object to an anonymous child-process
pipe and immediately close that pipe. The payload must never be written to a temporary file or included in a
log message.

This improves credential handling but is not a sandbox: Comet and Retromind still hold the credentials in
their process memory, and a sufficiently privileged local process can inspect either process.

## Repository state

Snapshot recorded on 2026-10-18:

| Item | Value |
| --- | --- |
| Upstream repository | `https://github.com/imLinguin/comet.git` |
| Retromind fork | `https://github.com/Dark574/comet.git` |
| Local working copy | `/home/neville/Projects/comet` |
| Upstream remote | `upstream` |
| Fork remote | `origin` |
| Working branch | `retromind/credentials-stdin` |
| Upstream base | `19b32597019ea1e64f1916010441960bcaac6e49` |
| Fork commit | `b150497dd5da0aa861a528c012f1de71a3280aeb` (`Add secure stdin credential input`) |
| Comet package version at the base | `0.3.2` |

The branch is pushed to the fork. It has not been merged into the fork's `main`, submitted upstream, tagged,
or published as a release. No Comet code or binary is integrated into Retromind yet.

Verify this state before continuing:

```bash
cd /home/neville/Projects/comet
git status --short --branch
git remote -v
git branch -vv
git log -1 --oneline
```

Expected head:

```text
b150497 Add secure stdin credential input
```

## Initial GitHub and checkout setup

These steps have already been completed on the current development machine. They are included for rebuilding
the setup elsewhere.

On CachyOS/Arch Linux, install GitHub CLI system-wide from a normal terminal:

```bash
sudo pacman -S github-cli
```

Authenticate as the normal desktop user, never with `sudo`:

```bash
gh auth login --web --git-protocol https
gh auth setup-git
gh auth status
```

The browser/device flow avoids copying a personal access token into shell history. `gh auth setup-git` is
required so ordinary HTTPS `git push` operations can use the GitHub CLI credential helper.

For a fresh checkout:

```bash
git clone --recurse-submodules https://github.com/Dark574/comet.git /home/neville/Projects/comet
cd /home/neville/Projects/comet
git remote add upstream https://github.com/imLinguin/comet.git
git fetch --all --prune
git switch retromind/credentials-stdin
git submodule update --init --recursive
```

If `upstream` already exists, do not add it again. Confirm that `origin` points to `Dark574/comet` and
`upstream` points to `imLinguin/comet` before pushing anything.

## Fork patch

The fork commit changes only:

- `src/main.rs`
- `src/import_parsers.rs`
- Comet's `README.md`

The new invocation is:

```bash
comet --credentials-stdin
```

Comet then reads exactly one JSON document from standard input:

```json
{
  "access_token": "<access-token>",
  "refresh_token": "<refresh-token>",
  "user_id": "<galaxy-user-id>",
  "username": "<user-name>"
}
```

The implementation has the following invariants:

- stdin credential input is limited to 16 KiB;
- all four properties are required and unknown properties are rejected;
- tokens must be non-empty, and the user ID and user name must contain non-whitespace text;
- `--credentials-stdin` conflicts with direct credential arguments and file-based imports;
- invalid input prints a generic error without echoing credentials and exits with status `2`;
- the raw JSON byte buffer is overwritten after parsing;
- existing Heroic, Lutris, wyvern, and direct-argument modes remain available;
- the parent must close the standard-input pipe after writing the JSON, otherwise Comet continues waiting for EOF.

The tests in the commit cover valid input, missing and empty fields, the size limit, command-line conflicts,
and the unchanged user-name requirement for the older import modes.

## Reproducible Docker toolchain

The patch was validated on Linux x86_64 with the official Rust 1.90 Bookworm image:

```text
rust:1.90-bookworm
rust@sha256:3914072ca0c3b8aad871db9169a651ccfce30cf58303e5d6f2db16d1d8a7e58f
rustc 1.90.0 (1159e78c4 2025-09-14)
cargo 1.90.0 (840b83a10 2025-07-30)
```

The digest is the exact image used for this handoff and is preferred for repeatable builds. The readable tag
is useful when deliberately updating the toolchain, but it is mutable. Comet's committed `Cargo.lock` is also
part of the reproducibility boundary, so validation and release builds use `--locked`.

Pull and verify the exact image:

```bash
docker pull rust@sha256:3914072ca0c3b8aad871db9169a651ccfce30cf58303e5d6f2db16d1d8a7e58f
docker run --rm \
  rust@sha256:3914072ca0c3b8aad871db9169a651ccfce30cf58303e5d6f2db16d1d8a7e58f \
  rustc --version
```

Docker commands should be run from the Comet checkout, not the Retromind checkout:

```bash
cd /home/neville/Projects/comet
```

### Validation

The official image includes Rustup but does not include its `rustfmt` and `clippy` components. The following
command installs them into the disposable container and then runs the complete validation. It intentionally uses
`sh -c`, not a login shell: `sh -lc` resets `PATH` in this image and hides `/usr/local/cargo/bin`. The command
mounts the source read-only and keeps Cargo downloads and build products in disposable container storage, so it
cannot leave root-owned files in the checkout:

```bash
docker run --rm \
  --mount type=bind,src="$PWD",dst=/workspace,readonly \
  --workdir /workspace \
  --env CARGO_HOME=/tmp/comet-cargo \
  --env CARGO_TARGET_DIR=/tmp/comet-target \
  rust@sha256:3914072ca0c3b8aad871db9169a651ccfce30cf58303e5d6f2db16d1d8a7e58f \
  sh -c 'set -eu
    rustup component add rustfmt clippy
    cargo fmt --all -- --check
    cargo test --locked --all-targets --all-features
    cargo clippy --locked --all-targets --all-features'
```

At commit `b150497`, formatting passed, all eight tests passed, and Clippy exited successfully. Clippy reported
four pre-existing warnings; they were not introduced or broadened by the credential patch. Do not add
`-D warnings` until those upstream warnings have either been fixed or intentionally allowed.

For repeated local runs, named Docker volumes can cache `/tmp/comet-cargo` and `/tmp/comet-target`, but they are
not required for correctness and must not be treated as release inputs.

### Build the Linux x86_64 binary

This variant runs the container as the current user and writes the ignored `target/` directory into the Comet
checkout, preventing root-owned host artifacts:

```bash
cd /home/neville/Projects/comet
docker run --rm \
  --user "$(id -u):$(id -g)" \
  --mount type=bind,src="$PWD",dst=/workspace \
  --workdir /workspace \
  --env CARGO_HOME=/tmp/comet-cargo \
  --env CARGO_TARGET_DIR=/workspace/target \
  rust@sha256:3914072ca0c3b8aad871db9169a651ccfce30cf58303e5d6f2db16d1d8a7e58f \
  cargo build --release --locked
```

The resulting binary is `target/release/comet`. Prepare a clearly named release artifact and checksum:

```bash
mkdir -p dist
install -m 0755 target/release/comet dist/comet-x86_64-unknown-linux-gnu
sha256sum dist/comet-x86_64-unknown-linux-gnu > dist/comet-x86_64-unknown-linux-gnu.sha256
```

Before publishing, run at least:

```bash
./dist/comet-x86_64-unknown-linux-gnu --help
printf '%s' '{"invalid":true}' | ./dist/comet-x86_64-unknown-linux-gnu --credentials-stdin
```

The invalid-input check must fail with exit status `2` without printing the JSON payload. Do not use live GOG
tokens in terminal examples, committed fixtures, CI logs, or shared screenshots.

## Publishing a fork build

Do not make Retromind download a mutable branch head. A consumable build needs an immutable tag, a published
binary, and a recorded SHA-256 checksum. A suitable fork-specific tag format is:

```text
retromind-v0.3.2-1
```

The first version segment records the upstream Comet version; the final number is the Retromind fork revision.
After validation:

```bash
cd /home/neville/Projects/comet
git status --short
git tag -a retromind-v0.3.2-1 -m "Retromind Comet 0.3.2 revision 1"
git push origin retromind-v0.3.2-1
gh release create retromind-v0.3.2-1 \
  dist/comet-x86_64-unknown-linux-gnu \
  dist/comet-x86_64-unknown-linux-gnu.sha256 \
  --repo Dark574/comet \
  --title "Comet 0.3.2 for Retromind, revision 1" \
  --notes "Adds bounded JSON credential delivery through --credentials-stdin."
```

Check `git status` carefully before tagging. A tag is a public release boundary and should not be moved after
publication; corrections receive the next fork revision. The inherited upstream GitHub Actions workflow builds
tags matching `v*.*.*`, not the `retromind-v...` convention above, so this documented flow deliberately uploads
the pinned Docker build. Changing that policy requires a separate review of the workflow and its release assets.

No tag or release has been created yet. The commands above are a procedure, not a record of completed work.

## Keeping the fork current

Use a rebase so the Retromind-specific delta remains one reviewable patch on top of upstream. Never overwrite
uncommitted changes, and inspect upstream changes to credential parsing before resolving conflicts.

```bash
cd /home/neville/Projects/comet
git status --short
git fetch upstream --prune
git switch main
git merge --ff-only upstream/main
git push origin main
git switch retromind/credentials-stdin
git rebase upstream/main
git submodule update --init --recursive
```

Then repeat the pinned Docker validation. If successful, update the remote feature branch with lease protection:

```bash
git push --force-with-lease origin retromind/credentials-stdin
```

For every upstream update:

1. inspect changes in argument parsing, credential import, networking, configuration paths, and logging;
2. confirm that stdin credentials are still mutually exclusive with all other sources;
3. confirm that errors and debug logs never contain the JSON or tokens;
4. rerun formatting, tests, Clippy, `--help`, and malformed-input checks;
5. build with `Cargo.lock` and the documented Rust image;
6. publish a new immutable fork revision and checksum;
7. update Retromind's pinned source/binary revision and checksum in the same Retromind change.

If upstream accepts an equivalent secure input mechanism, retire the fork only after Retromind has migrated to a
released upstream build and the complete credential and launch behavior has been retested.

## Remaining Retromind integration

The next implementation has not started. It should preserve the service and launch boundaries documented in
`docs/architecture.md` and should be split into reviewable changes.

### 1. Pin and package Comet

- Decide whether the AppImage build compiles Comet from the immutable fork tag/commit or consumes a release binary.
- Record the fork version, full commit, source or binary URL, and SHA-256 in one version manifest under `build/`.
- Extend the reproducible AppImage pipeline rather than relying on a host-installed Comet executable.
- Include the license and any required Comet runtime/configuration assets.
- Update the local component inventory when the bundled version changes.

Do not download from `main` or `retromind/credentials-stdin` during a release build.

### 2. Add a dedicated Comet process boundary

- Resolve only the Retromind-managed executable for the bundled integration; do not silently execute an unrelated
  `comet` found earlier on `PATH`.
- Start it without a shell and construct every option through `ProcessStartInfo.ArgumentList`.
- Enable standard-input redirection, serialize only the four documented fields, write the JSON, flush it, and close
  stdin before waiting for startup.
- Never place tokens in arguments, environment variables, persistent configuration, exception text, or process logs.
- Obtain the GOG refresh token through the existing `ISecretStore` boundary and obtain a fresh access token and
  account identity through the existing GOG authentication services.
- Give Comet a deliberate configuration/cache directory. Decide whether it belongs to portable runtime state or a
  host cache only after reviewing what Comet writes there; it must not leak credentials into Retromind metadata
  backups.
- Define ownership, readiness, cancellation, game-exit, and abnormal-exit behavior so orphan Comet processes are not
  left behind.

### 3. Integrate with the launch lifecycle

- Treat Comet as a managed sidecar around the existing launch plan, not as a replacement for
  `LauncherService`, Wine/Proton/UMU setup, wrapper ordering, or session tracking.
- Start Comet only for eligible GOG-linked games and stop the owned process when the tracked game session ends.
- Preserve test-launch semantics and ensure a failed optional Comet startup does not corrupt launch settings.
- Keep eligibility capability-based. Do not hard-code the feature to Windows games: native Linux GOG titles must be
  considered too, although actual Galaxy SDK/achievement support depends on each game's shipped SDK and must be
  verified with real titles.
- Verify the communication path separately for native Linux, Wine, Proton, and UMU. A successful Comet process start
  alone does not prove that the game connected or that an achievement was synchronized.

### 4. Expose achievement data only after the runtime path works

- First prove authentication, Comet readiness, game connection, and achievement synchronization with controlled
  test accounts/titles.
- Determine whether Retromind should read achievement state from Comet, GOG APIs, or another stable data source;
  do not infer a presentation API merely from Comet's runtime protocol support.
- Reuse Retromind's provider-neutral achievement presentation where possible without weakening Steam or
  RetroAchievements privacy/cache rules.
- Mark the feature experimental until both native and compatibility-layer behavior is understood.

## Validation checklist for the next instance

- [ ] Read this document and the launch/GOG sections of `docs/architecture.md` before changing code.
- [ ] Confirm the fork remotes, branch, base commit, and head commit.
- [ ] Fetch upstream and review changes before rebasing.
- [ ] Initialize the `proto` submodule recursively.
- [ ] Validate with the pinned Rust image and `Cargo.lock`.
- [ ] Confirm the stdin JSON size, schema, conflict, EOF, and redaction behavior.
- [ ] Do not use real credentials in tests or logs.
- [ ] Create an immutable fork release only after validation.
- [ ] Pin both version/commit and checksum in Retromind packaging.
- [ ] Build Retromind and add focused process-lifecycle/security tests when integration code is introduced.
- [ ] Document platform coverage and any unverified native Linux/Wine/Proton/UMU paths in the handoff.
