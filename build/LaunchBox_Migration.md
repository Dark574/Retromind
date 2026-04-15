LaunchBox to Retromind Migration Guide (Linux)
==============================================

This guide explains how to use `Launchbox_Migration_v5.sh` to migrate a LaunchBox library to Retromind.

Prerequisites
-------------
- Linux shell (`bash`)
- `python3` installed
- LaunchBox data available on disk
- Retromind folder available on disk

What the script does
--------------------
- Reads LaunchBox platform XML files from `LaunchBoxRoot/Data/Platforms/*.xml`
- Converts games into Retromind items
- Writes a Retromind tree JSON
- Optionally copies media assets into `Retromind/Library/<Platform>/<AssetType>/`
- Can replace `retromind_tree.json` (with backup)

Basic command
-------------
```bash
bash build/Launchbox_Migration_v5.sh "<LaunchBoxRoot>" "<RetromindRoot>"
```

Example:
```bash
bash build/Launchbox_Migration_v5.sh "/media/user/LaunchBox" "/home/user/Retromind"
```

Defaults
--------
- Output JSON: `<retromind_root>/retromind_tree.launchbox.json`
- Log file: `<retromind_root>/launchbox_migration.log`

Recommended first run (safe)
----------------------------
```bash
bash build/Launchbox_Migration_v5.sh "<LaunchBoxRoot>" "<RetromindRoot>" --dry-run --verbose
```

Then inspect:
- Terminal output
- Log file

Important options
-----------------
- `--dry-run`
  Parse and analyze only. No JSON and no asset copies are written.
- `--no-copy-assets`
  Generate JSON only, do not copy media files.
- `--scan-media-folders`
  Scans LaunchBox media folders and matches by game title.
  For covers, this scan is used to enforce folder priority order.
- `--suppress-missing-launch-path-warnings`
  Suppresses warnings for missing `ApplicationPath/LaunchingCommand`.
- `--library-subdir <name>`
  Stages copied assets under `Library/<name>/...`.
- `--stage-assets-only`
  Stages assets under `--library-subdir` but keeps JSON tree structure unchanged.
  Requires `--library-subdir`.
- `--output <path>`
  Custom output JSON path.
- `--replace`
  After generating output, replace `<retromind_root>/retromind_tree.json`.
  Backup file: `<retromind_root>/retromind_tree.pre_launchbox_backup.json`.
- `--map SRC=DST`
  Repeatable Windows-path mapping, for example:
  `--map "C:\Games=/mnt/games" --map "D:\ROMs=/mnt/roms"`.
- `--log-file <path>`
  Custom log file path.
- `--verbose`
  Enable debug logging.
- `--max-warnings <n>`
  Limit warning lines kept in summary/output (default `300`).

Cover image priority (when `--scan-media-folders` is enabled)
-------------------------------------------------------------
Cover images are resolved in this order:
1. `Steam Poster`
2. `Box - Front`
3. `Box - Front - Reconstructed`
4. `Fanart - Box - Front`
5. `GOG Poster`
6. `Epic Games Poster`
7. `Uplay Thumbnail`
8. `Origin Poster`
9. `Amazon Poster`

If matching files exist in multiple folders, all matches are imported as cover assets in that order.

Typical workflow
----------------
1. Dry-run with verbose output:
   ```bash
   bash build/Launchbox_Migration_v5.sh "<LaunchBoxRoot>" "<RetromindRoot>" --dry-run --verbose --scan-media-folders
   ```
2. Add or adjust `--map` rules if needed, then dry-run again.
3. Real conversion (keep active tree unchanged):
   ```bash
   bash build/Launchbox_Migration_v5.sh "<LaunchBoxRoot>" "<RetromindRoot>" --verbose --scan-media-folders
   ```
4. If output is correct, replace active tree:
   ```bash
   bash build/Launchbox_Migration_v5.sh "<LaunchBoxRoot>" "<RetromindRoot>" --replace --verbose --scan-media-folders
   ```

Troubleshooting
---------------
- `unresolved Windows launch path ... (consider --map)`
  Add suitable `--map` entries.
- `skipping remote URI asset ...`
  Remote URLs are not copied as local files.
- `missing_file` or `asset ... not imported`
  Source file does not exist on the current system or mapping is wrong.
- Too many warnings
  Increase limit, for example: `--max-warnings 1000`.

Help
----
```bash
bash build/Launchbox_Migration_v5.sh --help
```
