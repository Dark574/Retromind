# Download manager

Open the download manager from the button in the desktop title bar. An animated underline
shows when a job is active. Select a GOG or
Epic game before opening the window to add an update. GOG titles can also queue a Windows or
Linux offline installer for the archive. Use **Start now** to override the current download
window for one job. Use **Install** to approve an update that is pending. The existing store
dialogs handle first-time installations and separate DLC maintenance.

**Pause all downloads** suspends active transfers and holds new jobs in the queue while
Retromind is open. Closing the download manager window keeps the pause in effect. **Resume download
queue** restores normal scheduling and keeps individually paused jobs paused. An installation
already in progress, including a combined Epic download and update, runs to completion.

Excluding a game from automatic updates pauses its queued or active automatic download.
Removing the exclusion resumes jobs paused for this reason and queues any known update that
was skipped. Manually queued downloads and installations already in progress are unaffected.

The **Download manager** tab in Settings offers three automatic update modes. **Disabled** is the
default. Switching to an active mode immediately queues known GOG and Epic base-game updates and
requests a check for missing revisions. The daily window uses local time and may cross midnight.
Equal start and end times allow downloads throughout the day. A global numeric speed limit applies to GOG HTTP transfers. A value of
zero allows full speed. The archive path can be absolute or relative to the Retromind data directory.
A blank path selects `GogOfflineInstallers` below that directory. The setting to keep installers is
the default in the GOG installation and DLC dialogs. Changing it in the installation dialog applies
to the game and any DLCs reinstalled with it. In the DLC dialog the choice applies to the selected
DLCs. Retromind clears the temporary installer staging after a successful
installation and keeps it available if the requested archive could not be saved.

## Runtime and storage

`DownloadManagerService` owns ordering, duplicate detection, cancellation, waiting states and transitions.
It runs one job at a time. It sleeps until a queue change, a local window boundary or a blocked-job
retry. A start override applies to the individual job. A pause or cancellation waits for the active
transfer to unwind before the next job starts. Cancelling a queued installation returns it to its
pending state. Closing the window stops and awaits queue activity
before the existing final library save. In-progress installations ask the user to leave Retromind open
until the current operation finishes. The game launch and store workflows share their operation guard
with queued installation, covering game sessions that Retromind can still track.

`State/downloads.json` sits under Retromind's data directory and uses atomic replacements. It stores
library item and store game IDs, provider, operation, a hashed update revision and queue state. The store resolves
authorization and download URLs at execution. Restart returns an interrupted transfer to the queue.
An interrupted critical installation becomes **Repair required**. The repair action uses Legendary's
repair pipeline for Epic or opens the existing GOG reinstall workflow. **Remove from list** asks for
confirmation before dismissing a repair notice and preserves the game files and downloads.

GOG resumable files are staged below `State/Downloads/Gog`. The archive uses a separate directory
per product, platform and version fingerprint. The same GOG package validator checks reused files.
Archive publication creates a sibling temporary directory and moves it into place on completion.
After a queued package is published, its completed installer files are removed from queue staging.
Later installation reuses the archive through a writable staging directory for the installer and its logs.
Existing archived packages are preserved. The archive destination is checked against symlinks,
managed staging, the library and the current installation. The existing GOG disk-space assessment
accounts for staged bytes. An archive or installation on another filesystem gets a separate check.
The installer size provides a conservative estimate of space needed to install GOG games. Legendary
still makes its own patch-specific disk-space check. Neither queue wait states nor storage estimates
trigger removal of files to make space.

## Provider behavior

- GOG shares the existing update sweep, HTTP transfer, checksums, resume and installer workflow.
  Older update notices without a target revision are checked at startup or when enabling automatic
  updates, subject to retry deadlines.
  Queued downloads can complete while the game is running. GOG installers may need license approval,
  a compatible setup program, Wine interaction, executable selection or DLC reapplication. The
  update waits for explicit installation of the version prepared by the queue. If the available
  package has changed or the local payload cannot be verified, the job returns to the regular download
  schedule and requires fresh approval. The installed version is recorded after installation succeeds.
- Epic checks its managed titles from a shared Legendary catalog snapshot. **Download and install**
  can apply an update after checking authentication, registration, disk space and the install
  directory's ownership marker. **Download only** displays an approval-required job. Legendary
  0.21.1 constructs the downloader against the install directory and starts its file writer with or
  without the no-install flag. The flag governs registration and prerequisites. Worker-count tuning
  is available, and this version exposes no dependable MB/s cap. [Pinned Legendary CLI](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L910-L1048)
  [Pinned downloader](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L1482-L1488)
- A Legendary update can write game files throughout the transfer. Once it starts, it runs to
  completion across a time-window boundary. Retromind supervises the update's private Linux process
  group with a pipe tied to its own process lifetime. Parent-pipe closure terminates the group. A
  third-party installer started interactively can launch an independent terminal or Wine process.
  After a forced exit, the recovery state identifies the install for repair.
- GOG and Epic DLC checks and DLC installations continue through their dedicated dialogs. The queue
  automatically stages GOG base-game updates and can apply Epic base-game updates.

## GOG Galaxy depot investigation

`heroic-gogdl` v1.3.0 is a candidate for an optional Windows-game depot backend. The CLI exposes
`info`, `update` and `repair`. The implementation selects Galaxy V1 or V2 depots for supported builds,
and V2 can reuse existing chunks or request patches. Its Linux platform route invokes a native GOG
installer manager. A Linux host can run the tool for Windows games, while the Windows depot path
requires a matching Windows build in the GOG catalog. [CLI](https://github.com/Heroic-Games-Launcher/heroic-gogdl/blob/v1.3.0/gogdl/cli.py)
[Platform routing](https://github.com/Heroic-Games-Launcher/heroic-gogdl/blob/v1.3.0/gogdl/dl/managers/manager.py#L63-L128)

V2 compares manifests and checks file chunks by MD5 for repair. The catalog reports build IDs,
version names, languages, dependencies and owned DLC IDs. Updates and DLC selection depend on the
backend's stored manifest and platform/language. Retromind's existing offline version and installer
fingerprint only identify the installer. An adoption path would need to verify the existing files
against an exact depot build and record its manifest, language, DLCs and ownership before the first
patch. A separate managed depot installation is the safer first integration. [V2 manifest, repair and DLC handling](https://github.com/Heroic-Games-Launcher/heroic-gogdl/blob/v1.3.0/gogdl/dl/managers/v2.py)

A future adapter would need to isolate helper state under the Retromind data root and integrate
authentication with Retromind's credential storage. It would also need to verify support for
resume, path containment, disk-space estimates and critical shutdown against real installations.
The upstream CLI expects an authentication configuration file. The release packages a Python
backend with native xdelta support. An AppImage build would need reproducibly pinned dependencies,
native compatibility and the notices and corresponding-source distribution required by the
upstream GPL-3.0 license. No gogdl code or package is distributed with this feature.
[Upstream packaging and authentication](https://github.com/Heroic-Games-Launcher/heroic-gogdl/tree/v1.3.0#readme)
[Upstream license](https://github.com/Heroic-Games-Launcher/heroic-gogdl/blob/v1.3.0/LICENSE)
